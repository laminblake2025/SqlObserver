using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

[Trait("Category", "RequiresSqlServer")]
public sealed class IncrementalDeadlockNativeTests
{
    [Fact]
    public async Task IncrementalAdapterPublishesNativeWitnessesAndResumesTheSameSource()
    {
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync();
        SqlServerIncrementalDeadlockAssetCatalog catalog = SqlServerIncrementalDeadlockAssetCatalog.LoadEmbedded();
        var collector = new SqlServerIncrementalDeadlockCollector(catalog, (request, token) => Read(request, token));
        var target = new MonitoredInstanceId(Guid.NewGuid());
        var revision = new ObservationTargetRevision(1);
        DateTimeOffset at = DeadlockReadTime.ToRepositoryPrecision(DateTimeOffset.UtcNow);
        int major = int.Parse(connection.ServerVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        // Adapter/source qualification only: this profile does not attest the lab principal or TLS policy.
        var profile = new CapabilityProfile(target, revision, new("capability.connection"), 1, 1,
            new(new(major, 0, 1000, 1), new("native source fixture"), SqlServerEngineEdition.Express, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            true, false, [], [], TimeSpan.FromSeconds(1), 1, at, at.AddMinutes(5));
        CollectorExecutionRequest firstRequest = Request(null, null);
        CollectorExecutionResult first = await collector.CollectAsync(firstRequest, default);
        var validator = new CollectorOutputValidator(SqlServerIncrementalDeadlockCollector.OutputContract);
        validator.Validate(catalog.Manifest, firstRequest, first);
        Assert.Equal(CollectorRunOutcome.Succeeded, first.Outcome);
        Assert.Equal(3, first.Accounting.SourceRowsRead);
        Assert.False(first.DeadlockReadProof!.ReachedEnd);
        Assert.Equal(first.Accounting.SourceRowsRead, first.DeadlockReadProof.Positions.Count);
        Assert.True(first.Accounting.ResponseBytes >= 128 + 64 * first.DeadlockReadProof.Positions.Count);
        Assert.InRange(first.Accounting.ResponseBytes, 128, 4096);
        XeReadIdentity source = first.DeadlockReadProof.Source!;
        XeReadBookmark? bookmark = XeFileReadProgressCalculator.Compare(null, source, first.DeadlockReadProof.Positions, false).SafeBookmark;
        CollectorExecutionRequest nextRequest = Request(source, bookmark);
        CollectorExecutionResult next = await collector.CollectAsync(nextRequest, default);
        validator.Validate(catalog.Manifest, nextRequest, next);
        Assert.Equal(CollectorRunOutcome.Succeeded, next.Outcome);
        Assert.Equal(source, next.DeadlockReadProof!.Source);
        Assert.Equal(nextRequest.DeadlockReadContext!.ExpectedCheckpointDigest, next.DeadlockReadProof.ExpectedCheckpointDigest);
        Assert.Equal(next.Accounting.SourceRowsRead, next.DeadlockReadProof.Positions.Count);
        if (bookmark is not null)
            Assert.DoesNotContain(bookmark.Position, next.DeadlockReadProof.Positions);

        CollectorExecutionRequest Request(XeReadIdentity? resumeSource, XeReadBookmark? resumeBookmark)
        {
            var run = new CollectorRunId(Guid.NewGuid());
            var context = new DeadlockReadExecutionContext(target, revision, 1, run,
                resumeSource is null ? null : new(new string('a', 64)), resumeSource, resumeBookmark);
            return new(run, target, revision, new(new(new("native-source-fixture"), tcpPort: 1433), new(TimeSpan.FromSeconds(1))),
                profile, new(1), new(TimeSpan.FromSeconds(20)), context);
        }
        async ValueTask<XeFileSourcePage> Read(CollectorExecutionRequest request, CancellationToken token)
            => await catalog.Source.ReadAsync(connection, request.TargetId, request.TargetRevision, request.DeadlockReadContext!.Bookmark,
                request.DeadlockReadContext.ResumeSource, new(metadataRows: 2, responseBytes: 4096), token);
    }
}
