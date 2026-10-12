using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class DeadlockReadContractTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly XeReadIdentity Source = new(new(Guid.NewGuid()), new(1), new string('a', 64));
    private static readonly CollectorRunId Run = new(Guid.NewGuid());

    [Fact]
    public void ProofSnapshotsItsPositionsAndUsesRepositoryPrecision()
    {
        var positions = new[] { new XeFilePosition(1000, 0, 100) };
        DeadlockReadSourceProof proof = Proof(positions, at: At.AddTicks(19), retained: At.AddMinutes(-10).AddTicks(19));
        positions[0] = new(2000, 0, 200);
        Assert.Equal(1000, Assert.Single(proof.Positions).FileGeneration);
        Assert.Equal(At.AddTicks(10), proof.ReadAtUtc);
        Assert.Equal(At.AddMinutes(-10).AddTicks(10), proof.RetainedFromUtc);
        Assert.Throws<NotSupportedException>(() => ((IList<XeFilePosition>)proof.Positions).Clear());
    }

    [Fact]
    public void ProofRejectsCrossTargetSourceUnboundedPositionsAndContradictoryUnavailableEvidence()
    {
        Assert.Throws<ArgumentException>(() => Proof([], source: new(new(Guid.NewGuid()), Source.TargetRevision, Source.SourceEpoch)));
        Assert.Throws<ArgumentException>(() => Proof([], source: new(Source.TargetId, new(2), Source.SourceEpoch)));
        Assert.Throws<ArgumentException>(() => Proof([new(1000, 0, 101L * 1_048_576 + 1)]));
        Assert.Throws<ArgumentException>(() => Proof(Enumerable.Repeat(new XeFilePosition(1, 0, 0), 8194).ToArray()));
        Assert.Throws<ArgumentException>(() => Proof(Enumerable.Repeat(new XeFilePosition(1, 0, 0), 8193).ToArray()));
        Assert.Throws<ArgumentException>(() => Proof([], at: At.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => Proof([], retained: At.AddSeconds(1)));
        foreach (DeadlockReadSourceState state in new[] { DeadlockReadSourceState.Missing, DeadlockReadSourceState.ConfigurationInvalid,
            DeadlockReadSourceState.ResetRequired, DeadlockReadSourceState.Failed })
        {
            var unavailable = new DeadlockReadSourceProof(Source.TargetId, Source.TargetRevision, 1, Run, null, state,
                null, At, null, [], false, true);
            Assert.Empty(unavailable.Positions);
            Assert.Throws<ArgumentException>(() => new DeadlockReadSourceProof(Source.TargetId, Source.TargetRevision, 1, Run,
                null, state, Source, At, null, [], false, true));
            Assert.Throws<ArgumentException>(() => new DeadlockReadSourceProof(Source.TargetId, Source.TargetRevision, 1, Run,
                null, state, null, At, null, [], false, false));
        }
    }

    [Fact]
    public void RestoredWindowCannotInventKnownCoverageOrDuplicateEvidence()
    {
        var item = new DeadlockWindowEvent(Guid.NewGuid(), At.AddMinutes(-1));
        var events = new[] { item };
        DeadlockReadWindow current = DeadlockReadWindow.Restore(Source, At, At.AddMinutes(-10), null, events, DeadlockReadWindowReason.Current);
        events[0] = new(Guid.NewGuid(), At);
        Assert.Equal(item, Assert.Single(current.Events));
        Assert.Throws<ArgumentException>(() => Restore([item, item]));
        Assert.Throws<ArgumentException>(() => Restore([new(Guid.NewGuid(), At.AddMinutes(-5).AddTicks(-1))]));
        Assert.Throws<ArgumentException>(() => Restore([new(Guid.NewGuid(), At.AddTicks(1))]));
        Assert.Throws<ArgumentException>(() => Restore([new(item.EventId, item.OccurredAtUtc, true)]));
        Assert.Throws<ArgumentException>(() => DeadlockReadWindow.Restore(Source, At, At, null, [], DeadlockReadWindowReason.Current));
        Assert.Throws<ArgumentException>(() => DeadlockReadWindow.Restore(Source, At, At.AddMinutes(-10), null, [], DeadlockReadWindowReason.CoverageGap));
        Assert.Throws<ArgumentException>(() => DeadlockReadWindow.Restore(Source, At, At.AddMinutes(-10), null, [], DeadlockReadWindowReason.SourceIncomplete));
        Assert.Throws<ArgumentException>(() => DeadlockReadWindow.Restore(Source, At, At.AddMinutes(-10), At, [], DeadlockReadWindowReason.WindowOverflow));
        static DeadlockReadWindow Restore(DeadlockWindowEvent[] value) => DeadlockReadWindow.Restore(Source, At, At.AddMinutes(-10), null, value, DeadlockReadWindowReason.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BAD")]
    public void DigestRejectsNonCanonicalValues(string value) => Assert.Throws<ArgumentException>(() => new DeadlockReadDigest(value));

    private static DeadlockReadSourceProof Proof(XeFilePosition[] positions, XeReadIdentity? source = null, DateTimeOffset? at = null, DateTimeOffset? retained = null)
        => new(Source.TargetId, Source.TargetRevision, 1, Run, null, DeadlockReadSourceState.Ready, source ?? Source,
            at ?? At, retained ?? At.AddMinutes(-10), positions, true, false);
}
