using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class BackupZoneQualificationHttpTests(TargetHealthApiFactory factory) : IClassFixture<TargetHealthApiFactory>
{
    [Fact]
    public async Task AdministratorCanProvisionReadAndRevokeWithExplicitEvidenceAndRevisionChecks()
    {
        var port = new Repository(); var audit = new Audit();
        using WebApplicationFactory<Program> host = Host(port, audit);
        using HttpClient admin = host.CreateClient(); admin.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        string route = Route(FakeHealthProjectionRepository.ExistingTargetId);
        using HttpClient anonymous = host.CreateClient();
        using HttpResponseMessage anonymousRead = await anonymous.GetAsync(route + "?targetRevision=1");
        using HttpResponseMessage anonymousWrite = await anonymous.PutAsJsonAsync(route, Body());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousWrite.StatusCode);
        using HttpResponseMessage applied = await admin.PutAsJsonAsync(route, Body());
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode); Assert.Equal(1, port.Calls);
        Assert.NotNull(port.LastRequest!.Change.Qualification); Assert.Equal(new string('a', 64), port.LastRequest.Change.EvidenceSha256);
        using HttpResponseMessage read = await admin.GetAsync(route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        JsonElement body = await read.Content.ReadFromJsonAsync<JsonElement>(); Assert.True(body.GetProperty("enabled").GetBoolean());
        using HttpResponseMessage revoke = await admin.PutAsJsonAsync(route, new
        { expectedTargetRevision = 1, expectedQualificationRevision = 1, operationId = Guid.NewGuid(), enabled = false });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode); Assert.Null(port.LastRequest!.Change.Qualification);
        port.Conflict = true;
        using HttpResponseMessage conflict = await admin.PutAsJsonAsync(route, Body());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task MissingAttestationMalformedRulesCrossOriginAndOutOfScopeGrantsCannotProvision()
    {
        var port = new Repository(); var audit = new Audit();
        using WebApplicationFactory<Program> host = Host(port, audit);
        using HttpClient admin = host.CreateClient(); admin.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        string route = Route(FakeHealthProjectionRepository.ExistingTargetId);
        using HttpResponseMessage unconfirmed = await admin.PutAsJsonAsync(route, Body(confirmed: false));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        using HttpResponseMessage malformed = await admin.PutAsJsonAsync(route, Body(rules: "invalid"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var plain = new StringContent(JsonSerializer.Serialize(Body()), System.Text.Encoding.UTF8, "text/plain");
        using HttpResponseMessage contentType = await admin.PutAsync(route, plain);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, contentType.StatusCode);
        admin.DefaultRequestHeaders.Add("Origin", "https://other.example");
        using HttpResponseMessage origin = await admin.PutAsJsonAsync(route, Body());
        Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        using HttpClient mixed = host.CreateClient(); mixed.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "scoped");
        using HttpResponseMessage denied = await mixed.PutAsJsonAsync(route, Body());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(1, audit.Calls);
        Assert.Equal(AdministrativeAuditReason.TargetOutOfScope, audit.Reason);
        using HttpResponseMessage deniedRead = await mixed.GetAsync(route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode); Assert.Equal(0, port.Calls);
    }

    private WebApplicationFactory<Program> Host(Repository port, Audit audit) => factory.WithWebHostBuilder(builder =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IBackupZoneQualificationAdministrationPort>(); services.AddSingleton<IBackupZoneQualificationAdministrationPort>(port);
            services.RemoveAll<IAdministrativeAuditPort>(); services.AddSingleton<IAdministrativeAuditPort>(audit);
            services.RemoveAll<WindowsGroupRoleResolver>(); services.AddSingleton(new WindowsGroupRoleResolver(
            [new(new(TestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.TargetAdministrator], true),
             new(new(TestAuthenticationHandler.ScopedGroupSid), [ApplicationRole.Viewer, ApplicationRole.TargetAdministrator], false,
                [new MonitoredInstanceId(FakeHealthProjectionRepository.ScopedTargetId)])]));
        }));
    private static string Route(Guid id) => $"/api/v1/observation-targets/{id:D}/health/backup-zone-qualification/";
    private static object Body(bool confirmed = true, string? rules = null) => new
    {
        expectedTargetRevision = 1, expectedQualificationRevision = 0, operationId = Guid.NewGuid(), enabled = true,
        historicalConfigurationConfirmed = confirmed, serializedZoneRules = rules ?? TimeZoneInfo.Utc.ToSerializedString(),
        sourceZoneId = "UTC", fromUtc = "2026-01-01T00:00:00Z", toUtc = "2027-01-01T00:00:00Z", evidenceSha256 = new string('a', 64),
    };
    private sealed class Repository : IBackupZoneQualificationAdministrationPort
    {
        public int Calls { get; private set; }
        public bool Conflict { get; set; }
        public SetBackupZoneQualificationRequest? LastRequest { get; private set; }
        public ValueTask<BackupZoneQualificationMutationResult> SetAsync(SetBackupZoneQualificationRequest request, CancellationToken token)
        {
            Calls++; LastRequest = request;
            return ValueTask.FromResult(new BackupZoneQualificationMutationResult(Conflict ?
                BackupZoneQualificationMutationStatus.RevisionConflict : BackupZoneQualificationMutationStatus.Applied,
                request.Change.ExpectedRevision + 1));
        }
        public ValueTask<BackupZoneQualificationSnapshot?> ReadAsync(MonitoredInstanceId target, ObservationTargetRevision revision, CancellationToken token)
        { Calls++; return ValueTask.FromResult<BackupZoneQualificationSnapshot?>(new(1, LastRequest?.Change.Qualification, LastRequest?.Change.EvidenceSha256)); }
    }
    private sealed class Audit : IAdministrativeAuditPort
    {
        public int Calls { get; private set; }
        public AdministrativeAuditReason? Reason { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken token)
        { Calls++; Reason = request.Record.Reason; return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero))); }
    }
}
