using System.Collections.ObjectModel;
using SqlObserver.Domain.SensitiveData;

namespace SqlObserver.Domain.Collection;

public enum PlanCacheContentCaptureState
{
    Disabled = 1,
    Enabled = 2,
    ProtectionUnavailable = 3,
}

public enum PlanCacheContentState
{
    Unavailable = 1,
    Available = 2,
    Oversized = 3,
    ResponseByteLimit = 4,
    ProtectionUnavailable = 5,
    ProtectionFailed = 6,
}

/// <summary>Exactly one protected payload or repository reference for available content.</summary>
public sealed class PlanCacheContentValue
{
    private PlanCacheContentValue(SensitivePayloadKind kind, PlanCacheContentState state,
        ProtectedSensitivePayload? payload, SensitivePayloadReference? reference, int ciphertextBytes)
    {
        if (kind is not (SensitivePayloadKind.QueryText or SensitivePayloadKind.ExecutionPlan))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        int maximum = kind == SensitivePayloadKind.QueryText ? 16 * 1024 : ProtectedSensitivePayload.MaximumCiphertextBytes;
        if (state == PlanCacheContentState.Available)
        {
            if ((payload is null) == (reference is null) || ciphertextBytes < 1 || ciphertextBytes > maximum
                || payload is not null && (payload.Kind != kind || payload.CiphertextLengthBytes != ciphertextBytes)
                || reference is not null && reference.Kind != kind)
                throw new ArgumentException("Available cache content requires one bounded protected payload or reference.");
        }
        else if (payload is not null || reference is not null || ciphertextBytes != 0)
            throw new ArgumentException("Unavailable cache content cannot carry a payload or reference.");
        Kind = kind; State = state; ProtectedContent = payload; Reference = reference; CiphertextBytes = ciphertextBytes;
    }

    public SensitivePayloadKind Kind { get; }
    public PlanCacheContentState State { get; }
    public ProtectedSensitivePayload? ProtectedContent { get; }
    public SensitivePayloadReference? Reference { get; }
    public int CiphertextBytes { get; }

    public static PlanCacheContentValue Protected(ProtectedSensitivePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new(payload.Kind, PlanCacheContentState.Available, payload, null, payload.CiphertextLengthBytes);
    }

    public static PlanCacheContentValue Missing(SensitivePayloadKind kind, PlanCacheContentState state) =>
        new(kind, state, null, null, 0);

    public PlanCacheContentValue WithReference(SensitivePayloadReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        SensitivePayloadFingerprint? fingerprint = ProtectedContent?.Fingerprint ?? Reference?.Fingerprint;
        if (State != PlanCacheContentState.Available || reference.Kind != Kind || reference.Fingerprint != fingerprint)
            throw new ArgumentException("Cache content reference must retain its protected kind and fingerprint.", nameof(reference));
        return new(Kind, State, null, reference, CiphertextBytes);
    }
}

public sealed record PlanCacheStatementContentObservation
{
    public PlanCacheStatementContentObservation(PlanCacheRuntimeStatementObservation statement,
        PlanCacheContentValue text, PlanCacheContentValue plan)
    {
        Statement = statement ?? throw new ArgumentNullException(nameof(statement));
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        if (text.Kind != SensitivePayloadKind.QueryText || plan.Kind != SensitivePayloadKind.ExecutionPlan)
            throw new ArgumentException("Cache text and plan must retain their separate payload kinds.");
    }
    public PlanCacheRuntimeStatementObservation Statement { get; }
    public PlanCacheContentValue Text { get; }
    public PlanCacheContentValue Plan { get; }
}

/// <summary>Selected content qualified by the same live identity brackets as its counter capture.</summary>
public sealed class PlanCacheContentOutputBatch
{
    public const int MaximumEntries = 8;
    public const int MaximumSourceContentBytes = 2 * 1024 * 1024;
    public const int MaximumCiphertextBytes = 2 * 1024 * 1024;
    public const int MaximumSerializedBytes = 32 * 1024;
    private readonly ReadOnlyCollection<PlanCacheStatementContentObservation> items;

    public PlanCacheContentOutputBatch(PlanCacheRuntimeOutputBatch runtime, int contentLimit,
        int attemptedEntries, int unqualifiedEntries, int sourceContentBytes,
        IReadOnlyList<PlanCacheStatementContentObservation> observations, PlanCacheContentCaptureState? captureState = null)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(observations);
        if (contentLimit is < 0 or > MaximumEntries || contentLimit > runtime.CandidateLimit
            || attemptedEntries < 0 || attemptedEntries > contentLimit || unqualifiedEntries < 0
            || observations.Count + unqualifiedEntries != attemptedEntries
            || sourceContentBytes is < 0 or > MaximumSourceContentBytes
            || sourceContentBytes % 2 != 0 || attemptedEntries == 0 && sourceContentBytes != 0)
            throw new ArgumentException("Cache content accounting is outside the bounded source frame.");
        CaptureState = captureState ?? (contentLimit == 0 ? PlanCacheContentCaptureState.Disabled : PlanCacheContentCaptureState.Enabled);
        if (!Enum.IsDefined(CaptureState) || (CaptureState == PlanCacheContentCaptureState.Enabled) != (contentLimit > 0))
            throw new ArgumentException("Cache content capture state must match its effective selection limit.");
        var statements = runtime.Items.ToDictionary(static item => item.Watermark.Key.CacheEntryFingerprint, StringComparer.Ordinal);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        var copy = observations.ToArray();
        int bytes = 0;
        foreach (PlanCacheStatementContentObservation item in copy)
        {
            if (item is null || !entries.Add(item.Statement.Watermark.Key.CacheEntryFingerprint)
                || !statements.TryGetValue(item.Statement.Watermark.Key.CacheEntryFingerprint, out var statement)
                || statement != item.Statement)
                throw new ArgumentException("Cache content must identify one exact qualified statement in this frame.", nameof(observations));
            bytes = checked(bytes + item.Text.CiphertextBytes + item.Plan.CiphertextBytes);
        }
        if (bytes > MaximumCiphertextBytes || bytes > sourceContentBytes * 3 / 2)
            throw new ArgumentException("Cache content exceeds its protected output byte budget.", nameof(observations));
        items = Array.AsReadOnly(copy);
        ContentLimit = contentLimit; AttemptedEntries = attemptedEntries; UnqualifiedEntries = unqualifiedEntries;
        SourceContentBytes = sourceContentBytes; CiphertextBytes = bytes;
        EstimatedSizeBytes = checked(1024 + copy.Length * 2048 + bytes + copy.Length * 128);
    }

    public PlanCacheRuntimeOutputBatch Runtime { get; }
    public PlanCacheContentCaptureState CaptureState { get; }
    public int ContentLimit { get; }
    public int AttemptedEntries { get; }
    public int UnqualifiedEntries { get; }
    public int SourceContentBytes { get; }
    public int CiphertextBytes { get; }
    public int EstimatedSizeBytes { get; }
    public IReadOnlyList<PlanCacheStatementContentObservation> Items => items;
}
