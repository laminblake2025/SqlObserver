using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Domain.Collection;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class CpuComponentWindowHistoryEndpoints
{
    private const int MaximumResponseBytes = 512 * 1024;
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private static readonly HashSet<string> ListFields = ["targetRevision", "fromUtc", "toUtc", "limit", "cursor"];
    private static readonly HashSet<string> ExactFields = ["targetRevision", "sampleTargetRevision", "fromUtc", "toUtc"];
    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web)
    { Converters = { new CanonicalUtcDateTimeOffsetConverter() } };

    public static IEndpointRouteBuilder MapCpuComponentWindowHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/resources/cpu/observations").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapGet("/{runId:guid}", ExactAsync);
        endpoints.MapGet("/api/v1/observation-targets/{instanceId:guid}/resources/cpu/collection", GetCollectionAsync).RequireAuthorization();
        endpoints.MapPut("/api/v1/observation-targets/{instanceId:guid}/resources/cpu/collection", SetCollectionAsync)
            .RequireAuthorization().RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> GetCollectionAsync(HttpContext context, Guid instanceId,
        ICpuComponentsCollectionAdministrationService service, WindowsGroupRoleResolver roles)
    {
        Begin(context);
        try
        {
            Query(context, ["targetRevision"]); RouteIds(context, "instanceId");
            long revision = Number(context, "targetRevision");
            var result = await service.GetAsync(roles.Resolve(context.User),new MonitoredInstanceId(instanceId),revision,Timeout,context.RequestAborted).ConfigureAwait(false);
            return result is null ? Results.NotFound() : result.TargetRevision != revision ? Results.Conflict(new { code = "cpu_components_collection_conflict" }) :
                Results.Ok(new { targetId = instanceId, targetRevision = result.TargetRevision, collectorEnabled = result.Enabled, scheduleRevision = result.ScheduleRevision });
        }
        catch (CpuComponentWindowHistoryReadException error) { return Failure(context,error.Code,error.Message,error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> SetCollectionAsync(HttpContext context, Guid instanceId,
        CpuComponentsCollectionBody body, ICpuComponentsCollectionAdministrationService service, WindowsGroupRoleResolver roles)
    {
        Begin(context);
        try
        {
            if (body.ExpectedEnabled is null || body.Enabled is null) return Results.BadRequest();
            CpuComponentWindowHistoryValidation.Revision(body.ExpectedTargetRevision);
            var result = await service.SetAsync(roles.Resolve(context.User), new MonitoredInstanceId(instanceId),
                body.ExpectedTargetRevision, body.ExpectedEnabled.Value, body.Enabled.Value,
                new AuditCorrelationId(Guid.Parse(context.Response.Headers["X-Correlation-ID"].ToString())), Timeout, context.RequestAborted).ConfigureAwait(false);
            return result.Status switch
            {
                CpuComponentsCollectionMutationStatus.Applied => Results.Ok(new { targetId = instanceId, targetRevision = body.ExpectedTargetRevision, collectorEnabled = result.Enabled, scheduleRevision = result.ScheduleRevision }),
                CpuComponentsCollectionMutationStatus.NotFound => Results.NotFound(),
                _ => Results.Conflict(new { code = "cpu_components_collection_conflict", message = "Server or collection state changed. Reload before trying again." }),
            };
        }
        catch (CpuComponentWindowHistoryReadException error) { return Failure(context,error.Code,error.Message,error.StatusCode); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> ListAsync(HttpContext context, Guid instanceId,
        ICpuComponentWindowHistoryService service, WindowsGroupRoleResolver roles, CpuComponentWindowHistoryCursorProtector protector)
    {
        Begin(context);
        try
        {
            Query(context, ListFields); RouteIds(context, "instanceId");
            var request = new CpuComponentWindowHistoryListRequest(instanceId, Number(context, "targetRevision"),
                Utc(Required(context, "fromUtc")), Utc(Required(context, "toUtc")), Limit(context),
                context.Request.Query.ContainsKey("cursor") ? protector.Unprotect(Required(context, "cursor")) : null, Timeout);
            var page = await service.ListAsync(roles.Resolve(context.User), request, context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure(context, "target_not_found", "The observation target was not found.", 404);
            return Bounded(context, new { page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc,
                page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.Coverage, page.Items,
                nextCursor = page.NextAnchor is null ? null : protector.Protect(page.NextAnchor) });
        }
        catch (UnauthorizedAccessException) { return Failure(context, "forbidden", "A same-target server read role is required.", 403); }
        catch (CpuComponentWindowHistoryReadException error) { return Failure(context, error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Failure(context, "invalid_request", "The CPU observation history window or continuation is invalid.", 400); }
        catch (InvalidDataException) { return ContractFailure(context); }
    }

    private static async Task<IResult> ExactAsync(HttpContext context, Guid instanceId, Guid runId,
        ICpuComponentWindowHistoryService service, WindowsGroupRoleResolver roles)
    {
        Begin(context);
        try
        {
            Query(context, ExactFields); RouteIds(context, "instanceId", "runId");
            var request = new CpuComponentWindowHistoryExactRequest(instanceId, Number(context, "targetRevision"),
                Utc(Required(context, "fromUtc")), Utc(Required(context, "toUtc")), runId, Number(context, "sampleTargetRevision"), Timeout);
            var page = await service.GetAsync(roles.Resolve(context.User), request, context.RequestAborted).ConfigureAwait(false);
            if (page is null) return Failure(context, "target_not_found", "The observation target was not found.", 404);
            return Bounded(context, new { page.TargetId, page.TargetRevision, page.FromUtc, page.ToUtc, page.CutoffUtc,
                page.ReadEpoch, page.RepositoryTimeUtc, page.State, page.Reason, page.RunId, page.SampleTargetRevision,
                page.Receipt });
        }
        catch (UnauthorizedAccessException) { return Failure(context, "forbidden", "A same-target server read role is required.", 403); }
        catch (CpuComponentWindowHistoryReadException error) { return Failure(context, error.Code, error.Message, error.StatusCode); }
        catch (ArgumentException) { return Failure(context, "invalid_request", "The CPU observation detail, source revision, window or continuation is invalid.", 400); }
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
            : Failure(context, "cpu_component_response_overflow", "The retained response is too large. Choose a smaller page or narrower window.", 422);
    }
    private static IResult ContractFailure(HttpContext context) =>
        Failure(context, "request_failed", "The request could not be completed.", 500);
    private static IResult Failure(HttpContext context, string code, string message, int status) => Results.Problem(
        detail: message, statusCode: status, title: "CPU observation history request failed",
        extensions: new Dictionary<string, object?> { ["code"] = code, ["message"] = message,
            ["correlationId"] = context.Response.Headers["X-Correlation-ID"].ToString() });
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
        CpuComponentWindowHistoryValidation.Revision(result);
        return result;
    }
    private static int Limit(HttpContext context)
    {
        if (!context.Request.Query.ContainsKey("limit")) return 25;
        long value = Number(context, "limit");
        if (value > CpuComponentWindowHistoryValidation.MaximumPageSize) throw new ArgumentException("The page limit exceeds its bound.");
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
public sealed class CpuComponentWindowHistoryCursorProtector
{
    public const int MaximumTokenLength = 4096;
    private readonly ITimeLimitedDataProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
    public CpuComponentWindowHistoryCursorProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector("SqlObserver.Resources.CpuComponent.WindowHistory.Cursor.v1").ToTimeLimitedDataProtector();
    }
    public string Protect(CpuComponentWindowHistoryCursor cursor)
    {
        CpuComponentWindowHistoryRequestValidation.Cursor(cursor);
        string token = Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(cursor, Json),
            cursor.IssuedAtUtc.Add(CpuComponentWindowHistoryValidation.CursorLifetime)));
        if (token.Length > MaximumTokenLength) throw new InvalidDataException("The protected server CPU observation history cursor exceeded its bound.");
        return token;
    }
    public CpuComponentWindowHistoryCursor Unprotect(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength ||
            token.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not ('+' or '/' or '=')))
            throw new ArgumentException("Invalid protected server CPU observation history cursor.", nameof(token));
        try
        {
            byte[] bytes = Convert.FromBase64String(token);
            if (Convert.ToBase64String(bytes) != token) throw new ArgumentException("Invalid cursor encoding.", nameof(token));
            var cursor = JsonSerializer.Deserialize<CpuComponentWindowHistoryCursor>(protector.Unprotect(bytes), Json)
                ?? throw new ArgumentException("Invalid cursor payload.", nameof(token));
            CpuComponentWindowHistoryRequestValidation.Cursor(cursor);
            return cursor;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException)
        { throw new ArgumentException("The server CPU observation history cursor is invalid or expired.", nameof(token), error); }
    }
}

public sealed record CpuComponentsCollectionBody(long ExpectedTargetRevision, bool? ExpectedEnabled, bool? Enabled);
