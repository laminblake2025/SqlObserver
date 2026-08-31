using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Retention;

namespace SqlObserver.Application.Services;

public sealed class AnalyticsQueryService : IAnalyticsQueryService
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private readonly IAnalyticsRepositoryPort repository;
    public AnalyticsQueryService(IAnalyticsRepositoryPort repository) => this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public async ValueTask<IReadOnlyList<RollupResult>> GetRollupsAsync(AuthorizationContext authorization, AnalyticsQueryRequest request, RollupInterval interval, CancellationToken cancellationToken)
    {
        return (await GetRollupPageAsync(authorization, request, interval, null, cancellationToken).ConfigureAwait(false)).Items;
    }
    public async ValueTask<AnalyticsRollupPage> GetRollupPageAsync(AuthorizationContext authorization, AnalyticsQueryRequest request, RollupInterval interval, string? cursor, CancellationToken cancellationToken)
    {
        AuthorizeRead(authorization, request); Validate(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(Deadline);
        try { return await repository.ReadRollupPageAsync(request with { Timeout = new RepositoryCallTimeout(Deadline) }, interval, cursor, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Analytics repository deadline expired."); }
    }
    public async ValueTask<WindowComparisonResult> CompareAsync(AuthorizationContext authorization, AnalyticsQueryRequest request, DateTimeOffset leftStartUtc, DateTimeOffset leftEndUtc, DateTimeOffset rightStartUtc, DateTimeOffset rightEndUtc, CancellationToken cancellationToken)
    {
        ValidateComparisonWindow(leftStartUtc, leftEndUtc, rightStartUtc, rightEndUtc);
        RollupInterval interval = rightEndUtc - rightStartUtc > TimeSpan.FromHours(24) ? RollupInterval.Day : RollupInterval.Hour;
        var leftRows = await GetRollupsAsync(authorization, request with { FromUtc = leftStartUtc, ToUtc = leftEndUtc }, interval, cancellationToken).ConfigureAwait(false);
        var rightRows = await GetRollupsAsync(authorization, request with { FromUtc = rightStartUtc, ToUtc = rightEndUtc }, interval, cancellationToken).ConfigureAwait(false);
        // Keep the aggregation semantics in one place. In particular, a
        // comparison of rollups must be weighted by each bucket's sample
        // count; averaging bucket means gives the wrong answer for uneven
        // cadence/coverage.
        var left = leftRows.Where(x => x.BucketStartUtc >= leftStartUtc && x.BucketStartUtc < leftEndUtc).ToArray();
        var right = rightRows.Where(x => x.BucketStartUtc >= rightStartUtc && x.BucketStartUtc < rightEndUtc).ToArray();
        double? l = WeightedMean(left), r = WeightedMean(right);
        return new WindowComparisonResult { LeftValue = l, RightValue = r, Delta = l is null || r is null ? null : r - l, Percent = l is null || r is null || l == 0 ? null : (r.Value - l.Value) / Math.Abs(l.Value) * 100, LeftSamples = left.Sum(x => (long)Math.Max(0, x.Count)), RightSamples = right.Sum(x => (long)Math.Max(0, x.Count)), Complete = Complete(left) && Complete(right) };
    }
    private static double? WeightedMean(IEnumerable<RollupResult> rows)
    {
        var usable = rows.Where(x => x.Mean is not null && x.Count > 0 && x.Coverage > 0 && x.VisibilityState is not "unavailable" and not "unsupported").ToArray();
        long count = usable.Sum(x => (long)x.Count);
        return count == 0 ? null : usable.Sum(x => x.Mean!.Value * x.Count) / count;
    }
    private static bool Complete(IEnumerable<RollupResult> rows) => rows.Any() && rows.All(x => x.Mean is not null && x.Count > 0 && x.Coverage >= 1 && !x.Truncated && x.VisibilityState == "complete");
    private static void AuthorizeRead(AuthorizationContext authorization, AnalyticsQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(request);
        if (!authorization.IsActive || (!authorization.HasRole(ApplicationRole.Viewer) && !authorization.HasRole(ApplicationRole.Operator) && !authorization.HasRole(ApplicationRole.TargetAdministrator))) throw new UnauthorizedAccessException("Analytics read role denied.");
        if (!authorization.CanAccess(request.TargetId)) throw new UnauthorizedAccessException("Analytics target scope denied.");
    }
    private static void Validate(AnalyticsQueryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MetricKey) || request.MetricKey.Length > 128 || request.FromUtc.Offset != TimeSpan.Zero || request.ToUtc.Offset != TimeSpan.Zero || request.ToUtc <= request.FromUtc || request.ToUtc - request.FromUtc > TimeSpan.FromDays(90) || request.Limit is < 1 or > 100_000) throw new ArgumentException("Analytics query is outside its bounded UTC contract.", nameof(request));
    }

    private static void ValidateComparisonWindow(DateTimeOffset leftStartUtc, DateTimeOffset leftEndUtc, DateTimeOffset rightStartUtc, DateTimeOffset rightEndUtc)
    {
        if (leftStartUtc.Offset != TimeSpan.Zero || leftEndUtc.Offset != TimeSpan.Zero || rightStartUtc.Offset != TimeSpan.Zero || rightEndUtc.Offset != TimeSpan.Zero ||
            leftEndUtc <= leftStartUtc || rightEndUtc <= rightStartUtc || leftEndUtc - leftStartUtc != rightEndUtc - rightStartUtc ||
            leftEndUtc - leftStartUtc > TimeSpan.FromDays(31) || (leftStartUtc < rightEndUtc && rightStartUtc < leftEndUtc))
            throw new ArgumentException("Comparison windows must be equal-duration, UTC, non-overlapping intervals of at most 31 days.");
    }
}

