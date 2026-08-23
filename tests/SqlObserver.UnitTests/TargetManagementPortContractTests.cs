using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class TargetManagementPortContractTests
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly DateTimeOffset UtcNow = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DueRequestIsHardBoundedAndHasNoCallerClock()
    {
        var request = new CapabilityDiscoveryDueRequest(
            CapabilityDiscoveryDueRequest.MaximumTargets,
            Timeout);

        Assert.Equal(16, request.MaxTargets);
        Assert.Null(typeof(CapabilityDiscoveryDueRequest).GetProperty("DueAtUtc"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityDiscoveryDueRequest(0, Timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityDiscoveryDueRequest(17, Timeout));
    }

    [Fact]
    public void MutationRequestRejectsMismatchedAuditActionOrTarget()
    {
        MonitoredInstanceId targetId = CreateTargetId(1);
        var wrongAction = new AdministrativeAuditEnvelope(
            new ActorSecurityIdentifier("S-1-5-21-100"),
            new AuditCorrelationId(Guid.NewGuid()),
            AdministrativeAuditAction.RetireObservationTarget,
            targetId);
        var wrongTarget = new AdministrativeAuditEnvelope(
            new ActorSecurityIdentifier("S-1-5-21-100"),
            new AuditCorrelationId(Guid.NewGuid()),
            AdministrativeAuditAction.RequestCapabilityRediscovery,
            CreateTargetId(2));

        Assert.Throws<ArgumentException>(() => new RequestCapabilityRediscoveryRequest(
            targetId,
            new ObservationTargetRevision(1),
            wrongAction,
            Timeout));
        Assert.Throws<ArgumentException>(() => new RequestCapabilityRediscoveryRequest(
            targetId,
            new ObservationTargetRevision(1),
            wrongTarget,
            Timeout));
    }

    [Fact]
    public void TargetPageRequiresDeterministicKeyAndIdOrdering()
    {
        ObservationTarget first = CreateTarget(CreateTargetId(2), "a-target");
        ObservationTarget second = CreateTarget(CreateTargetId(1), "b-target");
        var cursor = new ObservationTargetListCursor(second.Key, second.TargetId);
        ObservationTarget[] source = [first, second];
        var page = new ObservationTargetPage(source, cursor);
        source[0] = second;

        Assert.Same(first, page.Targets[0]);
        Assert.Same(cursor, page.NextCursor);
        Assert.Throws<ArgumentException>(() => new ObservationTargetPage([second, first], nextCursor: null));
        Assert.Throws<ArgumentException>(() => new ObservationTargetPage(
            [first, second],
            new ObservationTargetListCursor(first.Key, first.TargetId)));
    }

    [Fact]
    public void ScopedListAndBulkProfileInputsAreDefensivelyCopiedAndBounded()
    {
        MonitoredInstanceId first = CreateTargetId(1);
        MonitoredInstanceId second = CreateTargetId(2);
        MonitoredInstanceId[] identifiers = [first];
        var bulk = new GetLatestCapabilityProfilesRequest(identifiers, Timeout);
        identifiers[0] = second;
        var list = new ListObservationTargetsRequest(
            TargetAuthorizationScope.ForTargets([first]),
            maxResults: 100,
            cursor: null,
            includeRetired: false,
            Timeout);

        Assert.Equal(first, bulk.TargetIds[0]);
        Assert.True(list.TargetScope.Contains(first));
        Assert.Throws<ArgumentException>(() => new GetLatestCapabilityProfilesRequest(
            [first, first],
            Timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListObservationTargetsRequest(
            TargetAuthorizationScope.ForAllTargets(),
            maxResults: 101,
            cursor: null,
            includeRetired: false,
            Timeout));
    }

    private static ObservationTarget CreateTarget(MonitoredInstanceId targetId, string key) =>
        new(
            targetId,
            new ObservationTargetKey(key),
            new ObservationTargetDisplayName(key),
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(new SqlServerHostName("sql.example.test"), tcpPort: 1433),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
            ObservationTargetLifecycle.Active,
            new ObservationTargetRevision(1),
            UtcNow,
            UtcNow,
            UtcNow);

    private static MonitoredInstanceId CreateTargetId(int suffix) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"));
}
