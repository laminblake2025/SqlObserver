using SqlObserver.Domain.Coordination;

namespace SqlObserver.UnitTests;

public sealed class WorkerLeaseContractTests
{
    [Fact]
    public void LeaseIdentityRequiresBoundedKeyUniqueOwnerAndPositiveFence()
    {
        var key = new WorkerLeaseKey("collector/target-1/core-engine");
        var owner = new WorkerExecutionId(Guid.Parse("089970a8-99f4-4c6a-bf42-ced471521fe4"));
        var identity = new WorkerLeaseIdentity(key, owner, new FencingToken(42));

        Assert.Equal("collector/target-1/core-engine", identity.Key.Value);
        Assert.Equal(42, identity.FencingToken.Value);
        Assert.Throws<ArgumentException>(() => new WorkerLeaseKey(string.Empty));
        Assert.Throws<ArgumentException>(() => new WorkerLeaseKey("contains whitespace"));
        Assert.Throws<ArgumentException>(() => new WorkerLeaseKey(new string('a', WorkerLeaseKey.MaximumLength + 1)));
        Assert.Throws<ArgumentException>(() => new WorkerExecutionId(Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FencingToken(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FencingToken(-1));
    }

    [Fact]
    public void LeaseDurationAcceptsOnlyShortBoundedTtl()
    {
        Assert.Equal(WorkerLeaseDuration.Minimum, new WorkerLeaseDuration(WorkerLeaseDuration.Minimum).Value);
        Assert.Equal(WorkerLeaseDuration.Maximum, new WorkerLeaseDuration(WorkerLeaseDuration.Maximum).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerLeaseDuration(
            WorkerLeaseDuration.Minimum - TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerLeaseDuration(
            WorkerLeaseDuration.Maximum + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void LeaseUsesRepositoryUtcAndValidRenewalOrdering()
    {
        WorkerLeaseIdentity identity = CreateIdentity();
        DateTimeOffset acquired = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var lease = new WorkerLease(
            identity,
            acquired,
            acquired.AddSeconds(5),
            acquired.AddSeconds(35));

        Assert.Equal(TimeSpan.FromSeconds(30), lease.TimeToLive.Value);
        Assert.Throws<ArgumentException>(() => new WorkerLease(
            identity,
            acquired.ToOffset(TimeSpan.FromHours(1)),
            acquired,
            acquired.AddSeconds(30)));
        Assert.Throws<ArgumentException>(() => new WorkerLease(
            identity,
            acquired,
            acquired.AddSeconds(-1),
            acquired.AddSeconds(30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkerLease(
            identity,
            acquired,
            acquired,
            acquired.AddSeconds(1)));
    }

    [Fact]
    public void LeaseResultsNeverReturnAUsableLeaseAfterContentionOrOwnershipLoss()
    {
        DateTimeOffset repositoryTime = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var lease = new WorkerLease(
            CreateIdentity(),
            repositoryTime,
            repositoryTime,
            repositoryTime.AddSeconds(30));
        LeaseAcquisitionResult acquired = LeaseAcquisitionResult.Acquired(
            lease,
            repositoryTime.AddSeconds(1));
        LeaseAcquisitionResult contended = LeaseAcquisitionResult.Contended(repositoryTime);
        LeaseRenewalResult lost = LeaseRenewalResult.OwnershipLost(repositoryTime);

        Assert.Same(lease, acquired.Lease);
        Assert.Equal(LeaseAcquisitionStatus.Contended, contended.Status);
        Assert.Null(contended.Lease);
        Assert.Equal(LeaseRenewalStatus.OwnershipLost, lost.Status);
        Assert.Null(lost.Lease);
        Assert.Throws<ArgumentException>(() => LeaseAcquisitionResult.Contended(
            repositoryTime.ToOffset(TimeSpan.FromHours(-4))));
        Assert.Throws<ArgumentException>(() => LeaseAcquisitionResult.Acquired(
            lease,
            lease.ExpiresAtUtc));
    }

    private static WorkerLeaseIdentity CreateIdentity() =>
        new(
            new WorkerLeaseKey("partition/telemetry"),
            new WorkerExecutionId(Guid.Parse("089970a8-99f4-4c6a-bf42-ced471521fe4")),
            new FencingToken(7));
}
