using SqlObserver.Domain.Hosts;

namespace SqlObserver.Application.Services;

/// <summary>Application boundary validator for host.metrics v1. No provider text is accepted.</summary>
public sealed class HostMetricsV1Validator
{
    public const int MaximumRows = 256;
    public const int MaximumBytes = 256 * 1024;

    public static void Validate(HostMetricsV1 output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Schema != HostMetricsV1.SchemaVersion || output.Volumes.Count > MaximumRows || output.EstimatedSizeBytes > MaximumBytes)
            throw new ArgumentException("host.metrics v1 output exceeds its contract bounds.", nameof(output));
        output.Validate();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Retain an injectable validator instance API for application composition.")]
    public bool IsValid(HostMetricsV1? output)
    {
        if (output is null) return false;
        try { Validate(output); return true; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
