using System.Text.Json;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentActivityObservationsCollectorTests
{
    [Fact]
    public void PinnedCatalogKeepsDisabledBoundedModuleContractWithoutDirectPermissionRequirements()
    {
        var assets = SqlAgentActivityObservationsAssetCatalog.LoadEmbedded();
        var collector = new SqlAgentActivityObservationsCollector((_, _) => throw new InvalidOperationException());
        using var document = JsonDocument.Parse(assets.ManifestJson);
        var manifest = document.RootElement;
        Assert.Equal(SqlAgentActivityObservationsAssetCatalog.ExpectedBundleChecksum, assets.BundleChecksum);
        Assert.False(manifest.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal(collector.Manifest.Id.Value, manifest.GetProperty("collectorId").GetString());
        Assert.Equal(collector.Manifest.DisplayName.Value, manifest.GetProperty("displayName").GetString());
        Assert.Equal(collector.Manifest.Limits.MaxRows, manifest.GetProperty("executionBounds").GetProperty("maximumRows").GetInt32());
        Assert.Equal(collector.Manifest.Limits.MaxResponseBytes, manifest.GetProperty("executionBounds").GetProperty("maximumResponseBytes").GetInt32());
        Assert.Equal(collector.Manifest.Limits.CommandTimeout.TotalSeconds, manifest.GetProperty("executionBounds").GetProperty("commandTimeoutSeconds").GetInt32());
        Assert.Equal(17, manifest.GetProperty("supportedTargets").GetProperty("minimumMajorVersion").GetInt32());
        Assert.Equal(17, manifest.GetProperty("supportedTargets").GetProperty("maximumMajorVersion").GetInt32());
        Assert.Equal(0, manifest.GetProperty("requiredPermissionsByMajor").GetProperty("17").GetArrayLength());
        Assert.Equal(collector.Manifest.DependsOn.Select(item => item.Value), manifest.GetProperty("dependsOn").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("Never execute this asset directly", assets.QuerySql, StringComparison.Ordinal);
        Assert.Contains("EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=@maximum_rows;", assets.QuerySql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(512)]
    public async Task StoppedServiceRetainsBoundedObservationsWithoutClaimingLiveState(int rows)
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var source = SqlAgentActivityObservationsTestData.Capture(request, rows);
        var collector = new SqlAgentActivityObservationsCollector((owned, token) =>
        { Assert.Same(request, owned); Assert.Equal(CancellationToken.None, token); return ValueTask.FromResult(source); });
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(CollectorRunReason.Completed, result.Reason);
        Assert.Same(source, result.Payload.SqlAgentModuleCapture);
        Assert.Equal(1, result.Payload.ItemCount); Assert.Equal(rows, result.Accounting.SourceRowsRead);
        Assert.Equal(4096 + rows * 768, result.Accounting.ResponseBytes);
        Assert.Equal(4096 + rows * 768, result.Accounting.OutputBytes); Assert.False(result.Loss.HasLoss);
        Assert.False(source.ProductionAdmitted); Assert.False(source.ServiceSessionAssociationQualified);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, source.NativeQualification);
        var evaluation = SqlAgentActivityEvaluator.Evaluate(source.Capture);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, evaluation.Gap);
        Assert.All(evaluation.Jobs, job =>
        { Assert.Equal(SqlAgentActivityCandidateState.Unknown, job.CandidateState); Assert.Null(job.ElapsedSeconds); Assert.Null(job.StartedAtUtc); Assert.Null(job.LongRunning); });
        Assert.Empty(collector.Manifest.RequiredPermissions);
        Assert.Contains(collector.Manifest.DependsOn, item => item.Value == "engine.core");
    }

    [Fact]
    public async Task Sentinel513Retains512JobsWithExplicitPartialAndConservativeLoss()
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var source = SqlAgentActivityObservationsTestData.Capture(request, 513);
        var collector = new SqlAgentActivityObservationsCollector((_, _) => ValueTask.FromResult(source));
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Partial, result.Outcome); Assert.Equal(CollectorRunReason.SourceRowLimit, result.Reason);
        Assert.Equal(512, result.Payload.SqlAgentModuleCapture!.Capture.Jobs.Count);
        Assert.Equal(513, result.Accounting.SourceRowsRead); Assert.Equal(1, result.Accounting.OutputItemsProduced);
        Assert.Equal(4096 + 513 * 768, result.Accounting.ResponseBytes);
        Assert.Equal(4096 + 512 * 768, result.Accounting.OutputBytes);
        Assert.Equal(CollectorLossKind.SourceRowLimit, result.Loss.Kind); Assert.Equal(1, result.Loss.MinimumLostItems);
        Assert.False(result.Loss.CountIsExact); Assert.Equal(0, result.Loss.MinimumLostBytes);
    }

    [Fact]
    public async Task ReversedLocalClockDatesRemainOffsetFreeSourceEvidenceWithoutElapsedArithmetic()
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var source = SqlAgentActivityObservationsTestData.Capture(request, mutation: "local-reversal");
        var collector = new SqlAgentActivityObservationsCollector((_, _) => ValueTask.FromResult(source));
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        var job = Assert.Single(result.Payload.SqlAgentModuleCapture!.Capture.Jobs);
        Assert.Equal(DateTimeKind.Unspecified, job.StartExecutionLocal!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, job.StopExecutionLocal!.Value.Kind);
        Assert.True(job.StopExecutionLocal < job.StartExecutionLocal);
        Assert.Equal(7, job.StartExecutionLocal.Value.Ticks % 10);
        var evaluation = Assert.Single(SqlAgentActivityEvaluator.Evaluate(source.Capture).Jobs);
        Assert.Null(evaluation.ElapsedSeconds); Assert.Null(evaluation.StartedAtUtc); Assert.Null(evaluation.LongRunning);
    }

    [Theory]
    [InlineData("major")] [InlineData("platform")] [InlineData("edition")]
    [InlineData("capability")] [InlineData("sysadmin")] [InlineData("tls")] [InlineData("authentication")]
    public async Task UnsupportedOrUnsafeTargetNeverReachesReader(string mutation)
    {
        int reads = 0;
        var collector = new SqlAgentActivityObservationsCollector((_, _) => { reads++; throw new InvalidOperationException(); });
        var result = await collector.CollectAsync(SqlAgentActivityObservationsTestData.Request(mutation), CancellationToken.None);
        Assert.Equal(0, reads); Assert.Equal(0, result.Payload.ItemCount);
        Assert.Equal(mutation is "sysadmin" or "tls" or "authentication" ? CollectorRunOutcome.PermissionDenied : CollectorRunOutcome.Unsupported, result.Outcome);
    }

    [Theory]
    [InlineData("target")] [InlineData("revision")] [InlineData("run")]
    public async Task ForeignSourceIdentityDiscardsEntireObservation(string mutation)
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var source = SqlAgentActivityObservationsTestData.Capture(request, 1, mutation);
        var collector = new SqlAgentActivityObservationsCollector((_, _) => ValueTask.FromResult(source));
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome);
        Assert.Null(result.Payload.SqlAgentModuleCapture); Assert.Equal(0, result.Accounting.OutputBytes);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Loss.Kind);
    }

    [Theory]
    [InlineData("timeout", CollectorRunOutcome.TimedOut)]
    [InlineData("inner-cancel", CollectorRunOutcome.TimedOut)]
    [InlineData("denied", CollectorRunOutcome.PermissionDenied)]
    [InlineData("unsupported", CollectorRunOutcome.Unsupported)]
    [InlineData("invalid", CollectorRunOutcome.OutputInvalid)]
    public async Task ReaderRefusalAndOriginalDeadlineNeverReleaseOutput(string refusal, CollectorRunOutcome expected)
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        var collector = new SqlAgentActivityObservationsCollector((owned, _) =>
        {
            Assert.Same(request, owned); Assert.Equal(TimeSpan.FromSeconds(1), owned.Timeout.Value);
            throw refusal switch
            {
                "timeout" => new TimeoutException("private values"), "inner-cancel" => new OperationCanceledException("private values"),
                "denied" => new UnauthorizedAccessException("private values"), "unsupported" => new PlatformNotSupportedException("private values"),
                _ => new InvalidDataException("private values"),
            };
        });
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(expected, result.Outcome); Assert.Equal(0, result.Payload.ItemCount);
        Assert.Equal(0, result.Accounting.SourceRowsRead); Assert.Equal(0, result.Accounting.ResponseBytes);
    }

    [Fact]
    public async Task CallerCancellationBeforeAndAfterReadPropagatesWithoutObservation()
    {
        var request = SqlAgentActivityObservationsTestData.Request();
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        var collector = new SqlAgentActivityObservationsCollector((_, _) =>
        { reads++; cancellation.Cancel(); return ValueTask.FromResult(SqlAgentActivityObservationsTestData.Capture(request)); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(request, cancellation.Token).AsTask());
        Assert.Equal(1, reads);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(request, cancellation.Token).AsTask());
        Assert.Equal(1, reads);
    }
}

