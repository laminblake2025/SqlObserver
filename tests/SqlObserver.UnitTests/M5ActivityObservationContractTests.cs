using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M5ActivityObservationContractTests
{
    private static readonly MonitoredInstanceId TargetId =
        new(Guid.Parse("51515151-5151-5151-5151-515151515151"));
    private static readonly ObservationTargetRevision Revision = new(5);
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 23, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActivityPayloadExposesOnlyBoundedTypedEvidence()
    {
        var session = new ActivitySessionObservation(
            TargetId,
            Revision,
            sessionId: 51,
            ActivitySessionStatus.Sleeping,
            isUserProcess: true,
            databaseId: 7,
            openTransactionCount: 0,
            cpuMilliseconds: 10,
            memoryUsagePages: 12,
            reads: 13,
            writes: 14,
            logicalReads: 15,
            totalElapsedMilliseconds: 16,
            ObservedAt);
        var request = new ActivityRequestObservation(
            TargetId,
            Revision,
            sessionId: 51,
            requestId: 0,
            ActivityRequestStatus.Running,
            ActivityRequestCommand.Select,
            databaseId: 7,
            cpuMilliseconds: 10,
            totalElapsedMilliseconds: 11,
            reads: 12,
            writes: 13,
            logicalReads: 14,
            rowCount: 15,
            percentComplete: 0,
            ObservedAt);
        var wait = CreateWait(100, 1_000, 200, ObservedAt);
        var edge = new BlockingEdgeObservation(
            TargetId,
            Revision,
            blockedSessionId: 51,
            BlockingBlockerKind.Session,
            blockerSessionId: 52,
            new SqlServerWaitType("LCK_M_S"),
            waitingTaskCount: 1,
            waitDurationMilliseconds: 25,
            rootBlockerSessionId: 52,
            chainDepth: 1,
            BlockingChainState.Resolved,
            ObservedAt);
        var payload = new CollectorPayload(
            activitySessions: new ActivitySessionObservationBatch([session]),
            activityRequests: new ActivityRequestObservationBatch([request]),
            serverWaits: new ServerWaitObservationBatch([wait]),
            blockingEdges: new BlockingEdgeObservationBatch([edge]));

        Assert.Equal(4, payload.ItemCount);
        Assert.Equal(
            session.EstimatedSizeBytes +
            request.EstimatedSizeBytes +
            wait.EstimatedSizeBytes +
            edge.EstimatedSizeBytes,
            payload.EstimatedSizeBytes);
        AssertNoSensitiveProperties(typeof(ActivitySessionObservation));
        AssertNoSensitiveProperties(typeof(ActivityRequestObservation));
        AssertNoSensitiveProperties(typeof(ServerWaitObservation));
        AssertNoSensitiveProperties(typeof(BlockingEdgeObservation));
    }

    [Fact]
    public void WaitDeltaIsExactUntilAnyCumulativeCounterResets()
    {
        ServerWaitObservation previous = CreateWait(100, 1_000, 200, ObservedAt);
        ServerWaitObservation current = CreateWait(110, 1_250, 240, ObservedAt.AddSeconds(30));
        ServerWaitCounterDelta delta = ServerWaitCounterDelta.Calculate(previous, current);

        Assert.False(delta.ResetDetected);
        Assert.Equal(10, delta.WaitingTasks);
        Assert.Equal(250, delta.WaitTimeMilliseconds);
        Assert.Equal(40, delta.SignalWaitTimeMilliseconds);

        ServerWaitObservation reset = CreateWait(5, 40, 10, ObservedAt.AddMinutes(1));
        ServerWaitCounterDelta resetDelta = ServerWaitCounterDelta.Calculate(current, reset);
        Assert.True(resetDelta.ResetDetected);
        Assert.Null(resetDelta.WaitingTasks);
        Assert.Null(resetDelta.WaitTimeMilliseconds);
        Assert.Null(resetDelta.SignalWaitTimeMilliseconds);
    }

    [Fact]
    public void WaitTypeAndClosedEnumsRejectUnboundedProviderValues()
    {
        Assert.Equal("LCK_M_S", new SqlServerWaitType("lck_m_s").Value);
        Assert.Throws<ArgumentException>(() => new SqlServerWaitType("LCK M S"));
        Assert.Throws<ArgumentException>(() => new SqlServerWaitType("LCK_M_S\rsecret"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ActivitySessionObservation(
            TargetId,
            Revision,
            1,
            (ActivitySessionStatus)999,
            true,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            ObservedAt));
    }

    [Fact]
    public void OutputValidatorEnforcesActivityCardinalityAndTargetRevision()
    {
        CollectorExecutionRequest request = CreateRequest();
        ServerWaitObservation wait = CreateWait(1, 10, 2, ObservedAt);
        var payload = new CollectorPayload(serverWaits: new ServerWaitObservationBatch([wait]));
        var manifest = new CollectorManifest(
            new CollectorId("waits.server"),
            new CollectorDisplayName("Waits"),
            new CollectorManifestVersion(1),
            requiredCapabilities: [],
            requiredPermissions: [],
            new SqlServerMajorVersionRange(15, 17),
            [SqlServerPlatform.Windows],
            new CollectorIntervalPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10)),
            new CollectorExecutionLimits(TimeSpan.FromSeconds(5), 2_049, 524_288, CollectorEstimatedCost.Low),
            new CollectorFallbackPolicy(CollectorFallbackMode.Unsupported),
            new CollectorOutputSchemaVersion(1),
            CollectorOperationalMode.Passive,
            outputKind: CollectorOutputKind.ServerWaits);
        var result = new CollectorExecutionResult(
            TargetId,
            Revision,
            manifest.Id,
            1,
            1,
            CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed,
            payload,
            new CollectorRunAccounting(1, 1, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
        var validator = new CollectorOutputValidator(new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1),
            metrics: [],
            maxMetricSamples: 0,
            maxDatabaseObservations: 0,
            maxDatabaseFileObservations: 0,
            maxServerWaitObservations: 1));

        validator.Validate(manifest, request, result);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1),
            metrics: [],
            maxMetricSamples: 0,
            maxDatabaseObservations: 0,
            maxDatabaseFileObservations: 0,
            maxServerWaitObservations: ServerWaitObservationBatch.MaximumItems + 1));
    }

    private static ServerWaitObservation CreateWait(
        long tasks,
        long waitMilliseconds,
        long signalMilliseconds,
        DateTimeOffset observedAt)
    {
        return new ServerWaitObservation(
            TargetId,
            Revision,
            new SqlServerWaitType("LCK_M_S"),
            tasks,
            waitMilliseconds,
            maximumWaitTimeMilliseconds: Math.Min(waitMilliseconds, 50),
            signalMilliseconds,
            observedAt);
    }

    private static CollectorExecutionRequest CreateRequest()
    {
        var profile = new CapabilityProfile(
            TargetId,
            Revision,
            new CollectorId("capability.connection"),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new SqlServerIdentity(
                new SqlServerVersion(16, 0, 1000, 0),
                new SqlServerEditionName("Standard Edition"),
                SqlServerEngineEdition.Standard,
                SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported,
            CapabilityDiscoveryReason.Verified,
            SqlServerAuthenticationScheme.Kerberos,
            transportEncrypted: true,
            isSysAdmin: false,
            capabilities: [],
            permissions: [],
            TimeSpan.FromMilliseconds(1),
            evidenceBytes: 1,
            ObservedAt,
            ObservedAt.AddMinutes(5));
        return new CollectorExecutionRequest(
            new CollectorRunId(Guid.Parse("52525252-5252-5252-5252-525252525252")),
            TargetId,
            Revision,
            new SqlServerConnectionPolicy(
                new SqlServerEndpoint(
                    new SqlServerHostName("sql01.example.test"),
                    new SqlServerInstanceName("MSSQLSERVER")),
                new SqlServerConnectTimeout(TimeSpan.FromSeconds(5))),
            profile,
            new CollectorAttemptNumber(1),
            new CollectorExecutionTimeout(TimeSpan.FromSeconds(5)));
    }

    private static void AssertNoSensitiveProperties(Type type)
    {
        string[] forbidden =
        [
            "Query", "Text", "Handle", "Plan", "Login", "Host", "Application", "Interface",
            "Address", "Resource", "Physical", "Path",
        ];
        Assert.DoesNotContain(
            type.GetProperties(),
            property => forbidden.Any(token => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }
}
