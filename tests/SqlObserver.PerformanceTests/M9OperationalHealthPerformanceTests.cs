using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.PerformanceTests;

public sealed class M9OperationalHealthPerformanceTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly AuthorizationContext Viewer = new(new ActorSecurityIdentifier("S-1-5-21-9400"), AuthorizationPrincipalState.Active, [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([Target]));

    [Fact]
    public async Task MaximumOperationalPagesSerializeBelowOneMiB()
    {
        var stopwatch = Stopwatch.StartNew();
        const string hostile = "<img src=x onerror=alert(1)>";
        object[] items = Enumerable.Range(0, 200).Select(i => (object)new { id = i, label = hostile }).ToArray();
        OperationalHealthEndpoints.OperationalHealthPageDto Page(string surface) => new(
            Target.Value, 1, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture), "Complete", items, false, null, false,
            surface == "tempdb-summary" ? 100L : null, surface == "tempdb-summary" ? 50L : null, null, null, null, null, null, surface.StartsWith("ag-", StringComparison.Ordinal) ? "PrimaryAllKnown" : null,
            new { observedAtUtc = "2026-08-25T12:00:00Z" });
        byte[][] payloads =
        [
            JsonSerializer.SerializeToUtf8Bytes(Page("backups"), WebJson),
            JsonSerializer.SerializeToUtf8Bytes(Page("agent"), WebJson),
            JsonSerializer.SerializeToUtf8Bytes(Page("tempdb-summary"), WebJson),
            JsonSerializer.SerializeToUtf8Bytes(Page("tempdb-files"), WebJson),
            JsonSerializer.SerializeToUtf8Bytes(Page("ag-replicas"), WebJson),
            JsonSerializer.SerializeToUtf8Bytes(Page("ag-databases"), WebJson),
        ];
        stopwatch.Stop();
        Assert.All(payloads, payload => Assert.InRange(payload.Length, 1, 1_048_576));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"M9 bounded page validation took {stopwatch.Elapsed}");
        Assert.All(payloads, payload => Assert.DoesNotContain((byte)'<', payload));
    }

    [Fact]
    public void ActualOperationalResponseCapReturns413ForOversizedMappedDto()
    {
        object[] oversized = Enumerable.Range(0, 200).Select(i => (object)new { fingerprint = new string('x', 8_000), id = i }).ToArray();
        var dto = new OperationalHealthEndpoints.OperationalHealthPageDto(Target.Value, 1, Guid.NewGuid(), DateTimeOffset.Parse("2026-08-25T12:00:00Z", CultureInfo.InvariantCulture), "Complete", oversized, false, null, false, null, null, null, null, null, null, null, null, new { observedAtUtc = "2026-08-25T12:00:00Z" });
        IResult result = OperationalHealthEndpoints.BoundedOk(dto);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task ServicePassesFiveSecondDeadlineAndHonorsCallerCancellation()
    {
        var repository = new MaximumPageRepository();
        var service = new OperationalHealthQueryService(repository);
        var request = new OperationalHealthRequest(Target, null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(30)));
        await service.GetBackupsAsync(Viewer, request, CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(5), repository.LastTimeout);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetBackupsAsync(Viewer, request, cancelled.Token).AsTask());
    }

    [Fact]
    public async Task DeadlineAndCallerCancellationAreMeasuredAfterProjectionStarts()
    {
        var repository = new DelayedRepository();
        var service = new OperationalHealthQueryService(repository);
        var request = new OperationalHealthRequest(Target, null, null, 1, null, new RepositoryCallTimeout(TimeSpan.FromSeconds(30)));
        Task<BackupStatusSnapshot?> deadline = service.GetBackupsAsync(Viewer, request, CancellationToken.None).AsTask();
        await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Stopwatch deadlineClock = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationalHealthRepositoryException>(() => deadline);
        deadlineClock.Stop();
        Assert.InRange(deadlineClock.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));

        repository.Reset();
        using var caller = new CancellationTokenSource();
        Task<BackupStatusSnapshot?> cancelled = service.GetBackupsAsync(Viewer, request, caller.Token).AsTask();
        await repository.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Stopwatch callerClock = Stopwatch.StartNew();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        callerClock.Stop();
        Assert.InRange(callerClock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private sealed class MaximumPageRepository : IOperationalHealthRepositoryPort
    {
        internal TimeSpan? LastTimeout { get; private set; }
        private static readonly ObservationTargetRevision Revision = new(1);
        private static readonly CollectorRunId Run = new(Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));
        private static readonly DateTimeOffset At = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        private void Observe(OperationalHealthRequest request, CancellationToken cancellationToken) { LastTimeout = request.Timeout.Value; cancellationToken.ThrowIfCancellationRequested(); }
        public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest r, CancellationToken c) { Observe(r, c); int count = Math.Min(r.Limit, 200); var items = Enumerable.Range(0, count).Select(i => new BackupStatusObservation(Target, Revision, i.ToString("x64", CultureInfo.InvariantCulture), BackupKind.Full, At, null, false, 100, false, true, false, BackupCoverage.Complete) { BackupSetId = i }).ToArray(); return ValueTask.FromResult<BackupStatusSnapshot?>(new(Target, Revision, Run, At, OperationalObservationState.Complete, items, count, false)); }
        public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest r, CancellationToken c) { Observe(r, c); var items = Enumerable.Range(0, 200).Select(i => new SqlAgentFailureObservation(Target, Revision, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), i, 1, 0, AgentFailureKind.Failed, null, null, 0, 1, new DateTime(2026, 8, 25), TimeSpan.FromMinutes(1), At.AddTicks(i), new string('a', 64))).ToArray(); return ValueTask.FromResult<SqlAgentFailureSnapshot?>(new(Target, Revision, Run, At, OperationalObservationState.Complete, items, 200, false, r.FromUtc ?? At.AddHours(-24), r.ToUtc ?? At)); }
        public ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest r, CancellationToken c) { Observe(r, c); return ValueTask.FromResult<TempDbSnapshot?>(new(Target, Revision, Run, At, OperationalObservationState.Complete, 100, 50, 100, 50, [], false)); }
        public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest r, CancellationToken c) { Observe(r, c); var items = Enumerable.Range(1, 128).Select(i => new TempDbFileObservation(Target, Revision, i, 100, 50, 50, TempDbComponentState.Healthy)).ToArray(); return ValueTask.FromResult<TempDbSnapshot?>(new(Target, Revision, Run, At, OperationalObservationState.Complete, null, null, null, null, items, false)); }
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest r, CancellationToken c) => GetAvailabilityGroupReplicasAsync(r, c);
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest r, CancellationToken c) { Observe(r, c); var items = Enumerable.Range(0, 200).Select(i => new AvailabilityReplicaObservation(Target, Revision, new string('b', 64), i.ToString("x64", CultureInfo.InvariantCulture), "PRIMARY", "ONLINE", "CONNECTED", AvailabilityVisibilityScope.PrimaryAllKnown, true)).ToArray(); return ValueTask.FromResult<AvailabilityGroupsSnapshot?>(new(Target, Revision, Run, At, OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, items, [], false)); }
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest r, CancellationToken c) { Observe(r, c); var items = Enumerable.Range(0, 200).Select(i => new AvailabilityDatabaseObservation(Target, Revision, new string('b', 64), i.ToString("x64", CultureInfo.InvariantCulture), "SYNCHRONIZED", "ONLINE", AvailabilityVisibilityScope.PrimaryAllKnown, true)).ToArray(); return ValueTask.FromResult<AvailabilityGroupsSnapshot?>(new(Target, Revision, Run, At, OperationalObservationState.Complete, AvailabilityVisibilityScope.PrimaryAllKnown, [], items, false)); }
    }

    private sealed class DelayedRepository : IOperationalHealthRepositoryPort
    {
        internal TaskCompletionSource<bool> Started { get; private set; } = NewSignal();
        internal void Reset() => Started = NewSignal();
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static async ValueTask<BackupStatusSnapshot?> Block(TaskCompletionSource<bool> started, CancellationToken cancellationToken)
        {
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
        public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(OperationalHealthRequest r, CancellationToken c) => Block(Started, c);
        public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<SqlAgentFailureSnapshot?>(null);
        public ValueTask<TempDbSnapshot?> GetTempDbAsync(OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<TempDbSnapshot?>(null);
        public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<TempDbSnapshot?>(null);
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null);
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null);
        public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(OperationalHealthRequest r, CancellationToken c) => ValueTask.FromResult<AvailabilityGroupsSnapshot?>(null);
    }
}
