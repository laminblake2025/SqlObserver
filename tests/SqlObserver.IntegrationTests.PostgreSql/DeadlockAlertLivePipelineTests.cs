using System.Security.Cryptography;
using System.Text.Json;
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
    private static readonly int[] LiveDeadlockEvents = [1, 2];
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task LiveDeadlockCollectionCommitsPermissionGapsAndDeliversSignedFireAndRecovery()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.NotEqual("Release", Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"));
        // The endpoint contract is shared with the SQL lab tests. This fixture
        // owns only a random SQL login and global temporary table, never lab data.
        string sqlConnection = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        {
            Pooling = false,
            ApplicationName = "SqlObserver deadlock qualification"
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
        string suffix = Guid.NewGuid().ToString("N"), login = "sqlobserver_deadlock_probe_" + suffix, table = "##sqlobserver_deadlock_probe_" + suffix;
        string password = "Aa9!" + Guid.NewGuid().ToString("N");
        string permission = major >= 16 ? "VIEW SERVER PERFORMANCE STATE" : "VIEW SERVER STATE";
        await using var blocker = new SqlConnection(sqlConnection);
        await blocker.OpenAsync();
        await using var waiter = new SqlConnection(sqlConnection);
        await waiter.OpenAsync();
        SqlTransaction? firstTransaction = null, secondTransaction = null;
        try
        {
            await SqlAsync($"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=ON; GRANT {permission} TO [{login}]; CREATE TABLE [{table}](id int PRIMARY KEY,value int NOT NULL); INSERT [{table}] VALUES(1,0),(2,0);");
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
                new(TimeSpan.FromSeconds(10)), new(TimeSpan.FromMinutes(15))), CancellationToken.None);
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
            await ExecuteAsync(database, "UPDATE control.collector_schedule SET collection_interval=interval '15 seconds' WHERE instance_id=@target AND collector_id='deadlocks.system-health';", ("target", target.Value));
            SqlServerActivityCollectorAssetCatalog assets = SqlServerActivityCollectorAssetCatalog.LoadEmbedded();
            CollectorRegistration registration = WithConnectionFactory(new SqlServerDeadlockCollector(SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded(), factory));
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog.LoadEmbedded(), factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerActivitySessionsCollector(assets, factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerActivityRequestsCollector(assets, factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerServerWaitsCollector(assets, factory)))).Summary.Outcome);
            Assert.Equal(CollectorRunOutcome.Succeeded, (await CollectRegistrationAsync(WithConnectionFactory(new SqlServerCurrentBlockingCollector(assets, factory)))).Summary.Outcome);
            CollectorEngineResult baseline = await CollectAsync();
            AssertComplete(baseline);
            // Retained events from a prior qualification remain in system_health.
            // Let the real count reach zero before creating this fixture's rule.
            DateTimeOffset baselineDeadline = DateTimeOffset.UtcNow.AddMinutes(6);
            while (baseline.Payload.Deadlocks.Items.Any(item => item.OccurredAtUtc >= DateTimeOffset.UtcNow.AddMinutes(-5)))
            {
                Assert.True(DateTimeOffset.UtcNow < baselineDeadline, "The disposable SQL lab has continuing deadlocks.");
                await Task.Delay(TimeSpan.FromSeconds(15.2));
                baseline = await CollectAsync();
                AssertComplete(baseline);
            }
            var rule = new AlertRuleDefinition(Guid.NewGuid(), "deadlocks.live", AlertRuleKind.DeadlockCount, null,
                AlertComparison.GreaterThan, 0, 0, 2, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15), clearConfirmationCount: 2);
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

            AlertRuleState zero = await EvaluateAsync();
            Assert.Equal(0d, zero.LastValue);
            Assert.Equal(AlertState.Normal, zero.State);
            DateTimeOffset workloadStartedAt = DateTimeOffset.UtcNow;
            await SqlAsyncOnAsync(blocker, "SET DEADLOCK_PRIORITY LOW;");
            await SqlAsyncOnAsync(waiter, "SET DEADLOCK_PRIORITY NORMAL;");
            firstTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
            secondTransaction = (SqlTransaction)await waiter.BeginTransactionAsync();
            await SqlAsyncOnAsync(blocker, $"UPDATE [{table}] WITH (ROWLOCK) SET value=value+1 WHERE id=1;", firstTransaction);
            await SqlAsyncOnAsync(waiter, $"UPDATE [{table}] WITH (ROWLOCK) SET value=value+1 WHERE id=2;", secondTransaction);
            int firstSession = blocker.ServerProcessId, secondSession = waiter.ServerProcessId;
            await using (var firstCross = new SqlCommand($"UPDATE [{table}] WITH (ROWLOCK) SET value=value+1 WHERE id=2;", blocker, firstTransaction) { CommandTimeout = 30 })
            await using (var secondCross = new SqlCommand($"UPDATE [{table}] WITH (ROWLOCK) SET value=value+1 WHERE id=1;", waiter, secondTransaction) { CommandTimeout = 30 })
            {
                int[] outcomes = await Task.WhenAll(DeadlockOutcomeAsync(firstCross), DeadlockOutcomeAsync(secondCross));
                Assert.Equal(1205, outcomes[0]);
                Assert.Equal(0, outcomes[1]);
            }
            await RollbackAsync(firstTransaction);
            firstTransaction = null;
            await RollbackAsync(secondTransaction);
            secondTransaction = null;
            // The lab's existing system_health dispatch interval is 120 seconds.
            // Poll normal scheduled collection without altering/flushing XE.
            CollectorEngineResult first = baseline;
            DateTimeOffset sourceDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (!first.Payload.Deadlocks.Items.Any(IsOwned))
            {
                Assert.True(DateTimeOffset.UtcNow < sourceDeadline, "Owned deadlock did not reach the existing XE file target.");
                await WaitForNextScanAsync();
                first = await CollectAsync();
                AssertComplete(first);
                if (!first.Payload.Deadlocks.Items.Any(IsOwned)) Assert.Equal(0d, (await EvaluateAsync()).LastValue);
            }
            DeadlockObservation owned = Assert.Single(first.Payload.Deadlocks.Items, IsOwned);
            Assert.True(Assert.Single(owned.Participants, item => item.SessionId == firstSession).IsVictim);
            Assert.False(Assert.Single(owned.Participants, item => item.SessionId == secondSession).IsVictim);
            Assert.NotEmpty(owned.Relations);
            Assert.InRange(DateTimeOffset.UtcNow - owned.OccurredAtUtc, TimeSpan.Zero, TimeSpan.FromMinutes(3));
            Assert.Equal(AlertState.Pending, (await EvaluateAsync()).State);
            Assert.Empty(receiver.Receipts);
            await WaitForNextScanAsync();
            CollectorEngineResult second = await CollectAsync();
            AssertComplete(second);
            Assert.Equal(owned.EventId, Assert.Single(second.Payload.Deadlocks.Items, IsOwned).EventId);
            AlertRuleState firing = await EvaluateAsync();
            Assert.Equal(AlertState.Firing, firing.State);
            Assert.Equal(1d, firing.LastValue);
            await delivery.StartAsync(CancellationToken.None);
            try
            {
                await AwaitDeliveriesAsync(1);
                await WaitForNextScanAsync();
                await SqlAsync($"REVOKE {permission} FROM [{login}];");
                Assert.Equal(CollectorRunOutcome.PermissionDenied, (await CollectAsync()).Summary.Outcome);
                AlertRuleState unknown = await EvaluateAsync();
                Assert.Equal(AlertState.Firing, unknown.State);
                Assert.Null(unknown.LastValue);
                Assert.Equal(firing.EpisodeId, unknown.EpisodeId);
                Assert.Equal(0, unknown.ConsecutiveClears);
                Assert.Contains("reason=source_unavailable", unknown.Reason, StringComparison.Ordinal);
                Assert.Single(receiver.Receipts);
                await SqlAsync($"GRANT {permission} TO [{login}];");
                DateTimeOffset recoveryDeadline = owned.OccurredAtUtc.AddMinutes(6);
                AlertRuleState state = unknown;
                int zeroObservations = 0;
                while (state.State != AlertState.Resolved)
                {
                    Assert.True(DateTimeOffset.UtcNow < recoveryDeadline, "Real five-minute deadlock window did not resolve.");
                    await WaitForNextScanAsync();
                    CollectorEngineResult scan = await CollectAsync();
                    AssertComplete(scan);
                    Assert.Equal(owned.EventId, Assert.Single(scan.Payload.Deadlocks.Items, IsOwned).EventId);
                    state = await EvaluateAsync();
                    if (state.LastValue == 0)
                    {
                        zeroObservations++;
                        Assert.Equal(zeroObservations == 1 ? AlertState.Firing : AlertState.Resolved, state.State);
                        Assert.Equal(zeroObservations == 1 ? 1 : 0, state.ConsecutiveClears);
                    }
                    if (DateTimeOffset.UtcNow < owned.OccurredAtUtc.AddMinutes(5))
                    {
                        Assert.Equal(1d, state.LastValue);
                        Assert.Equal(AlertState.Firing, state.State);
                        Assert.Equal(0, state.ConsecutiveClears);
                    }
                    if (state.State != AlertState.Resolved) Assert.Single(receiver.Receipts);
                }
                Assert.Equal(0d, state.LastValue);
                Assert.Equal(2, zeroObservations);
                Assert.Equal(0, state.ConsecutiveClears);
                Assert.Equal(firing.AlertId, state.AlertId);
                Assert.Equal(firing.EpisodeId, state.EpisodeId);
                await AwaitDeliveriesAsync(2);
                BlockingWebhookReceipt[] receipts = receiver.Receipts.ToArray();
                Assert.Equal(2, receipts.Select(receipt => receipt.DeliveryId).Distinct().Count());
                Assert.Equal(LiveDeadlockEvents, receipts.Select(receipt => { using var json = JsonDocument.Parse(receipt.Body); return json.RootElement.GetProperty("event").GetInt32(); }));
                Assert.All(receipts, receipt =>
                {
                    using var json = JsonDocument.Parse(receipt.Body);
                    Assert.Equal(target.Value, json.RootElement.GetProperty("targetId").GetGuid());
                    Assert.Equal(firing.AlertId, json.RootElement.GetProperty("alertId").GetGuid());
                });
                Assert.Equal(baseline.Payload.Deadlocks.Items.Count + 1L, await BlockingQueueCountAsync(database, target,
                    "SELECT count(*) FROM events.deadlock_summary WHERE instance_id=@target;"));
            }
            finally { await delivery.StopAsync(CancellationToken.None); }
            await leases.ReleaseAsync(new(evaluation, Timeout), CancellationToken.None);

            bool IsOwned(DeadlockObservation item) => item.OccurredAtUtc >= workloadStartedAt &&
                item.Participants.Any(p => p.SessionId == firstSession) &&
                item.Participants.Any(p => p.SessionId == secondSession);
            Task<CollectorEngineResult> CollectAsync() => CollectRegistrationAsync(registration);
            async Task WaitForNextScanAsync()
            {
                await Task.Delay(TimeSpan.FromSeconds(15.2));
                Assert.Equal(LeaseRenewalStatus.Renewed, (await leases.RenewAsync(
                    new(evaluation, new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None)).Status);
            }
            CollectorRegistration WithConnectionFactory(SqlObserver.Collector.Abstractions.ICollector actual)
            {
                CollectorRegistration registered = Assert.Single(registry.Registrations, item => item.Manifest.Id == actual.Manifest.Id);
                return new(registered.ExecutionOrder, actual, registered.OutputValidator, registered.ManifestDigest, registered.AssetBundleDigest);
            }
            async Task<CollectorEngineResult> CollectRegistrationAsync(CollectorRegistration selected)
            {
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
            static void AssertComplete(CollectorEngineResult result)
            {
                Assert.Equal(CollectorRunOutcome.Succeeded, result.Summary.Outcome);
                Assert.False(result.Summary.Loss.HasLoss);
                Assert.All(result.Payload.Deadlocks.Items, item => Assert.False(item.ParseTruncated));
                Assert.Equal(result.Payload.ItemCount, result.Summary.Accounting.OutputItemsProduced);
            }
            async Task AwaitDeliveriesAsync(int expected)
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                while (await BlockingQueueCountAsync(database, target, "SELECT count(*) FROM alerting.delivery_outbox WHERE instance_id=@target AND completed_at IS NOT NULL AND cancelled_at IS NULL;") < expected)
                {
                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        await using var command = database.DataSource.CreateCommand(
                            "SELECT jsonb_agg(jsonb_build_object('attempt',attempt,'error',last_error_code,'cancel',cancel_reason))::text FROM alerting.delivery_outbox WHERE instance_id=@target;");
                        command.Parameters.AddWithValue("target", target.Value);
                        Assert.Fail("Delivery deadline: " + await command.ExecuteScalarAsync() + "; transport: " + string.Join("; ", transport.Failures));
                    }
                    await Task.Delay(100);
                }
                Assert.Equal(expected, receiver.Receipts.Count);
            }
        }
        finally
        {
            if (firstTransaction is not null) await RollbackAsync(firstTransaction);
            if (secondTransaction is not null) await RollbackAsync(secondTransaction);
            await blocker.CloseAsync();
            await waiter.CloseAsync();
            await SqlAsync($"IF OBJECT_ID('tempdb..{table}') IS NOT NULL DROP TABLE [{table}]; IF EXISTS(SELECT 1 FROM sys.server_principals WHERE name=N'{login}') DROP LOGIN [{login}];");
        }
        async Task SqlAsync(string sql) => await SqlAsyncOnAsync(sqlAdmin, sql);
        static async Task SqlAsyncOnAsync(SqlConnection connection, string sql, SqlTransaction? transaction = null)
        {
            await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync();
        }
        static async Task<int> DeadlockOutcomeAsync(SqlCommand command)
        {
            try { await command.ExecuteNonQueryAsync(); return 0; }
            catch (SqlException exception) when (exception.Number == 1205) { return 1205; }
        }
        static async Task RollbackAsync(SqlTransaction transaction)
        {
            try { await transaction.RollbackAsync(); }
            catch (InvalidOperationException) when (transaction.Connection is null) { /* SQL Server already rolled back the deadlock victim. */ }
            finally { await transaction.DisposeAsync(); }
        }
    }
}
