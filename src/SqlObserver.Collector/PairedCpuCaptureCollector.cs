using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.Collector;

/// <summary>Staged bounded receipt adapter. It is deliberately absent from the production registry.</summary>
public sealed class PairedCpuCaptureCollector : ICollector
{
    private readonly Func<CollectorExecutionRequest, CancellationToken, ValueTask<CpuPairedCaptureObservation>> read;

    public PairedCpuCaptureCollector(PairedCpuCaptureSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        read = source.ReadAsync;
    }

    internal PairedCpuCaptureCollector(Func<CollectorExecutionRequest, CancellationToken,
        ValueTask<CpuPairedCaptureObservation>> read) => this.read = read ?? throw new ArgumentNullException(nameof(read));

    public CollectorManifest Manifest { get; } = CreateManifest();
    public static CollectorOutputContract OutputContract { get; } = new(new(1), [], 0, 0, 0,
        maxCpuPairedCaptureObservations: 1);

    public async ValueTask<CollectorExecutionResult> CollectAsync(CollectorExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = request.CapabilityProfile;
        var identity = profile.ServerIdentity;
        if (identity is null)
            return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.CapabilityProfileStale);
        if (!Manifest.SupportedVersions.Contains(identity.Version.Major) ||
            !Manifest.SupportedPlatforms.Contains(identity.Platform) ||
            !Manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported);
        if (Manifest.RequiredCapabilities.Any(required => !profile.Capabilities.Any(item =>
                item.CapabilityId == required && item.Availability == CapabilityAvailability.Available)))
            return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.CapabilityMissing);
        if (!profile.TransportEncrypted || profile.IsSysAdmin ||
            profile.AuthenticationScheme is not (SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm))
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        if (Manifest.RequiredPermissions.Where(item => item.ApplicableVersions.Contains(identity.Version.Major))
            .Any(required => !profile.Permissions.Any(item => item.PermissionId == required.PermissionId &&
                item.Scope == required.Scope && item.Outcome == PermissionEvidenceOutcome.Granted)))
            return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing);
        try
        {
            CpuPairedCaptureObservation capture = await read(request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var payload = new CollectorPayload(cpuPairedCapture: capture);
            var accounting = new CollectorRunAccounting(capture.SourceFrames, 1,
                checked((int)CpuPairedCaptureOutputValidation.AccountedResponseBytes(capture)), payload.EstimatedSizeBytes);
            CpuPairedCaptureOutputValidation.Validate(payload, request.TargetId, request.TargetRevision,
                request.RunId, CollectorRunOutcome.Succeeded, accounting, CollectorLossEvidence.None);
            return new(request.TargetId, request.TargetRevision, Manifest.Id, 1, 1,
                CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, payload, accounting, CollectorLossEvidence.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (CpuNativeTerminationUnprovenException)
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsCommandTimeout(exception.Number))
        { return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsPermissionDenied(exception.Number))
        { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (SqlException exception) when (SqlServerCollectorErrorClassifier.IsTransient(exception.Number))
        { return Failure(request, CollectorRunOutcome.TransientFailure, CollectorRunReason.TransientTargetFailure); }
        catch (SqlException)
        { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
        catch (UnauthorizedAccessException)
        { return Failure(request, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing); }
        catch (PlatformNotSupportedException)
        { return Failure(request, CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported); }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
        { return Failure(request, CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        { return Failure(request, CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure); }
    }

    private CollectorExecutionResult Failure(CollectorExecutionRequest request,
        CollectorRunOutcome outcome, CollectorRunReason reason) => new(request.TargetId,
        request.TargetRevision, Manifest.Id, 1, 1, outcome, reason, CollectorPayload.Empty, new(0, 0, 0, 0),
        outcome == CollectorRunOutcome.OutputInvalid
            ? new(CollectorLossKind.OutputValidationFailure, 1, false) : CollectorLossEvidence.None);

    private static CollectorManifest CreateManifest() => new(new("cpu.paired-capture"),
        new("Paired CPU candidate capture"), new(1),
        [new("connection.tds"), new("authentication.windows-integrated"), new("transport.tls-validated"),
         new("privilege.non-sysadmin"), new("platform.windows")],
        [new(new("server.view-state"), PermissionEvidenceScope.Server, new(15, 15)),
         new(new("server.view-performance-state"), PermissionEvidenceScope.Server, new(16, 17))],
        new(15, 17), [SqlServerPlatform.Windows], new(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30)),
        new(TimeSpan.FromSeconds(5), SqlServerCpuTargetWitnessReader.MaximumDuration,
            CpuPairedCaptureObservation.MaximumSourceFrames, CpuPairedCaptureOutputValidation.MaximumResponseBytes,
            CollectorEstimatedCost.Moderate), new(CollectorFallbackMode.Unsupported), new(1), CollectorOperationalMode.Passive,
        [SqlServerEngineEdition.Standard, SqlServerEngineEdition.Enterprise],
        [new("capability.connection")], new(1, TimeSpan.Zero, 3, TimeSpan.FromMinutes(5)), CollectorOutputKind.CpuPairedCapture);
}
