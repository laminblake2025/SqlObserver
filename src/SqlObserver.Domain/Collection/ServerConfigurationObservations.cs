using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Exact configured and running values from one complete SQL Server scan.</summary>
public sealed class ServerConfigurationObservation : IIngestionRecord
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public ServerConfigurationObservation(MonitoredInstanceId targetId,
        ObservationTargetRevision targetRevision, int serverMajorVersion,
        int configurationId, string name, string sourceType,
        string configuredValue, string inUseValue, bool isDynamic,
        bool isAdvanced, DateTimeOffset observedAtUtc)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId));
        TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision));
        if (serverMajorVersion is < 15 or > 17)
            throw new ArgumentOutOfRangeException(nameof(serverMajorVersion));
        if (configurationId is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(configurationId));
        if (!ValidText(name, 256) || !ValidText(configuredValue, 128) ||
            !ValidText(inUseValue, 128) ||
            sourceType is not ("bit" or "tinyint" or "smallint" or "int" or "bigint") ||
            !ValidNumber(sourceType, configuredValue) ||
            !ValidNumber(sourceType, inUseValue))
            throw new ArgumentException("Server configuration evidence is invalid.");
        ServerMajorVersion = serverMajorVersion;
        ConfigurationId = configurationId;
        Name = name;
        SourceType = sourceType;
        ConfiguredValue = configuredValue;
        InUseValue = inUseValue;
        IsDynamic = isDynamic;
        IsAdvanced = isAdvanced;
        ObservedAtUtc = DomainValidation.RequireUtcMicrosecondAligned(observedAtUtc,
            nameof(observedAtUtc));
        EstimatedSizeBytes = checked(64 + StrictUtf8.GetByteCount(name) +
            StrictUtf8.GetByteCount(sourceType) +
            StrictUtf8.GetByteCount(configuredValue) +
            StrictUtf8.GetByteCount(inUseValue));
    }

    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public int ServerMajorVersion { get; }
    public int ConfigurationId { get; }
    public string Name { get; }
    public string SourceType { get; }
    public string ConfiguredValue { get; }
    public string InUseValue { get; }
    public bool IsDynamic { get; }
    public bool IsAdvanced { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public int EstimatedSizeBytes { get; }

    private static bool ValidText(string value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return false;
        try { return StrictUtf8.GetByteCount(value) <= maximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool ValidNumber(string sourceType, string value)
    {
        if (value.Trim() != value ||
            !long.TryParse(value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long parsed) ||
            parsed.ToString(CultureInfo.InvariantCulture) != value)
            return false;
        return sourceType switch
        {
            "bit" => parsed is 0 or 1,
            "tinyint" => parsed is >= 0 and <= byte.MaxValue,
            "smallint" => parsed is >= short.MinValue and <= short.MaxValue,
            "int" => parsed is >= int.MinValue and <= int.MaxValue,
            "bigint" => true,
            _ => false,
        };
    }
}

public sealed class ServerConfigurationObservationBatch
{
    public const int MaximumItems = 512;
    private readonly ReadOnlyCollection<ServerConfigurationObservation> _items;

    public ServerConfigurationObservationBatch(IReadOnlyList<ServerConfigurationObservation> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Count > MaximumItems)
            throw new ArgumentException("Server configuration scan exceeded its row bound.", nameof(source));
        ServerConfigurationObservation[] copy = source.ToArray();
        if (copy.Any(static item => item is null) ||
            copy.Select(static item => item.ConfigurationId).Distinct().Count() != copy.Length ||
            copy.Length > 0 && copy.Any(item =>
                item.TargetId != copy[0].TargetId ||
                item.TargetRevision != copy[0].TargetRevision ||
                item.ServerMajorVersion != copy[0].ServerMajorVersion ||
                item.ObservedAtUtc != copy[0].ObservedAtUtc))
            throw new ArgumentException("Server configuration scan identities are inconsistent.", nameof(source));
        _items = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<ServerConfigurationObservation> Items => _items;
    public int EstimatedSizeBytes => _items.Sum(static item => item.EstimatedSizeBytes);
}
