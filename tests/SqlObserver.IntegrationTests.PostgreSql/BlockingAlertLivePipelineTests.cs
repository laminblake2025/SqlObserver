using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector;
using SqlObserver.Collectors;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;
using SqlObserver.IntegrationTests.SqlServer;
using SqlObserver.Server;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    private static readonly int[] LiveBlockingEvents = [1, 2];
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task LiveBlockingCollectionCommitsPermissionGapsAndDeliversSignedFireAndRecovery()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.NotEqual("Release", Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"));
        // The endpoint contract is shared with the SQL lab tests. This fixture
        // owns only a random SQL login and global temporary table, never lab data.
        string sqlConnection = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        {
            Pooling = false,
            ApplicationName = "SqlObserver blocking qualification"
        }.ConnectionString;
        await using var sqlAdmin = new SqlConnection(sqlConnection);
        await sqlAdmin.OpenAsync();
        int major;
        await using (var identity = new SqlCommand("SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion')),IS_SRVROLEMEMBER('sysadmin');", sqlAdmin))
        await using (SqlDataReader row = await identity.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            major = row.GetInt32(0);
            Assert.InRange(major, 15, 17);
            Assert.Equal(1, row.GetInt32(1));
        }
        string suffix = Guid.NewGuid().ToString("N"), login = "sqlobserver_blocking_probe_" + suffix, table = "##sqlobserver_blocking_probe_" + suffix;
        string password = "Aa9!" + Guid.NewGuid().ToString("N");
        string permission = major >= 16 ? "VIEW SERVER PERFORMANCE STATE" : "VIEW SERVER STATE";
        await using var blocker = new SqlConnection(sqlConnection);
        await blocker.OpenAsync();
        await using var waiter = new SqlConnection(sqlConnection);
        await waiter.OpenAsync();
        SqlTransaction? held = null;
        Task<int>? waiting = null;
        SqlCommand? waitCommand = null;
        try
        {
            await SqlAsync($"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=ON; GRANT {permission} TO [{login}]; CREATE TABLE [{table}](id int PRIMARY KEY,value int NOT NULL); INSERT [{table}] VALUES(1,0);");
            await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
            var target = new MonitoredInstanceId(Guid.NewGuid());
            await InsertActiveTargetAsync(database, target);
            SqlServerEndpoint endpoint = SqlServerLabContract.Endpoint;
            await ExecuteAsync(database, """
                UPDATE control.observation_target SET host_name=@host,instance_name=@instance,tcp_port=@port WHERE instance_id=@target;
                """, ("target", target.Value), ("host", endpoint.HostName.Value),
                ("instance", (object?)endpoint.InstanceName?.Value ?? DBNull.Value), ("port", (object?)endpoint.TcpPort ?? DBNull.Value));
            await using NpgsqlDataSource server = database.CreateServerDataSource();
            await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
            var leases = new PostgreSqlWorkerLeasePort(collector);
            var repository = new PostgreSqlAlertRepositoryPort(collector);
            var admin = new PostgreSqlAlertRepositoryPort(server);
            var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
            var factory = new BlockingProbeSqlFactory(sqlConnection, login);
            var discovery = new SqlServerCapabilityDiscoveryPort(factory, SqlServerCapabilityAssetCatalog.LoadEmbedded(),
                SqlServerCapabilityV2AssetCatalog.LoadEmbedded(), SqlServerCapabilityV3AssetCatalog.LoadEmbedded(),
                assetsV4: SqlServerCapabilityV4AssetCatalog.LoadEmbedded());
            CapabilityProfile profile = await discovery.DiscoverAsync(new(target, new(1), SqlServerLabContract.ConnectionPolicy,
                new(TimeSpan.FromSeconds(10)), new(TimeSpan.FromMinutes(5))), CancellationToken.None);
            Assert.NotNull(profile.ServerIdentity);
            Assert.Equal(major, profile.ServerIdentity.Version.Major);
            Assert.False(profile.IsSysAdmin);
            WorkerLeaseIdentity capabilityLease = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new("collector/capability.connection"),
                new(Guid.NewGuid()), new(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None)).Lease).Identity;
            Assert.Equal(CapabilityProfileRecordStatus.Recorded, (await new PostgreSqlCapabilityProfilePort(collector).RecordAsync(
                new(profile, capabilityLease, BlockingRuleAudit(target, AdministrativeAuditAction.RecordCapabilityProfile), Timeout), CancellationToken.None)).Status);
            await leases.ReleaseAsync(new(capabilityLease, Timeout), CancellationToken.None);
            // Use the production registry, reconciliation lease and prerequisite
            // collectors. No successful prerequisite run is inserted by the fixture.
            IConfiguration catalogConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:SqlObserverRepository"] = collector.ConnectionString,
                ["SqlObserver:IdentityFingerprintKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(catalogConfiguration);
            services.AddSqlObserverCollectorRuntime(catalogConfiguration);
            await using ServiceProvider composition = services.BuildServiceProvider();
            CollectorRegistry registry = composition.GetRequiredService<CollectorRegistry>();
            WorkerLeaseIdentity catalogLease = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new("collector/catalog/reconcile"),
                new(Guid.NewGuid()), new(TimeSpan.FromMinutes(1)), Timeout), CancellationToken.None)).Lease).Identity;
            try
            {
                await runtime.ReconcileCatalogAsync(new(registry.CatalogEntries, catalogLease, Timeout), CancellationToken.None);
            }
            finally { await leases.ReleaseAsync(new(catalogLease, Timeout), CancellationToken.None); }
            await ExecuteAsync(database, "UPDATE control.collector_schedule SET collection_interval=interval '5 seconds' WHERE instance_id=@target AND collector_id='blocking.current';", ("target", target.Value));
            SqlServerActivityCollectorAssetCatalog assets = SqlServerActivityCollectorAssetCatalog.LoadEmbedded();
            CollectorRegistration registration = WithConnectionFactory(new SqlServerCurrentBlockingCollector(assets, factory));
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog.LoadEmbedded(), factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerActivitySessionsCollector(assets, factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerActivityRequestsCollector(assets, factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerServerWaitsCollector(assets, factory)))).Summary.Outcome);
            var rule = new AlertRuleDefinition(Guid.NewGuid(), "blocking.live", AlertRuleKind.BlockingDuration, null,
                AlertComparison.GreaterThanOrEqual, 1, .25, 2, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), clearConfirmationCount: 2);
            await admin.UpsertRuleAsync(new(rule, Guid.NewGuid().ToString("D"),
                BlockingRuleAudit(target, AdministrativeAuditAction.CreateAlertRule), Timeout), CancellationToken.None);
            WorkerLeaseIdentity evaluation = await BlockingAlertLeaseAsync(collector);
            await using var receiver = await BlockingWebhookReceiver.StartAsync();
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(receiver.Configuration).Build();
            var dns = new BlockingProbeDns();
            var keys = new BlockingProbeKeys(receiver.Key);
            var approval = new ConfiguredAlertDestinationApproval(config, dns, new WindowsEventLogAlertWriter(), keys);
            var administration = new AlertAdministrationService(admin, new PostgreSqlAdministrativeAuditPort(server), approval);
            var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), AuthorizationPrincipalState.Active,
                [ApplicationRole.SecurityAdministrator], TargetAuthorizationScope.ForTargets([target]));
            Guid destination = Guid.NewGuid();
            var configure = AlertDestinationWriteRequest.Create(destination, "https-webhook", "live", true, Guid.NewGuid().ToString("D"),
                BlockingRuleAudit(target, AdministrativeAuditAction.ConfigureAlertDestination), Timeout);
            await administration.UpsertDestinationAsync(authorization, configure, CancellationToken.None);
            await administration.UpsertDestinationAsync(authorization, configure with
            {
                IdempotencyKey = Guid.NewGuid().ToString("D"),
                ExpectedRevision = 1,
                Approve = true,
                Audit = BlockingRuleAudit(target, AdministrativeAuditAction.ApproveAlertDestination)
            }, CancellationToken.None);
            using var client = new HttpClient();
            var transport = new BlockingProbeHttpFactory(receiver.Certificate, receiver.Port);
            var webhook = new HttpsWebhookAlertDestination(client, new ConfigurationAlertDestinationResolver(name => config[name], keys),
                dns, transport);
            using ILoggerFactory deliveryLogs = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
            using var delivery = new AlertDeliveryWorker(repository, new AlertDestinationDispatcher(webhook, new WindowsEventLogAlertDestination()),
                leases, new(Guid.NewGuid()), deliveryLogs.CreateLogger<AlertDeliveryWorker>());

            await SqlAsync($"REVOKE {permission} FROM [{login}];");
            CollectorEngineResult denied = await CollectAsync();
            Assert.Equal(CollectorRunOutcome.PermissionDenied, denied.Summary.Outcome);
            Assert.Null((await EvaluateAsync()).LastValue);
            Assert.Empty(receiver.Receipts);
            await SqlAsync($"GRANT {permission} TO [{login}];");
            held = (SqlTransaction)await blocker.BeginTransactionAsync();
            await using (var lockRow = new SqlCommand($"UPDATE [{table}] SET value=value+1 WHERE id=1;", blocker, held))
                await lockRow.ExecuteNonQueryAsync();
            int blocked = waiter.ServerProcessId, head = blocker.ServerProcessId;
            waitCommand = new SqlCommand($"SET LOCK_TIMEOUT 60000; BEGIN TRANSACTION; UPDATE [{table}] SET value=value+1 WHERE id=1; ROLLBACK;", waiter) { CommandTimeout = 65 };
            waiting = waitCommand.ExecuteNonQueryAsync();
            await Task.Delay(TimeSpan.FromSeconds(5.1));
            CollectorEngineResult first = await CollectAsync();
            AssertSource(first);
            Assert.Equal(AlertState.Pending, (await EvaluateAsync()).State);
            Assert.Empty(receiver.Receipts);
            await Task.Delay(TimeSpan.FromSeconds(5.1));
            CollectorEngineResult second = await CollectAsync();
            AssertSource(second);
            AlertRuleState firing = await EvaluateAsync();
            Assert.Equal(AlertState.Firing, firing.State);
            Assert.Equal(second.Payload.BlockingEdges.Items.Max(edge => edge.WaitDurationMilliseconds) / 1000d, firing.LastValue);
            await delivery.StartAsync(CancellationToken.None);
            try
            {
                await AwaitDeliveriesAsync(1);
                await Task.Delay(TimeSpan.FromSeconds(5.1));
                await SqlAsync($"REVOKE {permission} FROM [{login}];");
                Assert.Equal(CollectorRunOutcome.PermissionDenied, (await CollectAsync()).Summary.Outcome);
                AlertRuleState unknown = await EvaluateAsync();
                Assert.Equal(AlertState.Firing, unknown.State);
                Assert.Null(unknown.LastValue);
                Assert.Equal(firing.EpisodeId, unknown.EpisodeId);
                Assert.Equal(0, unknown.ConsecutiveClears);
                Assert.Single(receiver.Receipts);
                await SqlAsync($"GRANT {permission} TO [{login}];");
                await held.RollbackAsync();
                await held.DisposeAsync();
                held = null;
                await waiting;
                waiting = null;
                await Task.Delay(TimeSpan.FromSeconds(5.1));
                Assert.Empty((await CollectAsync()).Payload.BlockingEdges.Items);
                Assert.Equal(1, (await EvaluateAsync()).ConsecutiveClears);
                Assert.Single(receiver.Receipts);
                await Task.Delay(TimeSpan.FromSeconds(5.1));
                Assert.Empty((await CollectAsync()).Payload.BlockingEdges.Items);
                AlertRuleState resolved = await EvaluateAsync();
                Assert.Equal(AlertState.Resolved, resolved.State);
                Assert.Equal(firing.AlertId, resolved.AlertId);
                await AwaitDeliveriesAsync(2);
                BlockingWebhookReceipt[] receipts = receiver.Receipts.ToArray();
                Assert.Equal(2, receipts.Length);
                Assert.Equal(2, receipts.Select(receipt => receipt.DeliveryId).Distinct().Count());
                Assert.Equal(LiveBlockingEvents, receipts.Select(receipt => { using var json = JsonDocument.Parse(receipt.Body); return json.RootElement.GetProperty("event").GetInt32(); }));
                Assert.All(receipts, receipt =>
                {
                    using var json = JsonDocument.Parse(receipt.Body);
                    Assert.Equal(target.Value, json.RootElement.GetProperty("targetId").GetGuid());
                    Assert.Equal(firing.AlertId, json.RootElement.GetProperty("alertId").GetGuid());
                });
            }
            finally { await delivery.StopAsync(CancellationToken.None); }
            await leases.ReleaseAsync(new(evaluation, Timeout), CancellationToken.None);

            Task<CollectorEngineResult> CollectAsync() => CollectRegistrationAsync(registration);
            CollectorRegistration WithConnectionFactory(SqlObserver.Collector.Abstractions.ICollector actual)
            {
                CollectorRegistration registered = Assert.Single(registry.Registrations, item => item.Manifest.Id == actual.Manifest.Id);
                return new(registered.ExecutionOrder, actual, registered.OutputValidator, registered.ManifestDigest, registered.AssetBundleDigest);
            }
            async Task<CollectorEngineResult> CollectRegistrationAsync(CollectorRegistration selected)
            {
                // Discovery and real SQL output are used; this method runs the
                // normal engine and commit ports, without seeding run/edge rows.
                CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new(ListDueCollectorWorkRequest.MaximumItems, Timeout), CancellationToken.None)).Items,
                    item => item.TargetId == target && item.CollectorId == selected.Manifest.Id);
                Assert.NotNull(work.CapabilityProfile);
                WorkerLeaseIdentity lease = await AcquireRunLeaseAsync(leases, work);
                var run = new CollectorRunId(Guid.NewGuid());
                try
                {
                    Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), CancellationToken.None)).Status);
                    CollectorEngineResult result = await new CollectorExecutionEngine().ExecuteAsync(selected, work, run, CancellationToken.None);
                    Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
                        new(work, result.Summary, result.Payload, result.NextCircuit, lease, Timeout), CancellationToken.None)).Status);
                    return result;
                }
                finally { await leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None); }
            }
            async Task<AlertRuleState> EvaluateAsync()
            {
                IReadOnlyList<AlertEvaluationWork> work = await repository.ClaimDueEvaluationsAsync(evaluation, 100, Timeout, CancellationToken.None);
                Assert.Single(work);
                Assert.Equal(1, (await repository.EvaluateAndPersistAsync(await BlockingRuntimeBatchAsync(repository, evaluation, work), CancellationToken.None)).Evaluated);
                return Assert.IsType<AlertRuleState>(await repository.GetStateAsync(target, rule.RuleId, Timeout, CancellationToken.None));
            }
            void AssertSource(CollectorEngineResult result)
            {
                Assert.Equal(CollectorRunOutcome.Succeeded, result.Summary.Outcome);
                Assert.False(result.Summary.Loss.HasLoss);
                BlockingEdgeObservation edge = Assert.Single(result.Payload.BlockingEdges.Items, e => e.BlockedSessionId == blocked);
                Assert.Equal(head, edge.BlockerSessionId);
                Assert.True(edge.WaitDurationMilliseconds >= 1000);
                Assert.Equal(result.Payload.ItemCount, result.Summary.Accounting.OutputItemsProduced);
            }
            async Task AwaitDeliveriesAsync(int expected)
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (await BlockingQueueCountAsync(database, target, "SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target AND completed_at IS NOT NULL AND cancelled_at IS NULL;") < expected)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        await using var state = database.DataSource.CreateCommand(
                            "SELECT jsonb_agg(jsonb_build_object('attempt',attempt,'error',last_error_code,'cancel',cancel_reason," +
                            "'completed',completed_at IS NOT NULL,'claimed',leased_until IS NOT NULL))::text " +
                            "FROM alerting.delivery_outbox WHERE instance_id=@target;");
                        state.Parameters.AddWithValue("target", target.Value);
                        Assert.Fail("Delivery deadline: " + await state.ExecuteScalarAsync() + "; transport: " + string.Join("; ", transport.Failures));
                    }
                    await Task.Delay(100);
                }
                Assert.Equal(expected, receiver.Receipts.Count);
            }
        }
        finally
        {
            if (held is not null)
            {
                await held.RollbackAsync();
                await held.DisposeAsync();
            }
            if (waiting is not null)
            {
                try
                {
                    await waiting;
                }
                catch (SqlException) { /* Cleanup still owns the SQL sessions and temporary objects. */ }
            }
            waitCommand?.Dispose();
            await blocker.CloseAsync();
            await waiter.CloseAsync();
            await SqlAsync($"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE [{table}]; IF EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'{login}') DROP LOGIN [{login}];");
        }
        async Task SqlAsync(string sql)
        {
            await using var command = new SqlCommand(sql, sqlAdmin) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class BlockingProbeSqlFactory(string connectionString, string login) : ISqlServerConnectionFactory
    {
        public async ValueTask<SqlConnection> OpenConnectionAsync(SqlServerConnectionPolicy policy, CancellationToken token)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(token);
                await using var impersonate = new SqlCommand($"EXECUTE AS LOGIN=N'{login}'; SELECT IS_SRVROLEMEMBER('sysadmin');", connection) { CommandTimeout = 5 };
                Assert.Equal(0, await impersonate.ExecuteScalarAsync(token));
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
    private sealed class BlockingProbeDns : IAlertDnsResolver
    {
        internal static readonly IPAddress Address = IPAddress.Parse("93.184.216.34");
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("alerts.example.test", host);
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>([Address]);
        }
    }
    private sealed class BlockingProbeKeys(byte[] key) : IWebhookSigningKeyProvider
    {
        public byte[] ReadKey(string path)
        {
            Assert.Equal("ephemeral-live-test-key", path);
            return key.ToArray();
        }
    }
    private sealed class BlockingProbeHttpFactory(X509Certificate2 certificate, int port) : IAlertDestinationHttpClientFactory
    {
        public ConcurrentQueue<string> Failures { get; } = new();
        public HttpClient Create(IAlertDnsResolver resolver, Func<IPAddress, bool> allowlist, IReadOnlySet<IPAddress> pinned)
        {
            // Retain production address checks; the lab-only socket seam routes
            // the single synthetic approved address to our owned loopback server.
            SocketsHttpHandler handler = AddressBoundConnectHandler.Create(resolver, allowlist, pinned, async (address, destinationPort, token) =>
            {
                Assert.Equal(BlockingProbeDns.Address, address);
                Assert.Equal(port, destinationPort);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            });
            handler.SslOptions.RemoteCertificateValidationCallback = (_, actual, _, errors) => actual is not null &&
                actual.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256) &&
                errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors;
            return new HttpClient(new BlockingTransportDiagnostics(handler, Failures), disposeHandler: true);
        }
    }
    private sealed class BlockingTransportDiagnostics(HttpMessageHandler inner, ConcurrentQueue<string> failures) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            try
            {
                return await base.SendAsync(request, token);
            }
            catch (HttpRequestException exception) { failures.Enqueue(exception.ToString()); throw; }
        }
    }
    private sealed record BlockingWebhookReceipt(Guid DeliveryId, byte[] Body);
    private sealed class BlockingWebhookReceiver(WebApplication app, X509Certificate2 certificate, byte[] key) : IAsyncDisposable
    {
        public X509Certificate2 Certificate => certificate; public byte[] Key => key;
        public int Port
        {
            get; private set;
        }
        public ConcurrentQueue<BlockingWebhookReceipt> Receipts { get; } = new();
        public Dictionary<string, string?> Configuration => new()
        {
            ["AlertDestinations:live"] = $"https://alerts.example.test:{Port}/hook",
            ["AlertDestinations:live:Allowlist"] = BlockingProbeDns.Address.ToString(),
            ["AlertDestinations:live:Revision"] = "1",
            ["AlertDestinations:live:Signing:KeyId"] = "live-test",
            ["AlertDestinations:live:Signing:ProtectedKeyPath"] = "ephemeral-live-test-key"
        };
        public static async Task<BlockingWebhookReceiver> StartAsync()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=alerts.example.test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("alerts.example.test");
            request.CertificateExtensions.Add(names.Build());
            // Windows Schannel needs an owned imported private key after the
            // RSA/request scope ends. Do not install or trust this certificate.
            using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
            byte[] pfx = generated.Export(X509ContentType.Pkcs12);
            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            WebApplication app = builder.Build();
            var receiver = new BlockingWebhookReceiver(app, certificate, RandomNumberGenerator.GetBytes(32));
            app.MapPost("/hook", async (HttpRequest incoming) =>
            {
                using var bytes = new MemoryStream();
                await incoming.Body.CopyToAsync(bytes);
                byte[] body = bytes.ToArray();
                string timestamp = incoming.Headers["X-SqlObserver-Timestamp"].ToString(), delivery = incoming.Headers["X-SqlObserver-Delivery-Id"].ToString();
                Assert.Equal("live-test", incoming.Headers["X-SqlObserver-Key-Id"].ToString());
                Assert.InRange(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp, CultureInfo.InvariantCulture)), 0, 30);
                byte[] prefix = Encoding.UTF8.GetBytes($"sqlobserver.webhook.v1\nPOST\nhttps://alerts.example.test:{receiver.Port}/hook\nlive-test\n{timestamp}\n{delivery}\n");
                byte[] expected = HMACSHA256.HashData(receiver.Key, prefix.Concat(body).ToArray());
                string signature = incoming.Headers["X-SqlObserver-Signature"].ToString();
                Assert.StartsWith("v1=", signature, StringComparison.Ordinal);
                Assert.True(CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signature[3..])));
                receiver.Receipts.Enqueue(new(Guid.Parse(delivery), body));
                return Results.Accepted();
            });
            try
            {
                await app.StartAsync();
                receiver.Port = new Uri(app.Urls.Single()).Port;
                return receiver;
            }
            catch { await receiver.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            certificate.Dispose();
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
