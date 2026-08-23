using SqlObserver.Domain.Diagnostics;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class MigrationAndIngestionContractTests
{
    private static readonly MonitoredInstanceId InstanceId = new(Guid.Parse("ee8c5c73-d6f7-408f-ab25-252966dba5d5"));

    [Fact]
    public void MigrationDescriptorUsesFourDigitNumberAndSha256Checksum()
    {
        var descriptor = new MigrationDescriptor(
            new MigrationNumber(1),
            "create_repository_schemas",
            new MigrationChecksum(new byte[MigrationChecksum.RequiredLength]),
            isTransactional: true);

        Assert.Equal("0001", descriptor.Number.ToString());
        Assert.Equal(MigrationChecksum.RequiredLength * 2, descriptor.Checksum.ToHexString().Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MigrationNumber(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MigrationNumber(MigrationNumber.MaximumValue + 1));
        Assert.Throws<ArgumentException>(() => new MigrationChecksum(new byte[MigrationChecksum.RequiredLength - 1]));
        Assert.Throws<ArgumentException>(() => new MigrationDescriptor(
            new MigrationNumber(2),
            "Unsafe Migration Name",
            descriptor.Checksum,
            isTransactional: true));
    }

    [Fact]
    public void MigrationResultsEnforceUtcOrderingAndFailureCodes()
    {
        MigrationDescriptor descriptor = CreateDescriptor();
        DateTimeOffset started = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var failed = new MigrationExecutionResult(
            descriptor,
            MigrationOutcome.Failed,
            started,
            started.AddSeconds(1),
            "checksum_mismatch");

        Assert.Equal("checksum_mismatch", failed.FailureCode);
        Assert.Throws<ArgumentException>(() => new MigrationExecutionResult(
            descriptor,
            MigrationOutcome.Applied,
            started,
            started,
            "unexpected"));
        Assert.Throws<ArgumentException>(() => new MigrationExecutionResult(
            descriptor,
            MigrationOutcome.Failed,
            started,
            started));
        Assert.Throws<ArgumentException>(() => new MigrationExecutionResult(
            descriptor,
            MigrationOutcome.Applied,
            started,
            started.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new MigrationExecutionResult(
            descriptor,
            MigrationOutcome.Applied,
            started.ToOffset(TimeSpan.FromHours(1)),
            started));

        var applied = new MigrationExecutionResult(
            descriptor,
            MigrationOutcome.Applied,
            started,
            started.AddSeconds(1));
        var batch = new MigrationBatchResult([applied], started.AddSeconds(2));

        Assert.False(batch.HasFailures);
        Assert.Throws<ArgumentException>(() => new MigrationBatchResult([applied, applied], started.AddSeconds(2)));
        Assert.Throws<ArgumentException>(() => new MigrationBatchResult([applied], started));
    }

    [Fact]
    public void TelemetryBatchDefensivelyCopiesAndAccountsEveryItemByte()
    {
        MetricSample first = CreateSample("engine.cpu.percent", 1);
        MetricSample second = CreateSample("engine.memory.percent", 2);
        var source = new List<MetricSample> { first, second };
        var limits = new IngestionLimits(10, 10_000, 1_000);
        var batch = new TelemetryBatch(source, limits);

        source.Clear();

        Assert.Equal(2, batch.ItemCount);
        Assert.Equal(first.EstimatedSizeBytes + second.EstimatedSizeBytes, batch.TotalBytes);
        Assert.Throws<ArgumentException>(() => new TelemetryBatch(Array.Empty<MetricSample>(), limits));
    }

    [Fact]
    public void BatchRejectsCountItemAndTotalByteLimitViolations()
    {
        MetricSample sample = CreateSample("engine.cpu.percent", 1);

        Assert.Throws<ArgumentException>(() => new TelemetryBatch(
            [sample, sample],
            new IngestionLimits(maxItems: 1, maxBatchBytes: 10_000, maxItemBytes: 1_000)));
        Assert.Throws<ArgumentException>(() => new TelemetryBatch(
            [sample],
            new IngestionLimits(
                maxItems: 1,
                maxBatchBytes: sample.EstimatedSizeBytes - 1,
                maxItemBytes: sample.EstimatedSizeBytes - 1)));
        Assert.Throws<ArgumentException>(() => new TelemetryBatch(
            [sample, sample],
            new IngestionLimits(
                maxItems: 2,
                maxBatchBytes: (sample.EstimatedSizeBytes * 2) - 1,
                maxItemBytes: sample.EstimatedSizeBytes)));
    }

    [Fact]
    public void TelemetryBatchRejectsDuplicateCompositeIdentitiesWithinOneCall()
    {
        var sampleId = new MetricSampleId(Guid.Parse("7e4d25e0-97e2-4ee4-8937-a15153197ba9"));
        DateTimeOffset observedAt = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var first = new MetricSample(
            sampleId,
            InstanceId,
            new MetricId("engine.cpu.percent"),
            observedAt,
            1);
        var conflicting = new MetricSample(
            sampleId,
            InstanceId,
            new MetricId("engine.cpu.percent"),
            observedAt,
            99);
        var limits = new IngestionLimits(2, 10_000, 1_000);

        Assert.Throws<ArgumentException>(() => new TelemetryBatch([first, conflicting], limits));
        Assert.Throws<ArgumentException>(() => new MetricSample(
            sampleId,
            InstanceId,
            new MetricId("engine.cpu.percent"),
            observedAt.AddTicks(1),
            2));

        var retry = new TelemetryBatch([first], limits);
        var nextMicrosecond = new MetricSample(
            sampleId,
            InstanceId,
            new MetricId("engine.cpu.percent"),
            observedAt.AddTicks(TimeSpan.TicksPerMicrosecond),
            2);
        var distinctAtPostgreSqlPrecision = new TelemetryBatch([first, nextMicrosecond], limits);

        Assert.Single(retry.Samples);
        Assert.Equal(2, distinctAtPostgreSqlPrecision.ItemCount);
    }

    [Fact]
    public void DiagnosticEventBatchUsesTheSameBoundedAccountingContract()
    {
        DateTimeOffset now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var diagnosticEvent = new DiagnosticEventEnvelope(
            new DiagnosticEventId(Guid.Parse("cafaf84a-fba7-46ed-bef6-095b6c41bf3a")),
            InstanceId,
            new DiagnosticEventKind("sql_agent.job_failed"),
            now,
            now);
        var batch = new DiagnosticEventBatch(
            [diagnosticEvent],
            new IngestionLimits(5, 5_000, 1_000));

        Assert.Equal(1, batch.ItemCount);
        Assert.Equal(diagnosticEvent.EstimatedSizeBytes, batch.TotalBytes);

        var conflicting = new DiagnosticEventEnvelope(
            diagnosticEvent.EventId,
            InstanceId,
            new DiagnosticEventKind("sql_agent.job_failed"),
            now,
            now.AddSeconds(1));

        Assert.Throws<ArgumentException>(() => new DiagnosticEventBatch(
            [diagnosticEvent, conflicting],
            new IngestionLimits(2, 5_000, 1_000)));
        Assert.Throws<ArgumentException>(() => new DiagnosticEventEnvelope(
            diagnosticEvent.EventId,
            InstanceId,
            diagnosticEvent.Kind,
            now.AddTicks(1),
            now));

        var nextMicrosecond = new DiagnosticEventEnvelope(
            diagnosticEvent.EventId,
            InstanceId,
            diagnosticEvent.Kind,
            now.AddTicks(TimeSpan.TicksPerMicrosecond),
            now.AddTicks(TimeSpan.TicksPerMicrosecond));
        var distinctAtPostgreSqlPrecision = new DiagnosticEventBatch(
            [diagnosticEvent, nextMicrosecond],
            new IngestionLimits(2, 5_000, 1_000));

        Assert.Equal(2, distinctAtPostgreSqlPrecision.ItemCount);
    }

    [Fact]
    public void IngestionResultRequiresCompleteCountAndByteAccounting()
    {
        MetricSample first = CreateSample("engine.cpu.percent", 1);
        MetricSample second = CreateSample("engine.cpu.percent", 2);
        MetricSample third = CreateSample("engine.cpu.percent", 3);
        var batch = new TelemetryBatch(
            [first, second, third],
            new IngestionLimits(3, 10_000, 1_000));
        IngestionResult result = IngestionResult.FromTelemetryBatch(
            batch,
            insertedCount: 1,
            duplicateCount: 1,
            rejectedCount: 1,
            persistedBytes: first.EstimatedSizeBytes,
            completedAtUtc: DateTimeOffset.UtcNow);

        Assert.Equal(3, result.AttemptedCount);
        Assert.Equal(batch.TotalBytes, result.AttemptedBytes);
        Assert.Throws<ArgumentException>(() => IngestionResult.FromTelemetryBatch(
            batch,
            insertedCount: 1,
            duplicateCount: 0,
            rejectedCount: 0,
            persistedBytes: 0,
            completedAtUtc: DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(() => IngestionResult.FromTelemetryBatch(
            batch,
            insertedCount: 3,
            duplicateCount: 0,
            rejectedCount: 0,
            persistedBytes: batch.TotalBytes + 1,
            completedAtUtc: DateTimeOffset.UtcNow));
    }

    private static MigrationDescriptor CreateDescriptor() =>
        new(
            new MigrationNumber(1),
            "create_repository_schemas",
            new MigrationChecksum(new byte[MigrationChecksum.RequiredLength]),
            isTransactional: true);

    private static MetricSample CreateSample(string metricId, double value) =>
        new(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId(metricId),
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            value,
            [new MetricDimension("database", "warehouse")]);
}
