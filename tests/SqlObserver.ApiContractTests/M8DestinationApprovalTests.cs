using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.Windows;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class M8DestinationApprovalTests
{
    [Fact]
    public async Task ApprovalUsesConfiguredResolverAndServerOwnedCanonicalBinding()
    {
        Guid target = Guid.NewGuid();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AlertDestinations:primary"] = "https://alerts.example.test/hook",
            ["AlertDestinations:primary:Kind"] = "https-webhook",
            ["AlertDestinations:primary:Revision"] = "7",
            ["AlertDestinations:primary:Allowlist"] = "93.184.216.0/24",
        }).Build();
        var approval = new ConfiguredAlertDestinationApproval(configuration, new FakeDns(IPAddress.Parse("93.184.216.34")), new FakeEventWriter());
        AlertDestinationWriteRequest request = Request(target, "primary");

        AlertDestinationApproval result = await approval.PreflightAsync(request, CancellationToken.None);

        Assert.Equal("https-webhook", result.Kind);
        Assert.Equal(7, result.Revision);
        Assert.Equal(target, result.Scope);
        Assert.Matches("^[0-9a-f]{64}$", result.ConfigurationDigest);
        Assert.Null(request.Approval); // caller cannot smuggle approval metadata
    }

    [Fact]
    public async Task ApprovalRejectsUnknownUnsafeAndUnprovisionedSources()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AlertDestinations:unsafe"] = "https://127.0.0.1/hook",
        }).Build();
        var approval = new ConfiguredAlertDestinationApproval(configuration, new FakeDns(IPAddress.Parse("93.184.216.34")), new FakeEventWriter());

        await Assert.ThrowsAsync<AlertDestinationValidationException>(() => approval.PreflightAsync(Request(Guid.NewGuid(), "missing"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<AlertDestinationValidationException>(() => approval.PreflightAsync(Request(Guid.NewGuid(), "unsafe"), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<AlertDestinationValidationException>(() => approval.PreflightAsync(Request(Guid.NewGuid(), "event-log", "windows-event-log", "Other Source"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ApprovalMapsResolverOutageToBoundedTransientValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AlertDestinations:primary"] = "https://alerts.example.test/hook",
            ["AlertDestinations:primary:Kind"] = "https-webhook",
            ["AlertDestinations:primary:Revision"] = "1",
            ["AlertDestinations:primary:Allowlist"] = "93.184.216.0/24",
        }).Build();
        var approval = new ConfiguredAlertDestinationApproval(configuration, new FailingDns(), new FakeEventWriter());

        AlertDestinationValidationException exception = await Assert.ThrowsAsync<AlertDestinationValidationException>(() => approval.PreflightAsync(Request(Guid.NewGuid(), "primary"), CancellationToken.None).AsTask());

        Assert.Equal(503, exception.StatusCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task ApprovalMapsEventLogProviderFailureToBoundedTransientValidation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AlertDestinations:event-log:Kind"] = "windows-event-log",
        }).Build();
        var approval = new ConfiguredAlertDestinationApproval(configuration, new FakeDns(IPAddress.Parse("93.184.216.34")), new ThrowingEventWriter());

        AlertDestinationValidationException exception = await Assert.ThrowsAsync<AlertDestinationValidationException>(() => approval.PreflightAsync(Request(Guid.NewGuid(), "event-log", "windows-event-log", WindowsEventLogAlertDestination.SourceName), CancellationToken.None).AsTask());

        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task ApprovalMapsArbitraryConfigurationProviderFailureToBoundedTransientValidation()
    {
        var approval = new ConfiguredAlertDestinationApproval(new ThrowingConfiguration(), new FakeDns(IPAddress.Parse("93.184.216.34")), new FakeEventWriter());

        AlertDestinationValidationException exception = await Assert.ThrowsAsync<AlertDestinationValidationException>(() => approval.PreflightAsync(Request(Guid.NewGuid(), "primary"), CancellationToken.None).AsTask());

        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task RevisionAndScopeChangesProduceDifferentBindings()
    {
        var values = new Dictionary<string, string?>
        {
            ["AlertDestinations:primary"] = "https://alerts.example.test/hook",
            ["AlertDestinations:primary:Kind"] = "https-webhook",
            ["AlertDestinations:primary:Revision"] = "1",
            ["AlertDestinations:primary:Allowlist"] = "93.184.216.0/24",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var approval = new ConfiguredAlertDestinationApproval(configuration, new FakeDns(IPAddress.Parse("93.184.216.34")), new FakeEventWriter());
        AlertDestinationApproval first = await approval.PreflightAsync(Request(Guid.NewGuid(), "primary"), CancellationToken.None);
        values["AlertDestinations:primary:Revision"] = "2";
        IConfiguration changed = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        AlertDestinationApproval second = await new ConfiguredAlertDestinationApproval(changed, new FakeDns(IPAddress.Parse("93.184.216.34")), new FakeEventWriter()).PreflightAsync(Request(first.Scope, "primary"), CancellationToken.None);

        Assert.NotEqual(first.ConfigurationDigest, second.ConfigurationDigest);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.NotEqual(first.Scope, (await approval.PreflightAsync(Request(Guid.NewGuid(), "primary"), CancellationToken.None)).Scope);
    }

    private static AlertDestinationWriteRequest Request(Guid target, string reference, string kind = "https-webhook", string? actualReference = null) => AlertDestinationWriteRequest.Create(
        Guid.NewGuid(), kind, actualReference ?? reference, true, Guid.NewGuid().ToString("D"),
        new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-100"), new AuditCorrelationId(Guid.NewGuid()), AdministrativeAuditAction.UpdateAlertDestination, new MonitoredInstanceId(target)),
        new RepositoryCallTimeout(TimeSpan.FromSeconds(5))) with { Approve = true };

    private sealed class FakeDns(IPAddress address) : IAlertDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<IPAddress>>([address]);
    }
    private sealed class FailingDns : IAlertDnsResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => throw new SocketException((int)SocketError.HostNotFound);
    }
    private sealed class FakeEventWriter : IEventLogAlertWriter
    {
        public bool IsPreProvisioned => true;
        public ValueTask WriteAsync(string sourceName, string logName, string message, int eventId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class ThrowingEventWriter : IEventLogAlertWriter
    {
        public bool IsPreProvisioned => throw new InvalidOperationException("event provider unavailable");
        public ValueTask WriteAsync(string sourceName, string logName, string message, int eventId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class ThrowingConfiguration : IConfiguration
    {
        public string? this[string key] { get => throw new InvalidOperationException("provider failure"); set => throw new InvalidOperationException("provider failure"); }
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
        public IConfigurationSection GetSection(string key) => throw new InvalidOperationException("provider failure");
    }
}
