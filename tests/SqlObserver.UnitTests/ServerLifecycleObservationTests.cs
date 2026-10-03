using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class ServerLifecycleObservationTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly DateTimeOffset Time = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LocalStart = new(2026, 10, 3, 7, 0, 0, DateTimeKind.Unspecified);
    private static readonly string Node = new('a', 64), Logical = new('b', 64), Group = new('c', 64), First = new('d', 64), Second = new('e', 64);
    private static ServerLifecycleEngineEvidence Engine(int pid = 10, long ticks = 100, string? node = null,
        string? logical = null, DateTime? start = null, long current = 100_000) =>
        new(node ?? Node, logical ?? Logical, pid, start ?? LocalStart, ticks, current);
    private static ServerLifecycleSnapshot Snapshot(int minute, ServerLifecycleEngineEvidence? engine = null,
        IReadOnlyList<ServerLifecycleAgMemberEvidence>? members = null, long revision = 1, bool? hadr = false,
        bool truncated = false, int schema = 1, MonitoredInstanceId? target = null) =>
        new(target ?? Target, new(revision), new(Guid.NewGuid()), Time.AddMinutes(minute), engine ?? Engine(), hadr,
            members ?? [], (members?.Count ?? 0) + 1 + (truncated ? 1 : 0), truncated, schema);
    private static ServerLifecycleReceipt Receipt(ServerLifecycleSnapshot snapshot, ServerLifecycleSnapshot? previous = null,
        bool success = true, bool proof = false) => new(snapshot, snapshot.SourceObservedAtUtc.AddSeconds(1), previous?.RunId, success, proof);
    private static ServerLifecycleAgMemberEvidence[] Members(bool switched = false, string? observer = null,
        string? remoteRole = "SECONDARY", int expected = 2) =>
        [new(Group, First, observer ?? (switched ? Second : First), switched ? remoteRole : "PRIMARY", "PRIMARY", true, expected),
         new(Group, Second, observer ?? (switched ? Second : First), switched ? "PRIMARY" : remoteRole, "PRIMARY", true, expected)];

    [Fact]
    public void StandaloneCoverageDoesNotInventAgHealthOrPhysicalOccurrenceTime()
    {
        var snapshot = Snapshot(0);
        Assert.Equal(ServerLifecycleCoverage.Complete, snapshot.EngineCoverage);
        Assert.Equal(ServerLifecycleCoverage.Unsupported, snapshot.AvailabilityCoverage);
        Assert.False(snapshot.Engine.HardwareIdentityVerified);
        Assert.Null(snapshot.Engine.OccurredAtUtc);
        Assert.Equal(DateTimeKind.Unspecified, snapshot.Engine.SourceStartLocal!.Value.Kind);
    }

    [Fact]
    public void MissingPhysicalIdentityKeepsSourceValuesWithoutUsableStartupFingerprint()
    {
        var engine = new ServerLifecycleEngineEvidence(null, Logical, 10, LocalStart, 100, 1000);
        Assert.Equal(ServerLifecycleCoverage.Unknown, engine.Coverage);
        Assert.Null(engine.StartupIncarnationFingerprint);
        Assert.Equal(LocalStart, engine.SourceStartLocal);
        var before = Snapshot(0);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(Snapshot(1, engine), before)));
    }

    [Fact]
    public void TypedIdentityDomainsAndKeyChangesCannotAlias()
    {
        var firstKey = new IdentityFingerprintKey(new byte[32]);
        var secondKey = new IdentityFingerprintKey(Enumerable.Repeat((byte)1, 32).ToArray());
        string node = ServerLifecycleIdentityFingerprint.PhysicalNode("node", firstKey);
        Assert.Equal(node, ServerLifecycleIdentityFingerprint.PhysicalNode("NODE", firstKey));
        Assert.NotEqual(node, ServerLifecycleIdentityFingerprint.LogicalInstance("node", firstKey));
        Assert.NotEqual(node, ServerLifecycleIdentityFingerprint.PhysicalNode("node", secondKey));
        Assert.NotEqual(ServerLifecycleIdentityFingerprint.AvailabilityGroup(Target.Value, firstKey),
            ServerLifecycleIdentityFingerprint.AvailabilityReplica(Target.Value, firstKey));
        Assert.NotEqual(Engine(node: node).StartupIncarnationFingerprint,
            Engine(node: ServerLifecycleIdentityFingerprint.PhysicalNode("node", secondKey)).StartupIncarnationFingerprint);
    }

    [Fact]
    public void PidReuseWithChangedStartupTicksAndLocalStartDetectsRestart()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(ticks: 200, start: LocalStart.AddMinutes(1)));
        var detection = Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
        Assert.Equal(ServerLifecycleEventKind.RestartDetected, detection.Kind);
        Assert.Equal(before.SourceObservedAtUtc, detection.BeforeObservedAtUtc);
        Assert.Equal(after.SourceObservedAtUtc, detection.AfterObservedAtUtc);
        Assert.Equal(after.SourceObservedAtUtc.AddSeconds(1), detection.DetectedAtUtc);
        Assert.Null(detection.OccurredAtUtc);
        Assert.Equal(before.RunId, detection.BeforeRunId);
        Assert.Equal(after.RunId, detection.AfterRunId);
    }

    [Fact]
    public void StableStartupDespiteChangingUptimeDoesNotDetectRestart()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(current: 160_000));
        Assert.Equal(before.Engine.StartupIncarnationFingerprint, after.Engine.StartupIncarnationFingerprint);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
    }

    [Fact]
    public void LocalClockChangeAloneIsNotRestartEvidence()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(start: LocalStart.AddHours(1)));
        Assert.NotEqual(before.Engine.StartupIncarnationFingerprint, after.Engine.StartupIncarnationFingerprint);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
    }

    [Fact]
    public void RepeatedHostBootTicksNeedIndependentChangedProcessAndLocalStart()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(pid: 11, start: LocalStart.AddMinutes(1), current: 500));
        Assert.Equal(ServerLifecycleEventKind.RestartDetected,
            Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before))).Kind);
        var ambiguous = Snapshot(1, Engine(start: LocalStart.AddMinutes(1), current: 500));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(ambiguous, before)));
    }

    [Fact]
    public void ChangedRespondingNodeIsNotSameNodeRestart()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(pid: 11, node: new('f', 64), start: LocalStart.AddMinutes(1)));
        var detection = Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
        Assert.Equal(ServerLifecycleEventKind.RespondingNodeChanged, detection.Kind);
        Assert.Equal(Logical, detection.AffectedIdentityFingerprint);
    }

    [Fact]
    public void DifferentLogicalServiceCreatesBoundaryRatherThanRestart()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(pid: 11, logical: new('f', 64), start: LocalStart.AddMinutes(1)));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
    }

    [Fact]
    public void InitialAndNonAdjacentReceiptsCannotCreateEvents()
    {
        var before = Snapshot(0);
        var after = Snapshot(2, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)));
        Assert.Empty(ServerLifecycleClassifier.Compare(null, Receipt(after)));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, Snapshot(1))));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after)));
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public void FailedOrLossyReceiptCannotBeSkippedToDeriveEvent(bool beforeSuccess, bool afterSuccess)
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before, success: beforeSuccess), Receipt(after, before, success: afterSuccess)));
    }

    [Theory]
    [InlineData(2, 1)] [InlineData(1, 2)] [InlineData(2, 2)]
    public void UnrecognizedSchemasNeverPromoteTransitions(int beforeSchema, int afterSchema)
    {
        var before = Snapshot(0, schema: beforeSchema);
        var after = Snapshot(1, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)), schema: afterSchema);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
    }

    [Fact]
    public void TargetRevisionAndTargetIdentityFenceComparison()
    {
        var before = Snapshot(0);
        var engine = Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(Snapshot(1, engine, revision: 2), before)));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(Snapshot(1, engine, target: new(Guid.NewGuid())), before)));
    }

    [Fact]
    public void BackwardsOrFutureSourceClockIsNotAValidDetectionBracket()
    {
        var before = Snapshot(1);
        var after = Snapshot(0, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), new(after, Time.AddMinutes(2), before.RunId, true)));
        var later = Snapshot(2, after.Engine);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), new(later, Time.AddMinutes(2).AddSeconds(-1), before.RunId, true)));
    }

    [Fact]
    public void ImpossibleUptimeIsRetainedPartialRatherThanHealthy()
    {
        var engine = Engine(ticks: 2000, current: 1000);
        Assert.Equal(ServerLifecycleCoverage.Partial, engine.Coverage);
        Assert.Equal(2000, engine.StartMsTicks);
        var before = Snapshot(0);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(Snapshot(1, engine), before)));
    }

    [Fact]
    public void PartialAgDoesNotInvalidateIndependentEngineRestartEvidence()
    {
        var members = Members(remoteRole: null);
        var before = Snapshot(0, members: members, hadr: true);
        var after = Snapshot(1, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)), members, hadr: true);
        Assert.Equal(ServerLifecycleCoverage.Partial, before.AvailabilityCoverage);
        Assert.Equal(ServerLifecycleEventKind.RestartDetected,
            Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before))).Kind);
    }

    [Fact]
    public void PrimaryChangeNeedsTrustedConsistencyProofEvenForCompleteMembership()
    {
        var before = Snapshot(0, members: Members(), hadr: true);
        var after = Snapshot(1, members: Members(switched: true), hadr: true);
        Assert.Equal(ServerLifecycleCoverage.Complete, before.AvailabilityCoverage);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before, proof: true), Receipt(after, before)));
        var detection = Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before, proof: true), Receipt(after, before, proof: true)));
        Assert.Equal(ServerLifecycleEventKind.FailoverDetected, detection.Kind);
        Assert.Equal(Group, detection.AffectedIdentityFingerprint);
        Assert.Null(detection.OccurredAtUtc);
    }

    [Fact]
    public void PrimaryTransitionPermitsChangedObserverAndPhysicalNode()
    {
        var before = Snapshot(0, members: Members(), hadr: true);
        var after = Snapshot(1, Engine(node: new('f', 64)), Members(switched: true), hadr: true);
        var detections = ServerLifecycleClassifier.Compare(Receipt(before, proof: true), Receipt(after, before, proof: true));
        Assert.Contains(detections, static item => item.Kind == ServerLifecycleEventKind.RespondingNodeChanged);
        Assert.Contains(detections, static item => item.Kind == ServerLifecycleEventKind.FailoverDetected);
        Assert.DoesNotContain(detections, static item => item.Kind == ServerLifecycleEventKind.RestartDetected);
    }

    [Fact]
    public void MissingObserverLimitedVisibilityOrExcessExpectedMembershipIsPartial()
    {
        Assert.Equal(ServerLifecycleCoverage.Partial, Snapshot(0, members:
            [new(Group, First, null, "PRIMARY", null, true, 1)], hadr: true).AvailabilityCoverage);
        Assert.Equal(ServerLifecycleCoverage.Partial, Snapshot(0, members:
            [new(Group, First, First, "SECONDARY", "SECONDARY", true, 1)], hadr: true).AvailabilityCoverage);
        Assert.Equal(ServerLifecycleCoverage.Partial, Snapshot(0, members: Members(expected: int.MaxValue), hadr: true).AvailabilityCoverage);
        Assert.Equal(ServerLifecycleCoverage.Unknown, Snapshot(0, hadr: true).AvailabilityCoverage);
    }

    [Fact]
    public void MultiplePrimariesOrChangedMembershipCannotBeFailoverProof()
    {
        var before = Snapshot(0, members: Members(), hadr: true);
        var ambiguous = Snapshot(1, members: Members(switched: true, remoteRole: "PRIMARY"), hadr: true);
        Assert.Equal(ServerLifecycleCoverage.Partial, ambiguous.AvailabilityCoverage);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before, proof: true), Receipt(ambiguous, before, proof: true)));
        var changed = Snapshot(1, members: [new(Group, Second, Second, "PRIMARY", "PRIMARY", true, 1)], hadr: true);
        Assert.Empty(ServerLifecycleClassifier.Compare(Receipt(before, proof: true), Receipt(changed, before, proof: true)));
    }

    [Fact]
    public void SnapshotCopiesMembershipAndRejectsDuplicateOrUnaccountedRows()
    {
        var source = Members().ToList();
        var snapshot = Snapshot(0, members: source, hadr: true);
        source.Clear();
        Assert.Equal(2, snapshot.Members.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<ServerLifecycleAgMemberEvidence>)snapshot.Members).Clear());
        var row = Members()[0];
        Assert.Throws<ArgumentException>(() => Snapshot(0, members: [row, row], hadr: true));
        Assert.Throws<ArgumentException>(() => new ServerLifecycleSnapshot(Target, new(1), new(Guid.NewGuid()), Time,
            Engine(), true, Members(), 1, false));
        Assert.Throws<ArgumentException>(() => Snapshot(0, members: Members(), hadr: false));
    }

    [Fact]
    public void SourceUtcAndOffsetFreeLocalTimeHaveDistinctValidation()
    {
        Assert.Throws<ArgumentException>(() => new ServerLifecycleEngineEvidence(Node, Logical, 10,
            DateTime.SpecifyKind(LocalStart, DateTimeKind.Utc), 100, 1000));
        Assert.Throws<ArgumentException>(() => new ServerLifecycleSnapshot(Target, new(1), new(Guid.NewGuid()),
            Time.AddTicks(1), Engine(), false, [], 1, false));
        Assert.Throws<ArgumentException>(() => new ServerLifecycleSnapshot(Target, new(1), new(Guid.NewGuid()),
            Time.ToOffset(TimeSpan.FromHours(1)), Engine(), false, [], 1, false));
        Assert.Equal(LocalStart.AddTicks(33_333), Engine(start: LocalStart.AddTicks(33_333)).SourceStartLocal);
    }

    [Fact]
    public void EventIdentityIsStableForExactReceiptPairAndChangesForDifferentAfterRun()
    {
        var before = Snapshot(0);
        var after = Snapshot(1, Engine(pid: 11, ticks: 200, start: LocalStart.AddMinutes(1)));
        var first = Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
        var replay = Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(after, before)));
        Assert.Equal(first, replay);
        var alternative = Snapshot(1, after.Engine);
        Assert.NotEqual(first.EventId,
            Assert.Single(ServerLifecycleClassifier.Compare(Receipt(before), Receipt(alternative, before))).EventId);
        Assert.NotEqual(Guid.Empty, first.EventId);
        Assert.Equal('8', first.EventId.ToString("D")[14]);
    }
}
