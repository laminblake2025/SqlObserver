using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.Domain.Authorization;

/// <summary>
/// Canonical, server-owned idempotency digests for audited M10 mutations.
/// The requestDigest supplied by a caller is only an assertion; callers must
/// never be allowed to choose the digest used by replay or audit fencing.
/// </summary>
public static class MutationDigestV1
{
    public const string Version = "m10.mutation.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General) { WriteIndented = false };

    public static byte[] Backfill(Guid targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        string? metricKey, long expectedRevision, Guid operationId, string actorSid, string scope,
        Guid correlationId, string changeReason) => Hash(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actorSid"] = actorSid, ["changeReason"] = changeReason, ["correlationId"] = correlationId,
            ["expectedRevision"] = expectedRevision, ["fromUtc"] = Utc(fromUtc), ["metricKey"] = metricKey,
            ["operation"] = "analytics.backfill.enqueue", ["operationId"] = operationId,
            ["scope"] = scope, ["targetId"] = targetId, ["toUtc"] = Utc(toUtc), ["version"] = Version
        });

    public static byte[] HostBinding(Guid targetId, Guid hostId, string hostName, string identityFingerprint,
        long bindingRevision, long profileRevision, long expectedRevision, JsonElement profile,
        Guid operationId, string actorSid, string scope, Guid correlationId, string changeReason) =>
        Hash(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actorSid"] = actorSid, ["bindingRevision"] = bindingRevision, ["changeReason"] = changeReason,
            ["correlationId"] = correlationId, ["expectedRevision"] = expectedRevision,
            ["hostId"] = hostId, ["hostName"] = hostName, ["identityFingerprint"] = identityFingerprint.ToLowerInvariant(),
            ["operation"] = "host.binding.update", ["operationId"] = operationId,
            ["profile"] = CanonicalObject(profile), ["profileRevision"] = profileRevision,
            ["scope"] = scope, ["targetId"] = targetId, ["version"] = Version
        });

    public static byte[] Attestation(Guid attestationId, string evidenceDigest, string backupSetReference,
        string attestedBy, DateTimeOffset expiresAtUtc, string actorSid, string scope,
        Guid correlationId, string changeReason) => Hash(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actorSid"] = actorSid, ["attestedBy"] = attestedBy, ["attestationId"] = attestationId,
            ["backupSetReference"] = backupSetReference, ["changeReason"] = changeReason,
            ["correlationId"] = correlationId, ["evidenceDigest"] = evidenceDigest.ToLowerInvariant(),
            ["expiresAtUtc"] = Utc(expiresAtUtc), ["operation"] = "retention.attestation.record",
            ["scope"] = scope, ["version"] = Version
        });

    public static byte[] Retention(string operation, string dataClass, string parentSchema, string parentTable,
        string partitionName, long expectedPolicyRevision, Guid executionId, Guid operationId,
        string actorSid, string scope, Guid correlationId, string changeReason) =>
        Hash(new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actorSid"] = actorSid, ["changeReason"] = changeReason, ["correlationId"] = correlationId,
            ["dataClass"] = dataClass, ["executionId"] = executionId,
            ["expectedPolicyRevision"] = expectedPolicyRevision, ["operation"] = operation,
            ["operationId"] = operationId, ["parentSchema"] = parentSchema, ["parentTable"] = parentTable,
            ["partitionName"] = partitionName, ["scope"] = scope, ["version"] = Version
        });

    public static string Scope(TargetAuthorizationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.AllTargets
            ? "all"
            : string.Join(",", scope.TargetIds.Select(x => x.Value.ToString("D")).OrderBy(x => x, StringComparer.Ordinal));
    }

    public static string Hex(ReadOnlySpan<byte> digest) => Convert.ToHexString(digest).ToLowerInvariant();

    private static byte[] Hash(SortedDictionary<string, object?> fields)
    {
        string json = JsonSerializer.Serialize(fields, JsonOptions);
        return SHA256.HashData(Encoding.UTF8.GetBytes(json));
    }

    private static string Utc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("UTC is required.", nameof(value));
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static SortedDictionary<string, object?> CanonicalObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("A JSON object is required.", nameof(value));
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            result.Add(property.Name, property.Value.ValueKind == JsonValueKind.Object ? CanonicalObject(property.Value) : property.Value.Clone());
        return result;
    }
}
