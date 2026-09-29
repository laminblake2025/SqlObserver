using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class ConfigurationCollectionAdministrationHttpTests(TargetHealthApiFactory factory)
    : IClassFixture<TargetHealthApiFactory>
{
    [Fact]
    public async Task AdministratorCanToggleAndScopedViewerIsDeniedAndAudited()
    {
        var repository = new FakeAdministrationPort();
        var audit = new FakeAuditPort();
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConfigurationCollectionAdministrationPort>();
                services.AddSingleton<IConfigurationCollectionAdministrationPort>(repository);
                services.RemoveAll<IAdministrativeAuditPort>();
                services.AddSingleton<IAdministrativeAuditPort>(audit);
                services.RemoveAll<WindowsGroupRoleResolver>();
                services.AddSingleton(new WindowsGroupRoleResolver(
                [
                    new WindowsGroupRoleBinding(
                        new ActorSecurityIdentifier(TestAuthenticationHandler.ViewerGroupSid),
                        [ApplicationRole.TargetAdministrator], true),
                    new WindowsGroupRoleBinding(
                        new ActorSecurityIdentifier(TestAuthenticationHandler.ScopedGroupSid),
                        [ApplicationRole.Viewer], false,
                        [new MonitoredInstanceId(FakeHealthProjectionRepository.ScopedTargetId)]),
                ]));
            }));
        Guid target = FakeHealthProjectionRepository.ExistingTargetId;
        string route = $"/api/v1/observation-targets/{target:D}/health/server-configuration-collection";
        using HttpClient admin = host.CreateClient();
        admin.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        using HttpResponseMessage applied = await admin.PutAsJsonAsync(route,
            new { expectedTargetRevision = 1, expectedEnabled = false, enabled = true });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(1, repository.Calls);
        Assert.Equal(target, repository.LastRequest?.TargetId.Value);
        Assert.Equal(1L, repository.LastRequest?.ExpectedTargetRevision);
        Assert.Equal(AdministrativeAuditAction.UpdateObservationTarget,
            repository.LastRequest?.Audit.Action);

        using HttpClient viewer = host.CreateClient();
        viewer.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "scoped");
        using HttpResponseMessage denied = await viewer.PutAsJsonAsync(route,
            new { expectedTargetRevision = 1, expectedEnabled = false, enabled = true });
        Assert.True(denied.StatusCode == HttpStatusCode.Forbidden,
            $"Expected forbidden, got {denied.StatusCode}: {await denied.Content.ReadAsStringAsync()}; audit={audit.Calls}; repository={repository.Calls}");
        Assert.Equal(1, repository.Calls);
        Assert.Equal(1, audit.Calls);
        Assert.Equal(AdministrativeAuditReason.RequiredRoleMissing,
            audit.LastRequest?.Record.Reason);

        using HttpResponseMessage invalid = await admin.PutAsJsonAsync(route,
            new { expectedTargetRevision = 0, expectedEnabled = false, enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(1, repository.Calls);
    }

    private sealed class FakeAdministrationPort : IConfigurationCollectionAdministrationPort
    {
        internal int Calls { get; private set; }
        internal SetConfigurationCollectionRequest? LastRequest { get; private set; }

        public ValueTask<ConfigurationCollectionMutationResult> SetAsync(
            SetConfigurationCollectionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(new ConfigurationCollectionMutationResult(
                ConfigurationCollectionMutationStatus.Applied, 2, request.Enabled));
        }
    }

    private sealed class FakeAuditPort : IAdministrativeAuditPort
    {
        internal int Calls { get; private set; }
        internal AppendAdministrativeAuditRequest? LastRequest { get; private set; }

        public ValueTask<AdministrativeAuditReceipt> AppendAsync(
            AppendAdministrativeAuditRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return ValueTask.FromResult(new AdministrativeAuditReceipt(
                new AdministrativeAuditId(Guid.NewGuid()),
                new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        }
    }
}
