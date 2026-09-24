using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Deployment;
using SqlObserver.Domain.Repository;
using System.Text.Json;

namespace SqlObserver.UnitTests;

public sealed class M12LifecycleAssessmentContractTests
{
    [Fact]
    public void AssessmentAcceptsHistoryBeyondOneApplyBatch()
    {
        var request = new MigrationAssessmentRequest(257, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)));
        MigrationHistoryEntry[] history = Enumerable.Range(1, 257)
            .Select(number => new MigrationHistoryEntry(number, $"{number:D4}_example.sql", new string('a', 64)))
            .ToArray();
        var result = new MigrationAssessmentResult(MigrationAssessmentStatus.Pending, history,
            new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero), "pending_migrations", nextMigrationNumber: 258);

        Assert.Equal(257, request.MaxHistory);
        Assert.Equal(257, result.ObservedHistoryCount);
        Assert.Equal(257, result.History.Count);
        Assert.Equal(256, MigrationBatchResult.MaximumResults);
    }

    // Production-default options; enum attributes and null-property attributes
    // must make the wire shape conform without test-local converters.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ProductIdentityBinding Product = new(
        "sqlobserver",
        "1.2.0",
        new string('a', 40),
        new string('b', 64),
        1024,
        "run-01",
        "local-windows");

    [Fact]
    public async Task AssessmentIsDeterministicAndNeverReadyToMutate()
    {
        var port = new FakeMigrationPort(new MigrationAssessmentResult(
            MigrationAssessmentStatus.Current,
            [],
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
            "current"));
        var service = new LifecycleAssessmentService(
            port,
            () => new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));

        DeploymentLifecycleAssessment result = await service.AssessAsync(
            new LifecycleAssessmentRequest(
                DeploymentLifecycleAction.Install,
                Product,
                Product,
                new MigrationAssessmentRequest(256, new RepositoryCallTimeout(TimeSpan.FromSeconds(1))),
                ownerPolicy: null),
            CancellationToken.None);

        Assert.False(result.ReadyToMutate);
        Assert.Equal(LifecycleAssessmentStatus.NotReady, result.Status);
        Assert.Equal("owner_policy_unresolved", result.Checks[0].Code);
        Assert.Equal(TimeSpan.Zero, result.EvaluatedAtUtc.Offset);
        Assert.Equal(1, port.Calls);
    }

    [Fact]
    public async Task MissingObservedProductAndFailedMigrationFailClosedWithoutExceptionText()
    {
        var port = new FakeMigrationPort(new MigrationAssessmentResult(
            MigrationAssessmentStatus.Drift,
            [],
            DateTimeOffset.UtcNow,
            "checksum_drift"));
        var service = new LifecycleAssessmentService(port);

        DeploymentLifecycleAssessment result = await service.AssessAsync(
            new LifecycleAssessmentRequest(
                DeploymentLifecycleAction.Upgrade,
                Product,
                observedProduct: null,
                migration: new MigrationAssessmentRequest(1, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)))),
            CancellationToken.None);

        Assert.False(result.ReadyToMutate);
        Assert.Contains(result.Checks, c => c.Status == LifecycleCheckStatus.NotEvaluated);
        Assert.Contains(result.Checks, c => c.Code == "checksum_drift");
        Assert.DoesNotContain(result.Checks, c => c.Detail?.Contains("Exception", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Theory]
    [InlineData("version", "2.0.0")]
    [InlineData("product", "OtherProduct")]
    [InlineData("digest", "C")]
    [InlineData("commit", "D")]
    [InlineData("run", "other-run")]
    [InlineData("environment", "other-environment")]
    public async Task EveryProductBindingFieldUsesOrdinalComparison(string field, string marker)
    {
        ProductIdentityBinding observed = field switch
        {
            "version" => NewProduct(version: marker),
            "product" => NewProduct(productId: marker),
            "digest" => NewProduct(bundleDigest: new string('B', 64)),
            "commit" => NewProduct(commit: new string('A', 40)),
            "run" => NewProduct(runId: marker),
            "environment" => NewProduct(environment: marker),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var service = new LifecycleAssessmentService(new FakeMigrationPort(new MigrationAssessmentResult(MigrationAssessmentStatus.Current, [], DateTimeOffset.UtcNow, "current")));
        DeploymentLifecycleAssessment result = await service.AssessAsync(new LifecycleAssessmentRequest(DeploymentLifecycleAction.Install, Product, observed, new MigrationAssessmentRequest(1, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)))), CancellationToken.None);
        string expectedCheckId = field switch { "product" => "product_identity", "digest" => "bundle_digest", _ => field };
        Assert.Contains(result.Checks, check => check.CheckId == expectedCheckId && check.Status == LifecycleCheckStatus.Failed);
    }

    [Fact]
    public async Task CaseOnlyProductIdentityChangesAreMismatches()
    {
        ProductIdentityBinding observed = NewProduct(productId: "SQLOBSERVER", version: "1.2-RC");
        var expected = NewProduct(productId: "sqlobserver", version: "1.2-Rc");
        var service = new LifecycleAssessmentService(new FakeMigrationPort(new MigrationAssessmentResult(MigrationAssessmentStatus.Current, [], DateTimeOffset.UtcNow, "current")));
        DeploymentLifecycleAssessment result = await service.AssessAsync(new LifecycleAssessmentRequest(DeploymentLifecycleAction.Install, expected, observed, new MigrationAssessmentRequest(1, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)))), CancellationToken.None);
        Assert.Contains(result.Checks, check => check.CheckId == "product_identity" && check.Status == LifecycleCheckStatus.Failed);
        Assert.Contains(result.Checks, check => check.CheckId == "version" && check.Status == LifecycleCheckStatus.Failed);
    }

    [Fact]
    public void PublicApiCannotForgeOwnerApprovalAndNonEmptyHistorySerializesToClosedWireShape()
    {
        Assert.Empty(typeof(PinnedOwnerPolicy).GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
        var entry = new MigrationHistoryEntry(1, "0001_repository_bootstrap.sql", new string('a', 64));
        var result = new MigrationAssessmentResult(MigrationAssessmentStatus.Pending, [entry], new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), "pending_migrations", 2, new PostgreSqlVersion(18, 4));
        string json = JsonSerializer.Serialize(result, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("pending", document.RootElement.GetProperty("status").GetString());
        Assert.EndsWith("Z", document.RootElement.GetProperty("evaluatedAtUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal("0001_repository_bootstrap.sql", document.RootElement.GetProperty("history")[0].GetProperty("name").GetString());
        Assert.Equal(64, document.RootElement.GetProperty("history")[0].GetProperty("checksum").GetString()?.Length);
        Assert.DoesNotContain("accepted", json, StringComparison.OrdinalIgnoreCase);

        var lifecycle = new DeploymentLifecycleAssessment(
            DeploymentLifecycleAction.Install,
            Product,
            [new LifecycleAssessmentCheck("owner_policy", LifecycleCheckStatus.Blocked, "owner_policy_unresolved"), new LifecycleAssessmentCheck("product_identity", LifecycleCheckStatus.Passed, "matched"), new LifecycleAssessmentCheck("migration_history", LifecycleCheckStatus.Passed, "current")],
            new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        using JsonDocument lifecycleDocument = JsonDocument.Parse(JsonSerializer.Serialize(lifecycle, JsonOptions));
        Assert.Equal("install", lifecycleDocument.RootElement.GetProperty("action").GetString());
        Assert.False(lifecycleDocument.RootElement.GetProperty("readyToMutate").GetBoolean());
        Assert.EndsWith("Z", lifecycleDocument.RootElement.GetProperty("evaluatedAtUtc").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MigrationAssessmentStatus.NotEvaluated, "not_evaluated")]
    [InlineData(MigrationAssessmentStatus.Current, "current")]
    [InlineData(MigrationAssessmentStatus.Pending, "pending")]
    [InlineData(MigrationAssessmentStatus.Drift, "drift")]
    [InlineData(MigrationAssessmentStatus.Gap, "gap")]
    [InlineData(MigrationAssessmentStatus.Unknown, "unknown")]
    [InlineData(MigrationAssessmentStatus.UnsupportedMajor, "unsupported_major")]
    [InlineData(MigrationAssessmentStatus.LockUnavailable, "lock_unavailable")]
    [InlineData(MigrationAssessmentStatus.TimedOut, "timed_out")]
    [InlineData(MigrationAssessmentStatus.AccessDenied, "access_denied")]
    [InlineData(MigrationAssessmentStatus.InvalidHistory, "invalid_history")]
    public void EveryMigrationStatusUsesTheClosedWireToken(MigrationAssessmentStatus status, string wireValue)
    {
        string json = JsonSerializer.Serialize(new MigrationAssessmentResult(status, [], DateTimeOffset.UtcNow, "safe_code", status == MigrationAssessmentStatus.Pending ? 1 : null), JsonOptions);
        Assert.Equal(wireValue, JsonDocument.Parse(json).RootElement.GetProperty("status").GetString());
        Assert.Equal(status == MigrationAssessmentStatus.Pending, JsonDocument.Parse(json).RootElement.TryGetProperty("nextMigrationNumber", out _));
    }

    [Theory]
    [InlineData(DeploymentLifecycleAction.Install, "install")]
    [InlineData(DeploymentLifecycleAction.Upgrade, "upgrade")]
    [InlineData(DeploymentLifecycleAction.Recover, "recover")]
    [InlineData(DeploymentLifecycleAction.Uninstall, "uninstall")]
    [InlineData(DeploymentLifecycleAction.Purge, "purge")]
    public void EveryLifecycleActionUsesTheClosedWireToken(DeploymentLifecycleAction action, string wireValue)
    {
        var assessment = new DeploymentLifecycleAssessment(action, Product, [new LifecycleAssessmentCheck("owner_policy", LifecycleCheckStatus.Blocked, "owner_policy_unresolved"), new LifecycleAssessmentCheck("product_identity", LifecycleCheckStatus.NotEvaluated, "not_evaluated"), new LifecycleAssessmentCheck("migration_history", LifecycleCheckStatus.NotEvaluated, "not_evaluated")], DateTimeOffset.UtcNow);
        string json = JsonSerializer.Serialize(assessment, JsonOptions);
        Assert.Equal(wireValue, JsonDocument.Parse(json).RootElement.GetProperty("action").GetString());
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new LifecycleAssessmentService(new ThrowingPort());
        var task = service.AssessAsync(new LifecycleAssessmentRequest(DeploymentLifecycleAction.Install, Product, Product, new MigrationAssessmentRequest(1, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)))), cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    private static ProductIdentityBinding NewProduct(string? productId = null, string? version = null, string? commit = null, string? bundleDigest = null, string? runId = null, string? environment = null) =>
        new(productId ?? Product.ProductId, version ?? Product.Version, commit ?? Product.Commit, bundleDigest ?? Product.BundleDigest, Product.BundleSize, runId ?? Product.RunId, environment ?? Product.Environment);

    private sealed class FakeMigrationPort(MigrationAssessmentResult result) : IMigrationAssessmentPort
    {
        public int Calls { get; private set; }

        public ValueTask<MigrationAssessmentResult> AssessAsync(MigrationAssessmentRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ThrowingPort : IMigrationAssessmentPort
    {
        public async ValueTask<MigrationAssessmentResult> AssessAsync(MigrationAssessmentRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
