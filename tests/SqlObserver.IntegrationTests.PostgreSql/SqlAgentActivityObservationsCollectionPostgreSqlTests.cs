using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentActivityObservationsCollectionPostgreSqlTests(PostgreSql18Fixture fixture)
{
    internal static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task ForwardMigrationAndCatalogRequireExplicitOptInAndCurrentEnrollment()
    {
        await using var db = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(db.DataSource);
        Assert.False((await migrations.ApplyPendingAsync(new(370, new(TimeSpan.FromMinutes(2))), CancellationToken.None)).HasFailures);
        Guid existing = await TargetAsync(db);
        Assert.False((await migrations.ApplyPendingAsync(new(MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None)).HasFailures);
        Guid added = await TargetAsync(db);
        await using var server = db.CreateServerDataSource();
        var admin = new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(server);
        foreach (Guid target in new[] { existing, added })
            Assert.False((await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!.Enabled);
        await ReconcileAsync(db);
        var state = (await admin.GetAsync(new(existing), 1, Timeout, CancellationToken.None))!;
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.EnrollmentUnavailable,
            (await admin.SetAsync(Request(existing, 1, state.ScheduleRevision, false, true), CancellationToken.None)).Status);
        await new PostgreSqlSqlAgentModuleEnrollmentPort(server).SetAsync(Enrollment(existing, 0), CancellationToken.None);
        state = (await admin.GetAsync(new(existing), 1, Timeout, CancellationToken.None))!;
        Assert.False(state.Enabled);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied,
            (await admin.SetAsync(Request(existing, 1, state.ScheduleRevision, false, true), CancellationToken.None)).Status);
        await ReconcileAsync(db);
        Assert.True((await admin.GetAsync(new(existing), 1, Timeout, CancellationToken.None))!.Enabled);
        Assert.False((await admin.GetAsync(new(added), 1, Timeout, CancellationToken.None))!.Enabled);
        await ServerLifecycleAdmissionPostgreSqlTests.ExecuteAsync(db,
            "UPDATE control.observation_target SET revision=revision+1,updated_at=clock_timestamp() WHERE instance_id=@target", ("target", existing));
        await ReconcileAsync(db);
        var changed = (await admin.GetAsync(new(existing), 2, Timeout, CancellationToken.None))!;
        Assert.False(changed.Enabled);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.EnrollmentUnavailable,
            (await admin.SetAsync(Request(existing, 2, changed.ScheduleRevision, false, true), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task RevocationAndReenrollmentAdvanceScheduleWithoutResumingAndBlockOldCas()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource();
        var enrollment = new PostgreSqlSqlAgentModuleEnrollmentPort(server);
        var admin = new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(server);
        var first = Enrollment(target, 0);
        await enrollment.SetAsync(first, CancellationToken.None);
        var state = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        var optIn = Request(target, 1, state.ScheduleRevision, false, true);
        var enabled = await admin.SetAsync(optIn, CancellationToken.None);
        Assert.Equal(state.ScheduleRevision + 1, enabled.ScheduleRevision);
        var noop = await admin.SetAsync(Request(target, 1, enabled.ScheduleRevision!.Value, true, true), CancellationToken.None);
        Assert.Equal(enabled.ScheduleRevision, noop.ScheduleRevision);
        var revoke = Enrollment(target, 1, revoke: true);
        await enrollment.SetAsync(revoke, CancellationToken.None);
        var revoked = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        Assert.False(revoked.Enabled); Assert.Equal(enabled.ScheduleRevision + 1, revoked.ScheduleRevision);
        await enrollment.SetAsync(revoke, CancellationToken.None);
        Assert.Equal(revoked, await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None));
        var original = first.Change.Enrollment!;
        var samePins = new SqlAgentModuleEnrollment(new(target), new(1), 3, original.EnrollmentId,
            original.ExpectedWindowsSid, original.Binding, original.NotBeforeUtc, original.ExpiresAtUtc, original.EvidenceSha256);
        await enrollment.SetAsync(Enrollment(target, 2) with { Change = new(new(target), new(1), 2, Guid.NewGuid(), samePins) }, CancellationToken.None);
        var reenrolled = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        Assert.False(reenrolled.Enabled); Assert.Equal(revoked.ScheduleRevision + 1, reenrolled.ScheduleRevision);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.RevisionConflict,
            (await admin.SetAsync(optIn, CancellationToken.None)).Status);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied,
            (await admin.SetAsync(Request(target, 1, reenrolled.ScheduleRevision, false, true), CancellationToken.None)).Status);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.RevisionConflict,
            (await enrollment.SetAsync(first, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(-2, -1)]
    [InlineData(1, 2)]
    public async Task ExpiredOrFutureEnrollmentCannotEnableButOffNoopRemainsAvailable(int fromDays, int toDays)
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture); Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource();
        await new PostgreSqlSqlAgentModuleEnrollmentPort(server).SetAsync(Enrollment(target, 0,
            from: DateTimeOffset.UtcNow.AddDays(fromDays), to: DateTimeOffset.UtcNow.AddDays(toDays)), CancellationToken.None);
        var admin = new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(server);
        var state = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.EnrollmentUnavailable,
            (await admin.SetAsync(Request(target, 1, state.ScheduleRevision, false, true), CancellationToken.None)).Status);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied,
            (await admin.SetAsync(Request(target, 1, state.ScheduleRevision, false, false), CancellationToken.None)).Status);
        Assert.Equal(state, await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None));
        // Privileged fixture simulates an expired legacy opt-in, never a runtime authorization path.
        await ServerLifecycleAdmissionPostgreSqlTests.ExecuteAsync(db,
            "UPDATE control.collector_schedule SET enabled=true WHERE instance_id=@target AND collector_id='sql-agent.activity-observations'", ("target", target));
        var disabled = await admin.SetAsync(Request(target, 1, state.ScheduleRevision, true, false), CancellationToken.None);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied, disabled.Status);
        Assert.Equal(state.ScheduleRevision + 1, disabled.ScheduleRevision);
        await ServerLifecycleAdmissionPostgreSqlTests.ExecuteAsync(db,
            "UPDATE control.collector_schedule SET enabled=true WHERE instance_id=@target AND collector_id='sql-agent.activity-observations'", ("target", target));
        await ReconcileAsync(db);
        var reconciled = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        Assert.False(reconciled.Enabled); Assert.Equal(state.ScheduleRevision + 2, reconciled.ScheduleRevision);
    }

    [Fact]
    public async Task ScheduleCasAndTargetStatusAreAuditedAndDisableDoesNotRequireEnrollment()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture); Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource();
        await new PostgreSqlSqlAgentModuleEnrollmentPort(server).SetAsync(Enrollment(target, 0), CancellationToken.None);
        var admin = new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(server);
        var state = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        var enable = Request(target, 1, state.ScheduleRevision, false, true);
        var result = await admin.SetAsync(enable, CancellationToken.None);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied, result.Status);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.RevisionConflict, (await admin.SetAsync(enable, CancellationToken.None)).Status);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.RevisionConflict,
            (await admin.SetAsync(Request(target, 2, result.ScheduleRevision!.Value, true, false), CancellationToken.None)).Status);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Applied,
            (await admin.SetAsync(Request(target, 1, result.ScheduleRevision!.Value, true, false), CancellationToken.None)).Status);
        Assert.Equal(5L, await ServerLifecycleAdmissionPostgreSqlTests.CountAsync(db,
            "SELECT count(*) FROM audit.activity WHERE subject_identifier=@target", ("target", target.ToString("D"))));
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.NotFound,
            (await admin.SetAsync(Request(Guid.NewGuid(), 1, 1, false, true), CancellationToken.None)).Status);
        await ServerLifecycleAdmissionPostgreSqlTests.ExecuteAsync(db,
            "UPDATE control.observation_target SET lifecycle_state='pending_discovery',updated_at=clock_timestamp() WHERE instance_id=@target", ("target", target));
        state = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.Inactive,
            (await admin.SetAsync(Request(target, 1, state.ScheduleRevision, false, true), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task DefinitionMutationDisablesOptInEvenWithoutRevisionBumpAndRejectsStaleEndpointPins()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture); Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource();
        await new PostgreSqlSqlAgentModuleEnrollmentPort(server).SetAsync(Enrollment(target, 0), CancellationToken.None);
        var admin = new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(server);
        var state = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        var enabled = await admin.SetAsync(Request(target, 1, state.ScheduleRevision, false, true), CancellationToken.None);
        await ServerLifecycleAdmissionPostgreSqlTests.ExecuteAsync(db,
            "UPDATE control.observation_target SET host_name='other.invalid',updated_at=clock_timestamp() WHERE instance_id=@target", ("target", target));
        var changed = (await admin.GetAsync(new(target), 1, Timeout, CancellationToken.None))!;
        Assert.False(changed.Enabled); Assert.True(changed.ScheduleRevision > enabled.ScheduleRevision);
        Assert.Equal(SqlAgentActivityObservationsCollectionMutationStatus.EnrollmentUnavailable,
            (await admin.SetAsync(Request(target, 1, changed.ScheduleRevision, false, true), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task RestrictedRolesCannotBypassEnrollmentOrScheduleFunctions()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture); Guid target = await TargetAsync(db);
        await using var server = db.CreateServerDataSource(); await using var collector = db.CreateCollectorDataSource();
        await using (var bypass = server.CreateCommand("SELECT control.sql_agent_activity_enrollment_active(@target,1)"))
        {
            bypass.Parameters.AddWithValue("target", target);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => bypass.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var unscoped = server.CreateCommand("SELECT * FROM reporting.read_sql_agent_activity_observations_collection_state(@target,1)"))
        {
            unscoped.Parameters.AddWithValue("target", target);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => unscoped.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var mutation = collector.CreateCommand("SELECT * FROM control.set_sql_agent_activity_observations_collection_enabled(@target,1,1,false,true,'S-1-5-21-1700',@correlation)"))
        {
            mutation.Parameters.AddWithValue("target", target); mutation.Parameters.AddWithValue("correlation", Guid.NewGuid());
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => mutation.ExecuteNonQueryAsync())).SqlState);
        }
        await using var unavailable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=invalid;Username=invalid;Timeout=1");
        var port = new PostgreSqlSqlAgentActivityObservationsCollectionAdministrationPort(unavailable);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => port.SetAsync(Request(target, 1, 0, false, true), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => port.SetAsync(Request(target, 1, SqlAgentActivityObservationsCollectionValidation.MaximumRevision, false, true), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => port.SetAsync(Request(target, 1, 1, false, true) with
            { Audit = Request(Guid.NewGuid(), 1, 1, false, true).Audit }, CancellationToken.None).AsTask());
    }

    internal static SetSqlAgentActivityObservationsCollectionRequest Request(Guid target, long revision, long schedule, bool expected, bool enabled) =>
        new(new(target), revision, schedule, expected, enabled,
            new(new("S-1-5-21-1700"), new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, new(target)), Timeout);

    internal static SetSqlAgentModuleEnrollmentRequest Enrollment(Guid target, long expected, bool revoke = false,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var id = new MonitoredInstanceId(target);
        var binding = new SqlAgentModuleBinding("sql01", 1433, "sql01", "SQL01", "SQL01", "", 42, 43,
            SqlAgentModuleBinding.ApprovedDefinitionSha256, new string('a', 64), new string('b', 40));
        SqlAgentModuleEnrollment? value = revoke ? null : new(id, new(1), expected + 1, Guid.NewGuid(),
            "S-1-5-21-1-2-3-1002", binding, from ?? DateTimeOffset.UtcNow.AddDays(-1), to ?? DateTimeOffset.UtcNow.AddDays(1), new string('c', 64));
        return new(new(id, new(1), expected, Guid.NewGuid(), value),
            new(new("S-1-5-21-1700"), new(Guid.NewGuid()), AdministrativeAuditAction.UpdateObservationTarget, id), Timeout);
    }

    internal static async Task<Guid> TargetAsync(RepositoryTestDatabase db)
    {
        Guid target = Guid.NewGuid();
        await ServerLifecycleAdmissionPostgreSqlTests.ExecuteAsync(db, """
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,certificate_host_name,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Agent observation fixture','sql01',1433,'sql01',interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now())
            """, ("target", target), ("key", "agent.observations." + target.ToString("N")));
        return target;
    }

    private static async Task ReconcileAsync(RepositoryTestDatabase db)
    {
        await using var collector = db.CreateCollectorDataSource(); var leases = new PostgreSqlWorkerLeasePort(collector);
        var lease = (await leases.AcquireAsync(new(new("collector/catalog/reconcile"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(2)), Timeout), CancellationToken.None)).Lease!.Identity;
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:SqlObserverRepository"] = collector.ConnectionString, ["SqlObserver:IdentityFingerprintKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build();
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IConfiguration>(configuration); services.AddSqlObserverCollectorRuntime(configuration);
            await using var provider = services.BuildServiceProvider(); var registry = provider.GetRequiredService<CollectorRegistry>();
            Assert.Equal(22, registry.CatalogEntries.Count);
            Assert.Equal(22, (await new PostgreSqlCollectorRuntimeRepositoryPort(collector).ReconcileCatalogAsync(new(registry.CatalogEntries, lease, Timeout), CancellationToken.None)).UnchangedCount);
        }
        finally { await leases.ReleaseAsync(new(lease, Timeout), CancellationToken.None); }
    }
}
