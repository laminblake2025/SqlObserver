using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Bounded incremental XE adapter. Only the repository may publish its source proof.</summary>
public sealed class SqlServerIncrementalDeadlockCollector : ISqlServerCollector
{
    private readonly SqlServerIncrementalDeadlockAssetCatalog _catalog;
    private readonly Func<CollectorExecutionRequest, CancellationToken, ValueTask<XeFileSourcePage>> _read;
    public static CollectorOutputContract OutputContract { get; } = new(new(2), [], 0, 0, 0, maxDeadlockObservations: 256);
    public CollectorManifest Manifest => _catalog.Manifest;
    public SqlServerIncrementalDeadlockCollector() : this(SqlServerIncrementalDeadlockAssetCatalog.LoadEmbedded()) { }
    public SqlServerIncrementalDeadlockCollector(SqlServerIncrementalDeadlockAssetCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _read = ReadSourceAsync;
    }
    internal SqlServerIncrementalDeadlockCollector(SqlServerIncrementalDeadlockAssetCatalog catalog,
        Func<CollectorExecutionRequest, CancellationToken, ValueTask<XeFileSourcePage>> read)
    { _catalog = catalog; _read = read; }

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DeadlockReadExecutionContext context = request.DeadlockReadContext
            ?? throw new ArgumentException("Incremental deadlock collection requires a fenced read context.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout.Value < Manifest.Limits.CommandTimeout ? request.Timeout.Value : Manifest.Limits.CommandTimeout);
        try
        {
            if (request.CapabilityProfile.ServerIdentity is null)
                return Failure(CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityProfileStale);
            if (!Manifest.SupportedVersions.Contains(request.CapabilityProfile.ServerIdentity.Version.Major))
                return Failure(CollectorRunOutcome.Unsupported, CollectorRunReason.TargetVersionUnsupported);
            XeFileSourcePage page = await _read(request, deadline.Token).ConfigureAwait(false);
            if (page.State != XeFileSourceState.Ready)
            {
                if (page.State == XeFileSourceState.ResetRequired && page.Source is not null
                    && context.ResumeSource is not null && page.Source != context.ResumeSource)
                {
                    // Publish only the trusted header's new epoch. SQL independently
                    // detects the transition and clears the bookmark. No event or
                    // end-of-stream claim may escape this reset probe; coverage
                    // starts at the probe and remains unknown until the gap expires.
                    var resetProbe = new DeadlockReadSourceProof(request.TargetId, request.TargetRevision, context.ScheduleRevision, request.RunId,
                        context.ExpectedCheckpointDigest, DeadlockReadSourceState.Ready, page.Source, page.ReadAtUtc, page.ReadAtUtc, [], false, true);
                    context.Validate(resetProbe);
                    return Result(CollectorRunOutcome.Partial, CollectorRunReason.OutputValidationFailed, CollectorPayload.Empty,
                        new(0, 0, page.ResponseBytes, 0), new(CollectorLossKind.OutputValidationFailure, 1, false, 0), resetProbe);
                }
                DeadlockReadSourceState state = page.State switch
                {
                    XeFileSourceState.Missing => DeadlockReadSourceState.Missing,
                    XeFileSourceState.ConfigurationInvalid => DeadlockReadSourceState.ConfigurationInvalid,
                    XeFileSourceState.ResetRequired => DeadlockReadSourceState.ResetRequired,
                    _ => throw new InvalidDataException("Unsupported XE source state."),
                };
                var proof = new DeadlockReadSourceProof(request.TargetId, request.TargetRevision, context.ScheduleRevision, request.RunId,
                    context.ExpectedCheckpointDigest, state, null, page.ReadAtUtc, null, [], false, true);
                return Result(CollectorRunOutcome.Unsupported, CollectorRunReason.VisibilityIncomplete, CollectorPayload.Empty,
                    new(0, 0, page.ResponseBytes, 0), CollectorLossEvidence.None, proof);
            }
            var sourceProof = new DeadlockReadSourceProof(request.TargetId, request.TargetRevision, context.ScheduleRevision, request.RunId,
                context.ExpectedCheckpointDigest, DeadlockReadSourceState.Ready, page.Source, page.ReadAtUtc, page.RetainedFromUtc,
                page.Positions, page.ReachedEnd, page.SourceLoss);
            context.Validate(sourceProof);
            var payload = new CollectorPayload(deadlocks: page.Deadlocks);
            CollectorLossEvidence loss = page.SourceLoss ? new(CollectorLossKind.OutputValidationFailure, Math.Max(1, page.RejectedDeadlocks), false, 0) : CollectorLossEvidence.None;
            return Result(page.SourceLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
                page.SourceLoss ? CollectorRunReason.OutputValidationFailed : CollectorRunReason.Completed, payload,
                new(page.Positions.Count, payload.ItemCount, Math.Max(page.ResponseBytes, payload.EstimatedSizeBytes), payload.EstimatedSizeBytes), loss, sourceProof);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Failure(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { return Failure(CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number))
        { return Failure(CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsTransient(exception.Number))
        { return Failure(CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (SqlException) { return Failure(CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidCastException or OverflowException)
        { return Failure(CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed, rejected: true); }

        CollectorExecutionResult Failure(CollectorRunOutcome outcome, CollectorRunReason reason, bool rejected = false)
            => Result(outcome, reason, CollectorPayload.Empty, new(0, 0, 0, 0),
                rejected ? new(CollectorLossKind.OutputValidationFailure, 1, false, 0) : CollectorLossEvidence.None, context.UnavailableProof());
        CollectorExecutionResult Result(CollectorRunOutcome outcome, CollectorRunReason reason, CollectorPayload payload,
            CollectorRunAccounting accounting, CollectorLossEvidence loss, DeadlockReadSourceProof proof)
            => new(request.TargetId, request.TargetRevision, Manifest.Id, 2, 2, outcome, reason, payload, accounting, loss, proof);
    }

    private async ValueTask<XeFileSourcePage> ReadSourceAsync(CollectorExecutionRequest request, CancellationToken token)
    {
        var factory = new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        await using SqlConnection connection = await factory.OpenConnectionAsync(request.ConnectionPolicy, token).ConfigureAwait(false);
        DeadlockReadExecutionContext context = request.DeadlockReadContext!;
        return await _catalog.Source.ReadAsync(connection, request.TargetId, request.TargetRevision, context.Bookmark, context.ResumeSource, new(), token).ConfigureAwait(false);
    }
}
