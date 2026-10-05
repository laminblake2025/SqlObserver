using System.Security.Principal;
using System.Text.Json;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Opt-in read-only qualification of the staged production lifecycle reader.
/// Captures are not canonical commit receipts and do not certify AG consistency or physical failover.</summary>
public sealed class ServerLifecycleNativeQualificationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresServerLifecycleQualification")]
    public async Task ActualConsecutiveLifecycleCapturesPreserveStableEngineEvidence()
    {
        var context = await PrepareAsync();
        var before = await CaptureAsync(context);
        await Task.Delay(TimeSpan.FromSeconds(1));
        var after = await CaptureAsync(context);
        Assert.NotEqual(before.RunId, after.RunId);
        Assert.True(before.SourceObservedAtUtc < after.SourceObservedAtUtc, "Consecutive actual source clocks did not advance.");
        Assert.Equal(ServerLifecycleCoverage.Complete, before.EngineCoverage);
        Assert.Equal(ServerLifecycleCoverage.Complete, after.EngineCoverage);
        Assert.True(before.Engine.PhysicalNodeFingerprint == after.Engine.PhysicalNodeFingerprint, "Responding node changed during stable qualification; private identities withheld.");
        Assert.True(before.Engine.LogicalInstanceFingerprint == after.Engine.LogicalInstanceFingerprint, "Logical instance changed during stable qualification; private identities withheld.");
        Assert.True(before.Engine.StartupIncarnationFingerprint == after.Engine.StartupIncarnationFingerprint, "Engine startup incarnation changed during stable qualification; private identities withheld.");
        Assert.True(before.Engine.ProcessId == after.Engine.ProcessId, "Engine process changed during stable qualification; private identities withheld.");
        Assert.True(before.Engine.StartMsTicks == after.Engine.StartMsTicks, "Engine startup tick changed during stable qualification.");
        Assert.True(after.Engine.CurrentMsTicks >= before.Engine.CurrentMsTicks, "Engine uptime counter regressed.");
        Emit("stable_consecutive_source_captures", new
        {
            beforeObservedUtc = before.SourceObservedAtUtc, afterObservedUtc = after.SourceObservedAtUtc,
            distinctRunIds = true, respondingNodeStable = true, logicalInstanceStable = true, engineIncarnationStable = true,
            source_consistency_verified = false, canonicalCommitPerformed = false, physicalFailoverQualified = false,
            restartTransitionQualified = false,
        });
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresServerLifecycleQualification")]
    public async Task ActualLifecycleCoverageAndAccountingRemainExplicitAndBounded()
    {
        var context = await PrepareAsync();
        var snapshot = await CaptureAsync(context);
        Assert.Equal(ServerLifecycleSnapshot.CurrentSourceSchemaVersion, snapshot.SourceSchemaVersion);
        Assert.InRange(snapshot.SourceRowsRead, 1, SqlServerLifecycleCollector.MaximumRows + 1);
        Assert.InRange(snapshot.Members.Count, 0, SqlServerLifecycleCollector.MaximumRows - 1);
        Assert.InRange(snapshot.EstimatedSizeBytes, 512, SqlServerLifecycleCollector.MaximumResponseBytes);
        Assert.Equal(DateTimeKind.Unspecified, snapshot.Engine.SourceStartLocal!.Value.Kind);
        Assert.False(snapshot.Engine.HardwareIdentityVerified); Assert.Null(snapshot.Engine.OccurredAtUtc);
        if (snapshot.IsHadrEnabled == false)
        {
            Assert.Equal(ServerLifecycleCoverage.Unsupported, snapshot.AvailabilityCoverage);
            Assert.Empty(snapshot.Members);
        }
        else if (snapshot.IsHadrEnabled is null)
            Assert.Equal(ServerLifecycleCoverage.Unknown, snapshot.AvailabilityCoverage);
        // Complete membership remains a source observation; no snapshot isolation/AG consistency claim is added.
        Emit("actual_coverage_branch", new
        {
            snapshot.IsHadrEnabled, snapshot.EngineCoverage, snapshot.AvailabilityCoverage, snapshot.SourceRowsRead,
            memberCount = snapshot.Members.Count, snapshot.Truncated, snapshot.EstimatedSizeBytes,
            source_consistency_verified = false, physicalFailoverQualified = false,
            nativeOverflowScenarioExercised = snapshot.Truncated,
            nativePartialAgObserved = snapshot.AvailabilityCoverage is ServerLifecycleCoverage.Partial or ServerLifecycleCoverage.Unknown,
            nativeUnsupportedAgObserved = snapshot.AvailabilityCoverage == ServerLifecycleCoverage.Unsupported,
        });
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresServerLifecycleQualification")]
    public async Task PreCancelledLifecycleCollectorOpensNoSource()
    {
        var context = await PrepareAsync(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var collector = CreateCollector(context);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync(context.Contract.ExecutionRequest(context.Profile), cancelled.Token));
        Emit("pre_cancelled_collector", new { requestCancelled = true, source_consistency_verified = false,
            qualifiesInFlightProviderCancellation = false, canonicalCommitPerformed = false });
    }

    private async Task<ServerLifecycleSnapshot> CaptureAsync(Context context)
    {
        var collector = CreateCollector(context); var request = context.Contract.ExecutionRequest(context.Profile);
        CollectorExecutionResult result;
        try { result = await collector.CollectAsync(request, CancellationToken.None); }
        catch (Exception failure) { throw SafeFailure("production_lifecycle_collector", failure); }
        Emit("production_lifecycle_result", new { result.Outcome, result.Reason, result.Accounting.SourceRowsRead,
            result.Accounting.OutputItemsProduced, result.Accounting.ResponseBytes, result.Accounting.OutputBytes,
            loss = result.Loss.Kind, source_consistency_verified = false });
        Assert.True(result.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial,
            "Production lifecycle source did not return evidence: " + result.Outcome + "/" + result.Reason);
        new CollectorOutputValidator(SqlServerLifecycleCollector.OutputContract).Validate(collector.Manifest, request, result);
        var snapshot = Assert.IsType<ServerLifecycleSnapshot>(result.Payload.OperationalHealth!.Snapshot);
        Assert.Equal(request.TargetId, snapshot.TargetId); Assert.Equal(request.TargetRevision, snapshot.TargetRevision);
        Assert.Equal(request.RunId, snapshot.RunId);
        Assert.Equal(snapshot.SourceRowsRead, result.Accounting.SourceRowsRead);
        Assert.Equal(1 + snapshot.Members.Count, result.Accounting.OutputItemsProduced);
        Assert.Equal(snapshot.EstimatedSizeBytes, result.Accounting.OutputBytes);
        Assert.InRange(result.Accounting.ResponseBytes, result.Accounting.OutputBytes, SqlServerLifecycleCollector.MaximumResponseBytes);
        bool incomplete = snapshot.EngineCoverage != ServerLifecycleCoverage.Complete ||
            snapshot.AvailabilityCoverage is ServerLifecycleCoverage.Partial or ServerLifecycleCoverage.Unknown;
        if (snapshot.Truncated)
        {
            Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
            Assert.True(result.Loss.Kind is CollectorLossKind.SourceRowLimit or CollectorLossKind.ResponseByteLimit);
        }
        else if (incomplete)
        {
            Assert.Equal(CollectorRunOutcome.Partial, result.Outcome);
            Assert.Equal(CollectorLossKind.VisibilityIncomplete, result.Loss.Kind);
            Assert.Equal(CollectorRunReason.VisibilityIncomplete, result.Reason);
        }
        else { Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome); Assert.False(result.Loss.HasLoss); }
        return snapshot;
    }

    private async Task<Context> PrepareAsync()
    {
        var contract = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
        if (!OperatingSystem.IsWindows()) throw new XunitException("Native lifecycle qualification requires Windows; no source was opened.");
        using (var identity = WindowsIdentity.GetCurrent())
            Assert.True(string.Equals(identity.User?.Value, contract.ExpectedWindowsSid, StringComparison.Ordinal), "Current Windows token does not match the configured monitor; private identity withheld.");
        CapabilityProfile profile;
        try { profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(contract.DiscoveryRequest(), CancellationToken.None); }
        catch (Exception failure) { throw SafeFailure("production_capability_discovery", failure); }
        Assert.Equal(contract.TargetId, profile.TargetId); Assert.Equal(contract.TargetRevision, profile.TargetRevision);
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.NotNull(profile.ServerIdentity); Assert.Equal(contract.ExpectedSqlMajor, profile.ServerIdentity.Version.Major);
        Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
        var context = new Context(contract, profile); var manifest = CreateCollector(context).Manifest;
        foreach (var permission in manifest.RequiredPermissions.Where(p => p.ApplicableVersions.Contains(contract.ExpectedSqlMajor)))
            Assert.True(profile.Permissions.Any(p => p.PermissionId == permission.PermissionId && p.Scope == permission.Scope && p.Outcome == PermissionEvidenceOutcome.Granted),
                "Actual discovery lacks lifecycle source permission: " + permission.PermissionId.Value);
        foreach (var capability in manifest.RequiredCapabilities)
            Assert.True(profile.Capabilities.Any(c => c.CapabilityId == capability && c.Availability == CapabilityAvailability.Available),
                "Actual discovery lacks lifecycle source capability: " + capability.Value);
        Emit("actual_monitor_discovery", new { windowsMonitorIdentityMatched = true, encrypted = profile.TransportEncrypted,
            nonSysadmin = !profile.IsSysAdmin, observedSqlMajor = profile.ServerIdentity.Version.Major,
            source_consistency_verified = false });
        return context;
    }
    private static SqlServerLifecycleCollector CreateCollector(Context context) =>
        new(SqlServerLifecycleAssetCatalog.LoadEmbedded(), context.Contract.Key);
    private void Emit(string stage, object evidence) => output.WriteLine(JsonSerializer.Serialize(new { stage, evidence }, JsonOptions));
    private static XunitException SafeFailure(string stage, Exception failure) =>
        new("Native lifecycle qualification failed at " + stage + "; exception type=" + failure.GetType().Name + "; private details withheld.");
    private sealed record Context(PairedCpuQualificationLabContract Contract, CapabilityProfile Profile);
}
