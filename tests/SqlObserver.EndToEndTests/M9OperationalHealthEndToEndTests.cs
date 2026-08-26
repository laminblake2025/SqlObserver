using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Net;
using System.Reflection;
using Npgsql;
using SqlObserver.Collector.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Coordination;
using SqlObserver.Security;
using SqlObserver.Server;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.EndToEndTests;

public sealed class M9OperationalHealthEndToEndTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly AuthorizationContext Viewer = new(new ActorSecurityIdentifier("S-1-5-21-9300"), AuthorizationPrincipalState.Active, [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([Target]));
    private static readonly AuthorizationContext Denied = new(new ActorSecurityIdentifier("S-1-5-21-9301"), AuthorizationPrincipalState.Active, [], TargetAuthorizationScope.None());

    [Fact]
    public async Task ProductionKestrelCompositionServesAllSixOperationalRoutes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(HostedAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, HostedAuthenticationHandler>(HostedAuthenticationHandler.SchemeName, static _ => { });
        builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddSingleton(new WindowsGroupRoleResolver([
            new WindowsGroupRoleBinding(new ActorSecurityIdentifier(HostedAuthenticationHandler.ViewerSid), [ApplicationRole.Viewer], true),
        ]));
        builder.Services.AddOptions<OperationalHealthServerOptions>();
        builder.Services.AddSingleton<IOperationalHealthRepositoryPort, M9BoundedRepository>();
        builder.Services.AddSingleton<IOperationalHealthQueryService, OperationalHealthQueryService>();

        await using WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOperationalHealthEndpoints();
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Add(HostedAuthenticationHandler.IdentityHeader, "viewer");
            foreach (string route in new[] { "backups", "sql-agent/failures", "tempdb", "tempdb/files", "availability-groups/replicas", "availability-groups/databases" })
            {
                HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{Target.Value:D}/{route}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("targetId", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
        finally { await app.StopAsync(); }
    }

    [PostgreSqlFact]
    [Trait("Category", "RequiresPostgreSql")]
    public async Task EnvironmentBackedPostgreSqlControlPlaneServesSeededM9Projection()
    {
        string connectionString = Environment.GetEnvironmentVariable("SQLOBSERVER_E2E_POSTGRES")!;
        (string isolatedConnectionString, string databaseName) = await CreateIsolatedPostgreSqlDatabaseAsync(connectionString);
        try
        {
            Guid target = Guid.NewGuid();
            await using NpgsqlDataSource dataSource = PostgreSqlDataSourceFactory.Create(isolatedConnectionString, "SqlObserver.EndToEndTests");
            MigrationBatchResult migration = await new PostgreSqlMigrationPort(dataSource).ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
            Assert.DoesNotContain(migration.Results, static result => result.Outcome == MigrationOutcome.Failed);
            await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync())
            await using (var seed = new NpgsqlCommand("INSERT INTO control.observation_target (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,updated_at,discovery_requested_at) VALUES (@target,@key,'M9 E2E target','sql01',1433,interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,clock_timestamp(),clock_timestamp());", connection))
            {
                seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"m9.e2e.{target:N}"); await seed.ExecuteNonQueryAsync();
            }
            await CommitBackupProjectionAsync(dataSource, target);
            await using PostgreSqlTargetControlPlane controlPlane = PostgreSqlTargetControlPlane.Create(isolatedConnectionString, "SqlObserver.EndToEndTests.Host");
            WebApplicationBuilder builder = WebApplication.CreateBuilder(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddAuthentication(HostedAuthenticationHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, HostedAuthenticationHandler>(HostedAuthenticationHandler.SchemeName, static _ => { });
            builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            builder.Services.AddSingleton(new WindowsGroupRoleResolver([new WindowsGroupRoleBinding(new ActorSecurityIdentifier(HostedAuthenticationHandler.ViewerSid), [ApplicationRole.Viewer], true)]));
            builder.Services.AddSingleton<IOperationalHealthRepositoryPort>(controlPlane.OperationalHealth); builder.Services.AddSingleton<IOperationalHealthQueryService, OperationalHealthQueryService>();
            await using WebApplication app = builder.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapOperationalHealthEndpoints(); await app.StartAsync();
            try
            {
                using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) }; client.DefaultRequestHeaders.Add(HostedAuthenticationHandler.IdentityHeader, "viewer");
                HttpResponseMessage response = await client.GetAsync($"/api/v1/observation-targets/{target:D}/backups"); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("databaseFingerprint", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            finally { await app.StopAsync(); }
        }
        finally { await DropIsolatedPostgreSqlDatabaseAsync(connectionString, databaseName); }
    }

    [Fact]
    public void EnvironmentBackedPostgreSqlTestIsDiscoverableAndDynamicallyOptIn()
    {
        var method = typeof(M9OperationalHealthEndToEndTests).GetMethod(nameof(EnvironmentBackedPostgreSqlControlPlaneServesSeededM9Projection));
        Assert.NotNull(method);
        Assert.Contains(method!.CustomAttributes, static attribute => attribute.AttributeType == typeof(TraitAttribute) && attribute.ConstructorArguments.Count == 2 && attribute.ConstructorArguments[0].Value as string == "Category" && attribute.ConstructorArguments[1].Value as string == "RequiresPostgreSql");
        FactAttribute fact = method.GetCustomAttributes<FactAttribute>().Single();
        Assert.IsType<PostgreSqlFactAttribute>(fact);
        Assert.Equal(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SQLOBSERVER_E2E_POSTGRES")), fact.Skip is not null);
    }

    [Fact]
    public async Task ProductionQueryCompositionExposesSixBoundedSurfacesAndCursors()
    {
        var repository = new M9BoundedRepository();
        var service = new OperationalHealthQueryService(repository);
        var request = new OperationalHealthRequest(Target, null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        Assert.NotNull(await service.GetBackupsAsync(Viewer, request, CancellationToken.None));
        Assert.NotNull(await service.GetAgentFailuresAsync(Viewer, request, CancellationToken.None));
        Assert.NotNull(await service.GetTempDbAsync(Viewer, request, CancellationToken.None));
        Assert.NotNull(await service.GetTempDbFilesAsync(Viewer, request, CancellationToken.None));
        Assert.NotNull(await service.GetAvailabilityGroupReplicasAsync(Viewer, request, CancellationToken.None));
        Assert.NotNull(await service.GetAvailabilityGroupDatabasesAsync(Viewer, request, CancellationToken.None));
        BackupStatusSnapshot first = (await service.GetBackupsAsync(Viewer, request, CancellationToken.None))!;
        Assert.NotNull(first.NextCursor);
        BackupStatusSnapshot second = (await service.GetBackupsAsync(Viewer, request with { Cursor = first.NextCursor }, CancellationToken.None))!;
        Assert.Null(second.NextCursor);
        Assert.Equal(first.TargetId, second.TargetId);
    }

    [Fact]
    public async Task ProductionQueryCompositionEnforcesRoleAndTargetScopeAndAgentPrivacy()
    {
        var service = new OperationalHealthQueryService(new M9BoundedRepository());
        var request = new OperationalHealthRequest(Target, null, null, 10, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetBackupsAsync(Denied, request, CancellationToken.None).AsTask());
        SqlAgentFailureSnapshot page = (await service.GetAgentFailuresAsync(Viewer, request, CancellationToken.None))!;
        Assert.All(page.Items, item => { Assert.False(item.ContentAvailable); Assert.Equal(item.DetectedAtUtc, item.FirstObservedAtUtc); Assert.Null(item.MessageId); });
    }

    private static async Task CommitBackupProjectionAsync(NpgsqlDataSource dataSource, Guid target)
    {
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(dataSource);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None)).Items.Single(item => item.TargetId.Value == target && item.CollectorId.Value == "backups.status");
        Guid owner = Guid.NewGuid(); string leaseKey = $"collector/run/{work.CollectorId.Value}/{target:N}";
        await using (NpgsqlConnection connection = await dataSource.OpenConnectionAsync())
        await using (var leaseCommand = new NpgsqlCommand("SELECT acquired,fencing_token FROM control.acquire_worker_lease(@key,@owner,interval '5 minutes');", connection))
        {
            leaseCommand.Parameters.AddWithValue("key", leaseKey); leaseCommand.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader reader = await leaseCommand.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0));
            WorkerLeaseIdentity lease = new(new WorkerLeaseKey(leaseKey), new WorkerExecutionId(owner), new FencingToken(reader.GetInt64(1)));
            CollectorRunId run = new(Guid.NewGuid());
            Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None)).Status);
            var item = new BackupStatusObservation(new MonitoredInstanceId(target), work.TargetRevision, new string('a', 64), BackupKind.Full, work.RepositoryTimeUtc, null, false, 100, false, true, false, BackupCoverage.Complete) { BackupSetId = 1 };
            var snapshot = new BackupStatusSnapshot(new MonitoredInstanceId(target), work.TargetRevision, run, work.RepositoryTimeUtc, OperationalObservationState.Complete, [item], 1, false);
            var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, 1, 160));
            var summary = new CollectorRunSummary(run, new MonitoredInstanceId(target), work.TargetRevision, work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(1, 1, 160, 160), CollectorLossEvidence.None);
            Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(new CommitCollectorRunRequest(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None)).Status);
        }
    }

    private static async Task<(string ConnectionString, string DatabaseName)> CreateIsolatedPostgreSqlDatabaseAsync(string sourceConnectionString)
    {
        string databaseName = $"sqlobserver_e2e_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(sourceConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\";", connection); await command.ExecuteNonQueryAsync();
        var isolated = new NpgsqlConnectionStringBuilder(sourceConnectionString) { Database = databaseName, Pooling = false };
        return (isolated.ConnectionString, databaseName);
    }

    private static async Task DropIsolatedPostgreSqlDatabaseAsync(string sourceConnectionString, string databaseName)
    {
        var admin = new NpgsqlConnectionStringBuilder(sourceConnectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString); await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);", connection); await command.ExecuteNonQueryAsync();
    }

    internal sealed class M9BoundedRepository : IOperationalHealthRepositoryPort
    {
        private static readonly ObservationTargetRevision Revision = new(1);
        private static readonly CollectorRunId Run = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
        private static readonly DateTimeOffset Observed = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        private static string Cursor(OperationalHealthRequest request) => request.Cursor is null ? new OperationalHealthCursor(Target, Observed, "page-1").Encode() : null!;
        private static bool More(OperationalHealthRequest request) => request.Cursor is null;
        private static T Build<T>(Func<T> create) where T : class => create();
        public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest r, CancellationToken _) => ValueTask.FromResult<BackupStatusSnapshot?>(Build(() => new BackupStatusSnapshot(Target, Revision, Run, Observed, OperationalObservationState.Complete, [new BackupStatusObservation(Target, Revision, new string('a', 64), BackupKind.Full, Observed, null, false, 1, false, true, false, BackupCoverage.Complete) { BackupSetId = 1 }], 1, false) { NextCursor = Cursor(r) }));
        public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest r, CancellationToken _) => ValueTask.FromResult<SqlAgentFailureSnapshot?>(Build(() => new SqlAgentFailureSnapshot(Target, Revision, Run, Observed, OperationalObservationState.Complete, [new SqlAgentFailureObservation(Target, Revision, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), 1, 1, 0, AgentFailureKind.Failed, null, null, 0, 1, new DateTime(2026, 8, 25), TimeSpan.FromMinutes(1), Observed, new string('b', 64))], 1, false, r.FromUtc ?? Observed.AddHours(-24), r.ToUtc ?? Observed) { NextCursor = null }));
        public ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest r, CancellationToken _) => ValueTask.FromResult<TempDbSnapshot?>(new TempDbSnapshot(Target, Revision, Run, Observed, OperationalObservationState.Complete, 100, 50, 100, 50, [], false));
        public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest r, CancellationToken _) => ValueTask.FromResult<TempDbSnapshot?>(new TempDbSnapshot(Target, Revision, Run, Observed, OperationalObservationState.Complete, null, null, null, null, [new TempDbFileObservation(Target, Revision, 1, 100, 50, 50, TempDbComponentState.Healthy)], false));
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest r, CancellationToken _) => GetAvailabilityGroupReplicasAsync(r, _);
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest r, CancellationToken _) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(new AvailabilityGroupsSnapshot(Target, Revision, Run, Observed, OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, [new AvailabilityReplicaObservation(Target, Revision, new string('c', 64), new string('d', 64), "PRIMARY", "ONLINE", "CONNECTED", AvailabilityVisibilityScope.PrimaryAllKnown, true)], [], false));
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest r, CancellationToken _) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(new AvailabilityGroupsSnapshot(Target, Revision, Run, Observed, OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, [], [new AvailabilityDatabaseObservation(Target, Revision, new string('c', 64), new string('e', 64), "SYNCHRONIZED", "ONLINE", AvailabilityVisibilityScope.PrimaryAllKnown, true)], false));
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SQLOBSERVER_E2E_POSTGRES")))
            Skip = "RequiresPostgreSql: set SQLOBSERVER_E2E_POSTGRES to run the environment-backed E2E test.";
    }
}

internal sealed class HostedAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "M9.EndToEnd";
    internal const string IdentityHeader = "X-SqlObserver-M9-E2E-Identity";
    internal const string ViewerSid = "S-1-5-21-9300";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var values) || values.Count != 1 || values[0] != "viewer") return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-9300"), new Claim(ClaimTypes.GroupSid, ViewerSid)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
