using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Collectors;

public enum CollectorEligibilityStatus
{
    Eligible = 1,
    CapabilityProfileMissing = 2,
    CapabilityProfileStale = 3,
    TargetUnsupported = 4,
    VersionUnsupported = 5,
    PlatformUnsupported = 6,
    EditionUnsupported = 7,
    CapabilityMissing = 8,
    PermissionDenied = 9,
}

public sealed class CollectorEligibilityResult
{
    public CollectorEligibilityResult(
        CollectorEligibilityStatus status,
        CollectorId? alternateCollectorId = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        Status = status;
        AlternateCollectorId = alternateCollectorId;
    }

    public CollectorEligibilityStatus Status { get; }
    public CollectorId? AlternateCollectorId { get; }
    public bool IsEligible => Status == CollectorEligibilityStatus.Eligible;
}

public static class CollectorEligibilityEvaluator
{
    public static CollectorEligibilityResult Evaluate(
        CollectorManifest manifest,
        CollectorDueWorkItem work)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(work);
        CollectorEligibilityStatus status = EvaluateStatus(manifest, work);
        return new CollectorEligibilityResult(
            status,
            status == CollectorEligibilityStatus.Eligible
                ? null
                : manifest.Fallback.AlternateCollectorId);
    }

    private static CollectorEligibilityStatus EvaluateStatus(
        CollectorManifest manifest,
        CollectorDueWorkItem work)
    {
        CapabilityProfile? profile = work.CapabilityProfile;
        if (profile is null)
        {
            return CollectorEligibilityStatus.CapabilityProfileMissing;
        }

        if (profile.ValidUntilUtc <= work.RepositoryTimeUtc)
        {
            return CollectorEligibilityStatus.CapabilityProfileStale;
        }

        if (!profile.IsUsable || profile.ServerIdentity is null)
        {
            return CollectorEligibilityStatus.TargetUnsupported;
        }

        SqlServerIdentity identity = profile.ServerIdentity;
        if (!manifest.SupportedVersions.Contains(identity.Version.Major))
        {
            return CollectorEligibilityStatus.VersionUnsupported;
        }

        if (!manifest.SupportedPlatforms.Contains(identity.Platform))
        {
            return CollectorEligibilityStatus.PlatformUnsupported;
        }

        if (!manifest.SupportedEngineEditions.Contains(identity.EngineEdition))
        {
            return CollectorEligibilityStatus.EditionUnsupported;
        }

        var capabilities = profile.Capabilities.ToDictionary(
            static evidence => evidence.CapabilityId.Value,
            StringComparer.Ordinal);
        if (manifest.RequiredCapabilities.Any(required =>
                !capabilities.TryGetValue(required.Value, out CapabilityEvidence? evidence) ||
                evidence.Availability != CapabilityAvailability.Available))
        {
            return CollectorEligibilityStatus.CapabilityMissing;
        }

        var permissions = profile.Permissions.ToDictionary(
            static evidence => (evidence.PermissionId.Value, evidence.Scope));
        if (manifest.RequiredPermissions
            .Where(required => required.ApplicableVersions.Contains(identity.Version.Major))
            .Any(required =>
                !permissions.TryGetValue((required.PermissionId.Value, required.Scope), out PermissionEvidence? evidence) ||
                evidence.Outcome != PermissionEvidenceOutcome.Granted))
        {
            return CollectorEligibilityStatus.PermissionDenied;
        }

        return CollectorEligibilityStatus.Eligible;
    }

    public static CollectorRunReason ToRunReason(CollectorEligibilityStatus status) => status switch
    {
        CollectorEligibilityStatus.CapabilityProfileMissing => CollectorRunReason.CapabilityProfileMissing,
        CollectorEligibilityStatus.CapabilityProfileStale => CollectorRunReason.CapabilityProfileStale,
        CollectorEligibilityStatus.TargetUnsupported => CollectorRunReason.TargetUnsupported,
        CollectorEligibilityStatus.VersionUnsupported => CollectorRunReason.TargetVersionUnsupported,
        CollectorEligibilityStatus.PlatformUnsupported => CollectorRunReason.TargetPlatformUnsupported,
        CollectorEligibilityStatus.EditionUnsupported => CollectorRunReason.TargetEditionUnsupported,
        CollectorEligibilityStatus.CapabilityMissing => CollectorRunReason.CapabilityMissing,
        CollectorEligibilityStatus.PermissionDenied => CollectorRunReason.RequiredPermissionMissing,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
