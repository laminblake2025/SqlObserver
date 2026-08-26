using SqlObserver.Application.Ports;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using System.Text.Json;
using System.Globalization;

namespace SqlObserver.Application.Services;

/// <summary>Target-scoped read-only M9 projection facade.</summary>
public sealed class OperationalHealthQueryService : IOperationalHealthQueryService
{
    private static readonly TimeSpan RepositoryDeadline = TimeSpan.FromSeconds(5);
    private readonly IOperationalHealthRepositoryPort repository;
    public OperationalHealthQueryService(IOperationalHealthRepositoryPort repository) => this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public ValueTask<BackupStatusSnapshot?> GetBackupsAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken) => Read(authorization, request, repository.GetBackupsAsync, ValidateBackups, cancellationToken);
    public ValueTask<SqlAgentFailureSnapshot?> GetAgentFailuresAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken)
    {
        DateTimeOffset to = request.ToUtc ?? DateTimeOffset.UtcNow;
        return Read(authorization, request with { FromUtc = request.FromUtc ?? to.AddHours(-24), ToUtc = to }, repository.GetAgentFailuresAsync, ValidateAgent, cancellationToken);
    }
    public ValueTask<TempDbSnapshot?> GetTempDbAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken) => Read(authorization, request, repository.GetTempDbAsync, ValidateTempDb, cancellationToken);
    public ValueTask<TempDbSnapshot?> GetTempDbFilesAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken) => Read(authorization, request, repository.GetTempDbFilesAsync, ValidateTempDb, cancellationToken);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupsAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken) => Read(authorization, request, repository.GetAvailabilityGroupsAsync, ValidateAvailability, cancellationToken);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupReplicasAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken) => Read(authorization, request, repository.GetAvailabilityGroupReplicasAsync, ValidateAvailability, cancellationToken);
    public ValueTask<AvailabilityGroupsSnapshot?> GetAvailabilityGroupDatabasesAsync(AuthorizationContext authorization, OperationalHealthRequest request, CancellationToken cancellationToken) => Read(authorization, request, repository.GetAvailabilityGroupDatabasesAsync, ValidateAvailability, cancellationToken);
    private static async ValueTask<T?> Read<T>(AuthorizationContext authorization, OperationalHealthRequest request, Func<OperationalHealthRequest, CancellationToken, ValueTask<T?>> read, Action<T, OperationalHealthRequest> validate, CancellationToken cancellationToken) where T : class
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        if (!authorization.IsActive || (!authorization.HasRole(ApplicationRole.Viewer) && !authorization.HasRole(ApplicationRole.Operator) && !authorization.HasRole(ApplicationRole.TargetAdministrator)))
            throw new UnauthorizedAccessException("Operational-health read role denied.");
        if (!authorization.CanAccess(request.TargetId)) throw new UnauthorizedAccessException("Operational-health target scope denied.");
        ValidateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RepositoryDeadline);
        OperationalHealthRequest bounded = request with { Timeout = new RepositoryCallTimeout(RepositoryDeadline), FromUtc = request.FromUtc, ToUtc = request.ToUtc };
        try
        {
            T? result = await read(bounded, deadline.Token).ConfigureAwait(false);
            if (result is not null) validate(result, bounded);
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OperationalHealthRepositoryException("The operational-health repository deadline expired.");
        }
        catch (OperationalHealthContractException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or System.Data.Common.DbException)
        {
            throw new OperationalHealthRepositoryException("The operational-health repository is unavailable.", exception);
        }
    }

    private static void ValidateRequest(OperationalHealthRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.TargetId); ArgumentNullException.ThrowIfNull(request.Timeout);
        if (request.Limit is < 1 or > OperationalHealthBounds.MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Cursor is not null)
        {
            OperationalHealthCursor cursor = OperationalHealthCursor.Decode(request.Cursor);
            if (cursor.TargetId != request.TargetId) throw new OperationalHealthContractException("Cursor target binding drifted.", true);
        }
        if (request.FromUtc is not null && request.FromUtc.Value.Offset != TimeSpan.Zero || request.ToUtc is not null && request.ToUtc.Value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Operational-health timestamps must use UTC offsets.");
        DateTimeOffset to = request.ToUtc ?? DateTimeOffset.UtcNow;
        DateTimeOffset from = request.FromUtc ?? to.AddHours(-24);
        if (to <= from || to - from > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(request), "The first-observed window must be between one instant and seven days.");
    }

    private static void Header(IOperationalHealthSnapshot snapshot, OperationalHealthRequest request, int maxRows, int count, string? cursor)
    {
        OperationalHealthSnapshotHeader header = snapshot.AsHeader();
        if (header.TargetId != request.TargetId || header.TargetRevision is null || header.TargetRevision.Value <= 0) throw new OperationalHealthContractException("Repository target or revision drifted.", true);
        if (header.ObservedAtUtc.Offset != TimeSpan.Zero) throw new OperationalHealthContractException("Repository returned a non-UTC observation timestamp.");
        if (!Enum.IsDefined(header.State)) throw new OperationalHealthContractException("Repository returned an unknown observation state.");
        bool noData = header.State == OperationalObservationState.NoData;
        if (noData != (header.RunId is null)) throw new OperationalHealthContractException("NoData run binding is inconsistent.", true);
        if (noData && (count != 0 || cursor is not null)) throw new OperationalHealthContractException("NoData cannot carry page evidence or a cursor.", true);
        if (count > request.Limit || count > maxRows) throw new OperationalHealthContractException("Repository page cardinality exceeded its bound.");
        ValidateCursor(cursor, header);
    }

    private static void ValidateCursor(string? cursor, OperationalHealthSnapshotHeader snapshot)
    {
        if (cursor is null) return;
        OperationalHealthCursor decoded;
        try { decoded = OperationalHealthCursor.Decode(cursor); }
        catch (ArgumentException) { throw new OperationalHealthContractException("Repository returned an invalid cursor.", true); }
        if (decoded.TargetId != snapshot.TargetId || decoded.SnapshotUtc != snapshot.ObservedAtUtc) throw new OperationalHealthContractException("Repository cursor is not bound to the returned snapshot.", true);
    }

    private static void Fingerprint(string value)
    {
        if (value.Length != 64 || !value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new OperationalHealthContractException("Repository returned an invalid fingerprint.");
    }

    private static void ValidateBackups(BackupStatusSnapshot snapshot, OperationalHealthRequest request)
    {
        Header(snapshot, request, OperationalHealthBounds.BackupMaximumRows, snapshot.Items.Count, snapshot.NextCursor);
        if (snapshot.SourceRowsRead is < 0 or > OperationalHealthBounds.BackupMaximumRows) throw new OperationalHealthContractException("Repository source row count exceeded its bound.");
        foreach (BackupStatusObservation item in snapshot.Items)
        {
            if (item.TargetId != snapshot.TargetId || item.TargetRevision != snapshot.TargetRevision || !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Coverage) || item.SizeBytes < 0 || item.BackupSetId < 0) throw new OperationalHealthContractException("Repository returned invalid backup evidence.");
            Fingerprint(item.DatabaseFingerprint);
            if (item.LastFinishUtc is not null && item.LastFinishUtc.Value.Offset != TimeSpan.Zero) throw new OperationalHealthContractException("Backup timestamp was not UTC.");
            if (item.SourceTimeUnknown != (item.LastFinishUtc is null && item.SourceLocalFinish is not null)) throw new OperationalHealthContractException("Backup source-time evidence is inconsistent.");
        }
        EnsurePayloadBound(snapshot);
    }

    private static void ValidateAgent(SqlAgentFailureSnapshot snapshot, OperationalHealthRequest request)
    {
        Header(snapshot, request, OperationalHealthBounds.AgentMaximumRows, snapshot.Items.Count, snapshot.NextCursor);
        if (snapshot.SourceRowsRead is < 0 or > OperationalHealthBounds.AgentScanRows || snapshot.CoverageFromUtc is not null && snapshot.CoverageFromUtc.Value.Offset != TimeSpan.Zero || snapshot.CoverageToUtc is not null && snapshot.CoverageToUtc.Value.Offset != TimeSpan.Zero) throw new OperationalHealthContractException("Repository returned invalid Agent coverage.");
        DateTimeOffset to = request.ToUtc ?? DateTimeOffset.UtcNow, from = request.FromUtc ?? to.AddHours(-24);
        if (snapshot.CoverageFromUtc is not null && snapshot.CoverageFromUtc != from || snapshot.CoverageToUtc is not null && snapshot.CoverageToUtc != to) throw new OperationalHealthContractException("Agent coverage does not match its requested first-observed window.", true);
        foreach (SqlAgentFailureObservation item in snapshot.Items)
        {
            if (item.TargetId != snapshot.TargetId || item.TargetRevision != snapshot.TargetRevision || !Enum.IsDefined(item.FailureKind) || item.HistoryInstanceId < 0 || item.StepId < 0 || item.RetryAttempt < 0 || item.DurationSeconds < 0 || item.DetectedAtUtc.Offset != TimeSpan.Zero || item.ContentAvailable) throw new OperationalHealthContractException("Repository returned invalid or sensitive Agent evidence.");
            Fingerprint(item.FailureFingerprint);
        }
        EnsurePayloadBound(snapshot);
    }

    private static void ValidateTempDb(TempDbSnapshot snapshot, OperationalHealthRequest request)
    {
        Header(snapshot, request, OperationalHealthBounds.TempDbMaximumFiles, snapshot.Files.Count, snapshot.NextCursor);
        foreach (long? value in new[] { snapshot.TotalBytes, snapshot.UsedBytes, snapshot.LogTotalBytes, snapshot.LogUsedBytes }) if (value < 0) throw new OperationalHealthContractException("TempDB byte counters cannot be negative.");
        if (snapshot.UsedBytes > snapshot.TotalBytes || snapshot.LogUsedBytes > snapshot.LogTotalBytes) throw new OperationalHealthContractException("TempDB summary counters are inconsistent.");
        foreach (TempDbFileObservation item in snapshot.Files)
        {
            if (item.TargetId != snapshot.TargetId || item.TargetRevision != snapshot.TargetRevision || item.FileId < 0 || item.SizeBytes < 0 || item.UsedBytes < 0 || item.FreeBytes < 0 || item.UsedBytes > item.SizeBytes || item.FreeBytes > item.SizeBytes || item.UsedBytes + item.FreeBytes > item.SizeBytes || !Enum.IsDefined(item.State)) throw new OperationalHealthContractException("Repository returned invalid TempDB evidence.");
        }
        EnsurePayloadBound(snapshot);
    }

    private static void ValidateAvailability(AvailabilityGroupsSnapshot snapshot, OperationalHealthRequest request)
    {
        Header(snapshot, request, OperationalHealthBounds.AvailabilityMaximumRows, snapshot.Replicas.Count + snapshot.Databases.Count, snapshot.NextCursor);
        if (!Enum.IsDefined(snapshot.VisibilityScope)) throw new OperationalHealthContractException("Repository returned an unknown AG visibility scope.");
        foreach (AvailabilityReplicaObservation item in snapshot.Replicas)
        {
            if (item.TargetId != snapshot.TargetId || item.TargetRevision != snapshot.TargetRevision || !Enum.IsDefined(item.VisibilityScope) || item.VisibilityScope != snapshot.VisibilityScope || (!item.StateAvailable && snapshot.State == OperationalObservationState.Complete) || !SafeToken(item.Role) || !SafeToken(item.OperationalState) || !SafeToken(item.ConnectedState)) throw new OperationalHealthContractException("Repository returned invalid AG replica evidence.");
            Fingerprint(item.GroupFingerprint); Fingerprint(item.ReplicaFingerprint);
        }
        foreach (AvailabilityDatabaseObservation item in snapshot.Databases)
        {
            if (item.TargetId != snapshot.TargetId || item.TargetRevision != snapshot.TargetRevision || !Enum.IsDefined(item.VisibilityScope) || item.VisibilityScope != snapshot.VisibilityScope || (!item.StateAvailable && snapshot.State == OperationalObservationState.Complete) || !SafeToken(item.SynchronizationState) || !SafeToken(item.DatabaseState)) throw new OperationalHealthContractException("Repository returned invalid AG database evidence.");
            Fingerprint(item.GroupFingerprint); Fingerprint(item.DatabaseFingerprint);
        }
        EnsurePayloadBound(snapshot);
    }

    private static bool SafeToken(string value) => value.Length is > 0 and <= 64 && value.All(static c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or ' ');
    private static void EnsurePayloadBound<T>(T value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > 1_048_576) throw new OperationalHealthContractException("Repository payload exceeded the response bound.");
    }
}

