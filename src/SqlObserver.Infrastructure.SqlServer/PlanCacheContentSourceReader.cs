using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

// Source-only evidence. Database incarnation still requires live identity
// brackets around this complete read before it can enter a persisted batch.
internal sealed record PlanCacheProtectedContent(PlanCacheRuntimeSourceRow Statement,
    string TextState, ProtectedSensitivePayload? Text, string PlanState, ProtectedSensitivePayload? Plan);

internal sealed record PlanCacheContentSourceRead(PlanCacheRuntimeSourcePage Counters,
    IReadOnlyList<PlanCacheProtectedContent> Content, int SourceContentBytes, int ProtectedContentBytes);

internal static class PlanCacheContentSourceReader
{
    internal const int MaximumEntries = 8;
    internal const int MaximumSourceContentBytes = 2 * 1024 * 1024;
    internal const int MaximumProtectedContentBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static async Task<PlanCacheContentSourceRead> ReadAsync(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision revision, int maximumRows,
        int maximumContentEntries, IQuerySensitiveContentProtector protector, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (maximumContentEntries is < 0 or > MaximumEntries || maximumContentEntries > maximumRows)
            throw new ArgumentOutOfRangeException(nameof(maximumContentEntries));
        PlanCacheRuntimeSourcePage page = await PlanCacheRuntimeSourceReader.ReadFrameAsync(
            reader, targetId, revision, maximumRows, token).ConfigureAwait(false);
        if (!await reader.NextResultAsync(token).ConfigureAwait(false) || reader.FieldCount != 8)
            throw new InvalidDataException("Cache content omitted its reviewed result shape.");
        var statements = page.Rows.ToDictionary(static row => row.CacheEntryFingerprint, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var content = new List<PlanCacheProtectedContent>();
        int sourceBytes = 0, protectedBytes = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            if (content.Count >= maximumContentEntries)
                throw new InvalidDataException("Cache content exceeded its selected entry bound.");
            int databaseId = reader.GetInt32(0);
            var query = new QueryOpaqueIdentity(databaseId, reader.GetString(1));
            var plan = new PlanOpaqueIdentity(query, reader.GetString(2));
            string entry = QueryOpaqueIdentity.RequireDigest(reader.GetString(3), "cacheEntryFingerprint");
            if (!seen.Add(entry) || !statements.TryGetValue(entry, out var statement)
                || statement.Query != query || statement.Plan != plan)
                throw new InvalidDataException("Cache content differs from its captured statement identity.");
            (string textState, ProtectedSensitivePayload? text) = await Protect(4, 5, SensitivePayloadKind.QueryText, 16 * 1024);
            (string planState, ProtectedSensitivePayload? xml) = await Protect(6, 7, SensitivePayloadKind.ExecutionPlan,
                ProtectedSensitivePayload.MaximumCiphertextBytes);
            content.Add(new(statement, textState, text, planState, xml));
        }
        if (await reader.NextResultAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("Cache content returned an unexpected additional result.");
        return new(page, content.AsReadOnly(), sourceBytes, protectedBytes);

        async Task<(string State, ProtectedSensitivePayload? Payload)> Protect(int stateColumn, int contentColumn,
            SensitivePayloadKind kind, int maximumBytes)
        {
            string state = reader.GetString(stateColumn);
            if (state is not ("available" or "unavailable" or "oversized" or "response_byte_limit"))
                throw new InvalidDataException("Cache content source state is not approved.");
            bool missing = await reader.IsDBNullAsync(contentColumn, token).ConfigureAwait(false);
            if (state != "available")
            {
                if (!missing) throw new InvalidDataException("Unavailable cache content contains a payload.");
                return (state, null);
            }
            if (missing) throw new InvalidDataException("Available cache content omitted its payload.");
            long count = reader.GetChars(contentColumn, 0, null, 0, 0);
            if (count < 1 || count > maximumBytes || count * 2 > MaximumSourceContentBytes - sourceBytes)
                throw new InvalidDataException("Cache content exceeded its reviewed source byte bound.");
            sourceBytes = checked(sourceBytes + (int)count * 2);
            var chars = new char[(int)count];
            byte[]? bytes = null;
            try
            {
                if (reader.GetChars(contentColumn, 0, chars, 0, chars.Length) != count)
                    throw new InvalidDataException("Cache content returned an incomplete source payload.");
                int size;
                try { size = Utf8.GetByteCount(chars); }
                catch (EncoderFallbackException) { return ("unavailable", null); }
                if (size > maximumBytes) return ("oversized", null);
                if (size > MaximumProtectedContentBytes - protectedBytes) return ("response_byte_limit", null);
                if (!protector.IsAvailable) return ("protection_unavailable", null);
                bytes = Utf8.GetBytes(chars);
                ProtectedSensitivePayload? payload;
                try { payload = await protector.ProtectAsync(targetId.Value, kind, bytes, token).ConfigureAwait(false); }
                catch (CryptographicException)
                {
                    token.ThrowIfCancellationRequested();
                    return ("protection_failed", null);
                }
                token.ThrowIfCancellationRequested();
                if (payload is null) return ("protection_unavailable", null);
                if (payload.Kind != kind || payload.CiphertextLengthBytes != bytes.Length)
                    throw new InvalidDataException("Cache content protector returned an inconsistent payload.");
                protectedBytes = checked(protectedBytes + payload.CiphertextLengthBytes);
                return ("protected", payload);
            }
            finally
            {
                Array.Clear(chars);
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }
}
