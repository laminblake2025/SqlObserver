using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class PlanCacheContentOutputTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly ObservationTargetRevision Revision = new(2);
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly WorkerLeaseIdentity Lease = new(new("collector/run/queries.performance/content-test"), new(Guid.NewGuid()), new(1));
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task LinksDeduplicateByKindAndFingerprintAndSerializeDeterministicallyWithoutCiphertext()
    {
        var runtime = Runtime([Statement('a'), Statement('b')]);
        var rows = runtime.Items.Select(item => Content(item)).ToArray();
        var batch = new PlanCacheContentOutputBatch(runtime, 8, 2, 0, 96, rows);
        rows[0] = Content(Statement('f'));
        Assert.Equal(Digest('a'), batch.Items[0].Statement.Watermark.Key.CacheEntryFingerprint);
        Assert.Throws<InvalidOperationException>(() => PlanCacheContentPersistencePayload.Serialize(batch));
        var port = new Port();
        var linked = await new PlanCacheContentProtectedCommitter(port).WriteAsync(batch, Target, Lease, Timeout, default);
        Assert.Same(runtime, linked.Runtime); Assert.Equal(2, port.Writes.Count);
        Assert.All(port.Writes, request => { Assert.Equal(Target, request.TargetId); Assert.Equal(Lease, request.Lease); });
        Assert.All(linked.Items, item => { Assert.Null(item.Text.ProtectedContent); Assert.Null(item.Plan.ProtectedContent); });
        Assert.Equal(linked.Items[0].Text.Reference, linked.Items[1].Text.Reference);
        Assert.Equal(linked.Items[0].Plan.Reference, linked.Items[1].Plan.Reference);
        Assert.NotEqual(linked.Items[0].Text.Reference, linked.Items[0].Plan.Reference);
        Assert.Equal(batch.CiphertextBytes, linked.CiphertextBytes); Assert.Equal(batch.EstimatedSizeBytes, linked.EstimatedSizeBytes);
        byte[] serialized = PlanCacheContentPersistencePayload.Serialize(linked);
        var reverse = new PlanCacheContentOutputBatch(runtime, 8, 2, 0, 96, linked.Items.Reverse().ToArray());
        Assert.Equal(serialized, PlanCacheContentPersistencePayload.Serialize(reverse));
        var changed = linked.Items.ToArray();
        var first = changed[0];
        changed[0] = new(first.Statement, first.Text.WithReference(new(new(Guid.NewGuid()),
            first.Text.Reference!.Kind, first.Text.Reference.Fingerprint)), first.Plan);
        var changedBatch = new PlanCacheContentOutputBatch(runtime, 8, 2, 0, 96, changed);
        Assert.NotEqual(Convert.ToHexString(serialized), Convert.ToHexString(PlanCacheContentPersistencePayload.Serialize(changedBatch)));
        string json = Encoding.UTF8.GetString(serialized);
        Assert.DoesNotContain("ciphertext\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keyIdentifier", json, StringComparison.Ordinal);
        Assert.Contains("databaseIncarnation", json, StringComparison.Ordinal);
        Assert.Contains("payloadId", json, StringComparison.Ordinal);
        Assert.True(serialized.Length < PlanCacheContentOutputBatch.MaximumSerializedBytes);
        var again = await new PlanCacheContentProtectedCommitter(port).WriteAsync(linked, Target, Lease, Timeout, default);
        Assert.Equal(2, port.Writes.Count); Assert.Equal(serialized, PlanCacheContentPersistencePayload.Serialize(again));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("incarnation")]
    [InlineData("epoch")]
    [InlineData("counters")]
    [InlineData("capture")]
    public void ContentCannotBorrowAnotherScopeOrTimestampWithTheSameEntryHash(string fault)
    {
        var runtime = Runtime([Statement('a')]);
        var foreign = Statement('a', fault);
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 1, 0, 48, [Content(foreign)]));
    }

    [Fact]
    public void DuplicateEntriesUnboundedPayloadsAndInconsistentSourceAccountingAreRejected()
    {
        var runtime = Runtime([Statement('a'), Statement('b'), Statement('c')]);
        var item = Content(runtime.Items[0]);
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 2, 0, 96, [item, item]));
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 9, 1, 0, 48, [item]));
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 2, 0, 48, [item]));
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 1, 0, 0, [item]));
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 1, 0, 47, [item]));
        var tooLarge = runtime.Items.Select(row => Content(row, planBytes: 1024 * 1024)).ToArray();
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 3, 0, 2 * 1024 * 1024, tooLarge));
        Assert.Throws<ArgumentException>(() => PlanCacheContentValue.Protected(Payload(SensitivePayloadKind.QueryText, 16385)));
    }

    [Fact]
    public void UnavailableEvidenceCarriesNoReferenceAndEachContentKindRemainsSeparate()
    {
        Assert.Throws<ArgumentException>(() => PlanCacheContentValue.Missing(SensitivePayloadKind.QueryText, PlanCacheContentState.Available));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanCacheContentValue.Missing(SensitivePayloadKind.QueryText, (PlanCacheContentState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanCacheContentValue.Missing(SensitivePayloadKind.DeadlockXml, PlanCacheContentState.Unavailable));
        var runtime = Runtime([Statement('a')]);
        var item = new PlanCacheStatementContentObservation(runtime.Items[0],
            PlanCacheContentValue.Missing(SensitivePayloadKind.QueryText, PlanCacheContentState.Oversized),
            PlanCacheContentValue.Missing(SensitivePayloadKind.ExecutionPlan, PlanCacheContentState.ProtectionUnavailable));
        var batch = new PlanCacheContentOutputBatch(runtime, 8, 2, 1, 0, [item]);
        string json = Encoding.UTF8.GetString(PlanCacheContentPersistencePayload.Serialize(batch));
        Assert.Contains("oversized", json, StringComparison.Ordinal); Assert.Contains("protection_unavailable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("payloadId", json, StringComparison.Ordinal); Assert.Equal(0, batch.CiphertextBytes);
        Assert.Throws<ArgumentException>(() => new PlanCacheStatementContentObservation(runtime.Items[0], item.Plan, item.Text));
    }

    [Fact]
    public async Task NoKeyCaptureIsExplicitAndCannotCarryLinksOrLoseItsReasonDuringCiphertextStaging()
    {
        var runtime = Runtime([Statement('a')]);
        var batch = new PlanCacheContentOutputBatch(runtime, 0, 0, 0, 0, [], PlanCacheContentCaptureState.ProtectionUnavailable);
        var port = new Port();
        var linked = await new PlanCacheContentProtectedCommitter(port).WriteAsync(batch, Target, Lease, Timeout, default);
        Assert.Empty(port.Writes); Assert.Equal(batch.CaptureState, linked.CaptureState);
        Assert.Contains("\"captureState\":\"protection_unavailable\"", Encoding.UTF8.GetString(PlanCacheContentPersistencePayload.Serialize(linked)), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 8, 0, 0, 0, [], PlanCacheContentCaptureState.ProtectionUnavailable));
        Assert.Throws<ArgumentException>(() => new PlanCacheContentOutputBatch(runtime, 0, 0, 0, 0, [], PlanCacheContentCaptureState.Enabled));
    }

    [Fact]
    public async Task ALeasedWriterCannotReplaceTheFingerprintAndCancellationWinsOverAPortFailure()
    {
        var runtime = Runtime([Statement('a')]);
        var batch = new PlanCacheContentOutputBatch(runtime, 8, 1, 0, 48, [Content(runtime.Items[0])]);
        await Assert.ThrowsAsync<ArgumentException>(async () => await new PlanCacheContentProtectedCommitter(new Port { WrongFingerprint = true })
            .WriteAsync(batch, Target, Lease, Timeout, default));
        using var cancelled = new CancellationTokenSource();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await
            new PlanCacheContentProtectedCommitter(new Port { Cancel = cancelled }).WriteAsync(batch, Target, Lease, Timeout, cancelled.Token));
        Assert.Equal(cancelled.Token, failure.CancellationToken);
        Assert.NotNull(batch.Items[0].Text.ProtectedContent); Assert.Null(batch.Items[0].Text.Reference);
    }

    [Fact]
    public async Task AForeignScheduledTargetCannotWriteEvenAnUnlinkedPayload()
    {
        var runtime = Runtime([Statement('a')]);
        var batch = new PlanCacheContentOutputBatch(runtime, 8, 1, 0, 48, [Content(runtime.Items[0])]);
        var port = new Port();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new PlanCacheContentProtectedCommitter(port)
            .WriteAsync(batch, new MonitoredInstanceId(Guid.NewGuid()), Lease, Timeout, default));
        Assert.Empty(port.Writes);
    }

    private static PlanCacheStatementContentObservation Content(PlanCacheRuntimeStatementObservation statement, int planBytes = 16) =>
        new(statement, PlanCacheContentValue.Protected(Payload(SensitivePayloadKind.QueryText, 8)),
            PlanCacheContentValue.Protected(Payload(SensitivePayloadKind.ExecutionPlan, planBytes)));
    private static ProtectedSensitivePayload Payload(SensitivePayloadKind kind, int bytes) =>
        new(kind, new(new byte[32]), "AES-256-GCM", "fixture-only", new byte[12], new byte[16], new byte[bytes]);
    private static string Digest(char value) => new(value, 64);
    private static PlanCacheRuntimeOutputBatch Runtime(PlanCacheRuntimeStatementObservation[] rows) =>
        new(Target, Revision, Digest('c'), At, At.AddMilliseconds(20), 2000, rows.Length, 0, 0, 0, rows.Length, 0, rows);
    private static PlanCacheRuntimeStatementObservation Statement(char entry, string? fault = null)
    {
        var target = fault == "target" ? new MonitoredInstanceId(Guid.NewGuid()) : Target;
        var revision = fault == "revision" ? new(3) : Revision;
        string epoch = fault == "epoch" ? Digest('e') : Digest('c'), incarnation = fault == "incarnation" ? Digest('e') : Digest('d');
        var at = fault == "capture" ? At.AddMilliseconds(1) : At;
        var query = new QueryOpaqueIdentity(6, Digest('a'));
        var watermark = new PlanCacheRuntimeWatermark(new(target, revision, query, new(query, Digest('b')), incarnation, epoch, Digest(entry)),
            new(fault == "counters" ? 2 : 1, 2, 3, 4, 5, 6), at.AddMilliseconds(20));
        return new(watermark, new(target, revision, 6, epoch, incarnation, at.AddMilliseconds(-20), at.AddMilliseconds(-10)),
            new(target, revision, 6, epoch, incarnation, at.AddMilliseconds(30), at.AddMilliseconds(40)));
    }
    private sealed class Port : ISensitivePayloadPort
    {
        internal List<SensitivePayloadGetOrAddRequest> Writes { get; } = [];
        internal bool WrongFingerprint { get; init; }
        internal CancellationTokenSource? Cancel { get; init; }
        public ValueTask<SensitivePayloadReference> GetOrAddAsync(SensitivePayloadGetOrAddRequest request, CancellationToken token)
        {
            Writes.Add(request);
            if (Cancel is not null) { Cancel.Cancel(); throw new InvalidDataException("Cancelled provider failure"); }
            return ValueTask.FromResult(new SensitivePayloadReference(new(Guid.NewGuid()), request.Payload.Kind,
                WrongFingerprint ? new(new byte[32].Select(_ => (byte)1).ToArray()) : request.Payload.Fingerprint));
        }
    }
}
