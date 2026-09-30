using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class AvailabilityGroupV2NativeQualificationTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresAvailabilityGroup")]
    public async Task DirectPrimaryAndSecondaryPreserveNativeVisibilityAndDatabaseIdentity()
    {
        AvailabilityGroupLabContract lab = AvailabilityGroupLabContract.FromEnvironment();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var collector = new SqlServerAvailabilityGroupsV2Collector(
            SqlServerAvailabilityGroupV2AssetCatalog.LoadEmbedded(), new IdentityFingerprintKey(new byte[32]));
        // One test-only identity lets us compare source identities from both observation locations.
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var revision = new ObservationTargetRevision(1);
        AvailabilityGroupsV2Snapshot primary = await CollectAsync(lab.Primary);
        AvailabilityGroupsV2Snapshot secondary = await CollectAsync(lab.Secondary);
        var primaryLocal = primary.Replicas.Where(row => row.Identity.ReplicaIsLocal == true && row.Role == "PRIMARY").ToArray();
        var secondaryLocal = secondary.Replicas.Where(row => row.Identity.ReplicaIsLocal == true && row.Role == "SECONDARY").ToArray();
        string group = Assert.Single(primaryLocal.Select(row => row.Identity.GroupFingerprint)
            .Intersect(secondaryLocal.Select(row => row.Identity.GroupFingerprint)));
        var primaryRows = primary.Replicas.Where(row => row.Identity.GroupFingerprint == group).ToArray();
        var secondaryRows = secondary.Replicas.Where(row => row.Identity.GroupFingerprint == group).ToArray();
        Assert.True(primaryRows.Length >= 2, "Qualification requires a joined AG with at least two replicas.");
        Assert.Equal(primaryRows.Select(row => row.Identity.ReplicaFingerprint).Order(),
            secondaryRows.Select(row => row.Identity.ReplicaFingerprint).Order());
        Assert.All(primaryRows, row =>
        {
            Assert.True(row.StateObserved);
            Assert.Equal(AvailabilityVisibilityScope.PrimaryAllKnown, row.Identity.VisibilityScope);
            if (row.Identity.ReplicaIsLocal == false) Assert.Null(row.OperationalState);
        });
        Assert.All(secondaryRows, row =>
        {
            Assert.Equal(AvailabilityVisibilityScope.SecondaryLocalOnly, row.Identity.VisibilityScope);
            Assert.Equal(row.Identity.ReplicaIsLocal == true, row.StateObserved);
            if (!row.StateObserved)
            { Assert.Null(row.Role); Assert.Null(row.OperationalState); Assert.Null(row.ConnectedState); }
        });
        var primaryDatabases = primary.Databases.Where(row => row.Identity.GroupFingerprint == group).ToArray();
        var secondaryDatabases = secondary.Databases.Where(row => row.Identity.GroupFingerprint == group).ToArray();
        Assert.NotEmpty(primaryDatabases);
        Assert.Equal(primaryDatabases.Select(DatabaseKey).Order(), secondaryDatabases.Select(DatabaseKey).Order());
        Assert.All(primaryDatabases, row => Assert.True(row.StateObserved));
        Assert.Contains(secondaryDatabases, row => row.Identity.ReplicaIsLocal == true && row.StateObserved);
        Assert.Contains(secondaryDatabases, row => row.Identity.ReplicaIsLocal == false && !row.StateObserved);
        Assert.All(secondaryDatabases.Where(row => !row.StateObserved), row =>
        {
            Assert.Null(row.SynchronizationState); Assert.Null(row.SynchronizationHealth);
            Assert.Null(row.DatabaseState); Assert.Null(row.IsSuspended);
            Assert.Null(row.LogSendQueueKb); Assert.Null(row.LogSendRateKbPerSecond);
            Assert.Null(row.RedoQueueKb); Assert.Null(row.RedoRateKbPerSecond); Assert.Null(row.SourceLastCommitLocal);
        });
        Assert.All(primaryDatabases.Concat(secondaryDatabases), row =>
        {
            if (row.SourceLastCommitLocal.HasValue)
                Assert.Equal(DateTimeKind.Unspecified, row.SourceLastCommitLocal.Value.Kind);
        });
        Assert.Equal(OperationalObservationState.Degraded, secondary.State);

        async Task<AvailabilityGroupsV2Snapshot> CollectAsync(SqlServerConnectionPolicy policy)
        {
            CapabilityProfile profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(
                new CapabilityDiscoveryRequest(target, revision, policy,
                    new CapabilityDiscoveryTimeout(TimeSpan.FromSeconds(10)),
                    new CapabilityProfileRefreshInterval(TimeSpan.FromMinutes(5))), deadline.Token);
            Assert.Equal(CapabilityDiscoveryOutcome.Supported, profile.Outcome);
            Assert.NotNull(profile.ServerIdentity);
            Assert.Equal(lab.ExpectedMajor, profile.ServerIdentity.Version.Major);
            Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
            Assert.Contains(profile.ServerIdentity.EngineEdition, new[] { SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise });
            Assert.False(profile.IsSysAdmin); Assert.True(profile.TransportEncrypted);
            Assert.Contains(profile.AuthenticationScheme, new[] { SqlServerAuthenticationScheme.Kerberos, SqlServerAuthenticationScheme.Ntlm });
            await AssertReadOnlyPrincipalAsync(policy, deadline.Token);
            var request = new CollectorExecutionRequest(new CollectorRunId(Guid.NewGuid()), target, revision,
                policy, profile, new CollectorAttemptNumber(1), new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
            CollectorExecutionResult result = await collector.CollectAsync(request, deadline.Token);
            new CollectorOutputValidator(SqlServerAvailabilityGroupsV2Collector.OutputContract)
                .Validate(collector.Manifest, request, result);
            Assert.Contains(result.Outcome, new[] { CollectorRunOutcome.Succeeded, CollectorRunOutcome.Partial });
            Assert.Contains(result.Reason, new[] { CollectorRunReason.Completed, CollectorRunReason.VisibilityIncomplete });
            var snapshot = Assert.IsType<AvailabilityGroupsV2Snapshot>(result.Payload.OperationalHealth?.Snapshot);
            Assert.False(snapshot.Truncated);
            Assert.Equal(snapshot.Replicas.Count + snapshot.Databases.Count, snapshot.SourceRowsRead);
            Assert.Equal(snapshot.SourceRowsRead, result.Accounting.SourceRowsRead);
            return snapshot;
        }
    }

    private static string DatabaseKey(AvailabilityDatabaseReplicaEvidence row) =>
        row.Identity.ReplicaFingerprint + ":" + row.DatabaseFingerprint;

    private static async Task AssertReadOnlyPrincipalAsync(SqlServerConnectionPolicy policy, CancellationToken cancellationToken)
    {
        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        await using SqlConnection connection = await factory.OpenConnectionAsync(policy, cancellationToken);
        await using var command = new SqlCommand("""
            SELECT HAS_PERMS_BY_NAME(NULL,'SERVER','CONTROL SERVER'),
                   HAS_PERMS_BY_NAME(NULL,'SERVER','ALTER ANY AVAILABILITY GROUP'),
                   HAS_PERMS_BY_NAME(NULL,'SERVER','ALTER ANY LOGIN'),
                   CONVERT(int,SERVERPROPERTY('IsHadrEnabled'));
            """, connection) { CommandTimeout = 5 };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        Assert.Equal(0, reader.GetInt32(0)); Assert.Equal(0, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(1, reader.GetInt32(3));
    }
}
