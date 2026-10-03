using System.Text.Json;
using System.Text.Json.Serialization;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class AlertRuleTemplateEndpoints
{
    public const int MaximumBodyBytes = 16_384;
    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false, MaxDepth = 16 };
    public sealed record ValuesBody([property: JsonRequired] string Name, [property: JsonRequired] string Kind,
        [property: JsonRequired] string? MetricId, [property: JsonRequired] string Comparison,
        [property: JsonRequired] double Threshold, [property: JsonRequired] double Hysteresis,
        [property: JsonRequired] int ConfirmationCount, [property: JsonRequired] int ConfirmationSeconds,
        [property: JsonRequired] int EvaluationSeconds, [property: JsonRequired] int ClearConfirmationCount,
        [property: JsonRequired] bool Enabled)
    {
        public AlertRuleTemplateValues ToValues() => new(Name, Kind, MetricId, Comparison, Threshold, Hysteresis,
            ConfirmationCount, ConfirmationSeconds, EvaluationSeconds, ClearConfirmationCount, Enabled);
    }
    public sealed record CreateBody([property: JsonRequired] string OperationId, [property: JsonRequired] string TemplateId,
        [property: JsonRequired] string Title, [property: JsonRequired] string SourceTemplateName,
        [property: JsonRequired] string CatalogDigest, [property: JsonRequired] ValuesBody Values, [property: JsonRequired] string Reason);
    public sealed record UpdateBody([property: JsonRequired] string OperationId, [property: JsonRequired] long ExpectedVersion,
        [property: JsonRequired] string Title, [property: JsonRequired] string SourceTemplateName,
        [property: JsonRequired] string CatalogDigest, [property: JsonRequired] ValuesBody Values, [property: JsonRequired] string Reason);
    public sealed record GroupBody([property: JsonRequired] string Kind, [property: JsonRequired] string Value);
    public sealed record PreviewBody([property: JsonRequired] string OperationId, [property: JsonRequired] string DeploymentId,
        [property: JsonRequired] string TemplateId, [property: JsonRequired] long TemplateVersion,
        [property: JsonRequired] GroupBody Group, [property: JsonRequired] string Mode, [property: JsonRequired] string Reason);
    public sealed record RollbackBody([property: JsonRequired] string OperationId, [property: JsonRequired] string DeploymentId, [property: JsonRequired] string Reason);
    public sealed record BatchBody([property: JsonRequired] string OperationId, [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] string Reason);
    public static IEndpointRouteBuilder MapAlertRuleTemplateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var templates = endpoints.MapGroup("/api/v1/alerts/rule-templates").RequireAuthorization();
        templates.MapGet("/catalog", Catalog);
        templates.MapGet("", ListTemplatesAsync); templates.MapGet("/{templateId}", GetTemplateAsync);
        templates.MapGet("/{templateId}/versions", VersionsAsync);
        templates.MapPost("", CreateAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        templates.MapPut("/{templateId}", UpdateAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        var jobs = endpoints.MapGroup("/api/v1/alerts/rule-deployments").RequireAuthorization();
        jobs.MapGet("", ListDeploymentsAsync); jobs.MapGet("/{deploymentId}", GetDeploymentAsync);
        jobs.MapGet("/{deploymentId}/history", HistoryAsync);
        jobs.MapPost("/preview", PreviewAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        jobs.MapPost("/{sourceDeploymentId}/rollback-preview", RollbackAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        jobs.MapPost("/{deploymentId}/apply", ApplyAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        jobs.MapPost("/{deploymentId}/stop", StopAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }
    private static Guid Id(string value) => AlertIdentifier.TryParseRfc4122(value, out Guid id) ? id : throw new ArgumentException("A canonical nonempty UUID is required.", nameof(value));
    private static async ValueTask<T> BodyAsync<T>(HttpContext context)
    {
        string mediaType = context.Request.ContentType?.Split(';')[0].Trim() ?? string.Empty;
        if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)) throw new BadHttpRequestException("JSON is required.", 415);
        if (context.Request.ContentLength > MaximumBodyBytes) throw new BadHttpRequestException("The command exceeds its byte limit.", 413);
        using var bytes = new MemoryStream(); byte[] buffer = new byte[4096]; int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
        { if (bytes.Length + read > MaximumBodyBytes) throw new BadHttpRequestException("The command exceeds its byte limit.", 413); await bytes.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false); }
        return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), Input) ?? throw new JsonException("A request object is required.");
    }
    private static async Task<IResult> ResultAsync(HttpContext context, Func<ValueTask<object?>> read)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { object? result = await read().ConfigureAwait(false); return result is null ? Results.NotFound() : Results.Ok(result); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (JsonException) { return Results.BadRequest(); }
        catch (BadHttpRequestException exception) { return Results.StatusCode(exception.StatusCode); }
    }
    private static IResult Catalog(HttpContext context, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { var result = service.GetCatalog(roles.Resolve(context.User)); return Results.Ok(new { result.CatalogDigest, result.ActorSid,
            result.AllTargetAdministrator, items = result.Items.Select(static entry => new { entry.Name, entry.Kind, entry.MetricId, entry.Unit,
                entry.Comparison, entry.Threshold, entry.Hysteresis, entry.ConfirmationCount, entry.ConfirmationSeconds, entry.EvaluationSeconds, entry.ClearConfirmationCount }) }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }
    private static Task<IResult> ListTemplatesAsync(HttpContext context, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles,
        int limit = 50, string? beforeTemplateId = null, CancellationToken cancellationToken = default) => ResultAsync(context,
        async () => await service.ListTemplatesAsync(roles.Resolve(context.User), limit, beforeTemplateId is null ? null : Id(beforeTemplateId), cancellationToken).ConfigureAwait(false));
    private static Task<IResult> GetTemplateAsync(HttpContext context, string templateId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles,
        long? version = null, CancellationToken cancellationToken = default) => ResultAsync(context,
        async () => await service.GetTemplateAsync(roles.Resolve(context.User), Id(templateId), version, cancellationToken).ConfigureAwait(false));
    private static Task<IResult> VersionsAsync(HttpContext context, string templateId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles,
        int limit = 50, long? beforeVersion = null, CancellationToken cancellationToken = default) => ResultAsync(context,
        async () => await service.ListVersionsAsync(roles.Resolve(context.User), Id(templateId), limit, beforeVersion, cancellationToken).ConfigureAwait(false));
    private static Task<IResult> CreateAsync(HttpContext context, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => ResultAsync(context, async () =>
    {
        var body = await BodyAsync<CreateBody>(context).ConfigureAwait(false); ArgumentNullException.ThrowIfNull(body.Values);
        return await service.WriteTemplateAsync(roles.Resolve(context.User), new(Id(body.OperationId), Id(body.TemplateId), null,
            body.Title, body.SourceTemplateName, body.CatalogDigest, body.Values.ToValues(), body.Reason), cancellationToken).ConfigureAwait(false);
    });
    private static Task<IResult> UpdateAsync(HttpContext context, string templateId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => ResultAsync(context, async () =>
    {
        var body = await BodyAsync<UpdateBody>(context).ConfigureAwait(false); ArgumentNullException.ThrowIfNull(body.Values);
        return await service.WriteTemplateAsync(roles.Resolve(context.User), new(Id(body.OperationId), Id(templateId), body.ExpectedVersion,
            body.Title, body.SourceTemplateName, body.CatalogDigest, body.Values.ToValues(), body.Reason), cancellationToken).ConfigureAwait(false);
    });
    private static Task<IResult> PreviewAsync(HttpContext context, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => ResultAsync(context, async () =>
    {
        var body = await BodyAsync<PreviewBody>(context).ConfigureAwait(false); ArgumentNullException.ThrowIfNull(body.Group);
        return await service.PreviewAsync(roles.Resolve(context.User), new(Id(body.OperationId), Id(body.DeploymentId), Id(body.TemplateId),
            body.TemplateVersion, new(body.Group.Kind, body.Group.Value), body.Mode, body.Reason), cancellationToken).ConfigureAwait(false);
    });
    private static Task<IResult> RollbackAsync(HttpContext context, string sourceDeploymentId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => ResultAsync(context, async () =>
    {
        var body = await BodyAsync<RollbackBody>(context).ConfigureAwait(false);
        return await service.RollbackPreviewAsync(roles.Resolve(context.User), new(Id(body.OperationId), Id(body.DeploymentId), Id(sourceDeploymentId), body.Reason), cancellationToken).ConfigureAwait(false);
    });
    private static Task<IResult> ListDeploymentsAsync(HttpContext context, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles,
        int limit = 50, string? beforeDeploymentId = null, CancellationToken cancellationToken = default) => ResultAsync(context,
        async () => await service.ListDeploymentsAsync(roles.Resolve(context.User), limit, beforeDeploymentId is null ? null : Id(beforeDeploymentId), cancellationToken).ConfigureAwait(false));
    private static Task<IResult> GetDeploymentAsync(HttpContext context, string deploymentId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles,
        CancellationToken cancellationToken) => ResultAsync(context, async () => await service.GetDeploymentAsync(roles.Resolve(context.User), Id(deploymentId), cancellationToken).ConfigureAwait(false));
    private static Task<IResult> HistoryAsync(HttpContext context, string deploymentId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles,
        int limit = 50, string? beforeOperationId = null, CancellationToken cancellationToken = default) => ResultAsync(context,
        async () => await service.ListHistoryAsync(roles.Resolve(context.User), Id(deploymentId), limit, beforeOperationId is null ? null : Id(beforeOperationId), cancellationToken).ConfigureAwait(false));
    private static Task<IResult> ApplyAsync(HttpContext context, string deploymentId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => BatchAsync(context, deploymentId, "apply", service, roles, cancellationToken);
    private static Task<IResult> StopAsync(HttpContext context, string deploymentId, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => BatchAsync(context, deploymentId, "stop", service, roles, cancellationToken);
    private static Task<IResult> BatchAsync(HttpContext context, string deploymentId, string action, IAlertRuleTemplateService service, WindowsGroupRoleResolver roles, CancellationToken cancellationToken) => ResultAsync(context, async () =>
    {
        var body = await BodyAsync<BatchBody>(context).ConfigureAwait(false);
        return await service.BatchAsync(roles.Resolve(context.User), new(Id(body.OperationId), Id(deploymentId), body.ExpectedRevision, action, body.Reason), cancellationToken).ConfigureAwait(false);
    });
}
