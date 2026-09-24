using System.Globalization;
using System.Text;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

/// <summary>Maps bounded, repository-backed health evidence for one authorized target.</summary>
public static class TargetHealthEndpoints
{
    private const string RoutePrefix = "/api/v1/observation-targets/{instanceId:guid}/health";
    private const int DefaultPageSize = 25;
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    public static IEndpointRouteBuilder MapTargetHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup(RoutePrefix).RequireAuthorization();
        group.MapGet("/", GetAsync);
        group.MapGet("/databases", ListDatabasesAsync);
        group.MapGet("/files", ListDatabaseFilesAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        Guid instanceId,
        IHealthProjectionQueryService health,
        WindowsGroupRoleResolver authorizationResolver,
        CancellationToken cancellationToken)
    {
        AuditCorrelationId correlationId = ApiCorrelation.Begin(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            var targetId = new MonitoredInstanceId(instanceId);
            InstanceHealthProjection? projection = await health.GetInstanceAsync(
                    new GetInstanceHealthQuery(authorization, targetId, RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);

            return projection is null
                ? NotFound(correlationId)
                : Results.Ok(Map(projection));
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

    private static async Task<IResult> ListDatabasesAsync(
        HttpContext httpContext,
        Guid instanceId,
        IHealthProjectionQueryService health,
        WindowsGroupRoleResolver authorizationResolver,
        int limit = DefaultPageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        AuditCorrelationId correlationId = ApiCorrelation.Begin(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            var targetId = new MonitoredInstanceId(instanceId);
            DatabaseHealthCursor? decodedCursor = cursor is null
                ? null
                : HealthCursorCodec.DecodeDatabase(cursor);
            DatabaseHealthPage? page = await health.ListDatabasesAsync(
                    new ListDatabaseHealthQuery(
                        authorization,
                        targetId,
                        limit,
                        decodedCursor,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (page is null)
            {
                return NotFound(correlationId);
            }

            Validate(page, limit, decodedCursor);
            return Results.Ok(new DatabaseHealthPageResponse(
                page.TargetId.Value,
                page.RepositoryTimeUtc,
                Map(page.Collector),
                page.Items.Select(Map).ToArray(),
                page.NextCursor is null ? null : HealthCursorCodec.Encode(page.NextCursor)));
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

    private static async Task<IResult> ListDatabaseFilesAsync(
        HttpContext httpContext,
        Guid instanceId,
        IHealthProjectionQueryService health,
        WindowsGroupRoleResolver authorizationResolver,
        int limit = DefaultPageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        AuditCorrelationId correlationId = ApiCorrelation.Begin(httpContext);
        try
        {
            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            var targetId = new MonitoredInstanceId(instanceId);
            DatabaseFileHealthCursor? decodedCursor = cursor is null
                ? null
                : HealthCursorCodec.DecodeDatabaseFile(cursor);
            DatabaseFileHealthPage? page = await health.ListDatabaseFilesAsync(
                    new ListDatabaseFileHealthQuery(
                        authorization,
                        targetId,
                        limit,
                        decodedCursor,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (page is null)
            {
                return NotFound(correlationId);
            }

            Validate(page, limit, decodedCursor);
            return Results.Ok(new DatabaseFileHealthPageResponse(
                page.TargetId.Value,
                page.RepositoryTimeUtc,
                Map(page.Collector),
                page.Items.Select(Map).ToArray(),
                page.NextCursor is null ? null : HealthCursorCodec.Encode(page.NextCursor)));
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

    private static TargetHealthResponse Map(InstanceHealthProjection projection)
    {
        CollectorHealthProjection collector = projection.CoreCollector;
        return new TargetHealthResponse(
            projection.TargetId.Value,
            MapState(collector.State),
            projection.RepositoryTimeUtc,
            [Map(collector)],
            projection.CoreMetrics.Select(Map).ToArray());
    }

    private static CoreMetricResponse Map(MetricSample metric) => new(
        metric.SampleId.Value,
        metric.MetricId.Value,
        metric.ObservedAtUtc,
        metric.Value,
        metric.Dimensions
            .Select(static dimension => new MetricDimensionResponse(dimension.Key, dimension.Value))
            .ToArray());

    private static DatabaseHealthResponse Map(DatabaseHealthItem item)
    {
        DatabaseObservation observation = item.Observation;
        return new DatabaseHealthResponse(
            observation.DatabaseId,
            observation.Name.Value,
            MapDatabaseState(observation.State),
            MapRecoveryModel(observation.RecoveryModel),
            MapUserAccess(observation.UserAccess),
            observation.IsReadOnly,
            observation.CompatibilityLevel,
            observation.ObservedAtUtc,
            Map(item.Collector));
    }

    private static DatabaseFileHealthResponse Map(DatabaseFileHealthItem item)
    {
        DatabaseFileObservation observation = item.Observation;
        return new DatabaseFileHealthResponse(
            observation.DatabaseId,
            observation.FileId,
            observation.LogicalName.Value,
            MapFileType(observation.FileType),
            MapFileState(observation.State),
            FormatInt64(observation.SizeBytes),
            observation.MaximumSizeBytes is null ? null : FormatInt64(observation.MaximumSizeBytes.Value),
            FormatInt64(observation.GrowthBytes),
            observation.GrowthPercent,
            FormatInt64(observation.ReadCount),
            FormatInt64(observation.WriteCount),
            FormatInt64(observation.BytesRead),
            FormatInt64(observation.BytesWritten),
            FormatInt64(observation.IoStallMilliseconds),
            observation.ReadStallMilliseconds is long readStall ? FormatInt64(readStall) : null,
            observation.WriteStallMilliseconds is long writeStall ? FormatInt64(writeStall) : null,
            observation.ObservedAtUtc,
            Map(item.Collector));
    }

    private static CollectorHealthResponse Map(CollectorHealthProjection collector)
    {
        CollectorRunSummary? run = collector.LatestRun;
        CollectorLossEvidence? loss = run?.Loss;
        bool hasLoss = loss?.HasLoss is true;
        bool hasVisibilityGap = collector.State != CollectorHealthState.Current ||
            collector.Reason != CollectorHealthReason.None ||
            hasLoss ||
            collector.RejectedCount > 0;

        return new CollectorHealthResponse(
            collector.CollectorId.Value,
            collector.CollectorManifestVersion,
            collector.OutputSchemaVersion,
            MapState(collector.State),
            MapReason(collector.Reason),
            run is null ? null : MapOutcome(run.Outcome),
            MapCircuitState(collector.Circuit.State),
            run?.Duration.TotalMilliseconds,
            run?.RetryCount ?? 0,
            run?.Accounting.SourceRowsRead ?? 0,
            run?.Accounting.OutputItemsProduced ?? 0,
            collector.InsertedCount,
            collector.DuplicateCount,
            collector.RejectedCount,
            run?.Accounting.ResponseBytes ?? 0,
            collector.PersistedBytes,
            hasLoss ? MapLossKind(loss!.Kind) : null,
            hasLoss ? loss!.MinimumLostItems : null,
            hasLoss ? loss!.CountIsExact : null,
            hasLoss ? loss!.MinimumLostBytes : null,
            hasVisibilityGap,
            collector.ScheduledAtUtc,
            collector.LastAttemptAtUtc,
            collector.LastSuccessAtUtc,
            collector.NextDueAtUtc);
    }

    private static string MapState(CollectorHealthState state) => state switch
    {
        CollectorHealthState.Pending => "pending",
        CollectorHealthState.Current => "current",
        CollectorHealthState.Degraded => "degraded",
        CollectorHealthState.Unavailable => "unavailable",
        CollectorHealthState.Unsupported => "unsupported",
        CollectorHealthState.Stale => "stale",
        CollectorHealthState.Disabled => "disabled",
        _ => throw new InvalidDataException("The health repository returned an unknown health state."),
    };

    private static string MapReason(CollectorHealthReason reason) => reason switch
    {
        CollectorHealthReason.None => "none",
        CollectorHealthReason.NeverCollected => "never_collected",
        CollectorHealthReason.CapabilityProfileMissing => "capability_profile_missing",
        CollectorHealthReason.CapabilityProfileStale => "capability_profile_stale",
        CollectorHealthReason.CapabilityMissing => "capability_missing",
        CollectorHealthReason.PermissionDenied => "permission_denied",
        CollectorHealthReason.VersionUnsupported => "version_unsupported",
        CollectorHealthReason.PlatformUnsupported => "platform_unsupported",
        CollectorHealthReason.EditionUnsupported => "edition_unsupported",
        CollectorHealthReason.TimedOut => "timed_out",
        CollectorHealthReason.CollectionFailed => "collection_failed",
        CollectorHealthReason.OutputInvalid => "output_invalid",
        CollectorHealthReason.SampleLoss => "sample_loss",
        CollectorHealthReason.CircuitOpen => "circuit_open",
        CollectorHealthReason.EvidenceStale => "evidence_stale",
        _ => throw new InvalidDataException("The health repository returned an unknown health reason."),
    };

    private static string MapOutcome(CollectorRunOutcome outcome) => outcome switch
    {
        CollectorRunOutcome.Succeeded => "succeeded",
        CollectorRunOutcome.Partial => "partial",
        CollectorRunOutcome.TimedOut => "timed_out",
        CollectorRunOutcome.TransientFailure => "transient_failure",
        CollectorRunOutcome.PermanentFailure => "permanent_failure",
        CollectorRunOutcome.PermissionDenied => "permission_denied",
        CollectorRunOutcome.Unsupported => "unsupported",
        CollectorRunOutcome.OutputInvalid => "output_invalid",
        CollectorRunOutcome.LeaseLost => "lease_lost",
        CollectorRunOutcome.CircuitOpen => "circuit_open",
        _ => throw new InvalidDataException("The health repository returned an unknown run outcome."),
    };

    private static string MapCircuitState(CollectorCircuitState state) => state switch
    {
        CollectorCircuitState.Closed => "closed",
        CollectorCircuitState.Open => "open",
        CollectorCircuitState.HalfOpen => "half_open",
        _ => throw new InvalidDataException("The health repository returned an unknown circuit state."),
    };

    private static string MapLossKind(CollectorLossKind kind) => kind switch
    {
        CollectorLossKind.SourceRowLimit => "source_row_limit",
        CollectorLossKind.ResponseByteLimit => "response_byte_limit",
        CollectorLossKind.OutputValidationFailure => "output_validation_failure",
        CollectorLossKind.IngestionRejection => "ingestion_rejection",
        CollectorLossKind.DuplicateOverlap => "duplicate_overlap",
        CollectorLossKind.VisibilityIncomplete => "visibility_incomplete",
        _ => throw new InvalidDataException("The health repository returned an unknown loss kind."),
    };

    private static string MapDatabaseState(DatabaseOperationalState state) => state switch
    {
        DatabaseOperationalState.Online => "online",
        DatabaseOperationalState.Restoring => "restoring",
        DatabaseOperationalState.Recovering => "recovering",
        DatabaseOperationalState.RecoveryPending => "recovery_pending",
        DatabaseOperationalState.Suspect => "suspect",
        DatabaseOperationalState.Emergency => "emergency",
        DatabaseOperationalState.Offline => "offline",
        DatabaseOperationalState.Copying => "copying",
        DatabaseOperationalState.OfflineSecondary => "offline_secondary",
        DatabaseOperationalState.Other => "other",
        _ => throw new InvalidDataException("The health repository returned an unknown database state."),
    };

    private static string MapRecoveryModel(DatabaseRecoveryModel model) => model switch
    {
        DatabaseRecoveryModel.Full => "full",
        DatabaseRecoveryModel.BulkLogged => "bulk_logged",
        DatabaseRecoveryModel.Simple => "simple",
        DatabaseRecoveryModel.Other => "other",
        _ => throw new InvalidDataException("The health repository returned an unknown recovery model."),
    };

    private static string MapUserAccess(DatabaseUserAccess access) => access switch
    {
        DatabaseUserAccess.MultiUser => "multi_user",
        DatabaseUserAccess.RestrictedUser => "restricted_user",
        DatabaseUserAccess.SingleUser => "single_user",
        DatabaseUserAccess.Other => "other",
        _ => throw new InvalidDataException("The health repository returned an unknown user-access state."),
    };

    private static string MapFileType(DatabaseFileType type) => type switch
    {
        DatabaseFileType.Rows => "rows",
        DatabaseFileType.Log => "log",
        DatabaseFileType.Filestream => "filestream",
        DatabaseFileType.FullText => "full_text",
        DatabaseFileType.Other => "other",
        _ => throw new InvalidDataException("The health repository returned an unknown logical-file type."),
    };

    private static string MapFileState(DatabaseFileState state) => state switch
    {
        DatabaseFileState.Online => "online",
        DatabaseFileState.Restoring => "restoring",
        DatabaseFileState.Recovering => "recovering",
        DatabaseFileState.RecoveryPending => "recovery_pending",
        DatabaseFileState.Suspect => "suspect",
        DatabaseFileState.Emergency => "emergency",
        DatabaseFileState.Offline => "offline",
        DatabaseFileState.Defunct => "defunct",
        DatabaseFileState.Other => "other",
        _ => throw new InvalidDataException("The health repository returned an unknown logical-file state."),
    };

    private static void Validate(
        DatabaseHealthPage page,
        int requestedLimit,
        DatabaseHealthCursor? requestedCursor)
    {
        if (page.Collector.CollectorId.Value != "database.inventory" ||
            page.Items.Count > requestedLimit ||
            page.Items.Any(static item => item.Collector.CollectorId.Value != "database.inventory") ||
            (requestedCursor is not null &&
                (page.SnapshotRunId != requestedCursor.SnapshotRunId ||
                 page.SnapshotTargetRevision != requestedCursor.SnapshotTargetRevision)))
        {
            throw new InvalidDataException("The health repository exceeded the requested database page size.");
        }

        int previousId = requestedCursor?.DatabaseId ?? 0;
        foreach (DatabaseHealthItem item in page.Items)
        {
            if (item.Observation.DatabaseId <= previousId)
            {
                throw new InvalidDataException("The health repository returned an invalid database keyset page.");
            }

            previousId = item.Observation.DatabaseId;
        }

        if (page.NextCursor is not null &&
            (page.Items.Count == 0 ||
             page.NextCursor.DatabaseId != previousId ||
             page.NextCursor.TargetId != page.TargetId ||
             page.NextCursor.SnapshotRunId != page.SnapshotRunId ||
             page.NextCursor.SnapshotTargetRevision != page.SnapshotTargetRevision))
        {
            throw new InvalidDataException("The health repository returned an invalid database continuation cursor.");
        }
    }

    private static void Validate(
        DatabaseFileHealthPage page,
        int requestedLimit,
        DatabaseFileHealthCursor? requestedCursor)
    {
        if (page.Collector.CollectorId.Value != "database.files" ||
            page.Items.Count > requestedLimit ||
            page.Items.Any(static item => item.Collector.CollectorId.Value != "database.files") ||
            (requestedCursor is not null &&
                (page.SnapshotRunId != requestedCursor.SnapshotRunId ||
                 page.SnapshotTargetRevision != requestedCursor.SnapshotTargetRevision)))
        {
            throw new InvalidDataException("The health repository exceeded the requested logical-file page size.");
        }

        (int DatabaseId, int FileId) previous = requestedCursor is null
            ? (0, 0)
            : (requestedCursor.DatabaseId, requestedCursor.FileId);
        foreach (DatabaseFileHealthItem item in page.Items)
        {
            var current = (item.Observation.DatabaseId, item.Observation.FileId);
            if (current.CompareTo(previous) <= 0)
            {
                throw new InvalidDataException("The health repository returned an invalid logical-file keyset page.");
            }

            previous = current;
        }

        if (page.NextCursor is not null &&
            (page.Items.Count == 0 ||
                page.NextCursor.DatabaseId != previous.DatabaseId ||
                page.NextCursor.FileId != previous.FileId ||
                page.NextCursor.TargetId != page.TargetId ||
                page.NextCursor.SnapshotRunId != page.SnapshotRunId ||
                page.NextCursor.SnapshotTargetRevision != page.SnapshotTargetRevision))
        {
            throw new InvalidDataException("The health repository returned an invalid logical-file continuation cursor.");
        }
    }

    private static string FormatInt64(long value) => value.ToString(CultureInfo.InvariantCulture);

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
}

internal static class HealthCursorCodec
{
    private const int MaximumEncodedLength = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string Encode(DatabaseHealthCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Encode(string.Join(
            '\n',
            "d",
            cursor.TargetId.Value.ToString("N"),
            cursor.SnapshotRunId.Value.ToString("N"),
            cursor.SnapshotTargetRevision.Value.ToString(CultureInfo.InvariantCulture),
            cursor.DatabaseId.ToString(CultureInfo.InvariantCulture)));
    }

    internal static string Encode(DatabaseFileHealthCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Encode(string.Concat(
            "f\n",
            cursor.TargetId.Value.ToString("N"),
            "\n",
            cursor.SnapshotRunId.Value.ToString("N"),
            "\n",
            cursor.SnapshotTargetRevision.Value.ToString(CultureInfo.InvariantCulture),
            "\n",
            cursor.DatabaseId.ToString(CultureInfo.InvariantCulture),
            "\n",
            cursor.FileId.ToString(CultureInfo.InvariantCulture)));
    }

    internal static DatabaseHealthCursor DecodeDatabase(string encoded)
    {
        string[] components = Decode(encoded).Split('\n', StringSplitOptions.None);
        if (components.Length != 5 ||
            components[0] != "d" ||
            !TryParseCanonicalGuid(components[1], out Guid targetId) ||
            !TryParseCanonicalGuid(components[2], out Guid runId) ||
            !TryParsePositiveInt64(components[3], out long targetRevision) ||
            !TryParsePositiveInt32(components[4], out int databaseId))
        {
            throw InvalidCursor(nameof(encoded));
        }

        return new DatabaseHealthCursor(
            new MonitoredInstanceId(targetId),
            new CollectorRunId(runId),
            new ObservationTargetRevision(targetRevision),
            databaseId);
    }

    internal static DatabaseFileHealthCursor DecodeDatabaseFile(string encoded)
    {
        string[] components = Decode(encoded).Split('\n', StringSplitOptions.None);
        if (components.Length != 6 ||
            components[0] != "f" ||
            !TryParseCanonicalGuid(components[1], out Guid targetId) ||
            !TryParseCanonicalGuid(components[2], out Guid runId) ||
            !TryParsePositiveInt64(components[3], out long targetRevision) ||
            !TryParsePositiveInt32(components[4], out int databaseId) ||
            !TryParsePositiveInt32(components[5], out int fileId))
        {
            throw InvalidCursor(nameof(encoded));
        }

        return new DatabaseFileHealthCursor(
            new MonitoredInstanceId(targetId),
            new CollectorRunId(runId),
            new ObservationTargetRevision(targetRevision),
            databaseId,
            fileId);
    }

    private static string Encode(string value) => Convert
        .ToBase64String(StrictUtf8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoded);
        if (encoded.Length > MaximumEncodedLength ||
            encoded.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw InvalidCursor(nameof(encoded));
        }

        string base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            string value = StrictUtf8.GetString(Convert.FromBase64String(base64));
            if (!string.Equals(Encode(value), encoded, StringComparison.Ordinal))
            {
                throw InvalidCursor(nameof(encoded));
            }

            return value;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw InvalidCursor(nameof(encoded));
        }
    }

    private static bool TryParsePositiveInt32(string value, out int result)
    {
        bool parsed = int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result);
        return parsed && result > 0 &&
            string.Equals(result.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    private static bool TryParsePositiveInt64(string value, out long result)
    {
        bool parsed = long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out result);
        return parsed && result > 0 &&
            string.Equals(result.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    private static bool TryParseCanonicalGuid(string value, out Guid result)
    {
        bool parsed = Guid.TryParseExact(value, "N", out result);
        return parsed && result != Guid.Empty &&
            string.Equals(result.ToString("N"), value, StringComparison.Ordinal);
    }

    private static ArgumentException InvalidCursor(string parameterName) =>
        new("The continuation cursor is invalid.", parameterName);
}
