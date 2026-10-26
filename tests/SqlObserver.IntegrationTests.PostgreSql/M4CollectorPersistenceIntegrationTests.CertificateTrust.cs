using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M4CollectorPersistenceIntegrationTests
{
    [Fact]
    public async Task CertificateTrustReachesTargetDiscoveryScheduledClaimsAndLiveActivityWithoutDatabaseChanges()
    {
        await using var database = await CreateMigratedDatabaseAsync();
        var approved = new MonitoredInstanceId(Guid.NewGuid());
        var other = new MonitoredInstanceId(Guid.NewGuid());
        await InsertActiveTargetAsync(database, approved, 1);
        await InsertActiveTargetAsync(database, other, 1);
        var trust = new SqlServerCertificateTrustOverrides([new(approved.Value, "sql01.example.test", 1433)]);
        await using var server = database.CreateServerDataSource();
        await using var collector = database.CreateCollectorDataSource();
        var targets = new PostgreSqlObservationTargetPort(server, trust);
        Assert.True((await targets.GetAsync(new(approved, DefaultTimeout), CancellationToken.None))!.ConnectionPolicy.TrustServerCertificate);
        Assert.False((await targets.GetAsync(new(other, DefaultTimeout), CancellationToken.None))!.ConnectionPolicy.TrustServerCertificate);
        Assert.False((await new PostgreSqlObservationTargetPort(server).GetAsync(new(approved, DefaultTimeout), CancellationToken.None))!.ConnectionPolicy.TrustServerCertificate);

        var due = await new PostgreSqlCapabilityProfilePort(collector, trust).ListDueAsync(new(16, DefaultTimeout), CancellationToken.None);
        Assert.True(Assert.Single(due.Targets, row => row.TargetId == approved).ConnectionPolicy.TrustServerCertificate);
        Assert.False(Assert.Single(due.Targets, row => row.TargetId == other).ConnectionPolicy.TrustServerCertificate);

        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector, new IdentityFingerprintKey(new byte[32]), trust);
        var scheduled = await runtime.ListDueAsync(new(16, DefaultTimeout), CancellationToken.None);
        Assert.NotEmpty(scheduled.Items);
        Assert.All(scheduled.Items, row => Assert.Equal(row.TargetId == approved, row.ConnectionPolicy.TrustServerCertificate));
        var claim = await runtime.ClaimDueAsync(new(new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromSeconds(30)), DefaultTimeout), CancellationToken.None);
        Assert.NotNull(claim);
        Assert.Equal(claim.Work.TargetId == approved, claim.Work.ConnectionPolicy.TrustServerCertificate);

        var live = await new PostgreSqlLiveActivityRepository(collector, trust).TargetsAsync(CancellationToken.None);
        Assert.True(Assert.Single(live, row => row.Id == approved.Value).Connection.TrustServerCertificate);
        Assert.False(Assert.Single(live, row => row.Id == other.Value).Connection.TrustServerCertificate);
        await using var changeEndpoint = database.DataSource.CreateCommand("UPDATE control.observation_target SET tcp_port=1444 WHERE instance_id=@id");
        changeEndpoint.Parameters.AddWithValue("id", approved.Value);
        await changeEndpoint.ExecuteNonQueryAsync();
        Assert.False((await targets.GetAsync(new(approved, DefaultTimeout), CancellationToken.None))!.ConnectionPolicy.TrustServerCertificate);
    }
}