internal static class SqlAgentActivityObservationsTestData
{
    private static readonly string[] Capabilities = ["connection.tds", "authentication.windows-integrated", "transport.tls-validated", "privilege.non-sysadmin", "platform.windows"];
    internal static CollectorExecutionRequest Request(string mutation = "")
    {
        var original = M4TestData.CreateExecutionRequest();
        var template = M4TestData.CreateProfile(majorVersion: mutation == "major" ? 16 : 17,
            platform: mutation == "platform" ? SqlServerPlatform.Linux : SqlServerPlatform.Windows,
            edition: mutation == "edition" ? SqlServerEngineEdition.Express : SqlServerEngineEdition.Standard);
        bool rejected = mutation is "authentication" or "tls" or "sysadmin";
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, 1, 1,
            template.ServerIdentity, rejected ? CapabilityDiscoveryOutcome.SecurityPolicyRejected : template.Outcome,
            mutation switch { "authentication" => CapabilityDiscoveryReason.AuthenticationSchemeMismatch,
                "tls" => CapabilityDiscoveryReason.TransportNotEncrypted, "sysadmin" => CapabilityDiscoveryReason.ExcessivePrivilege, _ => template.Reason },
            mutation == "authentication" ? SqlServerAuthenticationScheme.SqlAuthentication : template.AuthenticationScheme,
            mutation != "tls", mutation == "sysadmin",
            Capabilities.Where(item => mutation != "capability" || item != "platform.windows").Select(item =>
                new CapabilityEvidence(new(item), CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)).ToArray(),
            [new(new("msdb.syssessions.select"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.Denied)],
            template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc, template.ValidUntilUtc);
        return new(original.RunId, original.TargetId, original.TargetRevision, original.ConnectionPolicy, profile, original.Attempt, new(TimeSpan.FromSeconds(1)));
    }

