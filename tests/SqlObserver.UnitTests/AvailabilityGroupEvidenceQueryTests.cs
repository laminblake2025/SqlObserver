using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class AvailabilityGroupEvidenceQueryTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly DateTimeOffset Cutoff = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly string Group = new('a', 64), Local = new('1', 64), Remote = new('2', 64);
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task MixedGrantsCannotBorrowReadAccessForEitherStream()
    {
        var repository = new Repository();
        var service = new AvailabilityGroupEvidenceQueryService(repository);
        var other = new MonitoredInstanceId(Guid.NewGuid());
        AuthorizationContext mixed = Auth(new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([other])),
            new(ApplicationRole.Auditor, TargetAuthorizationScope.ForTargets([Target])));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadReplicasAsync(mixed, Request(), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await service.ReadDatabasesAsync(mixed, Request(AvailabilityGroupEvidenceKind.Databases), CancellationToken.None));
        Assert.Equal(0, repository.Reads);
        await service.ReadReplicasAsync(Authorized(), Request(), CancellationToken.None);
        await service.ReadDatabasesAsync(Authorized(), Request(AvailabilityGroupEvidenceKind.Databases), CancellationToken.None);
        Assert.Equal(2, repository.Reads);
        Assert.Equal(TimeSpan.FromSeconds(5), repository.LastRequest!.Timeout.Value);
        Assert.Equal(Cutoff, repository.LastRequest.AtUtc);
    }

    [Fact]
    public void RequestBindsCutoffStreamAndEveryIdentityKey()
    {
        AvailabilityGroupEvidenceCursor cursor = Cursor(Local);
        Assert.Same(cursor, Request(cursor: cursor).Cursor);
        AvailabilityGroupEvidenceCursor[] invalid =
        [
            cursor with { TargetId = new MonitoredInstanceId(Guid.NewGuid()) },
            cursor with { AtUtc = Cutoff.AddSeconds(1) }, cursor with { TargetRevision = 0 },
            cursor with { RunId = Guid.Empty }, cursor with { Stream = AvailabilityGroupEvidenceKind.Databases },
            cursor with { GroupFingerprint = new string('A', 64) }, cursor with { ReplicaFingerprint = "bad" },
            cursor with { DatabaseFingerprint = Group }, cursor with { AtUtc = Cutoff.ToOffset(TimeSpan.FromHours(1)) },
        ];
        foreach (AvailabilityGroupEvidenceCursor value in invalid)
            Assert.Throws<ArgumentException>(() => Request(cursor: value));
        Assert.Throws<ArgumentException>(() => Request(AvailabilityGroupEvidenceKind.Databases,
            cursor with { Stream = AvailabilityGroupEvidenceKind.Databases }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AvailabilityGroupEvidenceRequest(Target, Cutoff,
            AvailabilityGroupEvidenceKind.Replicas, 101, Timeout));
        Assert.Throws<ArgumentException>(() => new AvailabilityGroupEvidenceRequest(Target, Cutoff.AddTicks(1),
            AvailabilityGroupEvidenceKind.Replicas, 1, Timeout));
    }

    [Fact]
    public async Task UnknownRemoteStateAndFaultsRemainReadableWithStableContinuation()
    {
        var repository = new Repository
        {
            Replicas = new(Header(), [Replica(Local), Replica(Remote)], Cursor(Remote)),
        };
        var service = new AvailabilityGroupEvidenceQueryService(repository);
        var request = new AvailabilityGroupEvidenceRequest(Target, Cutoff, AvailabilityGroupEvidenceKind.Replicas, 2, Timeout);
        var page = await service.ReadReplicasAsync(Authorized(), request, CancellationToken.None);
        Assert.Equal(OperationalObservationState.Complete, page.Header.State);
        Assert.Equal("DISCONNECTED", page.Items[1].ConnectedState);
        Assert.Null(page.Items[1].OperationalState);
        Assert.Equal(Cutoff, page.NextCursor!.AtUtc);
        Assert.Equal(Run, page.NextCursor.RunId);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task RepositoryDriftAndMalformedEvidenceFailClosed(int scenario)
    {
        AvailabilityGroupEvidenceHeader header = Header();
        IReadOnlyList<AvailabilityReplicaEvidence> items = [Replica(Local)];
        AvailabilityGroupEvidenceCursor? next = null;
        switch (scenario)
        {
            case 0: header = header with { TargetId = new MonitoredInstanceId(Guid.NewGuid()) }; break;
            case 1: header = header with { AtUtc = Cutoff.AddSeconds(1) }; break;
            case 2: header = header with { RetainedItems = 5 }; break;
            case 3: header = header with { EvidenceStatus = AvailabilityGroupEvidenceStatus.NoData }; break;
            case 4: items = [Replica(Local), Replica(Local)]; break;
            case 5: items = [Replica(Remote), Replica(Local)]; break;
            case 6: items = [Replica(Local) with { Role = "arbitrary" }]; break;
            case 7: next = Cursor(Remote); break;
        }
        var service = new AvailabilityGroupEvidenceQueryService(new Repository { Replicas = new(header, items, next) });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.ReadReplicasAsync(Authorized(), Request(), CancellationToken.None));
    }

    [Fact]
    public async Task StaleRunAndRowsBeforeTheContinuationFailClosed()
    {
        var repository = new Repository { Replicas = new(Header() with { RunId = Guid.NewGuid() }, [], null) };
        var service = new AvailabilityGroupEvidenceQueryService(repository);
        await Assert.ThrowsAsync<AvailabilityGroupEvidenceCursorStaleException>(async () =>
            await service.ReadReplicasAsync(Authorized(), Request(cursor: Cursor(Local)), CancellationToken.None));
        repository.Replicas = new(Header(), [Replica(Local)], null);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await service.ReadReplicasAsync(Authorized(), Request(cursor: Cursor(Local)), CancellationToken.None));
    }

    [Fact]
    public async Task DatabaseUnitsAndRawLocalTimeArePreservedAndInvalidValuesRejected()
    {
        var localTime = new DateTime(2026, 9, 29, 8, 15, 0, DateTimeKind.Unspecified);
        var item = new AvailabilityDatabaseReplicaEvidence(Identity(Remote), Group, true,
            "SYNCHRONIZED", "NOT_HEALTHY", "SUSPECT", false, null, 0, long.MaxValue, null,
            localTime);
        var repository = new Repository { Databases = new(Header(), [item], null) };
        var service = new AvailabilityGroupEvidenceQueryService(repository);
        var request = Request(AvailabilityGroupEvidenceKind.Databases);
        var page = await service.ReadDatabasesAsync(Authorized(), request, CancellationToken.None);
        Assert.Null(page.Items[0].LogSendQueueKb);
        Assert.Equal(0, page.Items[0].LogSendRateKbPerSecond);
        Assert.Equal(DateTimeKind.Unspecified, page.Items[0].SourceLastCommitLocal!.Value.Kind);
        foreach (var invalid in new[] { item with { LogSendQueueKb = -1 },
            item with { SourceLastCommitLocal = DateTime.SpecifyKind(localTime, DateTimeKind.Utc) },
            item with { StateObserved = false }, item with { DatabaseFingerprint = "bad" } })
        {
            repository.Databases = new(Header(), [invalid], null);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await service.ReadDatabasesAsync(Authorized(), request, CancellationToken.None));
        }
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndWrongStreamNeverReadsRepository()
    {
        var repository = new Repository();
        var service = new AvailabilityGroupEvidenceQueryService(repository);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.ReadReplicasAsync(Authorized(), Request(AvailabilityGroupEvidenceKind.Databases), CancellationToken.None));
        Assert.Equal(0, repository.Reads);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReadReplicasAsync(Authorized(), Request(), cancellation.Token));
    }

    [Fact]
    public async Task RepositoryDeadlineUsesTheSmallerCallerBudgetAndReportsTimeout()
    {
        var repository = new Repository { WaitForCancellation = true };
        var service = new AvailabilityGroupEvidenceQueryService(repository);
        var request = new AvailabilityGroupEvidenceRequest(Target, Cutoff, AvailabilityGroupEvidenceKind.Replicas,
            1, new RepositoryCallTimeout(TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await service.ReadReplicasAsync(Authorized(), request, CancellationToken.None));
        Assert.Equal(TimeSpan.FromMilliseconds(100), repository.LastRequest!.Timeout.Value);
    }

    private static AvailabilityGroupEvidenceRequest Request(AvailabilityGroupEvidenceKind stream = AvailabilityGroupEvidenceKind.Replicas,
        AvailabilityGroupEvidenceCursor? cursor = null) => new(Target, Cutoff, stream, 2, Timeout, cursor);
    private static AvailabilityGroupEvidenceCursor Cursor(string replica) =>
        new(Target, Cutoff, 1, Run, AvailabilityGroupEvidenceKind.Replicas, Group, replica, null);
    private static AvailabilityGroupEvidenceHeader Header() => new(Target, Cutoff, 1, Run,
        Cutoff.AddSeconds(-2), Cutoff.AddSeconds(-1), OperationalObservationState.Complete,
        AvailabilityGroupEvidenceStatus.Retained, 4, 4, 4, CollectorLossKind.None, false, true);
    private static AvailabilityReplicaEvidenceIdentity Identity(string replica) =>
        new(Group, replica, Local, replica == Local, AvailabilityVisibilityScope.PrimaryAllKnown);
    private static AvailabilityReplicaEvidence Replica(string replica) => new(Identity(replica), true,
        replica == Local ? "PRIMARY" : "SECONDARY", replica == Local ? "FAILED" : null, "DISCONNECTED");
    private static AuthorizationContext Authorized() => Auth(new RoleAuthorizationGrant(ApplicationRole.Viewer,
        TargetAuthorizationScope.ForTargets([Target])));
    private static AuthorizationContext Auth(params RoleAuthorizationGrant[] grants) => new(
        new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active, grants);

    private sealed class Repository : IAvailabilityGroupEvidenceRepositoryPort
    {
        public int Reads { get; private set; }
        public AvailabilityGroupEvidenceRequest? LastRequest { get; private set; }
        public bool WaitForCancellation { get; init; }
        public AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence> Replicas { get; set; } = new(Header(), [], null);
        public AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence> Databases { get; set; } = new(Header(), [], null);
        public async ValueTask<AvailabilityGroupEvidencePage<AvailabilityReplicaEvidence>> ReadReplicasAsync(
            AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; LastRequest = request;
            if (WaitForCancellation) await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return Replicas;
        }
        public ValueTask<AvailabilityGroupEvidencePage<AvailabilityDatabaseReplicaEvidence>> ReadDatabasesAsync(
            AvailabilityGroupEvidenceRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; LastRequest = request;
            return ValueTask.FromResult(Databases);
        }
    }
}
