using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class AlertLifecycleEndpoints
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly HashSet<string> ListFields = ["targetRevision", "fromUtc", "toUtc", "limit", "cursor"];
    private static readonly HashSet<string> ExactFields = ["targetRevision", "fromUtc", "toUtc", "alertId", "episodeId", "eventAtUtc"];
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    { Converters = { new CanonicalUtcDateTimeOffsetConverter() } };
    public static IEndpointRouteBuilder MapAlertLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{targetId:guid}/alerts/history").RequireAuthorization();
        group.MapGet("", ListAsync); group.MapGet("/{eventId:guid}", ExactAsync); return endpoints;
    }
    private static async Task<IResult> ListAsync(HttpContext context, Guid targetId,
        IAlertLifecycleService service, WindowsGroupRoleResolver roles, AlertLifecycleCursorProtector protector)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            Query(context,ListFields);
            var request = new AlertLifecycleListRequest(targetId,Number(context,"targetRevision"),Utc(Required(context,"fromUtc")),Utc(Required(context,"toUtc")),
                Limit(context),context.Request.Query.ContainsKey("cursor") ? protector.Unprotect(Required(context,"cursor")) : null,Timeout);
            var page = await service.ListAsync(roles.Resolve(context.User),request,context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure("target_not_found","The observation target was not found.",404);
            return Bounded(new { page.TargetId,page.TargetRevision,page.FromUtc,page.ToUtc,page.CutoffUtc,page.ReadEpoch,page.RepositoryTimeUtc,
                page.State,page.Reason,page.Coverage,page.Items,nextCursor = page.NextAnchor is null ? null : protector.Protect(page.NextAnchor) });
        }
        catch (UnauthorizedAccessException) { return Failure("forbidden","A same-target alert read role is required.",403); }
        catch (AlertLifecycleReadException error) { return Failure(error.Code,error.Message,error.StatusCode); }
        catch (ArgumentException) { return Failure("invalid_request","The lifecycle window or continuation is invalid.",400); }
    }
    private static async Task<IResult> ExactAsync(HttpContext context, Guid targetId, Guid eventId,
        IAlertLifecycleService service, WindowsGroupRoleResolver roles)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            Query(context,ExactFields);
            string episode = Required(context,"episodeId");
            var request = new AlertLifecycleExactRequest(targetId,Number(context,"targetRevision"),Utc(Required(context,"fromUtc")),Utc(Required(context,"toUtc")),
                eventId,Id(Required(context,"alertId")),episode == "legacy" ? null : Id(episode),Utc(Required(context,"eventAtUtc")),Timeout);
            var page = await service.GetAsync(roles.Resolve(context.User),request,context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure("target_not_found","The observation target was not found.",404);
            return Bounded(page);
        }
        catch (UnauthorizedAccessException) { return Failure("forbidden","A same-target alert read role is required.",403); }
        catch (AlertLifecycleReadException error) { return Failure(error.Code,error.Message,error.StatusCode); }
        catch (ArgumentException) { return Failure("invalid_request","The exact event, cycle, time or window is invalid.",400); }
    }
    private static IResult Bounded(object response)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(response,Output);
        return bytes.Length <= 512 * 1024 ? Results.Bytes(bytes,"application/json")
            : Failure("lifecycle_response_overflow","The retained response is too large. Choose a smaller page or narrower window.",422);
    }
    private static IResult Failure(string code,string message,int status) => Results.Json(new { code,message },statusCode: status);
    private static void Query(HttpContext context,HashSet<string> fields)
    { if (context.Request.Query.Any(pair => !fields.Contains(pair.Key) || pair.Value.Count != 1)) throw new ArgumentException("Unknown or repeated query field."); }
    private static string Required(HttpContext context,string field) => context.Request.Query[field].ToString() is { Length: > 0 } value ? value : throw new ArgumentException("A required field is absent.");
    private static Guid Id(string value) => AlertIdentifier.TryParseRfc4122(value,out var id) ? id : throw new ArgumentException("A canonical UUID is required.");
    private static long Number(HttpContext context,string field)
    {
        string value = Required(context,field);
        if (!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out long number) || number.ToString(CultureInfo.InvariantCulture) != value)
            throw new ArgumentException("A canonical positive revision is required.");
        AlertLifecycleValidation.Revision(number); return number;
    }
    private static int Limit(HttpContext context)
    {
        if (!context.Request.Query.ContainsKey("limit")) return 100;
        long value = Number(context,"limit"); if (value > AlertLifecycleValidation.MaximumPageSize) throw new ArgumentException("The page limit exceeds its bound."); return (int)value;
    }
    internal static DateTimeOffset Utc(string value)
    {
        bool shape = value.Length == 20 && value[19] == 'Z' || value.Length is >= 22 and <= 27 && value[19] == '.' && value[^1] == 'Z' && value.AsSpan(20,value.Length - 21).IndexOfAnyExceptInRange('0','9') < 0;
        if (!shape || !DateTimeOffset.TryParseExact(value,value.Length == 20 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'",CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,out var parsed))
            throw new ArgumentException("A microsecond-aligned UTC timestamp ending in Z is required.");
        return parsed;
    }
}

public sealed class AlertLifecycleCursorProtector
{
    public const int MaximumTokenLength = 4096;
    private readonly ITimeLimitedDataProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,PropertyNameCaseInsensitive = false };
    public AlertLifecycleCursorProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("SqlObserver.Alerts.Lifecycle.Cursor.v1").ToTimeLimitedDataProtector();
    }
    public string Protect(AlertLifecycleCursor cursor)
    {
        AlertLifecycleRequestValidation.Cursor(cursor);
        string token = Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(cursor,Json),cursor.IssuedAtUtc.AddMinutes(15)));
        if (token.Length > MaximumTokenLength) throw new InvalidDataException("The protected lifecycle cursor exceeded its bound."); return token;
    }
    public AlertLifecycleCursor Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength || token.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=')))
            throw new ArgumentException("Invalid protected lifecycle cursor.",nameof(token));
        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            if (Convert.ToBase64String(bytes) != token) throw new ArgumentException("Invalid cursor encoding.",nameof(token));
            var cursor = JsonSerializer.Deserialize<AlertLifecycleCursor>(protector.Unprotect(bytes),Json) ?? throw new ArgumentException("Invalid cursor payload.",nameof(token));
            AlertLifecycleRequestValidation.Cursor(cursor); return cursor;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException)
        { throw new ArgumentException("The lifecycle cursor is invalid or expired.",nameof(token),error); }
    }
}
