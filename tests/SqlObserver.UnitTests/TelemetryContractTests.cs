using SqlObserver.Domain.Diagnostics;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using System.Text.Json;

namespace SqlObserver.UnitTests;

public sealed class TelemetryContractTests
{
    private static readonly MonitoredInstanceId InstanceId = new(Guid.Parse("42670880-9513-4c86-b368-b80dfaf2724b"));
    private static readonly DateTimeOffset AlignedTimestamp = new(
        2026,
        8,
        23,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public void MetricSampleCopiesBoundedDimensionsAndAccountsForTheirBytes()
    {
        var dimensions = new List<MetricDimension>
        {
            new("database", "warehouse"),
            new("file_type", "data"),
        };
        var sample = new MetricSample(
            new MetricSampleId(Guid.Parse("72397c80-b2c4-47b1-87c2-28c53d1e8a25")),
            InstanceId,
            new MetricId("sqlserver.storage.read_latency_ms"),
            new DateTimeOffset(2024, 2, 29, 23, 59, 59, TimeSpan.Zero),
            3.5,
            dimensions);

        dimensions.Clear();

        Assert.Equal(2, sample.Dimensions.Count);
        Assert.True(sample.EstimatedSizeBytes > MetricSample.ConservativeFixedRecordBytes);
        Assert.Equal(TimeSpan.Zero, sample.ObservedAtUtc.Offset);
    }

    [Fact]
    public void MetricSampleRejectsNonUtcAndNonFiniteValues()
    {
        DateTimeOffset nonUtc = new(2026, 8, 23, 12, 0, 0, TimeSpan.FromHours(1));

        Assert.Throws<ArgumentException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.cpu.percent"),
            nonUtc,
            1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.cpu.percent"),
            AlignedTimestamp,
            double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.cpu.percent"),
            AlignedTimestamp,
            double.PositiveInfinity));
    }

    [Fact]
    public void MetricIdentifiersEnforceCatalogTokenBoundaries()
    {
        string maximum = $"m{new string('a', MetricId.MaximumLength - 1)}";

        Assert.Equal(maximum, new MetricId(maximum).Value);
        Assert.Throws<ArgumentException>(() => new MetricId(string.Empty));
        Assert.Throws<ArgumentException>(() => new MetricId(new string('a', MetricId.MaximumLength + 1)));
        Assert.Throws<ArgumentException>(() => new MetricId("1starts.with.number"));
        Assert.Throws<ArgumentException>(() => new MetricId("metric with spaces"));
        Assert.Throws<ArgumentException>(() => new MetricSampleId(Guid.Empty));
    }

    [Fact]
    public void MetricDimensionsEnforceCountUniquenessAndValueBytes()
    {
        MetricDimension[] maximum = Enumerable.Range(0, MetricSample.MaximumDimensionCount)
            .Select(index => new MetricDimension($"key{index}", "value"))
            .ToArray();

        var accepted = new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.wait.count"),
            AlignedTimestamp,
            4,
            maximum);

        Assert.Equal(MetricSample.MaximumDimensionCount, accepted.Dimensions.Count);
        Assert.Throws<ArgumentException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.wait.count"),
            AlignedTimestamp,
            4,
            maximum.Append(new MetricDimension("overflow", "value")).ToArray()));
        Assert.Throws<ArgumentException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.wait.count"),
            AlignedTimestamp,
            4,
            [new MetricDimension("duplicate", "one"), new MetricDimension("duplicate", "two")]));
        Assert.Throws<ArgumentException>(() => new MetricDimension(
            "database",
            new string('\u20ac', (MetricDimension.MaximumValueUtf8Bytes / 3) + 1)));
    }

    [Fact]
    public void DiagnosticEventEnvelopeCarriesOnlySafeMetadataAndProtectedReference()
    {
        var fingerprint = new SensitivePayloadFingerprint(new byte[SensitivePayloadFingerprint.RequiredLength]);
        var reference = new SensitivePayloadReference(
            new SensitivePayloadId(Guid.Parse("1188de4c-b666-46d1-bdf3-8c0e492977c2")),
            SensitivePayloadKind.DeadlockXml,
            fingerprint);
        DateTimeOffset occurred = new(2024, 12, 31, 23, 59, 59, TimeSpan.Zero);
        var envelope = new DiagnosticEventEnvelope(
            new DiagnosticEventId(Guid.Parse("d26bf2c9-721d-4cee-98dd-2527678a0b67")),
            InstanceId,
            new DiagnosticEventKind("deadlock.captured"),
            occurred,
            occurred.AddSeconds(1),
            reference);

        Assert.Equal(reference, envelope.ProtectedPayload);
        Assert.Equal(SensitivePayloadKind.DeadlockXml, envelope.ProtectedPayload?.Kind);
        Assert.True(envelope.EstimatedSizeBytes > 0);
        Assert.Throws<ArgumentException>(() => new DiagnosticEventEnvelope(
            envelope.EventId,
            InstanceId,
            envelope.Kind,
            occurred.ToOffset(TimeSpan.FromHours(-5)),
            occurred));
    }

    [Fact]
    public void PersistenceTimestampsRejectSubMicrosecondPrecision()
    {
        DateTimeOffset subMicrosecond = AlignedTimestamp.AddTicks(1);

        Assert.Throws<ArgumentException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.cpu.percent"),
            subMicrosecond,
            1));
        Assert.Throws<ArgumentException>(() => new DiagnosticEventEnvelope(
            new DiagnosticEventId(Guid.NewGuid()),
            InstanceId,
            new DiagnosticEventKind("deadlock.captured"),
            subMicrosecond,
            AlignedTimestamp));
        Assert.Throws<ArgumentException>(() => new DiagnosticEventEnvelope(
            new DiagnosticEventId(Guid.NewGuid()),
            InstanceId,
            new DiagnosticEventKind("deadlock.captured"),
            AlignedTimestamp,
            subMicrosecond));
    }

    [Fact]
    public void DimensionAccountingMatchesDefaultJsonUtf8ForEscapingHeavyValues()
    {
        MetricDimension[] dimensions =
        [
            new("markup", "\"\\<>&'\u20ac\U0001F600"),
            new("formula", "=SUM(A1:A2)"),
        ];
        var sample = new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.dimension.bytes"),
            AlignedTimestamp,
            1,
            dimensions);
        var serializedShape = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (MetricDimension dimension in dimensions)
        {
            serializedShape.Add(dimension.Key, dimension.Value);
        }

        int exactUtf8Bytes = JsonSerializer.SerializeToUtf8Bytes(serializedShape).Length;

        Assert.Equal(exactUtf8Bytes, sample.SerializedDimensionsUtf8Bytes);
        Assert.True(sample.StoredDimensionsSizeUpperBoundBytes >= exactUtf8Bytes);
    }

    [Fact]
    public void EscapedDimensionsCannotExceedVisiblePostgreSqlJsonbCap()
    {
        MetricDimension[] acceptedDimensions = Enumerable.Range(0, 10)
            .Select(index => new MetricDimension($"key{index}", new string('<', 256)))
            .ToArray();
        MetricDimension[] rejectedDimensions = Enumerable.Range(0, 11)
            .Select(index => new MetricDimension($"key{index}", new string('<', 256)))
            .ToArray();
        var accepted = new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.dimension.boundary"),
            AlignedTimestamp,
            1,
            acceptedDimensions);

        Assert.True(accepted.StoredDimensionsSizeUpperBoundBytes <= MetricSample.MaximumStoredDimensionsUtf8Bytes);
        Assert.True(rejectedDimensions.Sum(static dimension => dimension.Value.Length) <
            MetricSample.MaximumStoredDimensionsUtf8Bytes);
        Assert.Throws<ArgumentException>(() => new MetricSample(
            new MetricSampleId(Guid.NewGuid()),
            InstanceId,
            new MetricId("engine.dimension.boundary"),
            AlignedTimestamp,
            1,
            rejectedDimensions));
    }
}
