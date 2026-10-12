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
using SqlObserver.Domain.Backups;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class BackupFreshnessPolicyHttpTests(TargetHealthApiFactory factory) : IClassFixture<TargetHealthApiFactory>
{
    [Fact]
    public async Task ExplicitPolicyCanBeSetAndReadAtTheRequestedCutoffAndDenialsAreAudited()
    {
        var port = new Repository(); var audit = new Audit(); using WebApplicationFactory<Program> host = Host(port, audit);
        string route = Route(); using HttpClient admin = host.CreateClient(); admin.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        using HttpResponseMessage missing = await admin.GetAsync(route + "?targetRevision=1"); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using HttpResponseMessage set = await admin.PutAsJsonAsync(route, Body()); Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(TimeSpan.FromDays(1), port.Last!.Policy.MaximumFullAge); Assert.Equal(TimeSpan.FromMinutes(15), port.Last.Policy.MaximumLogAge);
        using HttpResponseMessage read = await admin.GetAsync(route + "?targetRevision=1&atUtc=2026-01-01T00%3A00%3A00Z");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode); Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), port.ReadAt);
        Assert.Equal("full_image", (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fullRequirement").GetString());
        using HttpClient viewer = host.CreateClient(); viewer.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "scoped");
        using HttpResponseMessage denied = await viewer.PutAsJsonAsync(route, Body()); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(1, port.Writes); Assert.Equal(1, audit.Calls);
        using HttpResponseMessage viewerRead = await viewer.GetAsync(route + "?targetRevision=1"); Assert.Equal(HttpStatusCode.OK, viewerRead.StatusCode);
        using HttpClient anonymous = host.CreateClient(); using HttpResponseMessage unauthenticated = await anonymous.PutAsJsonAsync(route, Body());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task MissingDefaultsInvalidThresholdsAndCrossOriginCannotMutateAndRepositoryConflictsAreActionable()
    {
        var port = new Repository(); var audit = new Audit(); using WebApplicationFactory<Program> host = Host(port, audit);
        using HttpClient admin = host.CreateClient(); admin.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        using HttpResponseMessage missing = await admin.PutAsJsonAsync(Route(), new { expectedTargetRevision = 1, operationId = Guid.NewGuid(), enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using HttpResponseMessage bounds = await admin.PutAsJsonAsync(Route(), Body(full: 2592001)); Assert.Equal(HttpStatusCode.BadRequest, bounds.StatusCode);
        using var plain = new StringContent(JsonSerializer.Serialize(Body()), System.Text.Encoding.UTF8, "text/plain");
        using HttpResponseMessage media = await admin.PutAsync(Route(), plain); Assert.Equal(HttpStatusCode.UnsupportedMediaType, media.StatusCode);
        admin.DefaultRequestHeaders.Add("Origin", "https://other.example");
        using HttpResponseMessage origin = await admin.PutAsJsonAsync(Route(), Body()); Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        Assert.Equal(0, port.Writes); admin.DefaultRequestHeaders.Remove("Origin");
        foreach (var (status, http, code) in new[]
        {
            (BackupPolicyMutationStatus.LogThresholdRequired, HttpStatusCode.BadRequest, "log_threshold_required"),
            (BackupPolicyMutationStatus.EvidenceUnavailable, HttpStatusCode.Conflict, "backup_inventory_unavailable"),
            (BackupPolicyMutationStatus.RevisionConflict, HttpStatusCode.Conflict, "backup_policy_conflict"),
        })
        {
            port.Status = status; using HttpResponseMessage result = await admin.PutAsJsonAsync(Route(), Body());
            Assert.Equal(http, result.StatusCode); Assert.Equal(code, (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    private WebApplicationFactory<Program> Host(Repository repository, Audit audit) => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.RemoveAll<IBackupFreshnessPolicyPort>(); services.AddSingleton<IBackupFreshnessPolicyPort>(repository);
        services.RemoveAll<IAdministrativeAuditPort>(); services.AddSingleton<IAdministrativeAuditPort>(audit);
        services.RemoveAll<WindowsGroupRoleResolver>(); services.AddSingleton(new WindowsGroupRoleResolver(
        [new(new(TestAuthenticationHandler.ViewerGroupSid), [ApplicationRole.TargetAdministrator], true),
         new(new(TestAuthenticationHandler.ScopedGroupSid), [ApplicationRole.Viewer], true)]));
    }));
    private static string Route() => $"/api/v1/observation-targets/{FakeHealthProjectionRepository.ExistingTargetId:D}/health/backups/databases/{new string('a', 64)}/policy/";
    private static object Body(int full = 86400) => new { expectedTargetRevision = 1, expectedPolicyRevision = 0, operationId = Guid.NewGuid(),
        enabled = true, maximumFullAgeSeconds = full, maximumLogAgeSeconds = 900, maximumObservationAgeSeconds = 300,
        fullRequirement = "full_image", requireChecksums = false };
    private sealed class Repository : IBackupFreshnessPolicyPort
    {
        public int Writes { get; private set; }
        public SetBackupFreshnessPolicyRequest? Last { get; private set; }
        public DateTimeOffset ReadAt { get; private set; }
        public BackupPolicyMutationStatus Status { get; set; } = BackupPolicyMutationStatus.Applied;
        public ValueTask<BackupPolicyMutationResult> SetAsync(SetBackupFreshnessPolicyRequest request, CancellationToken token)
        { Writes++; Last = request; return ValueTask.FromResult(new BackupPolicyMutationResult(Status, request.Policy.PolicyRevision)); }
        public ValueTask<BackupFreshnessPolicy?> ReadAsync(ReadBackupFreshnessPolicyRequest request, CancellationToken token)
        { ReadAt = request.AtUtc; return ValueTask.FromResult(Last?.Policy); }
    }
    private sealed class Audit : IAdministrativeAuditPort
    {
        public int Calls { get; private set; }
        public ValueTask<AdministrativeAuditReceipt> AppendAsync(AppendAdministrativeAuditRequest request, CancellationToken token)
        { Calls++; return ValueTask.FromResult(new AdministrativeAuditReceipt(new(Guid.NewGuid()), DateTimeOffset.UnixEpoch)); }
    }
}
