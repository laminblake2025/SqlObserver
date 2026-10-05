using System.Globalization;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Closed source projection; identity credentials and TCP endpoints never enter history.</summary>
internal static class CpuComponentsPersistencePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static object Build(CommitCollectorRunRequest request, string outcome, string reason) => new
    {
        schemaVersion = 1, targetId = request.Work.TargetId.Value, targetRevision = request.Work.TargetRevision.Value,
        runId = request.Summary.RunId.Value, outcome, reason, sourceFrames = request.Summary.Accounting.SourceRowsRead,
        truncated = false, observation = Observation(request.Payload.CpuComponents),
    };
    public static byte[] SerializeObservation(CpuComponentsObservation value) => JsonSerializer.SerializeToUtf8Bytes(Observation(value), Options);
    private static string Utc(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string State(CpuComponentsObservation value) => value.SqlProcess.Quality == CpuComponentQuality.Measured && value.OperatingSystem.Quality == CpuComponentQuality.Measured
        ? "available" : value.SqlProcess.Available || value.OperatingSystem.Available ? "partial" : "unavailable";
    private static object? Observation(CpuComponentsObservation? value) => value is null ? null : new
    {
        value.SourceSchemaVersion, targetId = value.TargetId.Value, targetRevision = value.TargetRevision.Value, runId = value.RunId.Value,
        value.HostBindingRevision, value.HostProfileRevision, observedAtUtc = Utc(value.ObservedAtUtc), value.SourceFrames, value.Bytes,
        state = State(value), reason = State(value) == "available" ? "measured" : State(value) == "partial" ? "component_quality" : "components_unavailable",
        sourceAssociationVerified = value.AssociationState == CpuComponentAssociationState.Verified,
        associationReason = value.AssociationReason.ToString(), nativeAssociationReason = value.NativeAssociationReason.ToString(),
        exactPairEligible = value.ExactPairEligible, sqlProcess = Component(value.SqlProcess), operatingSystem = Component(value.OperatingSystem),
    };
    private static object Component(CpuComponentValue value) => new
    {
        value.Percent, rawPercent = value.RawFormulaPercent,
        quality = value.Quality switch { CpuComponentQuality.Measured => "available", CpuComponentQuality.ProviderAdjusted => "provider_adjusted", _ => "unavailable" },
        reason = value.Reason.ToString(), fromNative100ns = value.FromNative100ns?.ToString(CultureInfo.InvariantCulture),
        toNative100ns = value.ToNative100ns?.ToString(CultureInfo.InvariantCulture),
        observedFromUtc = value.ObservedFromUtc is { } from ? Utc(from) : null,
        observedToUtc = value.ObservedToUtc is { } to ? Utc(to) : null,
        normalizationLogicalProcessors = value.LogicalProcessorDivisor, value.Method, value.ProviderApiStatus, value.ProviderCStatus,
        value.RawFormulaEligible, rawFormulaReason = value.RawFormulaReason.ToString(), value.ProviderRawPercent, value.RawBefore, value.RawAfter,
    };
}
