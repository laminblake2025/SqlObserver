using SqlObserver.Application.Ports;
using SqlObserver.Domain.Deployment;

namespace SqlObserver.Application.Services;

/// <summary>
/// Performs a side-effect-free lifecycle preflight. This service has no mutation port by design.
/// </summary>
public sealed class LifecycleAssessmentService : ILifecycleAssessmentService
{
    private readonly IMigrationAssessmentPort _migrations;
    private readonly Func<DateTimeOffset> _clock;

    public LifecycleAssessmentService(
        IMigrationAssessmentPort migrations,
        Func<DateTimeOffset>? clock = null)
    {
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<DeploymentLifecycleAssessment> AssessAsync(
        LifecycleAssessmentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        DateTimeOffset evaluatedAt = _clock();
        if (evaluatedAt.Offset != TimeSpan.Zero) evaluatedAt = evaluatedAt.ToUniversalTime();

        var checks = new List<LifecycleAssessmentCheck>(8)
        {
            CheckOwnerPolicy(request.OwnerPolicy),
        };

        if (request.ObservedProduct is null)
        {
            checks.Add(new LifecycleAssessmentCheck("product_identity", LifecycleCheckStatus.NotEvaluated, "not_evaluated"));
        }
        else
        {
            AddIdentityChecks(checks, request.ExpectedProduct, request.ObservedProduct);
        }

        MigrationAssessmentResult migration;
        try
        {
            migration = await _migrations.AssessAsync(request.Migration, cancellationToken).ConfigureAwait(false);
            checks.Add(MigrationCheck(migration));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(new LifecycleAssessmentCheck("migration_history", LifecycleCheckStatus.Blocked, "timed_out"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No exception is included in a serializable/loggable assessment.
            checks.Add(new LifecycleAssessmentCheck("migration_history", LifecycleCheckStatus.Blocked, "assessment_failed"));
        }

        return new DeploymentLifecycleAssessment(
            request.Action,
            request.ExpectedProduct,
            checks,
            evaluatedAt,
            request.OwnerPolicy);
    }

    private static LifecycleAssessmentCheck CheckOwnerPolicy(PinnedOwnerPolicy? policy) =>
        new("owner_policy", LifecycleCheckStatus.Blocked, "owner_policy_unresolved");

    private static void AddIdentityChecks(
        List<LifecycleAssessmentCheck> checks,
        ProductIdentityBinding expected,
        ProductIdentityBinding observed)
    {
        checks.Add(new LifecycleAssessmentCheck(
            "product_identity",
            string.Equals(expected.ProductId, observed.ProductId, StringComparison.Ordinal)
                ? LifecycleCheckStatus.Passed : LifecycleCheckStatus.Failed,
            string.Equals(expected.ProductId, observed.ProductId, StringComparison.Ordinal) ? "matched" : "product_mismatch"));
        checks.Add(Match("version", expected.Version, observed.Version, "version_mismatch"));
        checks.Add(Match("bundle_digest", expected.BundleDigest, observed.BundleDigest, "digest_mismatch"));
        checks.Add(new LifecycleAssessmentCheck(
            "bundle_size",
            expected.BundleSize > 0 && expected.BundleSize == observed.BundleSize ? LifecycleCheckStatus.Passed : LifecycleCheckStatus.Failed,
            expected.BundleSize > 0 && expected.BundleSize == observed.BundleSize ? "matched" : "size_mismatch"));
        checks.Add(Match("commit", expected.Commit, observed.Commit, "commit_mismatch"));
        checks.Add(Match("run", expected.RunId, observed.RunId, "run_mismatch"));
        checks.Add(Match("environment", expected.Environment, observed.Environment, "environment_mismatch"));
    }

    private static LifecycleAssessmentCheck Match(string id, string expected, string actual, string mismatchCode) =>
        new(id,
            string.Equals(expected, actual, StringComparison.Ordinal) ? LifecycleCheckStatus.Passed : LifecycleCheckStatus.Failed,
            string.Equals(expected, actual, StringComparison.Ordinal) ? "matched" : mismatchCode);

    private static LifecycleAssessmentCheck MigrationCheck(MigrationAssessmentResult result)
    {
        LifecycleCheckStatus status = result.Status == MigrationAssessmentStatus.Current
            ? LifecycleCheckStatus.Passed
            : result.Status == MigrationAssessmentStatus.NotEvaluated
                ? LifecycleCheckStatus.NotEvaluated
                : LifecycleCheckStatus.Blocked;
        return new LifecycleAssessmentCheck("migration_history", status, result.Code);
    }
}
