using System.Security.Principal;
using System.Text.Json;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Opt-in production source qualification; no permissions, workload or service state is changed.</summary>
public sealed class CpuComponentsNativeQualificationTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresCpuComponentsQualification")]
    public async Task ActualAttestedCollectorPublishesIndependentNativeComponents()
    {
        var context = await PrepareAsync(); var request = context.Contract.ExecutionRequest(context.Profile);
        var collector = new CpuComponentsCollector(Source(context, context.Contract.Host));
        CollectorExecutionResult result;
        try { result = await collector.CollectAsync(request, CancellationToken.None); }
        catch (Exception ex) { throw SafeFailure(ex); }
        output.WriteLine(JsonSerializer.Serialize(new { result.Outcome, result.Reason }, JsonOptions));
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        new CollectorOutputValidator(CpuComponentsCollector.OutputContract).Validate(collector.Manifest, request, result);
        var value = Assert.IsType<CpuComponentsObservation>(result.Payload.CpuComponents);
        output.WriteLine(JsonSerializer.Serialize(new { value.AssociationState, value.AssociationReason, value.NativeAssociationReason,
            value.ExactPairEligible, sqlProcess = Public(value.SqlProcess), operatingSystem = Public(value.OperatingSystem) }, JsonOptions));
        Assert.Equal(request.TargetId, value.TargetId); Assert.Equal(request.TargetRevision, value.TargetRevision);
        Assert.Equal(request.RunId, value.RunId); Assert.Equal(CpuComponentAssociationState.Verified, value.AssociationState);
        Assert.Equal(context.Contract.Host.Binding.Revision.Value, value.HostBindingRevision);
        Assert.Equal(6, value.SourceFrames);
        foreach (var component in new[] { value.SqlProcess, value.OperatingSystem })
        {
            component.Validate(); Assert.True(component.Available, "Independent native provider component unavailable: " + component.Reason);
            Assert.InRange(component.Percent!.Value, 0m, 100m);
            Assert.Equal(CpuComponentValue.CalculationMethod, component.Method);
            Assert.NotNull(component.ObservedFromUtc); Assert.True(component.ObservedToUtc > component.ObservedFromUtc);
            Assert.True(component.ToNative100ns > component.FromNative100ns);
            Assert.Equal(0U, component.ProviderApiStatus); Assert.True(component.ProviderCStatus is 0 or 1);
        }
        Assert.Equal(value.SqlProcess.RawFormulaEligible && value.OperatingSystem.RawFormulaEligible &&
            value.SqlProcess.FromNative100ns == value.OperatingSystem.FromNative100ns &&
            value.SqlProcess.ToNative100ns == value.OperatingSystem.ToNative100ns, value.ExactPairEligible);
        // Private qualification evidence for exact replay into an isolated repository.
        // This closed observation contains counter evidence, not credentials, SIDs or TCP endpoints.
        // It is deliberately distinct from the redacted public history projection.
        output.WriteLine("CPU_COMPONENT_NATIVE_RECEIPT_V1:" + JsonSerializer.Serialize(new
        {
            schemaVersion = 1, observation = value, result.Outcome, result.Reason,
            result.Accounting, result.Loss, result.CollectorManifestVersion, result.OutputSchemaVersion,
        }, JsonOptions));
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresCpuComponentsQualification")]
    public async Task DeniedHostBindingProducesExplicitUnavailableComponents()
    {
        var context = await PrepareAsync(); var existing = context.Contract.Host;
        var denied = new HostTarget(new(existing.TargetId, existing.Binding.HostFingerprint, existing.Binding.Revision, HostBindingState.Denied),
            new(existing.TargetId, existing.TargetRevision, existing.Profile.ProfileRevision, HostMetricCapability.Cpu, HostBindingState.Denied), existing.TargetRevision);
        var collector = new CpuComponentsCollector(Source(context, denied)); var request = context.Contract.ExecutionRequest(context.Profile);
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        new CollectorOutputValidator(CpuComponentsCollector.OutputContract).Validate(collector.Manifest, request, result);
        var value = Assert.IsType<CpuComponentsObservation>(result.Payload.CpuComponents);
        Assert.Equal(CpuComponentAssociationReason.HostBindingDenied, value.AssociationReason);
        Assert.False(value.SqlProcess.Available); Assert.False(value.OperatingSystem.Available); Assert.Equal(0, value.SourceFrames);
        output.WriteLine("Host binding denial was explicit; this case does not claim native process access denial coverage.");
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresCpuComponentsQualification")]
    public async Task PreCancelledProductionCollectorOpensNoSource()
    {
        var context = await PrepareAsync(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var collector = new CpuComponentsCollector(Source(context, context.Contract.Host));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await collector.CollectAsync(context.Contract.ExecutionRequest(context.Profile), cancelled.Token));
        output.WriteLine("Production collector refused pre-cancellation; this case does not claim in-flight native cancellation coverage.");
    }

    private async Task<Context> PrepareAsync()
    {
        var contract = PairedCpuQualificationLabContract.Load(Environment.GetEnvironmentVariable);
        if (!OperatingSystem.IsWindows()) throw new XunitException("CPU components native qualification requires Windows.");
        using (var identity = WindowsIdentity.GetCurrent())
            Assert.True(string.Equals(identity.User?.Value, contract.ExpectedWindowsSid, StringComparison.Ordinal), "Actual monitor identity mismatch; identity withheld.");
        CapabilityProfile profile;
        try { profile = await new SqlServerCapabilityDiscoveryPort().DiscoverAsync(contract.DiscoveryRequest(), CancellationToken.None); }
        catch (Exception ex) { throw SafeFailure(ex); }
        Assert.True(profile.Outcome is CapabilityDiscoveryOutcome.Supported or CapabilityDiscoveryOutcome.Degraded);
        Assert.Equal(contract.ExpectedSqlMajor, profile.ServerIdentity!.Version.Major);
        Assert.Equal(SqlServerPlatform.Windows, profile.ServerIdentity.Platform);
        Assert.True(profile.TransportEncrypted); Assert.False(profile.IsSysAdmin);
        Assert.True(profile.AuthenticationScheme is SqlServerAuthenticationScheme.Kerberos or SqlServerAuthenticationScheme.Ntlm);
        output.WriteLine("Actual Windows monitor identity and encrypted non-sysadmin integrated SQL discovery verified.");
        return new(contract, profile);
    }
    private static CpuComponentsSource Source(Context context, HostTarget host) => new(new Resolver(host),
        new SqlServerCpuTargetWitnessReader(context.Contract.Key), new WindowsCpuAssociationSamplingReader(
            new WindowsCpuTcpAssociationReader(context.Contract.Key), new WindowsTargetCpuPairReader(context.Contract.Key)));
    private static object Public(CpuComponentValue c) => new { c.Percent, c.Quality, c.Reason, c.FromNative100ns, c.ToNative100ns,
        c.ObservedFromUtc, c.ObservedToUtc, c.RawFormulaPercent, c.RawFormulaEligible, c.RawFormulaReason,
        c.ProviderRawPercent, c.ProviderApiStatus, c.ProviderCStatus, c.LogicalProcessorDivisor, c.Method };
    private static XunitException SafeFailure(Exception ex) => new("Production CPU source failed; exception type=" + ex.GetType().Name + "; private details withheld.");
    private sealed record Context(PairedCpuQualificationLabContract Contract, CapabilityProfile Profile);
    private sealed class Resolver(HostTarget host) : IHostTargetResolver
    {
        public ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId id, ObservationTargetRevision revision, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<HostTarget?>(host.TargetId == id && host.TargetRevision == revision ? host : null); }
    }
}
