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

/// <summary>Maps bounded, target-scoped activity projections through application services only.</summary>
public static class TargetActivityEndpoints
{
    private const string RoutePrefix = "/api/v1/observation-targets/{instanceId:guid}/activity";
    private const int DefaultPageSize = 25;
    private static readonly RepositoryCallTimeout RepositoryTimeout = new(TimeSpan.FromSeconds(5));

    public static IEndpointRouteBuilder MapTargetActivityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder group = endpoints.MapGroup(RoutePrefix).RequireAuthorization();
        group.MapGet("/sessions", ListSessionsAsync);
        group.MapGet("/requests", ListRequestsAsync);
        group.MapGet("/waits", ListWaitsAsync);
        group.MapGet("/blocking/current", ListCurrentBlockingAsync);
        group.MapGet("/blocking/history", ListBlockingHistoryAsync);
        return endpoints;
    }

    private static async Task<IResult> ListSessionsAsync(
        HttpContext httpContext,
        Guid instanceId,
        IActivityProjectionQueryService activity,
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
            ActivitySessionCursor? decodedCursor = cursor is null
                ? null
                : ActivityCursorCodec.DecodeSession(cursor);
            ActivitySessionPage? page = await activity.ListSessionsAsync(
                    new ListActivitySessionsQuery(
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

            Validate(page, limit, decodedCursor, "activity.sessions");
            return Results.Ok(new ActivitySessionPageResponse(
                page.TargetId.Value,
                page.RepositoryTimeUtc,
                Map(page.Evidence, page.RepositoryTimeUtc),
                page.Items.Select(Map).ToArray(),
                page.NextCursor is null ? null : ActivityCursorCodec.Encode(page.NextCursor)));
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

    private static async Task<IResult> ListRequestsAsync(
        HttpContext httpContext,
        Guid instanceId,
        IActivityProjectionQueryService activity,
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
            ActivityRequestCursor? decodedCursor = cursor is null
                ? null
                : ActivityCursorCodec.DecodeRequest(cursor);
            ActivityRequestPage? page = await activity.ListRequestsAsync(
                    new ListActivityRequestsQuery(
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

            Validate(page, limit, decodedCursor, "activity.requests");
            return Results.Ok(new ActivityRequestPageResponse(
                page.TargetId.Value,
                page.RepositoryTimeUtc,
                Map(page.Evidence, page.RepositoryTimeUtc),
                page.Items.Select(Map).ToArray(),
                page.NextCursor is null ? null : ActivityCursorCodec.Encode(page.NextCursor)));
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

    private static async Task<IResult> ListWaitsAsync(
        HttpContext httpContext,
        Guid instanceId,
        IActivityProjectionQueryService activity,
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
            ServerWaitSummaryCursor? decodedCursor = cursor is null
                ? null
                : ActivityCursorCodec.DecodeWait(cursor);
            ServerWaitSummaryPage? page = await activity.ListWaitSummaryAsync(
                    new ListServerWaitSummaryQuery(
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

            Validate(page, limit, decodedCursor, "waits.server");
            return Results.Ok(new ServerWaitSummaryPageResponse(
                page.TargetId.Value,
                page.RepositoryTimeUtc,
                Map(page.Evidence, page.RepositoryTimeUtc),
                page.BaselineRunId?.Value,
                page.Items.Select(Map).ToArray(),
                page.NextCursor is null ? null : ActivityCursorCodec.Encode(page.NextCursor)));
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

    private static async Task<IResult> ListCurrentBlockingAsync(
        HttpContext httpContext,
        Guid instanceId,
        IActivityProjectionQueryService activity,
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
            BlockingEdgeCursor? decodedCursor = cursor is null
                ? null
                : ActivityCursorCodec.DecodeBlocking(cursor);
            CurrentBlockingPage? page = await activity.ListCurrentBlockingAsync(
                    new ListCurrentBlockingQuery(
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

            Validate(page, limit, decodedCursor, "blocking.current");
            return Results.Ok(new CurrentBlockingPageResponse(
                page.TargetId.Value,
                page.RepositoryTimeUtc,
                Map(page.Evidence, page.RepositoryTimeUtc),
                BlockingChainLimits.MaximumDepth,
                BlockingChainLimits.MaximumNodes,
                page.Items.Select(Map).ToArray(),
                page.NextCursor is null ? null : ActivityCursorCodec.Encode(page.NextCursor)));
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

    private static async Task<IResult> ListBlockingHistoryAsync(
        HttpContext httpContext,
        Guid instanceId,
        IActivityProjectionQueryService activity,
        WindowsGroupRoleResolver authorizationResolver,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int limit = DefaultPageSize,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        AuditCorrelationId correlationId = ApiCorrelation.Begin(httpContext);
        try
        {
            if (fromUtc is null || toUtc is null)
            {
                return InvalidRequest(correlationId);
            }

            AuthorizationContext authorization = authorizationResolver.Resolve(httpContext.User);
            var targetId = new MonitoredInstanceId(instanceId);
            BlockingHistoryCursor? decodedCursor = cursor is null
                ? null
                : ActivityCursorCodec.DecodeHistory(cursor);
            BlockingHistoryPage? page = await activity.ListBlockingHistoryAsync(
                    new ListBlockingHistoryQuery(
                        authorization,
                        targetId,
                        fromUtc.Value,
                        toUtc.Value,
                        limit,
                        decodedCursor,
                        RepositoryTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (page is null)
            {
                return NotFound(correlationId);
            }

            Validate(page, limit, decodedCursor, fromUtc.Value, toUtc.Value);
            return Results.Ok(new BlockingHistoryPageResponse(
                page.TargetId.Value,
                page.FromUtc,
                page.ToUtc,
                page.RepositoryTimeUtc,
                page.Items.Select(item => new BlockingHistoryResponse(
                    Map(item.Evidence, page.RepositoryTimeUtc, historical: true)!,
                    Map(item.Edge))).ToArray(),
                page.NextCursor is null ? null : ActivityCursorCodec.Encode(page.NextCursor)));
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

    private static ActivitySnapshotEvidenceResponse? Map(
        ActivitySnapshotEvidence? evidence,
        DateTimeOffset repositoryTimeUtc,
        bool historical = false)
    {
        if (evidence is null)
        {
            return null;
        }

        if (evidence.CompletedAtUtc > repositoryTimeUtc)
        {
            throw new InvalidDataException("Activity evidence cannot complete after repository time.");
        }

        string freshness = historical
            ? "historical"
            : repositoryTimeUtc - evidence.CompletedAtUtc > FreshnessWindow(evidence.CollectorId.Value)
                ? "stale"
                : "current";
        return new ActivitySnapshotEvidenceResponse(
            evidence.RunId.Value,
            evidence.TargetRevision,
            evidence.CollectorId.Value,
            freshness,
            MapOutcome(evidence.Outcome),
            MapReason(evidence.Reason),
            evidence.Outcome == CollectorRunOutcome.Partial,
            evidence.Loss.HasLoss ? Map(evidence.Loss) : null,
            evidence.CompletedAtUtc);
    }

    private static ActivityLossResponse Map(CollectorLossEvidence loss) => new(
        MapLossKind(loss.Kind),
        loss.MinimumLostItems,
        loss.CountIsExact,
        loss.MinimumLostBytes);

    private static ActivitySessionResponse Map(ActivitySessionSnapshotItem item) => new(
        item.SessionId,
        MapSessionStatus(item.Status),
        item.IsUserProcess,
        item.DatabaseId,
        item.OpenTransactionCount,
        item.CpuMilliseconds,
        item.MemoryUsagePages,
        item.Reads,
        item.Writes,
        item.LogicalReads,
        item.TotalElapsedMilliseconds,
        item.ObservedAtUtc);

    private static ActivityRequestResponse Map(ActivityRequestSnapshotItem item) => new(
        item.SessionId,
        item.RequestId,
        MapRequestStatus(item.Status),
        MapRequestCommand(item.Command),
        item.DatabaseId,
        item.CpuMilliseconds,
        item.TotalElapsedMilliseconds,
        item.Reads,
        item.Writes,
        item.LogicalReads,
        item.RowCount,
        item.PercentComplete,
        item.ObservedAtUtc);

    private static ServerWaitSummaryResponse Map(ServerWaitSummaryItem item) => new(
        item.WaitType.Value,
        item.WaitingTasksCount,
        item.WaitTimeMilliseconds,
        item.MaximumWaitTimeMilliseconds,
        item.SignalWaitTimeMilliseconds,
        item.BaselineAvailable,
        item.ResetDetected,
        item.WaitingTasksDelta,
        item.WaitTimeMillisecondsDelta,
        item.SignalWaitTimeMillisecondsDelta,
        item.ObservedAtUtc);

    private static BlockingEdgeResponse Map(BlockingEdgeSnapshotItem item) => new(
        item.BlockedSessionId,
        MapBlockerKind(item.BlockerKind),
        item.BlockerSessionId,
        item.WaitType.Value,
        item.WaitingTaskCount,
        item.WaitDurationMilliseconds,
        item.RootBlockerSessionId,
        item.ChainDepth,
        MapChainState(item.ChainState),
        item.ObservedAtUtc);

    private static void Validate(
        ActivitySessionPage page,
        int requestedLimit,
        ActivitySessionCursor? requestedCursor,
        string expectedCollectorId)
    {
        ValidateSnapshot(page, requestedLimit, requestedCursor, expectedCollectorId);
        int previous = requestedCursor?.SessionId ?? 0;
        foreach (ActivitySessionSnapshotItem item in page.Items)
        {
            if (item.SessionId <= previous)
            {
                throw InvalidPage();
            }

            previous = item.SessionId;
        }

        if (page.NextCursor is not null &&
            (page.Items.Count == 0 || page.NextCursor.SessionId != previous))
        {
            throw InvalidPage();
        }
    }

    private static void Validate(
        ActivityRequestPage page,
        int requestedLimit,
        ActivityRequestCursor? requestedCursor,
        string expectedCollectorId)
    {
        ValidateSnapshot(page, requestedLimit, requestedCursor, expectedCollectorId);
        (int SessionId, int RequestId) previous = requestedCursor is null
            ? (0, -1)
            : (requestedCursor.SessionId, requestedCursor.RequestId);
        foreach (ActivityRequestSnapshotItem item in page.Items)
        {
            var current = (item.SessionId, item.RequestId);
            if (current.CompareTo(previous) <= 0)
            {
                throw InvalidPage();
            }

            previous = current;
        }

        if (page.NextCursor is not null &&
            (page.Items.Count == 0 ||
             page.NextCursor.SessionId != previous.SessionId ||
             page.NextCursor.RequestId != previous.RequestId))
        {
            throw InvalidPage();
        }
    }

    private static void Validate(
        ServerWaitSummaryPage page,
        int requestedLimit,
        ServerWaitSummaryCursor? requestedCursor,
        string expectedCollectorId)
    {
        ValidateSnapshot(page, requestedLimit, requestedCursor, expectedCollectorId);
        string? previous = requestedCursor?.WaitType.Value;
        foreach (ServerWaitSummaryItem item in page.Items)
        {
            if (previous is not null && StringComparer.Ordinal.Compare(item.WaitType.Value, previous) <= 0)
            {
                throw InvalidPage();
            }

            previous = item.WaitType.Value;
        }

        if (page.NextCursor is not null &&
            (page.Items.Count == 0 ||
             !string.Equals(page.NextCursor.WaitType.Value, previous, StringComparison.Ordinal) ||
             page.NextCursor.BaselineRunId != page.BaselineRunId))
        {
            throw InvalidPage();
        }
    }

    private static void Validate(
        CurrentBlockingPage page,
        int requestedLimit,
        BlockingEdgeCursor? requestedCursor,
        string expectedCollectorId)
    {
        ValidateSnapshot(page, requestedLimit, requestedCursor, expectedCollectorId);
        BlockingKey? previous = requestedCursor is null ? null : BlockingKey.From(requestedCursor);
        foreach (BlockingEdgeSnapshotItem item in page.Items)
        {
            BlockingKey current = BlockingKey.From(item);
            if (previous is not null && current.CompareTo(previous.Value) <= 0)
            {
                throw InvalidPage();
            }

            previous = current;
        }

        if (page.NextCursor is not null &&
            (previous is null || BlockingKey.From(page.NextCursor).CompareTo(previous.Value) != 0))
        {
            throw InvalidPage();
        }
    }

    private static void ValidateSnapshot<TItem, TCursor>(
        ActivityPage<TItem, TCursor> page,
        int requestedLimit,
        TCursor? requestedCursor,
        string expectedCollectorId)
        where TItem : class
        where TCursor : class
    {
        if (page.Items.Count > requestedLimit ||
            page.Evidence is not null && page.Evidence.CollectorId.Value != expectedCollectorId)
        {
            throw InvalidPage();
        }

        if (requestedCursor is not null &&
            (ActivityCursorCodec.Run(requestedCursor) != page.Evidence?.RunId ||
             ActivityCursorCodec.Revision(requestedCursor).Value.ToString(CultureInfo.InvariantCulture) !=
                page.Evidence?.TargetRevision))
        {
            throw new ArgumentException("The continuation cursor no longer identifies the current activity snapshot.");
        }

        if (page.NextCursor is not null &&
            (ActivityCursorCodec.Target(page.NextCursor) != page.TargetId ||
             ActivityCursorCodec.Run(page.NextCursor) != page.Evidence?.RunId ||
             ActivityCursorCodec.Revision(page.NextCursor).Value.ToString(CultureInfo.InvariantCulture) !=
                page.Evidence?.TargetRevision))
        {
            throw InvalidPage();
        }
    }

    private static void Validate(
        BlockingHistoryPage page,
        int requestedLimit,
        BlockingHistoryCursor? requestedCursor,
        DateTimeOffset requestedFromUtc,
        DateTimeOffset requestedToUtc)
    {
        if (page.Items.Count > requestedLimit ||
            page.FromUtc != requestedFromUtc ||
            page.ToUtc != requestedToUtc)
        {
            throw InvalidPage();
        }

        HistoryKey? previous = requestedCursor is null ? null : HistoryKey.From(requestedCursor);
        foreach (BlockingHistoryItem item in page.Items)
        {
            if (item.Evidence.CollectorId.Value != "blocking.current" ||
                item.Evidence.RunId.Value == Guid.Empty ||
                item.Edge.ObservedAtUtc < page.FromUtc ||
                item.Edge.ObservedAtUtc >= page.ToUtc)
            {
                throw InvalidPage();
            }

            HistoryKey current = HistoryKey.From(item);
            if (previous is not null && current.CompareTo(previous.Value) >= 0)
            {
                throw InvalidPage();
            }

            previous = current;
        }

        if (page.NextCursor is not null &&
            (previous is null || HistoryKey.From(page.NextCursor).CompareTo(previous.Value) != 0))
        {
            throw InvalidPage();
        }
    }

    private static TimeSpan FreshnessWindow(string collectorId) => collectorId switch
    {
        "activity.sessions" => TimeSpan.FromSeconds(30),
        "activity.requests" => TimeSpan.FromSeconds(20),
        "waits.server" => TimeSpan.FromSeconds(60),
        "blocking.current" => TimeSpan.FromSeconds(20),
        _ => throw new InvalidDataException("The activity repository returned an unknown collector."),
    };

    private static string MapSessionStatus(ActivitySessionStatus status) => status switch
    {
        ActivitySessionStatus.Running => "running",
        ActivitySessionStatus.Sleeping => "sleeping",
        ActivitySessionStatus.Dormant => "dormant",
        ActivitySessionStatus.Preconnect => "preconnect",
        ActivitySessionStatus.Other => "other",
        _ => throw new InvalidDataException("The activity repository returned an unknown session state."),
    };

    private static string MapRequestStatus(ActivityRequestStatus status) => status switch
    {
        ActivityRequestStatus.Background => "background",
        ActivityRequestStatus.Running => "running",
        ActivityRequestStatus.Runnable => "runnable",
        ActivityRequestStatus.Sleeping => "sleeping",
        ActivityRequestStatus.Suspended => "suspended",
        ActivityRequestStatus.Other => "other",
        _ => throw new InvalidDataException("The activity repository returned an unknown request state."),
    };

    private static string MapRequestCommand(ActivityRequestCommand command) => command switch
    {
        ActivityRequestCommand.Select => "select",
        ActivityRequestCommand.Insert => "insert",
        ActivityRequestCommand.Update => "update",
        ActivityRequestCommand.Delete => "delete",
        ActivityRequestCommand.Merge => "merge",
        ActivityRequestCommand.Backup => "backup",
        ActivityRequestCommand.Restore => "restore",
        ActivityRequestCommand.Dbcc => "dbcc",
        ActivityRequestCommand.Other => "other",
        _ => throw new InvalidDataException("The activity repository returned an unknown request command."),
    };

    private static string MapBlockerKind(BlockingBlockerKind kind) => kind switch
    {
        BlockingBlockerKind.Session => "session",
        BlockingBlockerKind.OrphanedDistributedTransaction => "orphaned_distributed_transaction",
        BlockingBlockerKind.DeferredRecovery => "deferred_recovery",
        BlockingBlockerKind.Undetermined => "undetermined",
        BlockingBlockerKind.AsyncLatch => "async_latch",
        BlockingBlockerKind.Other => "other",
        _ => throw new InvalidDataException("The activity repository returned an unknown blocker kind."),
    };

    private static string MapChainState(BlockingChainState state) => state switch
    {
        BlockingChainState.Resolved => "resolved",
        BlockingChainState.Cycle => "cycle",
        BlockingChainState.DepthLimit => "depth_limit",
        BlockingChainState.ExternalBlocker => "external_blocker",
        _ => throw new InvalidDataException("The activity repository returned an unknown blocking-chain state."),
    };

    private static string MapOutcome(CollectorRunOutcome outcome) => outcome switch
    {
        CollectorRunOutcome.Succeeded => "succeeded",
        CollectorRunOutcome.Partial => "partial",
        _ => throw new InvalidDataException("Activity snapshot evidence has an invalid run outcome."),
    };

    private static string MapReason(CollectorRunReason reason) => reason switch
    {
        CollectorRunReason.Completed => "completed",
        CollectorRunReason.SourceRowLimit => "source_row_limit",
        CollectorRunReason.ResponseByteLimit => "response_byte_limit",
        CollectorRunReason.OutputValidationFailed => "output_validation_failed",
        CollectorRunReason.BlockingGraphLimit => "blocking_graph_limit",
        _ => throw new InvalidDataException("Activity snapshot evidence has an invalid run reason."),
    };

    private static string MapLossKind(CollectorLossKind kind) => kind switch
    {
        CollectorLossKind.SourceRowLimit => "source_row_limit",
        CollectorLossKind.ResponseByteLimit => "response_byte_limit",
        CollectorLossKind.OutputValidationFailure => "output_validation_failure",
        CollectorLossKind.IngestionRejection => "ingestion_rejection",
        CollectorLossKind.BlockingGraphLimit => "blocking_graph_limit",
        CollectorLossKind.DuplicateOverlap => "duplicate_overlap",
        _ => throw new InvalidDataException("Activity snapshot evidence has an invalid loss kind."),
    };

    private static InvalidDataException InvalidPage() =>
        new("The activity repository returned an invalid or unbounded page.");

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

    private readonly record struct BlockingKey(
        int BlockedSessionId,
        string BlockerKind,
        int BlockerSessionId,
        string WaitType) : IComparable<BlockingKey>
    {
        public static BlockingKey From(BlockingEdgeCursor value) => new(
            value.BlockedSessionId,
            MapBlockerKind(value.BlockerKind),
            value.BlockerSessionId ?? 0,
            value.WaitType.Value);

        public static BlockingKey From(BlockingEdgeSnapshotItem value) => new(
            value.BlockedSessionId,
            MapBlockerKind(value.BlockerKind),
            value.BlockerSessionId ?? 0,
            value.WaitType.Value);

        public int CompareTo(BlockingKey other)
        {
            int comparison = BlockedSessionId.CompareTo(other.BlockedSessionId);
            comparison = comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(BlockerKind, other.BlockerKind);
            comparison = comparison != 0 ? comparison : BlockerSessionId.CompareTo(other.BlockerSessionId);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(WaitType, other.WaitType);
        }
    }

    private readonly record struct HistoryKey(
        DateTimeOffset ObservedAtUtc,
        string RunId,
        BlockingKey Edge) : IComparable<HistoryKey>
    {
        public static HistoryKey From(BlockingHistoryCursor value) => new(
            value.ObservedAtUtc,
            value.RunId.Value.ToString("N"),
            new BlockingKey(
                value.BlockedSessionId,
                MapBlockerKind(value.BlockerKind),
                value.BlockerSessionId ?? 0,
                value.WaitType.Value));

        public static HistoryKey From(BlockingHistoryItem value) => new(
            value.Edge.ObservedAtUtc,
            value.Evidence.RunId.Value.ToString("N"),
            BlockingKey.From(value.Edge));

        public int CompareTo(HistoryKey other)
        {
            int comparison = ObservedAtUtc.CompareTo(other.ObservedAtUtc);
            comparison = comparison != 0 ? comparison : StringComparer.Ordinal.Compare(RunId, other.RunId);
            return comparison != 0 ? comparison : Edge.CompareTo(other.Edge);
        }
    }
}

internal static class ActivityCursorCodec
{
    private const int MaximumEncodedLength = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static string Encode(ActivitySessionCursor cursor) => EncodeParts(
        "s",
        Target(cursor),
        Run(cursor),
        Revision(cursor),
        cursor.SessionId.ToString(CultureInfo.InvariantCulture));

    internal static string Encode(ActivityRequestCursor cursor) => EncodeParts(
        "r",
        Target(cursor),
        Run(cursor),
        Revision(cursor),
        cursor.SessionId.ToString(CultureInfo.InvariantCulture),
        cursor.RequestId.ToString(CultureInfo.InvariantCulture));

    internal static string Encode(ServerWaitSummaryCursor cursor) => EncodeParts(
        "w",
        Target(cursor),
        Run(cursor),
        Revision(cursor),
        cursor.BaselineRunId?.Value.ToString("N") ?? "-",
        cursor.WaitType.Value);

    internal static string Encode(BlockingEdgeCursor cursor) => EncodeParts(
        "b",
        Target(cursor),
        Run(cursor),
        Revision(cursor),
        cursor.BlockedSessionId.ToString(CultureInfo.InvariantCulture),
        ((int)cursor.BlockerKind).ToString(CultureInfo.InvariantCulture),
        cursor.BlockerSessionId?.ToString(CultureInfo.InvariantCulture) ?? "-",
        cursor.WaitType.Value);

    internal static string Encode(BlockingHistoryCursor cursor) => Encode(string.Join(
        '\n',
        "h",
        cursor.TargetId.Value.ToString("N"),
        cursor.FromUtc.ToString("O", CultureInfo.InvariantCulture),
        cursor.ToUtc.ToString("O", CultureInfo.InvariantCulture),
        cursor.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        cursor.RunId.Value.ToString("N"),
        cursor.BlockedSessionId.ToString(CultureInfo.InvariantCulture),
        ((int)cursor.BlockerKind).ToString(CultureInfo.InvariantCulture),
        cursor.BlockerSessionId?.ToString(CultureInfo.InvariantCulture) ?? "-",
        cursor.WaitType.Value));

    internal static ActivitySessionCursor DecodeSession(string encoded)
    {
        string[] parts = DecodeParts(encoded, "s", 5);
        return new ActivitySessionCursor(
            ParseTarget(parts[1]),
            ParseRun(parts[2]),
            ParseRevision(parts[3]),
            ParsePositiveInt32(parts[4]));
    }

    internal static ActivityRequestCursor DecodeRequest(string encoded)
    {
        string[] parts = DecodeParts(encoded, "r", 6);
        return new ActivityRequestCursor(
            ParseTarget(parts[1]),
            ParseRun(parts[2]),
            ParseRevision(parts[3]),
            ParsePositiveInt32(parts[4]),
            ParseNonNegativeInt32(parts[5]));
    }

    internal static ServerWaitSummaryCursor DecodeWait(string encoded)
    {
        string[] parts = DecodeParts(encoded, "w", 6);
        return new ServerWaitSummaryCursor(
            ParseTarget(parts[1]),
            ParseRun(parts[2]),
            parts[4] == "-" ? null : ParseRun(parts[4]),
            ParseRevision(parts[3]),
            new SqlServerWaitType(parts[5]));
    }

    internal static BlockingEdgeCursor DecodeBlocking(string encoded)
    {
        string[] parts = DecodeParts(encoded, "b", 8);
        BlockingBlockerKind kind = ParseBlockerKind(parts[5]);
        int? blockerSessionId = parts[6] == "-" ? null : ParsePositiveInt32(parts[6]);
        return new BlockingEdgeCursor(
            ParseTarget(parts[1]),
            ParseRun(parts[2]),
            ParseRevision(parts[3]),
            ParsePositiveInt32(parts[4]),
            kind,
            blockerSessionId,
            new SqlServerWaitType(parts[7]));
    }

    internal static BlockingHistoryCursor DecodeHistory(string encoded)
    {
        string[] parts = DecodeParts(encoded, "h", 10);
        BlockingBlockerKind kind = ParseBlockerKind(parts[7]);
        int? blockerSessionId = parts[8] == "-" ? null : ParsePositiveInt32(parts[8]);
        return new BlockingHistoryCursor(
            ParseTarget(parts[1]),
            ParseTimestamp(parts[2]),
            ParseTimestamp(parts[3]),
            ParseTimestamp(parts[4]),
            ParseRun(parts[5]),
            ParsePositiveInt32(parts[6]),
            kind,
            blockerSessionId,
            new SqlServerWaitType(parts[9]));
    }

    internal static MonitoredInstanceId Target<TCursor>(TCursor cursor) => cursor switch
    {
        ActivitySessionCursor value => value.TargetId,
        ActivityRequestCursor value => value.TargetId,
        ServerWaitSummaryCursor value => value.TargetId,
        BlockingEdgeCursor value => value.TargetId,
        _ => throw new ArgumentException("Unknown activity cursor type.", nameof(cursor)),
    };

    internal static CollectorRunId Run<TCursor>(TCursor cursor) => cursor switch
    {
        ActivitySessionCursor value => value.SnapshotRunId,
        ActivityRequestCursor value => value.SnapshotRunId,
        ServerWaitSummaryCursor value => value.SnapshotRunId,
        BlockingEdgeCursor value => value.SnapshotRunId,
        _ => throw new ArgumentException("Unknown activity cursor type.", nameof(cursor)),
    };

    internal static ObservationTargetRevision Revision<TCursor>(TCursor cursor) => cursor switch
    {
        ActivitySessionCursor value => value.SnapshotTargetRevision,
        ActivityRequestCursor value => value.SnapshotTargetRevision,
        ServerWaitSummaryCursor value => value.SnapshotTargetRevision,
        BlockingEdgeCursor value => value.SnapshotTargetRevision,
        _ => throw new ArgumentException("Unknown activity cursor type.", nameof(cursor)),
    };

    private static string EncodeParts(
        string kind,
        MonitoredInstanceId targetId,
        CollectorRunId runId,
        ObservationTargetRevision targetRevision,
        params string[] tail) => Encode(string.Join(
            '\n',
            new[]
            {
                kind,
                targetId.Value.ToString("N"),
                runId.Value.ToString("N"),
                targetRevision.Value.ToString(CultureInfo.InvariantCulture),
            }.Concat(tail)));

    private static string[] DecodeParts(string encoded, string expectedKind, int expectedCount)
    {
        string[] parts = Decode(encoded).Split('\n', StringSplitOptions.None);
        if (parts.Length != expectedCount || parts[0] != expectedKind)
        {
            throw InvalidCursor(nameof(encoded));
        }

        return parts;
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

    private static MonitoredInstanceId ParseTarget(string value) =>
        new(ParseCanonicalGuid(value));

    private static CollectorRunId ParseRun(string value) =>
        new(ParseCanonicalGuid(value));

    private static ObservationTargetRevision ParseRevision(string value) =>
        new(ParsePositiveInt64(value));

    private static Guid ParseCanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "N", out Guid result) ||
            result == Guid.Empty ||
            !string.Equals(result.ToString("N"), value, StringComparison.Ordinal))
        {
            throw InvalidCursor(nameof(value));
        }

        return result;
    }

    private static int ParsePositiveInt32(string value)
    {
        int result = ParseNonNegativeInt32(value);
        if (result == 0)
        {
            throw InvalidCursor(nameof(value));
        }

        return result;
    }

    private static int ParseNonNegativeInt32(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) ||
            result < 0 ||
            !string.Equals(result.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw InvalidCursor(nameof(value));
        }

        return result;
    }

    private static long ParsePositiveInt64(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long result) ||
            result <= 0 ||
            !string.Equals(result.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw InvalidCursor(nameof(value));
        }

        return result;
    }

    private static BlockingBlockerKind ParseBlockerKind(string value)
    {
        int numeric = ParsePositiveInt32(value);
        if (!Enum.IsDefined(typeof(BlockingBlockerKind), numeric))
        {
            throw InvalidCursor(nameof(value));
        }

        return (BlockingBlockerKind)numeric;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset result) ||
            result.Offset != TimeSpan.Zero ||
            result.Ticks % TimeSpan.TicksPerMicrosecond != 0 ||
            !string.Equals(result.ToString("O", CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            throw InvalidCursor(nameof(value));
        }

        return result;
    }

    private static ArgumentException InvalidCursor(string parameterName) =>
        new("The continuation cursor is invalid.", parameterName);
}
