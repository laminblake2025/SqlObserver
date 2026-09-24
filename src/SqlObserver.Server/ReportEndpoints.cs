using System.Text.Json;
using System.Globalization;
using SqlObserver.Domain.Authorization;
using SqlObserver.Reporting;
using SqlObserver.Security;
using Npgsql;

namespace SqlObserver.Server;

/// <summary>Target-scoped bounded reports and inert exports.</summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder catalog = endpoints.MapGroup("/api/v1/reports").RequireAuthorization();
        catalog.MapGet("/catalog", CatalogAsync);
        RouteGroupBuilder target = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/reports").RequireAuthorization();
        target.MapPost("/", CreateAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        target.MapGet("/{runId:guid}", GetRunAsync);
        target.MapGet("/{runId:guid}/{section}", ReadPageAsync);
        target.MapGet("/{runId:guid}/html", HtmlAsync);
        target.MapGet("/{runId:guid}/{section}.csv", CsvAsync);
        return endpoints;
    }

    private static async Task<IResult> CatalogAsync(IReportService service, CancellationToken cancellationToken)
        => Results.Ok(await service.CatalogAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> CreateAsync(HttpContext context, Guid instanceId, ReportCreateBody? body, IReportService service, WindowsGroupRoleResolver resolver, IReportAuditPort auditPort, CancellationToken cancellationToken)
    {
        using CancellationTokenSource operation = StartOperationDeadline(cancellationToken);
        if (context.Request.ContentLength is > ReportContract.RequestBytes) return await AuditTerminalAsync(context, instanceId, null, "oversize", "failed", StatusCodes.Status413PayloadTooLarge, service, resolver, CancellationToken.None).ConfigureAwait(false);
        if (body is null || body.ReportKind is null || body.OperationId == Guid.Empty) return await AuditTerminalAsync(context, instanceId, null, "failure", "failed", StatusCodes.Status400BadRequest, service, resolver, CancellationToken.None).ConfigureAwait(false);
        try
        {
            AuthorizationContext auth;
            try { auth = resolver.Resolve(context.User); }
            catch (UnauthorizedAccessException) { return await AuditTerminalAsync(context, instanceId, null, "deny", "denied", StatusCodes.Status403Forbidden, service, resolver, CancellationToken.None, auditPort).ConfigureAwait(false); }
            ReportRun run = await service.CreateAsync(new ReportCreateRequest(instanceId, new ReportRequest(body.ReportKind, body.OperationId, body.FromUtc, body.ToUtc), auth), operation.Token).ConfigureAwait(false);
            context.Response.Headers["X-Correlation-ID"] = Guid.NewGuid().ToString("D");
            return Results.Ok(run);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ReportCapacityException) { return Results.StatusCode(StatusCodes.Status429TooManyRequests); }
        catch (ArgumentException) { return Results.BadRequest(); }
        catch (InvalidOperationException) { return Results.Conflict(); }
        catch (PostgresException exception) when (exception.SqlState == "23505") { return Results.Conflict(); }
        catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status504GatewayTimeout); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return await AuditTerminalAsync(context, instanceId, null, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _ = await AuditTerminalAsync(context, instanceId, null, "timeout", "failed", StatusCodes.Status499ClientClosedRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); throw; }
        catch (ReportLimitException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    private static async Task<IResult> GetRunAsync(HttpContext context, Guid instanceId, Guid runId, IReportService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        using CancellationTokenSource operation = StartOperationDeadline(cancellationToken);
        try { AuthorizationContext auth = resolver.Resolve(context.User); ReportRun run = await service.GetRunAsync(instanceId, runId, auth, operation.Token, false).ConfigureAwait(false); IResult response = Results.Ok(run); await service.RecordTerminalAsync(instanceId, runId, "read", "succeeded", auth, operation.Token).ConfigureAwait(false); return response; }
        catch (UnauthorizedAccessException) { return await AuditTerminalAsync(context, instanceId, runId, "deny", "denied", StatusCodes.Status403Forbidden, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (KeyNotFoundException) { return await AuditTerminalAsync(context, instanceId, runId, "read", "failed", StatusCodes.Status410Gone, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (TimeoutException) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _ = await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status499ClientClosedRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); throw; } catch { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status503ServiceUnavailable, service, resolver, CancellationToken.None).ConfigureAwait(false); }
    }

    private static async Task<IResult> ReadPageAsync(HttpContext context, Guid instanceId, Guid runId, string section, string? cursor, IReportService service, WindowsGroupRoleResolver resolver, ReportCursorProtector protector, CancellationToken cancellationToken)
    {
        using CancellationTokenSource operation = StartOperationDeadline(cancellationToken);
        try
        {
            long after = 0; AuthorizationContext auth = resolver.Resolve(context.User); ReportRun run = await service.GetRunAsync(instanceId, runId, auth, operation.Token, false).ConfigureAwait(false);
            if (cursor is not null) { ReportCursor decoded = protector.Unprotect(cursor); if (decoded.RunId != runId || decoded.TargetId != instanceId || decoded.TargetRevision != run.TargetRevision || decoded.ReportKind != run.Kind || decoded.DefinitionVersion != run.DefinitionVersion || decoded.SnapshotUtc != run.SnapshotUtc || !decoded.Section.Equals(section, StringComparison.Ordinal)) throw new ArgumentException("Cursor does not belong to this report page."); after = decoded.AfterOrdinal; }
            ReportSectionPage page = await service.ReadPageAsync(instanceId, runId, section, after, ReportContract.PageRows, auth, operation.Token, false).ConfigureAwait(false);
            IResult response = Results.Ok(new { page.Run, page.Section, page.Rows, nextCursor = page.Cursor is null ? null : protector.Protect(new ReportCursor(runId, instanceId, page.Run.TargetRevision, page.Run.Kind, page.Run.DefinitionVersion, section, long.Parse(page.Cursor, CultureInfo.InvariantCulture), page.Run.SnapshotUtc, page.Run.ExpiresAtUtc)), page.HasMore });
            await service.RecordTerminalAsync(instanceId, runId, "read", "succeeded", auth, operation.Token).ConfigureAwait(false);
            return response;
        }
        catch (UnauthorizedAccessException) { return await AuditTerminalAsync(context, instanceId, runId, "deny", "denied", StatusCodes.Status403Forbidden, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (KeyNotFoundException) { return await AuditTerminalAsync(context, instanceId, runId, "read", "failed", StatusCodes.Status410Gone, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (ReportCursorExpiredException) { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status410Gone, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (ArgumentException) { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status400BadRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (TimeoutException) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _ = await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status499ClientClosedRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); throw; } catch { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status503ServiceUnavailable, service, resolver, CancellationToken.None).ConfigureAwait(false); }
    }

    private static async Task<IResult> HtmlAsync(HttpContext context, Guid instanceId, Guid runId, IReportService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        using CancellationTokenSource operation = StartOperationDeadline(cancellationToken);
        try
        {
            AuthorizationContext auth = resolver.Resolve(context.User); ReportRun run = await service.GetRunAsync(instanceId, runId, auth, operation.Token, false).ConfigureAwait(false);
            var sections = new Dictionary<string, IReadOnlyList<ReportRow>>(StringComparer.Ordinal);
            foreach (ReportSectionDefinition section in ReportCatalog.Get(run.Kind).Sections) sections[section.Key] = await service.ReadAllAsync(instanceId, runId, section.Key, ReportContract.HtmlRows, ReportContract.ResponseBytes, auth, operation.Token, "html", false).ConfigureAwait(false);
            await service.RecordTerminalAsync(instanceId, runId, "read", "succeeded", auth, operation.Token).ConfigureAwait(false);
            byte[] bytes = ReportRenderer.RenderHtml(run, ReportCatalog.Get(run.Kind), sections);
            await service.CompleteExportAsync(instanceId, runId, ReportCatalog.Get(run.Kind).Sections[0].Key, "html", sections.Values.Sum(static rows => rows.Count), auth, operation.Token, false).ConfigureAwait(false);
            SetSafeHeaders(context, "text/html; charset=utf-8"); return Results.Bytes(bytes, "text/html; charset=utf-8");
        }
        catch (UnauthorizedAccessException) { return await AuditTerminalAsync(context, instanceId, runId, "deny", "denied", StatusCodes.Status403Forbidden, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (KeyNotFoundException) { return await AuditTerminalAsync(context, instanceId, runId, "read", "failed", StatusCodes.Status410Gone, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (ReportLimitException) { return await AuditTerminalAsync(context, instanceId, runId, "oversize", "failed", StatusCodes.Status413PayloadTooLarge, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (TimeoutException) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _ = await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status499ClientClosedRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); throw; } catch { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status503ServiceUnavailable, service, resolver, CancellationToken.None).ConfigureAwait(false); }
    }

    private static async Task<IResult> CsvAsync(HttpContext context, Guid instanceId, Guid runId, string section, IReportService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        using CancellationTokenSource operation = StartOperationDeadline(cancellationToken);
        try
        {
            AuthorizationContext auth = resolver.Resolve(context.User); ReportRun run = await service.GetRunAsync(instanceId, runId, auth, operation.Token, false).ConfigureAwait(false); ReportDefinition definition = ReportCatalog.Get(run.Kind); ReportSectionDefinition schema = definition.Sections.Single(x => x.Key == section); IReadOnlyList<ReportRow> rows = await service.ReadAllAsync(instanceId, runId, section, ReportContract.TotalRows, ReportContract.MaterializationBytes, auth, operation.Token, "csv", false).ConfigureAwait(false); await service.RecordTerminalAsync(instanceId, runId, "read", "succeeded", auth, operation.Token).ConfigureAwait(false); byte[] bytes = ReportRenderer.RenderCsv(definition, schema, rows); await service.CompleteExportAsync(instanceId, runId, section, "csv", rows.Count, auth, operation.Token, false).ConfigureAwait(false); SetSafeHeaders(context, "text/csv; charset=utf-8"); context.Response.Headers["Content-Disposition"] = $"attachment; filename=report-{runId:N}-{section}.csv"; return Results.Bytes(bytes, "text/csv; charset=utf-8");
        }
        catch (UnauthorizedAccessException) { return await AuditTerminalAsync(context, instanceId, runId, "deny", "denied", StatusCodes.Status403Forbidden, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (KeyNotFoundException) { return await AuditTerminalAsync(context, instanceId, runId, "read", "failed", StatusCodes.Status410Gone, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status404NotFound, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (ArgumentException) { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status400BadRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (ReportLimitException) { return await AuditTerminalAsync(context, instanceId, runId, "oversize", "failed", StatusCodes.Status413PayloadTooLarge, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (TimeoutException) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { return await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status504GatewayTimeout, service, resolver, CancellationToken.None).ConfigureAwait(false); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _ = await AuditTerminalAsync(context, instanceId, runId, "timeout", "failed", StatusCodes.Status499ClientClosedRequest, service, resolver, CancellationToken.None).ConfigureAwait(false); throw; } catch { return await AuditTerminalAsync(context, instanceId, runId, "failure", "failed", StatusCodes.Status503ServiceUnavailable, service, resolver, CancellationToken.None).ConfigureAwait(false); }
    }

    private static void SetSafeHeaders(HttpContext context, string contentType)
    { context.Response.ContentType = contentType; context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'"; context.Response.Headers["X-Content-Type-Options"] = "nosniff"; context.Response.Headers["Cache-Control"] = "private, no-store"; }
    private static async Task<IResult> AuditTerminalAsync(HttpContext context, Guid targetId, Guid? runId, string activityKind, string outcome, int statusCode, IReportService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken, IReportAuditPort? fallbackAudit = null)
    {
        try
        {
            AuthorizationContext authorization;
            try { authorization = resolver.Resolve(context.User); }
            catch (UnauthorizedAccessException) when (fallbackAudit is not null)
            {
                string actor = context.User.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value ?? "anonymous";
                await fallbackAudit.AppendAsync(new ReportAuditEvent(targetId, runId, actor, activityKind, outcome), cancellationToken).ConfigureAwait(false);
                return Results.StatusCode(statusCode);
            }
            await service.RecordTerminalAsync(targetId, runId, activityKind, outcome, authorization, cancellationToken).ConfigureAwait(false); return Results.StatusCode(statusCode);
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
    private static CancellationTokenSource StartOperationDeadline(CancellationToken callerToken)
    { CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken); deadline.CancelAfter(ReportContract.TotalOperationTimeout); return deadline; }
    public sealed record ReportCreateBody(string? ReportKind, Guid OperationId, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null);
}
