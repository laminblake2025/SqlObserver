using System.Globalization;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

public static class ObservationTargetEndpoints
{
    private const string RoutePrefix = "/api/v1/observation-targets";
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    public static IEndpointRouteBuilder MapObservationTargetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/api/v1/me", MeAsync).RequireAuthorization();

        RouteGroupBuilder group = endpoints.MapGroup(RoutePrefix).RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPost("/", RegisterAsync)
            .RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapGet("/{instanceId:guid}", GetAsync);
        group.MapPut("/{instanceId:guid}", UpdateAsync)
            .RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/{instanceId:guid}/retire", RetireAsync)
            .RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        group.MapPost("/{instanceId:guid}/rediscovery", RediscoverAsync)
            .RequireRateLimiting(AdministrativeMutationRateLimitPolicy.PolicyName);
        return endpoints;
    }

    private static IResult MeAsync(HttpContext context, WindowsGroupRoleResolver resolver, Guid? targetId = null)
    {
        if (targetId == Guid.Empty) return Results.BadRequest();
        try
        {
            AuthorizationContext authorization = resolver.Resolve(context.User);
            var target = targetId is { } id ? new MonitoredInstanceId(id) : null;
            string[] grantedRoles = authorization.IsActive
                ? authorization.Roles.Select(static role => role.ToString()).ToArray()
                : [];
            string[] allTargetRoles = authorization.Roles
                .Where(authorization.HasRoleForAllTargets)
                .Select(static role => role.ToString()).ToArray();
            string[] targetRoles = target is null
                ? []
                : authorization.Roles.Where(role => authorization.CanAccess(role, target))
                    .Select(static role => role.ToString()).ToArray();
            return Results.Ok(new { active = authorization.IsActive, grantedRoles, allTargetRoles, targetId, targetRoles });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        IObservationTargetStatusQueryService statuses,
        WindowsGroupRoleResolver authorizationResolver,
        int limit = 50,
        string? cursor = null,
        bool includeRetired = false,
        CancellationToken cancellationToken = default)
    {
        AuditCorrelationId correlationId = BeginRequest(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            ObservationTargetListCursor? decodedCursor = cursor is null
                ? null
                : ObservationTargetCursorCodec.Decode(cursor);
            ObservationTargetStatusPage page = await statuses.ListAsync(
                    new ListObservationTargetsQuery(
                        authorization,
                        limit,
                        decodedCursor,
                        includeRetired,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);

            ObservationTargetResponse[] items = page.Targets.Select(Map).ToArray();

            return Results.Ok(new ObservationTargetPageResponse(
                items,
                page.NextCursor is null ? null : ObservationTargetCursorCodec.Encode(page.NextCursor)));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbidden(correlationId);
        }
        catch (ArgumentException)
        {
            return InvalidRequest(correlationId);
        }
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext httpContext,
        RegisterObservationTargetBody body,
        IObservationTargetOnboardingService onboarding,
        IObservationTargetStatusQueryService statuses,
        WindowsGroupRoleResolver authorizationResolver,
        CancellationToken cancellationToken)
    {
        AuditCorrelationId correlationId = BeginRequest(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            var targetId = new MonitoredInstanceId(body.InstanceId);
            ObservationTargetRegistration registration = CreateRegistration(
                targetId,
                body.InstanceKey,
                body.DisplayName,
                body.Host,
                body.NamedInstance,
                body.TcpPort,
                body.CertificateHostName);
            ObservationTargetOnboardingResult result = await onboarding.OnboardAsync(
                    new OnboardObservationTargetCommand(
                        authorization,
                        registration,
                        correlationId,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Status switch
            {
                ObservationTargetOnboardingStatus.RegisteredPendingDiscovery => Results.Created(
                    $"{RoutePrefix}/{result.Target!.TargetId.Value:D}",
                    Map(new ObservationTargetStatusSnapshot(result.Target, null))),
                ObservationTargetOnboardingStatus.AlreadyExists => await MapReplayAsync(
                        result,
                        targetId,
                        authorization,
                        statuses,
                        cancellationToken)
                    .ConfigureAwait(false),
                ObservationTargetOnboardingStatus.Conflict => Conflict(correlationId),
                ObservationTargetOnboardingStatus.Denied => Forbidden(correlationId),
                _ => throw new InvalidOperationException("The onboarding service returned an unknown status."),
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Forbidden(correlationId);
        }
        catch (ArgumentException)
        {
            return InvalidRequest(correlationId);
        }
    }

    private static async ValueTask<IResult> MapReplayAsync(
        ObservationTargetOnboardingResult onboardingResult,
        MonitoredInstanceId requestedTargetId,
        AuthorizationContext authorization,
        IObservationTargetStatusQueryService statuses,
        CancellationToken cancellationToken)
    {
        ObservationTarget replayedTarget = onboardingResult.Target ??
            throw new InvalidOperationException("An exact registration replay has no target snapshot.");
        if (replayedTarget.TargetId != requestedTargetId)
        {
            throw new InvalidOperationException("An exact registration replay returned a different target.");
        }

        ObservationTargetStatusSnapshot? authoritativeStatus = await statuses.GetAsync(
                new GetObservationTargetStatusQuery(
                    authorization,
                    requestedTargetId,
                    RepositoryTimeout),
                cancellationToken)
            .ConfigureAwait(false);
        if (authoritativeStatus is null ||
            authoritativeStatus.Target.TargetId != requestedTargetId)
        {
            throw new InvalidOperationException(
                "The authoritative status for an exact registration replay is unavailable or mismatched.");
        }

        return Results.Ok(Map(authoritativeStatus));
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        Guid instanceId,
        IObservationTargetStatusQueryService statuses,
        WindowsGroupRoleResolver authorizationResolver,
        CancellationToken cancellationToken)
    {
        AuditCorrelationId correlationId = BeginRequest(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            ObservationTargetStatusSnapshot? result = await statuses.GetAsync(
                    new GetObservationTargetStatusQuery(
                        authorization,
                        new MonitoredInstanceId(instanceId),
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            return result is null ? NotFound(correlationId) : Results.Ok(Map(result));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbidden(correlationId);
        }
        catch (ArgumentException)
        {
            return InvalidRequest(correlationId);
        }
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext httpContext,
        Guid instanceId,
        UpdateObservationTargetBody body,
        IObservationTargetManagementService management,
        WindowsGroupRoleResolver authorizationResolver,
        CancellationToken cancellationToken)
    {
        AuditCorrelationId correlationId = BeginRequest(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            var targetId = new MonitoredInstanceId(instanceId);
            SqlServerConnectionPolicy connectionPolicy = CreateConnectionPolicy(
                body.Host,
                body.NamedInstance,
                body.TcpPort,
                body.CertificateHostName);
            ObservationTargetManagementResult result = await management.UpdateAsync(
                    new UpdateObservationTargetCommand(
                        authorization,
                        targetId,
                        new ObservationTargetRevision(body.ExpectedRevision),
                        new ObservationTargetDisplayName(body.DisplayName),
                        connectionPolicy,
                        correlationId,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            return MapManagementResult(result, correlationId);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbidden(correlationId);
        }
        catch (ArgumentException)
        {
            return InvalidRequest(correlationId);
        }
    }

    private static async Task<IResult> RetireAsync(
        HttpContext httpContext,
        Guid instanceId,
        TargetRevisionBody body,
        IObservationTargetManagementService management,
        WindowsGroupRoleResolver authorizationResolver,
        CancellationToken cancellationToken)
    {
        AuditCorrelationId correlationId = BeginRequest(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            ObservationTargetManagementResult result = await management.RetireAsync(
                    new RetireObservationTargetCommand(
                        authorization,
                        new MonitoredInstanceId(instanceId),
                        new ObservationTargetRevision(body.ExpectedRevision),
                        correlationId,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            return MapManagementResult(result, correlationId);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbidden(correlationId);
        }
        catch (ArgumentException)
        {
            return InvalidRequest(correlationId);
        }
    }

    private static async Task<IResult> RediscoverAsync(
        HttpContext httpContext,
        Guid instanceId,
        TargetRevisionBody body,
        IObservationTargetManagementService management,
        WindowsGroupRoleResolver authorizationResolver,
        CancellationToken cancellationToken)
    {
        AuditCorrelationId correlationId = BeginRequest(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            ObservationTargetManagementResult result = await management.RequestRediscoveryAsync(
                    new RequestCapabilityRediscoveryCommand(
                        authorization,
                        new MonitoredInstanceId(instanceId),
                        new ObservationTargetRevision(body.ExpectedRevision),
                        correlationId,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            return MapManagementResult(result, correlationId);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbidden(correlationId);
        }
        catch (ArgumentException)
        {
            return InvalidRequest(correlationId);
        }
    }

    private static ObservationTargetRegistration CreateRegistration(
        MonitoredInstanceId targetId,
        string instanceKey,
        string displayName,
        string host,
        string? namedInstance,
        int? tcpPort,
        string? certificateHostName)
    {
        return new ObservationTargetRegistration(
            targetId,
            new ObservationTargetKey(instanceKey),
            new ObservationTargetDisplayName(displayName),
            CreateConnectionPolicy(host, namedInstance, tcpPort, certificateHostName));
    }

    private static SqlServerConnectionPolicy CreateConnectionPolicy(
        string host,
        string? namedInstance,
        int? tcpPort,
        string? certificateHostName)
    {
        var endpoint = new SqlServerEndpoint(
            new SqlServerHostName(host),
            namedInstance is null ? null : new SqlServerInstanceName(namedInstance),
            tcpPort);
        return new SqlServerConnectionPolicy(
            endpoint,
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)),
            certificateHostName is null ? null : new SqlServerCertificateHostName(certificateHostName));
    }

    private static IResult MapManagementResult(
        ObservationTargetManagementResult result,
        AuditCorrelationId correlationId)
    {
        return result.Status switch
        {
            ObservationTargetManagementStatus.Applied => Results.Ok(
                Map(new ObservationTargetStatusSnapshot(result.Target!, null))),
            ObservationTargetManagementStatus.AlreadyRetired => Results.Ok(
                Map(new ObservationTargetStatusSnapshot(result.Target!, null))),
            ObservationTargetManagementStatus.NotFound => NotFound(correlationId),
            ObservationTargetManagementStatus.RevisionConflict => Conflict(correlationId),
            ObservationTargetManagementStatus.Denied => Forbidden(correlationId),
            _ => throw new InvalidOperationException("The target-management service returned an unknown status."),
        };
    }

    private static ObservationTargetResponse Map(ObservationTargetStatusSnapshot snapshot)
    {
        ObservationTarget target = snapshot.Target;
        CapabilityProfile? profile = snapshot.CapabilityProfileIsCurrent
            ? snapshot.LatestCapabilityProfile
            : null;
        string lifecycle = target.Lifecycle switch
        {
            ObservationTargetLifecycle.PendingDiscovery => "pending_discovery",
            ObservationTargetLifecycle.Active => "active",
            ObservationTargetLifecycle.Disabled => "disabled",
            ObservationTargetLifecycle.Retired => "retired",
            _ => throw new InvalidOperationException("The target has an unknown lifecycle."),
        };
        string capabilityStatus = target.Lifecycle switch
        {
            ObservationTargetLifecycle.Disabled => "disabled",
            ObservationTargetLifecycle.Retired => "retired",
            _ when profile is null => "pending",
            _ => ToApiValue(profile.Outcome),
        };
        IReadOnlyList<string> reasons = target.Lifecycle switch
        {
            ObservationTargetLifecycle.Disabled or ObservationTargetLifecycle.Retired => [],
            _ when profile is null =>
                [snapshot.LatestCapabilityProfile is null ? "discovery_pending" : "rediscovery_pending"],
            _ => [ToApiValue(profile.Reason)],
        };

        return new ObservationTargetResponse(
            target.TargetId.Value,
            target.Key.Value,
            target.DisplayName.Value,
            target.ConnectionPolicy.Endpoint.HostName.Value,
            target.ConnectionPolicy.Endpoint.InstanceName?.Value,
            target.ConnectionPolicy.Endpoint.TcpPort,
            target.ConnectionPolicy.CertificateHostName?.Value,
            "windows_integrated_service_identity",
            "mandatory_validated",
            lifecycle,
            capabilityStatus,
            reasons,
            target.Revision.Value,
            target.DiscoveryRequestedAtUtc,
            profile?.CheckedAtUtc);
    }

    private static string ToApiValue<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        var result = new StringBuilder(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char character = name[index];
            if (char.IsUpper(character) && index > 0)
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }

    private static AuditCorrelationId BeginRequest(HttpContext context)
    {
        return ApiCorrelation.Begin(context);
    }

    private static IResult InvalidRequest(AuditCorrelationId correlationId) => Results.Json(
        new SqlObserverProblemResponse(
            "invalid_request",
            "The request is invalid.",
            correlationId.Value.ToString("D", CultureInfo.InvariantCulture)),
        statusCode: StatusCodes.Status400BadRequest);

    private static IResult Forbidden(AuditCorrelationId correlationId) => Results.Json(
        new SqlObserverProblemResponse(
            "forbidden",
            "The principal is not authorized for this operation.",
            correlationId.Value.ToString("D", CultureInfo.InvariantCulture)),
        statusCode: StatusCodes.Status403Forbidden);

    private static IResult NotFound(AuditCorrelationId correlationId) => Results.Json(
        new SqlObserverProblemResponse(
            "not_found",
            "The observation target was not found.",
            correlationId.Value.ToString("D", CultureInfo.InvariantCulture)),
        statusCode: StatusCodes.Status404NotFound);

    private static IResult Conflict(AuditCorrelationId correlationId) => Results.Json(
        new SqlObserverProblemResponse(
            "conflict",
            "The observation target changed or conflicts with an existing target.",
            correlationId.Value.ToString("D", CultureInfo.InvariantCulture)),
        statusCode: StatusCodes.Status409Conflict);
}

internal static class ObservationTargetCursorCodec
{
    private const int MaximumEncodedLength = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string Encode(ObservationTargetListCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        string value = string.Concat(
            cursor.LastKey.Value,
            "\n",
            cursor.LastTargetId.Value.ToString("D", CultureInfo.InvariantCulture));
        return Convert.ToBase64String(StrictUtf8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static ObservationTargetListCursor Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        if (encoded.Length > MaximumEncodedLength ||
            encoded.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("The continuation cursor is invalid.", nameof(encoded));
        }

        string base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            string value = StrictUtf8.GetString(Convert.FromBase64String(base64));
            string[] components = value.Split('\n', StringSplitOptions.None);
            if (components.Length != 2 || !Guid.TryParseExact(components[1], "D", out Guid targetId))
            {
                throw new ArgumentException("The continuation cursor is invalid.", nameof(encoded));
            }

            return new ObservationTargetListCursor(
                new ObservationTargetKey(components[0]),
                new MonitoredInstanceId(targetId));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("The continuation cursor is invalid.", nameof(encoded));
        }
    }
}
