using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class AlertEndpoints
{
    public static IEndpointRouteBuilder MapAlertEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/v1/alerts").RequireAuthorization()
            .MapGet("/active", GetFleetActiveAsync);
        var group = endpoints.MapGroup("/api/v1/observation-targets/{instanceId:guid}/alerts").RequireAuthorization();
        group.MapGet("/active", GetActiveAsync);
        group.MapPost("/{alertId:guid}/acknowledge", AcknowledgeAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/rules", CreateRuleAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPut("/rules/{ruleId:guid}", UpdateRuleAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/rules/{ruleId:guid}/disable", DisableRuleAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/maintenance", CreateMaintenanceAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPut("/maintenance/{windowId:guid}", UpdateMaintenanceAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/maintenance/{windowId:guid}/cancel", CancelMaintenanceAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/destinations", ConfigureDestinationAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPut("/destinations/{destinationId:guid}", UpdateDestinationAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/destinations/{destinationId:guid}/approve", ApproveDestinationAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/destinations/{destinationId:guid}/disable", DisableDestinationAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/delivery/{deliveryId:guid}/cancel", CancelDeliveryAsync).RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static async Task<IResult> GetActiveAsync(HttpContext context, Guid instanceId, IAlertQueryService service, WindowsGroupRoleResolver resolver, int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 100) return Results.BadRequest();
        try
        {
            AlertActiveCursor? after = DecodeCursor(cursor, instanceId);
            AlertActivePage result = await service.ListActivePageAsync(resolver.Resolve(context.User), new MonitoredInstanceId(instanceId), limit, after, cancellationToken).ConfigureAwait(false);
            string? nextCursor = result.NextCursor is null ? null : EncodeCursor(result.NextCursor);
            return Results.Ok(new { targetId = instanceId, snapshotUtc = result.SnapshotUtc, items = result.Items.Select(static row => new { alertId = row.AlertId, ruleId = row.RuleId, ruleName = row.RuleName, state = row.State.ToString().ToLowerInvariant(), firstObservedUtc = row.FirstObservedUtc, firedUtc = row.FiredUtc, acknowledgedUtc = row.AcknowledgedUtc, value = row.Value, reason = row.Reason, deliverySuppressed = row.DeliverySuppressed }), nextCursor });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> GetFleetActiveAsync(HttpContext context, IAlertQueryService service, WindowsGroupRoleResolver resolver, int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (limit is <= 0 or > 100) return Results.BadRequest();
        try
        {
            FleetAlertCursor? after = DecodeFleetCursor(cursor);
            FleetAlertPage page = await service.ListFleetActivePageAsync(resolver.Resolve(context.User), limit, after, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                snapshotUtc = page.SnapshotUtc,
                items = page.Items.Select(static item => new
                {
                    targetId = item.Alert.TargetId.Value, targetName = item.TargetName,
                    alertId = item.Alert.AlertId, ruleId = item.Alert.RuleId,
                    ruleName = item.Alert.RuleName,
                    state = item.Alert.State.ToString().ToLowerInvariant(),
                    firstObservedUtc = item.Alert.FirstObservedUtc,
                    firedUtc = item.Alert.FiredUtc,
                    acknowledgedUtc = item.Alert.AcknowledgedUtc,
                    value = item.Alert.Value, reason = item.Alert.Reason,
                    deliverySuppressed = item.Alert.DeliverySuppressed,
                }),
                nextCursor = page.NextCursor is null ? null : EncodeFleetCursor(page.NextCursor),
            });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static string EncodeFleetCursor(FleetAlertCursor cursor) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{cursor.SnapshotUtc:O}|{cursor.SortAtUtc:O}|{cursor.TargetId:D}|{cursor.AlertId:D}"));

    private static FleetAlertCursor? DecodeFleetCursor(string? value)
    {
        if (value is null) return null;
        if (value.Length is 0 or > 1024) throw new ArgumentException("Cursor is outside its bounds.");
        string[] parts;
        try { parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('|'); }
        catch (FormatException exception) { throw new ArgumentException("Cursor is invalid.", exception); }
        if (parts.Length != 4 || !TryCanonicalUtc(parts[0], out DateTimeOffset snapshot) ||
            !TryCanonicalUtc(parts[1], out DateTimeOffset sortAt) || sortAt > snapshot ||
            !AlertIdentifier.TryParseRfc4122(parts[2], out Guid target) ||
            !AlertIdentifier.TryParseRfc4122(parts[3], out Guid alert))
            throw new ArgumentException("Cursor binding is invalid.");
        return new FleetAlertCursor(sortAt, target, alert, snapshot);
    }

    private static string EncodeCursor(AlertActiveCursor cursor) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{cursor.TargetId.Value:D}|{cursor.SnapshotUtc:O}|{cursor.SortAtUtc:O}|{cursor.AlertId:D}"));
    private static AlertActiveCursor? DecodeCursor(string? value, Guid targetId)
    {
        if (value is null) return null;
        if (value.Length is 0 or > 1024) throw new ArgumentException("Cursor is outside its bounds.");
        string[] parts; try { parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('|'); } catch (FormatException ex) { throw new ArgumentException("Cursor is invalid.", ex); }
        if (parts.Length != 4 || !AlertIdentifier.TryParseRfc4122(parts[0], out Guid cursorTarget) || cursorTarget != targetId || !TryCanonicalUtc(parts[1], out DateTimeOffset snapshot) || !TryCanonicalUtc(parts[2], out DateTimeOffset timestamp) || !AlertIdentifier.TryParseRfc4122(parts[3], out Guid alertId)) throw new ArgumentException("Cursor binding is invalid.");
        return new AlertActiveCursor(new MonitoredInstanceId(targetId), timestamp, alertId, snapshot);
    }

    private sealed record AcknowledgeBody(string OperationId, string? CorrelationId, long? ExpectedRevision = null, string? RequestDigest = null, Guid? ExpectedEpisodeId = null);
    private sealed record RuleBody(string RuleId, string Name, string Kind, string? MetricId, string Comparison, double Threshold, double Hysteresis, int ConfirmationCount, int ConfirmationSeconds, int EvaluationSeconds, bool Enabled, string OperationId, string? CorrelationId, long? ExpectedRevision = null, string? RequestDigest = null, int? ClearConfirmationCount = null);
    private sealed record MaintenanceBody(string WindowId, string StartsAtUtc, string EndsAtUtc, string Reason, string OperationId, string? CorrelationId, long? ExpectedRevision = null, string? RequestDigest = null);
    private sealed record MaintenanceCancelBody(string OperationId, string? CorrelationId, long? ExpectedRevision = null, string? RequestDigest = null);
    private sealed record DestinationBody(string DestinationId, string Kind, string ConfigurationReference, bool Enabled, string OperationId, string? CorrelationId, long? ExpectedRevision = null, string? RequestDigest = null);
    private sealed record DeliveryCancelBody(string OperationId, string Reason, string? CorrelationId, string? RequestDigest = null);

    private static bool TryUtc(string value, out DateTimeOffset timestamp) => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out timestamp) && timestamp.Offset == TimeSpan.Zero && timestamp.Ticks % TimeSpan.TicksPerMicrosecond == 0;
    private static bool TryCanonicalUtc(string value, out DateTimeOffset timestamp) => DateTimeOffset.TryParseExact(value, "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out timestamp) && timestamp.Offset == TimeSpan.Zero && timestamp.ToString("O", System.Globalization.CultureInfo.InvariantCulture) == value;
    private static Guid ParseOptionalCorrelation(string? value) => value is null ? Guid.NewGuid() : AlertIdentifier.TryParseRfc4122(value, out Guid parsed) ? parsed : throw new ArgumentException("CorrelationId must be a canonical RFC4122 UUID.", nameof(value));
    private static SqlObserver.Domain.Auditing.AdministrativeAuditEnvelope Audit(AuthorizationContext authorization, Guid correlation, SqlObserver.Domain.Auditing.AdministrativeAuditAction action, MonitoredInstanceId target) => new(authorization.ActorSid, new SqlObserver.Domain.Auditing.AuditCorrelationId(correlation), action, target);

    private static Task<IResult> CreateRuleAsync(HttpContext context, Guid instanceId, RuleBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => WriteRuleAsync(context, instanceId, body, AdministrativeAuditAction.CreateAlertRule, service, resolver, cancellationToken);
    private static Task<IResult> UpdateRuleAsync(HttpContext context, Guid instanceId, Guid ruleId, RuleBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => ruleId.ToString("D").Equals(body.RuleId, StringComparison.OrdinalIgnoreCase) ? WriteRuleAsync(context, instanceId, body, AdministrativeAuditAction.UpdateAlertRule, service, resolver, cancellationToken) : Task.FromResult<IResult>(Results.BadRequest());
    private static Task<IResult> DisableRuleAsync(HttpContext context, Guid instanceId, Guid ruleId, RuleBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => ruleId.ToString("D").Equals(body.RuleId, StringComparison.OrdinalIgnoreCase) ? WriteRuleAsync(context, instanceId, body with { Enabled = false }, AdministrativeAuditAction.RetireAlertRule, service, resolver, cancellationToken) : Task.FromResult<IResult>(Results.BadRequest());
    private static async Task<IResult> WriteRuleAsync(HttpContext context, Guid instanceId, RuleBody body, AdministrativeAuditAction action, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        bool create = action == AdministrativeAuditAction.CreateAlertRule;
        if (!AlertIdentifier.TryParseRfc4122(body.RuleId, out Guid ruleId) || !AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId) || create && body.ExpectedRevision is not null || !create && body.ExpectedRevision is null || !create && body.ClearConfirmationCount is null || body.ClearConfirmationCount is < 1 or > 100 || body.ConfirmationCount is < 1 or > 100 || body.EvaluationSeconds is < 1 or > 3600 || body.ConfirmationSeconds is < 0 or > 604800) return Results.BadRequest();
        var auth = resolver.Resolve(context.User); var target = new MonitoredInstanceId(instanceId);
        try { var kind = body.Kind == "metric_threshold" ? AlertRuleKind.MetricThreshold : body.Kind == "collector_health" ? AlertRuleKind.CollectorHealth : throw new ArgumentException("Rule kind is not allowlisted.", nameof(body)); var comparison = body.Comparison switch { "greater_than" => AlertComparison.GreaterThan, "greater_than_or_equal" => AlertComparison.GreaterThanOrEqual, "less_than" => AlertComparison.LessThan, "less_than_or_equal" => AlertComparison.LessThanOrEqual, "equal" => AlertComparison.Equal, _ => throw new ArgumentException("Comparison is not allowlisted.", nameof(body)) }; var rule = new AlertRuleDefinition(ruleId, body.Name, kind, body.MetricId is null ? null : new MetricId(body.MetricId), comparison, body.Threshold, body.Hysteresis, body.ConfirmationCount, TimeSpan.FromSeconds(body.ConfirmationSeconds), TimeSpan.FromSeconds(body.EvaluationSeconds), body.Enabled, clearConfirmationCount: body.ClearConfirmationCount ?? 2); var correlation = ParseOptionalCorrelation(body.CorrelationId); var receipt = await service.UpsertRuleAsync(auth, new AlertRuleWriteRequest(rule, body.OperationId, Audit(auth, correlation, action, target), new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), body.ExpectedRevision, body.RequestDigest), cancellationToken); return Results.Ok(new { auditId = receipt.AuditId.Value, recordedAtUtc = receipt.RecordedAtUtc }); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static Task<IResult> CreateMaintenanceAsync(HttpContext context, Guid instanceId, MaintenanceBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => WriteMaintenanceAsync(context, instanceId, body, AdministrativeAuditAction.CreateMaintenanceWindow, service, resolver, cancellationToken);
    private static Task<IResult> UpdateMaintenanceAsync(HttpContext context, Guid instanceId, Guid windowId, MaintenanceBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => windowId.ToString("D").Equals(body.WindowId, StringComparison.OrdinalIgnoreCase) ? WriteMaintenanceAsync(context, instanceId, body, AdministrativeAuditAction.UpdateMaintenanceWindow, service, resolver, cancellationToken) : Task.FromResult<IResult>(Results.BadRequest());
    private static async Task<IResult> WriteMaintenanceAsync(HttpContext context, Guid instanceId, MaintenanceBody body, AdministrativeAuditAction action, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        if (!AlertIdentifier.TryParseRfc4122(body.WindowId, out Guid windowId) || !AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId) || action == AdministrativeAuditAction.CreateMaintenanceWindow && body.ExpectedRevision is not null || action != AdministrativeAuditAction.CreateMaintenanceWindow && body.ExpectedRevision is null || !TryUtc(body.StartsAtUtc, out DateTimeOffset starts) || !TryUtc(body.EndsAtUtc, out DateTimeOffset ends)) return Results.BadRequest();
        var auth = resolver.Resolve(context.User); var target = new MonitoredInstanceId(instanceId); try { var window = new MaintenanceWindow(windowId, target, starts, ends, body.Reason); var correlation = ParseOptionalCorrelation(body.CorrelationId); var receipt = await service.UpsertMaintenanceAsync(auth, new MaintenanceWriteRequest(window, body.OperationId, Audit(auth, correlation, action, target), new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), body.ExpectedRevision, body.RequestDigest), cancellationToken); return Results.Ok(new { auditId = receipt.AuditId.Value, recordedAtUtc = receipt.RecordedAtUtc }); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static Task<IResult> ConfigureDestinationAsync(HttpContext context, Guid instanceId, DestinationBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => WriteDestinationAsync(context, instanceId, body, AdministrativeAuditAction.ConfigureAlertDestination, service, resolver, cancellationToken);
    private static Task<IResult> UpdateDestinationAsync(HttpContext context, Guid instanceId, Guid destinationId, DestinationBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => destinationId.ToString("D").Equals(body.DestinationId, StringComparison.Ordinal) ? WriteDestinationAsync(context, instanceId, body, AdministrativeAuditAction.UpdateAlertDestination, service, resolver, cancellationToken, false) : Task.FromResult<IResult>(Results.BadRequest());
    private static Task<IResult> ApproveDestinationAsync(HttpContext context, Guid instanceId, Guid destinationId, DestinationBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => destinationId.ToString("D").Equals(body.DestinationId, StringComparison.OrdinalIgnoreCase) ? WriteDestinationAsync(context, instanceId, body, AdministrativeAuditAction.ApproveAlertDestination, service, resolver, cancellationToken, true) : Task.FromResult<IResult>(Results.BadRequest());
    private static Task<IResult> DisableDestinationAsync(HttpContext context, Guid instanceId, Guid destinationId, DestinationBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken) => destinationId.ToString("D").Equals(body.DestinationId, StringComparison.OrdinalIgnoreCase) ? WriteDestinationAsync(context, instanceId, body with { Enabled = false }, AdministrativeAuditAction.RetireAlertDestination, service, resolver, cancellationToken, false) : Task.FromResult<IResult>(Results.BadRequest());
    private static async Task<IResult> WriteDestinationAsync(HttpContext context, Guid instanceId, DestinationBody body, AdministrativeAuditAction action, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken, bool approve = false)
    {
        bool create = action == AdministrativeAuditAction.ConfigureAlertDestination;
        if (!AlertIdentifier.TryParseRfc4122(body.DestinationId, out Guid destinationId) || !AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId) || (create ? body.ExpectedRevision is not null : body.ExpectedRevision is null or <= 0)) return Results.BadRequest(); var auth = resolver.Resolve(context.User); var target = new MonitoredInstanceId(instanceId); try { var correlation = ParseOptionalCorrelation(body.CorrelationId); var request = AlertDestinationWriteRequest.Create(destinationId, body.Kind, body.ConfigurationReference, body.Enabled, body.OperationId, Audit(auth, correlation, action, target), new RepositoryCallTimeout(TimeSpan.FromSeconds(5))) with { ExpectedRevision = body.ExpectedRevision, RequestDigest = body.RequestDigest, Approve = approve }; var receipt = await service.UpsertDestinationAsync(auth, request, cancellationToken); return Results.Ok(new { auditId = receipt.AuditId.Value, recordedAtUtc = receipt.RecordedAtUtc }); } catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (AlertDestinationValidationException exception) { return exception.StatusCode == StatusCodes.Status503ServiceUnavailable ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.BadRequest(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> CancelMaintenanceAsync(HttpContext context, Guid instanceId, Guid windowId, MaintenanceCancelBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        if (!AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId)) return Results.BadRequest();
        var auth = resolver.Resolve(context.User); var target = new MonitoredInstanceId(instanceId);
        try { var correlation = ParseOptionalCorrelation(body.CorrelationId); var request = new MaintenanceCancellationRequest(windowId, body.OperationId, Audit(auth, correlation, AdministrativeAuditAction.RetireMaintenanceWindow, target), new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), body.ExpectedRevision, body.RequestDigest); var receipt = await service.CancelMaintenanceAsync(auth, request, cancellationToken); return Results.Ok(new { operationId, auditId = receipt.AuditId.Value, recordedAtUtc = receipt.RecordedAtUtc }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); } catch (ArgumentException) { return Results.BadRequest(); }
    }
    private static async Task<IResult> AcknowledgeAsync(HttpContext context, Guid instanceId, Guid alertId, AcknowledgeBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        if (!AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId) || body.CorrelationId is not null && !AlertIdentifier.TryParseRfc4122(body.CorrelationId, out _)) return Results.BadRequest();
        var authorization = resolver.Resolve(context.User);
        try
        {
            var target = new MonitoredInstanceId(instanceId);
            var audit = new SqlObserver.Domain.Auditing.AdministrativeAuditEnvelope(authorization.ActorSid, new SqlObserver.Domain.Auditing.AuditCorrelationId(ParseOptionalCorrelation(body.CorrelationId)), SqlObserver.Domain.Auditing.AdministrativeAuditAction.AcknowledgeAlert, target);
            var receipt = await service.AcknowledgeAsync(authorization, new AlertAcknowledgeRequest(alertId, body.OperationId, audit, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), body.ExpectedRevision, body.RequestDigest, body.ExpectedEpisodeId), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { operationId = body.OperationId, auditId = receipt.AuditId.Value, recordedAtUtc = receipt.RecordedAtUtc });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }

    private static async Task<IResult> CancelDeliveryAsync(HttpContext context, Guid instanceId, Guid deliveryId, DeliveryCancelBody body, IAlertAdministrationService service, WindowsGroupRoleResolver resolver, CancellationToken cancellationToken)
    {
        if (!AlertIdentifier.TryParseRfc4122(body.OperationId, out Guid operationId) || string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 64) return Results.BadRequest();
        var authorization = resolver.Resolve(context.User);
        try
        {
            var target = new MonitoredInstanceId(instanceId);
            var correlation = ParseOptionalCorrelation(body.CorrelationId);
            var audit = new AdministrativeAuditEnvelope(authorization.ActorSid, new AuditCorrelationId(correlation), AdministrativeAuditAction.CancelAlertDelivery, target);
            var request = new AlertDeliveryAdminCancellationRequest(deliveryId, body.Reason, body.OperationId, audit, new RepositoryCallTimeout(TimeSpan.FromSeconds(5)), body.RequestDigest);
            var receipt = await service.CancelDeliveryAsync(authorization, request, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { operationId, auditId = receipt.AuditId.Value, recordedAtUtc = receipt.RecordedAtUtc });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (ArgumentException) { return Results.BadRequest(); }
    }
}