public sealed class RetentionService : IRetentionService
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private readonly IRetentionRepositoryPort repository;
    public RetentionService(IRetentionRepositoryPort repository) => this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public async ValueTask<SqlObserver.Domain.Retention.RetentionPreview> PreviewAsync(RetentionPreviewQuery request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); if (request.Authorization is null || !request.Authorization.HasRoleForAllTargets(ApplicationRole.SecurityAdministrator)) throw new UnauthorizedAccessException("Only a global SecurityAdministrator may preview retention."); if (request.MaxEntries is < 1 or > 1024 || request.Cursor is { Length: > 1024 } || request.Cursor is not null && request.Cursor.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '|' or '/' or '+' or '='))) throw new ArgumentOutOfRangeException(nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(Deadline); try { return await repository.PreviewAsync(request with { Timeout = new RepositoryCallTimeout(Deadline) }, timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Retention preview deadline expired."); }
    }
    public async ValueTask<RetentionExecutionResult> ExecuteAsync(RetentionExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); if (!request.Authorization.HasRoleForAllTargets(ApplicationRole.SecurityAdministrator)) throw new UnauthorizedAccessException("Only a global SecurityAdministrator may execute retention.");
        if (request.ExecutionId == Guid.Empty || request.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(request.DataClass) || string.IsNullOrWhiteSpace(request.ParentSchema) || string.IsNullOrWhiteSpace(request.ParentTable) || string.IsNullOrWhiteSpace(request.PartitionName) || request.ExpectedPolicyRevision < 1 || request.CorrelationId == Guid.Empty || string.IsNullOrWhiteSpace(request.ChangeReason) || request.ChangeReason.Length > 512 || request.RequestDigest is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit) || request.Operation is not ("detach" or "drop")) throw new ArgumentException("Exact retention identifiers, policy revision, idempotency, digest, correlation, and reason are required.", nameof(request));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(Deadline); try { return await repository.ExecuteAsync(request with { Timeout = new RepositoryCallTimeout(Deadline) }, timeout.Token).ConfigureAwait(false); } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Retention execution deadline expired."); }
    }
    private static void Authorize(AuthorizationContext? authorization, ApplicationRole role, object _)
    { ArgumentNullException.ThrowIfNull(authorization); if (!authorization.IsActive || !authorization.HasRole(role)) throw new UnauthorizedAccessException("Retention read role denied."); }
}

public sealed class RetentionPolicyService : IRetentionPolicyService
{
    private readonly IRetentionPolicyRepositoryPort repository;
    public RetentionPolicyService(IRetentionPolicyRepositoryPort repository) => this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
    public async ValueTask<RetentionPolicyReadResult> GetPolicyAsync(AuthorizationContext authorization, string dataClass, CancellationToken cancellationToken)
    {
        if (authorization is null || !authorization.HasRoleForAllTargets(ApplicationRole.SecurityAdministrator)) throw new UnauthorizedAccessException("Only a global SecurityAdministrator may read retention policy.");
        ValidateDataClass(dataClass); return await repository.GetPolicyAsync(dataClass, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<RetentionPolicyReadResult> UpdatePolicyAsync(RetentionPolicyUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); if (!request.Authorization.IsActive || !request.Authorization.HasRoleForAllTargets(ApplicationRole.SecurityAdministrator)) throw new UnauthorizedAccessException("Only a global SecurityAdministrator may change retention policy.");
        RetentionPolicyValidator.Validate(request.Policy); if (request.ExpectedRevision < 1 || string.IsNullOrWhiteSpace(request.ChangeReason) || request.ChangeReason.Length > 512) throw new ArgumentException("Optimistic revision and change reason are required.", nameof(request));
        return await repository.UpdatePolicyAsync(request with { Timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(5)) }, cancellationToken).ConfigureAwait(false);
    }
    private static void ValidateDataClass(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))) throw new ArgumentException("Invalid retention data class.", nameof(value)); }
}
