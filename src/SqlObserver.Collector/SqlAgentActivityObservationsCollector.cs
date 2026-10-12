using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.Collector;

/// <summary>Captures explicitly enrolled, bounded Agent source observations without live-state admission.</summary>
public sealed class SqlAgentActivityObservationsCollector : ICollector
{
    private readonly Func<CollectorExecutionRequest, CancellationToken, ValueTask<SqlAgentModuleCaptureResult>> read;

    public SqlAgentActivityObservationsCollector(SqlServerAgentActivityModuleCaptureReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        read = reader.ReadAsync;
    }

    internal SqlAgentActivityObservationsCollector(Func<CollectorExecutionRequest, CancellationToken,
        ValueTask<SqlAgentModuleCaptureResult>> read) => this.read = read ?? throw new ArgumentNullException(nameof(read));

    public CollectorManifest Manifest { get; } = CreateManifest();
    public static CollectorOutputContract OutputContract { get; } = new(new(1), [], 0, 0, 0,
        maxSqlAgentActivityObservations: 1);

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = request.CapabilityProfile;
        var identity = profile.ServerIdentity;
        if (identity is null)
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major) ||
            !Manifest.SupportedPlatforms.Contains(identity.Platform) || !Manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (Manifest.RequiredCapabilities.Any(required => !profile.Capabilities.Any(item =>
            item.CapabilityId == required && item.Availability == CapabilityAvailability.Available)))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        if (!profile.IsUsable || !profile.TransportEncrypted || profile.IsSysAdmin ||
            profile.AuthenticationScheme is not (SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm))
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        try
        {
            // The public reader owns the original deadline, fresh enrollment fences and all native identity checks.
            SqlAgentModuleCaptureResult capture = await read(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new CollectorPayload(sqlAgentModuleCapture: capture);
            var accounting = new CollectorRunAccounting(capture.Capture.SourceRowsRead, 1,
                SqlAgentActivityObservationsOutputValidation.AccountedResponseBytes(capture), payload.EstimatedSizeBytes);
            CollectorRunOutcome outcome = capture.Capture.Truncated ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded;
            CollectorLossEvidence loss = capture.Capture.Truncated ? new(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None;
            SqlAgentActivityObservationsOutputValidation.Validate(payload, request.TargetId, request.TargetRevision, request.RunId, outcome, accounting, loss);
            return new(request.TargetId, request.TargetRevision, Manifest.Id, 1, 1, outcome,
                capture.Capture.Truncated ? CollectorRunReason.SourceRowLimit : CollectorRunReason.Completed, payload, accounting, loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException error) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(error.Number))
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException error) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(error.Number))
        { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException error) when (SqlServerCollectorErrorClassifier.IsTransient(error.Number))
        { return Failure(request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (SqlException)
        { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (UnauthorizedAccessException)
        { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (PlatformNotSupportedException)
        { return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException)
        { return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
    }

    private CollectorExecutionResult Failure(CollectorExecutionRequest request, CollectorRunOutcome outcome, CollectorRunReason reason) =>
        new(request.TargetId, request.TargetRevision, Manifest.Id, 1, 1, outcome, reason, CollectorPayload.Empty, new(0, 0, 0, 0),
            outcome == CollectorRunOutcome.OutputInvalid ? new(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None);

    private static CollectorManifest CreateManifest() => new(new("sql-agent.activity-observations"),
        new("SQL Agent activity observations"), new(1),
        [new("connection.tds"), new("authentication.windows-integrated"), new("transport.tls-validated"), new("privilege.non-sysadmin"), new("platform.windows")],
        [], new(17, 17), [SqlServerPlatform.Windows], new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)),
        new(TimeSpan.FromSeconds(5), SqlServerAgentActivityModuleCaptureReader.MaximumBudget,
            SqlAgentActivityCapture.MaximumSourceRows, SqlAgentActivityObservationsOutputValidation.MaximumResponseBytes, CollectorEstimatedCost.Moderate),
        new(CollectorFallbackMode.Unsupported), new(1), CollectorOperationalMode.Passive,
        [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise], [new("capability.connection"), new("engine.core")],
        new(1, TimeSpan.Zero, 3, TimeSpan.FromMinutes(5)), CollectorOutputKind.SqlAgentActivityObservations);
}
