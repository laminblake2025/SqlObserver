using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class SqlVolumeWindowHistoryEndpoints
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly HashSet<string> CatalogFields = ["targetRevision", "fromUtc", "toUtc", "limit", "cursor"];
    private static readonly HashSet<string> HistoryFields = ["targetRevision", "sampleTargetRevision", "fromUtc", "toUtc", "limit", "cursor"];
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    { Converters = { new CanonicalUtcDateTimeOffsetConverter() } };
    public static IEndpointRouteBuilder MapSqlVolumeWindowHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/observation-targets/{instanceId:guid}/resources/volumes/history", CatalogAsync).RequireAuthorization();
        endpoints.MapGet("/api/v1/observation-targets/{instanceId:guid}/resources/volumes/{volumeKey}/history", HistoryAsync).RequireAuthorization();
        return endpoints;
    }
    private static async Task<IResult> CatalogAsync(HttpContext context, Guid instanceId,
        ISqlVolumeWindowHistoryService service, WindowsGroupRoleResolver roles, SqlVolumeWindowHistoryCursorProtector protector)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            Query(context, CatalogFields);
            long revision = Number(context, "targetRevision"); DateTimeOffset from = Utc(Required(context, "fromUtc")), to = Utc(Required(context, "toUtc"));
            int limit = Limit(context, 25); var cursor = Cursor(context, protector);
            var page = await service.ReadCatalogAsync(roles.Resolve(context.User), new(instanceId, revision, from, to, limit, cursor, Timeout), context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure("not_found", "The observation target was not found.", 404);
            return Bounded(new { page.InstanceId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc,
                page.State, page.Reason, page.Coverage, page.Items, nextCursor = page.NextAnchor is null ? null : protector.Protect(page.NextAnchor) });
        }
        catch (UnauthorizedAccessException) { return Failure("forbidden", "A target read role is required.", 403); }
        catch (SqlVolumeWindowHistoryReadException error) { return Failure(error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Failure("invalid_request", "The volume window or cursor is invalid.", 400); }
    }
    private static async Task<IResult> HistoryAsync(HttpContext context, Guid instanceId, string volumeKey,
        ISqlVolumeWindowHistoryService service, WindowsGroupRoleResolver roles, SqlVolumeWindowHistoryCursorProtector protector)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            Query(context, HistoryFields);
            long revision = Number(context, "targetRevision"), sampleRevision = Number(context, "sampleTargetRevision");
            DateTimeOffset from = Utc(Required(context, "fromUtc")), to = Utc(Required(context, "toUtc"));
            int limit = Limit(context, 250); var cursor = Cursor(context, protector);
            var page = await service.ReadHistoryAsync(roles.Resolve(context.User), new(instanceId, revision, from, to, sampleRevision, volumeKey, limit, cursor, Timeout), context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure("not_found", "The observation target was not found.", 404);
            return Bounded(new { page.InstanceId, page.TargetRevision, page.SampleTargetRevision, page.VolumeKey, page.IdentityKind,
                page.FromUtc, page.ToUtc, page.CutoffUtc, page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.Coverage, page.Items,
                nextCursor = page.NextAnchor is null ? null : protector.Protect(page.NextAnchor) });
        }
        catch (UnauthorizedAccessException) { return Failure("forbidden", "A target read role is required.", 403); }
        catch (SqlVolumeWindowHistoryReadException error) { return Failure(error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Failure("invalid_request", "The exact volume identity, window or cursor is invalid.", 400); }
    }
    private static IResult Bounded(object response)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Output);
        return bytes.Length <= 512 * 1024 ? Results.Bytes(bytes, "application/json")
            : Failure("window_too_large", "The volume history response is too large. Choose a narrower window or smaller page.", 422);
    }
    private static IResult Failure(string code, string message, int status) => Results.Json(new { code, message }, statusCode: status);
    private static string Required(HttpContext context, string field) => context.Request.Query[field].ToString() is { Length: > 0 } value ? value : throw new ArgumentException("A required field is absent.");
    private static long Number(HttpContext context, string field)
    {
        string value = Required(context, field);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result.ToString(CultureInfo.InvariantCulture) != value)
            throw new ArgumentException("A canonical positive integer is required.");
        SqlVolumeWindowHistoryValidation.Revision(result); return result;
    }
    private static int Limit(HttpContext context, int fallback)
    {
        if (!context.Request.Query.ContainsKey("limit")) return fallback;
        long value = Number(context, "limit");
        if (value > 250) throw new ArgumentOutOfRangeException(nameof(context));
        return (int)value;
    }
    private static SqlVolumeWindowHistoryCursor? Cursor(HttpContext context, SqlVolumeWindowHistoryCursorProtector protector) =>
        context.Request.Query.ContainsKey("cursor") ? protector.Unprotect(Required(context, "cursor")) : null;
    private static void Query(HttpContext context, HashSet<string> allowed)
    { if (context.Request.Query.Any(pair => !allowed.Contains(pair.Key) || pair.Value.Count != 1)) throw new ArgumentException("Unknown or repeated query field."); }
    internal static DateTimeOffset Utc(string value)
    {
        bool shape = value.Length == 20 && value[19] == 'Z' || value.Length is >= 22 and <= 27 && value[19] == '.' && value[^1] == 'Z' && value.AsSpan(20, value.Length - 21).IndexOfAnyExceptInRange('0', '9') < 0;
        if (!shape || !DateTimeOffset.TryParseExact(value, value.Length == 20 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new ArgumentException("A microsecond-aligned UTC timestamp ending in Z is required.");
        return parsed;
    }
}

/// <summary>Exact-window continuations expire relative to the original native receipt, not each page read.</summary>
public sealed class SqlVolumeWindowHistoryCursorProtector
{
    public const int MaximumTokenLength = 4096;
    private readonly ITimeLimitedDataProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public SqlVolumeWindowHistoryCursorProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("SqlObserver.Resources.SqlVolume.WindowHistory.Cursor.v1").ToTimeLimitedDataProtector();
    }
    public string Protect(SqlVolumeWindowHistoryCursor cursor)
    {
        SqlVolumeWindowHistoryRequestValidation.Cursor(cursor);
        string token = Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(cursor, Json), cursor.IssuedAtUtc.AddMinutes(15)));
        if (token.Length > MaximumTokenLength) throw new InvalidDataException("The protected volume cursor exceeded its bound.");
        return token;
    }
    public SqlVolumeWindowHistoryCursor Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength || token.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=')))
            throw new ArgumentException("Invalid protected volume cursor.", nameof(token));
        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            if (Convert.ToBase64String(bytes) != token) throw new ArgumentException("Invalid cursor encoding.", nameof(token));
            var cursor = JsonSerializer.Deserialize<SqlVolumeWindowHistoryCursor>(protector.Unprotect(bytes), Json)
                ?? throw new ArgumentException("Invalid cursor payload.", nameof(token));
            SqlVolumeWindowHistoryRequestValidation.Cursor(cursor); return cursor;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException)
        { throw new ArgumentException("The volume cursor is invalid or expired.", nameof(token), error); }
    }
}