    internal static SqlAgentModuleCaptureResult Capture(CollectorExecutionRequest request, int rows = 1, string mutation = "")
    {
        var target = mutation == "target" ? new MonitoredInstanceId(Guid.NewGuid()) : request.TargetId;
        var revision = mutation == "revision" ? request.TargetRevision.Next() : request.TargetRevision;
        var run = mutation == "run" ? new CollectorRunId(Guid.NewGuid()) : request.RunId;
        var at = M4TestData.RepositoryTime;
        var local = new DateTime(2026, 8, 23, 13, 0, 0, DateTimeKind.Unspecified).AddTicks(7);
        var header = new SqlAgentActivityHeader(at, new(1, 1, 0, null), new(1, local));
        var jobs = Enumerable.Range(1, Math.Min(rows, 512)).Select(index => new SqlAgentActivityJob(
            Guid.Parse(FormattableString.Invariant($"00000000-0000-0000-0000-{index:x12}")), 1, local, null, local,
            mutation == "local-reversal" ? local.AddHours(-1) : null, 1, local)).ToArray();
        var capture = new SqlAgentActivityCapture(target, revision, run, header, jobs,
            new(at.AddSeconds(1), header.Service, header.Session), rows, rows > 512);
        var binding = new SqlAgentModuleBinding("sql.test.example", 1433, "sql.test.example", "SQLTEST", "SQLTEST", "", 123, 456,
            SqlAgentModuleBinding.ApprovedDefinitionSha256, new string('b', 64), new string('c', 40));
        var enrollment = new SqlAgentModuleEnrollment(target, revision, 7, Guid.Parse("96c57f44-8208-49b7-b568-5e657b867ac4"),
            "S-1-5-21", binding, at.AddMinutes(-1), at.AddMinutes(1), new string('e', 64));
        return new(capture, enrollment);
    }
}
