using System.Collections.ObjectModel;
using System.Text.Json;

namespace SqlObserver.Domain.Telemetry;

/// <summary>A caller-owned idempotency identity for one metric sample.</summary>
public sealed record MetricSampleId
{
    public MetricSampleId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A metric-sample identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

/// <summary>Identifies a monitored SQL Server instance without carrying connection details.</summary>
public sealed record MonitoredInstanceId
{
    public MonitoredInstanceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A monitored-instance identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

/// <summary>A stable, bounded identifier from the product-owned metric catalog.</summary>
public sealed record MetricId
{
    public const int MaximumLength = 128;

    public MetricId(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character => DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or '-',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A bounded, non-sensitive metric dimension.</summary>
public sealed record MetricDimension
{
    public const int MaximumKeyLength = 64;
    public const int MaximumValueUtf8Bytes = 256;

    public MetricDimension(string key, string value)
    {
        Key = DomainValidation.RequireAsciiToken(
            key,
            nameof(key),
            MaximumKeyLength,
            static character => DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '_' or '-',
            requireLeadingLetter: true);
        Value = DomainValidation.RequireSafeText(value, nameof(value), MaximumValueUtf8Bytes);
    }

    public string Key { get; }

    public string Value { get; }

}

/// <summary>An immutable UTC metric observation ready for bounded ingestion.</summary>
public sealed class MetricSample : IIngestionRecord
{
    public const int MaximumDimensionCount = 16;
    public const int MaximumStoredDimensionsUtf8Bytes = 16_384;

    /// <summary>
    /// Covers 56 fixed persisted bytes, binary COPY field framing, and small format-version headroom.
    /// </summary>
    public const int ConservativeFixedRecordBytes = 96;

    private readonly ReadOnlyCollection<MetricDimension> _dimensions;

    public MetricSample(
        MetricSampleId sampleId,
        MonitoredInstanceId instanceId,
        MetricId metricId,
        DateTimeOffset observedAtUtc,
        double value,
        IReadOnlyList<MetricDimension>? dimensions = null)
    {
        ArgumentNullException.ThrowIfNull(sampleId);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(metricId);

        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Metric values must be finite.");
        }

        dimensions ??= Array.Empty<MetricDimension>();

        if (dimensions.Count > MaximumDimensionCount)
        {
            throw new ArgumentException(
                $"A metric sample cannot contain more than {MaximumDimensionCount} dimensions.",
                nameof(dimensions));
        }

        var copiedDimensions = new MetricDimension[dimensions.Count];
        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < dimensions.Count; index++)
        {
            MetricDimension dimension = dimensions[index] ?? throw new ArgumentException(
                "A metric sample cannot contain a null dimension.",
                nameof(dimensions));

            if (!keys.Add(dimension.Key))
            {
                throw new ArgumentException("Metric dimension keys must be unique.", nameof(dimensions));
            }

            copiedDimensions[index] = dimension;
        }

        SampleId = sampleId;
        InstanceId = instanceId;
        MetricId = metricId;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(
            observedAtUtc,
            nameof(observedAtUtc));
        Value = value;
        _dimensions = Array.AsReadOnly(copiedDimensions);

        SerializedDimensionsUtf8Bytes = CalculateSerializedDimensionsUtf8Bytes(copiedDimensions);
        int jsonbFormattingHeadroom = copiedDimensions.Length == 0
            ? 0
            : checked((copiedDimensions.Length * 2) - 1);
        StoredDimensionsSizeUpperBoundBytes = checked(
            SerializedDimensionsUtf8Bytes + jsonbFormattingHeadroom);

        if (StoredDimensionsSizeUpperBoundBytes > MaximumStoredDimensionsUtf8Bytes)
        {
            throw new ArgumentException(
                $"The serialized dimensions cannot exceed the PostgreSQL JSONB storage cap of {MaximumStoredDimensionsUtf8Bytes} UTF-8 bytes.",
                nameof(dimensions));
        }

        EstimatedSizeBytes = checked(
            ConservativeFixedRecordBytes +
            DomainValidation.GetUtf8ByteCount(metricId.Value) +
            StoredDimensionsSizeUpperBoundBytes);
    }

    public MetricSampleId SampleId { get; }

    public MonitoredInstanceId InstanceId { get; }

    public MetricId MetricId { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public double Value { get; }

    public IReadOnlyList<MetricDimension> Dimensions => _dimensions;

    /// <summary>The exact UTF-8 byte count produced by default System.Text.Json object serialization.</summary>
    public int SerializedDimensionsUtf8Bytes { get; }

    /// <summary>
    /// A conservative PostgreSQL jsonb text-storage bound including canonical separator whitespace.
    /// </summary>
    public int StoredDimensionsSizeUpperBoundBytes { get; }

    public int EstimatedSizeBytes { get; }

    private static int CalculateSerializedDimensionsUtf8Bytes(MetricDimension[] dimensions)
    {
        int byteCount = 2; // Opening and closing object braces.

        for (int index = 0; index < dimensions.Length; index++)
        {
            if (index > 0)
            {
                byteCount = checked(byteCount + 1); // Comma.
            }

            MetricDimension dimension = dimensions[index];
            int encodedKeyBytes = JsonEncodedText.Encode(dimension.Key).EncodedUtf8Bytes.Length;
            int encodedValueBytes = JsonEncodedText.Encode(dimension.Value).EncodedUtf8Bytes.Length;
            byteCount = checked(
                byteCount +
                encodedKeyBytes +
                encodedValueBytes +
                5); // Four quotes and one colon.
        }

        return byteCount;
    }
}

/// <summary>Implemented by records that expose deterministic bounded-ingestion accounting.</summary>
public interface IIngestionRecord
{
    int EstimatedSizeBytes { get; }
}
