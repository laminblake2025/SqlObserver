using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class TempDbWindowHistoryEndpoints
{
    private const int MaximumResponseBytes = 512 * 1024;
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly HashSet<string> ListFields = ["targetRevision", "fromUtc", "toUtc", "limit", "cursor"];
    private static readonly HashSet<string> SnapshotFields = ["targetRevision", "sampleTargetRevision", "fromUtc", "toUtc", "limit", "cursor"];
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    { Converters = { new CanonicalUtcDateTimeOffsetConverter() } };

    public static IEndpointRouteBuilder MapTempDbWindowHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/tempdb/history").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{runId:guid}", SnapshotAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, Guid instanceId,
        ITempDbWindowHistoryService service, WindowsGroupRoleResolver roles, TempDbWindowHistoryCursorProtector protector)
    {
        Begin(context);
        try
        {
            Query(context, ListFields); RouteIds(context, "instanceId");
            var request = new TempDbWindowHistoryListRequest(instanceId, Number(context, "targetRevision"),
                Utc(Required(context, "fromUtc")), Utc(Required(context, "toUtc")), Limit(context),
                context.Request.Query.ContainsKey("cursor") ? protector.Unprotect(Required(context, "cursor")) : null, Timeout);
            var page = await service.ListAsync(roles.Resolve(context.User), request, context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure(context, "target_not_found", "The observation target was not found.", 404);
            return Bounded(context, new { page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc,
                page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.Coverage, page.Items,
                nextCursor = page.NextAnchor is null ? null : protector.Protect(page.NextAnchor) });
        }
        catch (UnauthorizedAccessException) { return Failure(context, "forbidden", "A same-target server read role is required.", 403); }
        catch (TempDbWindowHistoryReadException error) { return Failure(context, error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Failure(context, "invalid_request", "The TempDB history window or continuation is invalid.", 400); }
        catch (InvalidDataException) { return ContractFailure(context); }
    }

    private static async Task<IResult> SnapshotAsync(HttpContext context, Guid instanceId, Guid runId,
        ITempDbWindowHistoryService service, WindowsGroupRoleResolver roles, TempDbWindowHistoryCursorProtector protector)
    {
        Begin(context);
        try
        {
            Query(context, SnapshotFields); RouteIds(context, "instanceId", "runId");
            var request = new TempDbWindowHistorySnapshotRequest(instanceId, Number(context, "targetRevision"),
                Utc(Required(context, "fromUtc")), Utc(Required(context, "toUtc")), runId, Number(context, "sampleTargetRevision"), Limit(context),
                context.Request.Query.ContainsKey("cursor") ? protector.Unprotect(Required(context, "cursor")) : null, Timeout);
            var page = await service.GetAsync(roles.Resolve(context.User), request, context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure(context, "target_not_found", "The observation target was not found.", 404);
            return Bounded(context, new { page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc,
                page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.RunId, page.SampleTargetRevision, page.Receipt, page.Summary, page.Items,
                nextCursor = page.NextAnchor is null ? null : protector.Protect(page.NextAnchor) });
        }
        catch (UnauthorizedAccessException) { return Failure(context, "forbidden", "A same-target server read role is required.", 403); }
        catch (TempDbWindowHistoryReadException error) { return Failure(context, error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Failure(context, "invalid_request", "The TempDB snapshot, source revision, window or continuation is invalid.", 400); }
        catch (InvalidDataException) { return ContractFailure(context); }
    }

    private static void Begin(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        _ = ApiCorrelation.Begin(context);
    }
    private static IResult Bounded(HttpContext context, object response)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response, Output);
        return bytes.Length <= MaximumResponseBytes ? Results.Bytes(bytes, "application/json")
            : Failure(context, "tempdb_history_response_overflow", "The retained response is too large. Choose a smaller page or narrower window.", 422);
    }
    private static IResult ContractFailure(HttpContext context) =>
        Failure(context, "request_failed", "The request could not be completed.", 500);
    private static IResult Failure(HttpContext context, string code, string message, int status) => Results.Json(
        new SqlObserverProblemResponse(code, message, context.Response.Headers["X-Correlation-ID"].ToString()), statusCode: status);
    private static void Query(HttpContext context, HashSet<string> fields)
    {
        if (context.Request.Query.Any(pair => !fields.Contains(pair.Key) || pair.Value.Count != 1))
            throw new ArgumentException("Unknown or repeated query field.");
    }
    private static void RouteIds(HttpContext context, params string[] fields)
    {
        foreach (string field in fields) _ = Id(context.Request.RouteValues[field] as string ?? throw new ArgumentException("A route UUID is missing."));
    }
    private static string Required(HttpContext context, string field) => context.Request.Query[field].ToString() is { Length: > 0 } value
        ? value : throw new ArgumentException("A required field is absent.");
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty && id.ToString("D") == value
        ? id : throw new ArgumentException("A nonempty canonical UUID is required.");
    private static long Number(HttpContext context, string field)
    {
        string value = Required(context, field);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) || result.ToString(CultureInfo.InvariantCulture) != value)
            throw new ArgumentException("A canonical positive revision is required.");
        TempDbWindowHistoryValidation.Revision(result);
        return result;
    }
    private static int Limit(HttpContext context)
    {
        if (!context.Request.Query.ContainsKey("limit")) return 25;
        long value = Number(context, "limit");
        if (value > TempDbWindowHistoryValidation.MaximumPageSize) throw new ArgumentException("The page limit exceeds its bound.");
        return (int)value;
    }
    private static DateTimeOffset Utc(string value)
    {
        bool shape = value.Length == 20 && value[19] == 'Z' || value.Length is >= 22 and <= 27 && value[19] == '.' &&
            value[^1] == 'Z' && value.AsSpan(20, value.Length - 21).IndexOfAnyExceptInRange('0', '9') < 0;
        if (!shape || !DateTimeOffset.TryParseExact(value, value.Length == 20 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new ArgumentException("A microsecond-aligned UTC timestamp ending in Z is required.");
        return parsed;
    }
}

/// <summary>Dedicated authenticated continuations retain the original native receipt's expiry.</summary>
public sealed class TempDbWindowHistoryCursorProtector
{
    public const int MaximumTokenLength = 4096;
    private readonly ITimeLimitedDataProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public TempDbWindowHistoryCursorProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("SqlObserver.TempDb.WindowHistory.Cursor.v1").ToTimeLimitedDataProtector();
    }
    public string Protect(TempDbWindowHistoryCursor cursor)
    {
        TempDbWindowHistoryRequestValidation.Cursor(cursor);
        string token = Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(cursor, Json),
            cursor.IssuedAtUtc.Add(TempDbWindowHistoryValidation.CursorLifetime)));
        if (token.Length > MaximumTokenLength) throw new InvalidDataException("The protected server TempDB history cursor exceeded its bound.");
        return token;
    }
    public TempDbWindowHistoryCursor Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength ||
            token.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=')))
            throw new ArgumentException("Invalid protected server TempDB history cursor.", nameof(token));
        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            if (Convert.ToBase64String(bytes) != token) throw new ArgumentException("Invalid cursor encoding.", nameof(token));
            var cursor = JsonSerializer.Deserialize<TempDbWindowHistoryCursor>(protector.Unprotect(bytes), Json)
                ?? throw new ArgumentException("Invalid cursor payload.", nameof(token));
            TempDbWindowHistoryRequestValidation.Cursor(cursor);
            return cursor;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException)
        { throw new ArgumentException("The server TempDB history cursor is invalid or expired.", nameof(token), error); }
    }
}