public sealed class OperationalHealthContractException : InvalidOperationException
{
    public OperationalHealthContractException(string message, bool drift = false) : base(message) => IsDrift = drift;
    public bool IsDrift { get; }
}

public sealed class OperationalHealthRepositoryException : Exception
{
    public OperationalHealthRepositoryException(string message, Exception? inner = null) : base(message, inner) { }
}

internal interface OperationalHealthSnapshotHeader
{
    SqlObserver.Domain.Telemetry.MonitoredInstanceId TargetId { get; }
    SqlObserver.Domain.Targets.ObservationTargetRevision TargetRevision { get; }
    CollectorRunId? RunId { get; }
    DateTimeOffset ObservedAtUtc { get; }
    OperationalObservationState State { get; }
}

internal static class OperationalHealthHeaderExtensions
{
    public static OperationalHealthSnapshotHeader AsHeader(this IOperationalHealthSnapshot snapshot) => snapshot switch
    {
        BackupStatusSnapshot x => new HeaderAdapter(x.TargetId, x.TargetRevision, x.RunId, x.ObservedAtUtc, x.State),
        SqlAgentFailureSnapshot x => new HeaderAdapter(x.TargetId, x.TargetRevision, x.RunId, x.ObservedAtUtc, x.State),
        TempDbSnapshot x => new HeaderAdapter(x.TargetId, x.TargetRevision, x.RunId, x.ObservedAtUtc, x.State),
        AvailabilityGroupsSnapshot x => new HeaderAdapter(x.TargetId, x.TargetRevision, x.RunId, x.ObservedAtUtc, x.State),
        _ => throw new OperationalHealthContractException("Repository returned an unknown operational-health snapshot.")
    };
    private sealed record HeaderAdapter(SqlObserver.Domain.Telemetry.MonitoredInstanceId TargetId, SqlObserver.Domain.Targets.ObservationTargetRevision TargetRevision, CollectorRunId? RunId, DateTimeOffset ObservedAtUtc, OperationalObservationState State) : OperationalHealthSnapshotHeader;
}
