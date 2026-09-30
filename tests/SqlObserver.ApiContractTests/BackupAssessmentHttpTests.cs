using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class BackupAssessmentHttpTests(TargetHealthApiFactory factory) : IClassFixture<TargetHealthApiFactory>
{
    private static readonly Guid Target = FakeHealthProjectionRepository.ExistingTargetId;
    private static readonly string Route = $"/api/v2/observation-targets/{Target:D}/health/backups";

    [Fact]
    public async Task PagingRestoresCutoffAndReturnsUnconfiguredOrRevokedEvidenceWithoutGreenVerdicts()
    {
        var repository = new Repository(); using WebApplicationFactory<Program> host = Host(repository);
        using HttpClient client = Client(host, "viewer");
        using HttpResponseMessage first = await client.GetAsync(Route + "?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        JsonElement one = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("policy_not_configured", one.GetProperty("items")[0].GetProperty("full").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, one.GetProperty("items")[0].GetProperty("policy").ValueKind);
        Assert.EndsWith("Z", one.GetProperty("atUtc").GetString(), StringComparison.Ordinal);
        DateTimeOffset cutoff = repository.Last!.AtUtc; string cursor = one.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage second = await client.GetAsync(Route + "?limit=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); Assert.Equal(cutoff, repository.Last!.AtUtc);
        JsonElement two = await second.Content.ReadFromJsonAsync<JsonElement>(); JsonElement database = two.GetProperty("items")[0];
        Assert.Equal(2, database.GetProperty("databaseOrdinal").GetInt32());
        Assert.Equal("indeterminate", database.GetProperty("full").GetProperty("status").GetString());
        Assert.Equal("timestamp_qualification_unavailable", database.GetProperty("full").GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, database.GetProperty("full").GetProperty("observedAgeSeconds").ValueKind);
        Assert.Contains("historical source-zone", database.GetProperty("full").GetProperty("action").GetString(), StringComparison.Ordinal);
        Assert.False(database.GetProperty("samples")[0].GetProperty("qualificationTrusted").GetBoolean());
        Assert.EndsWith("Z", database.GetProperty("samples")[0].GetProperty("recordedFinishUtc").GetString(), StringComparison.Ordinal);
        Assert.Equal(long.MaxValue, database.GetProperty("samples")[0].GetProperty("sizeBytes").GetInt64());
        Assert.Equal(2, database.GetProperty("observedIssues").GetArrayLength()); Assert.False(two.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task AuthenticationSignedBindingsInvalidFieldsAndExpiredCursorsAreEnforced()
    {
        var repository = new Repository(); using WebApplicationFactory<Program> host = Host(repository);
        using HttpClient anonymous = Client(host, null); using HttpResponseMessage challenged = await anonymous.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        using HttpClient scoped = Client(host, "scoped"); using HttpResponseMessage denied = await scoped.GetAsync(Route);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, repository.Calls);
        await EndpointAuthorizationAssertions.RequireAuthenticationAsync(host.Services, webInterfaceConfigured: false);
        using HttpClient client = Client(host, "viewer");
        using HttpResponseMessage first = await client.GetAsync(Route + "?limit=1");
        string cursor = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nextCursor").GetString()!;
        var codec = host.Services.GetRequiredService<BackupEvidenceCursorCodec>();
        string foreign = codec.Encode(codec.Decode(cursor) with { TargetId = new(Guid.NewGuid()) });
        foreach (string query in new[] { "limit=0", "limit=101", "targetRevision=0", "atUtc=9999-01-01T00%3A00%3A00Z",
            "atUtc=2026-01-01T00%3A00%3A00%2B00%3A00", "cursor=invalid", "cursor=" + Uri.EscapeDataString(foreign),
            "cursor=" + Uri.EscapeDataString(cursor) + "&targetRevision=2", "cursor=" + Uri.EscapeDataString(cursor) + "&atUtc=2026-01-01T00%3A00%3A00Z" })
        {
            using HttpResponseMessage invalid = await client.GetAsync(Route + "?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("invalid_request", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(1, repository.Calls); repository.Stale = true;
        using HttpResponseMessage expired = await client.GetAsync(Route + "?cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
        Assert.Equal("cursor_stale", (await expired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private WebApplicationFactory<Program> Host(Repository repository) => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    { services.RemoveAll<IBackupEvidencePort>(); services.AddSingleton<IBackupEvidencePort>(repository); }));
    private static HttpClient Client(WebApplicationFactory<Program> host, string? identity)
    {
        HttpClient client = host.CreateClient();
        if (identity is not null) client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        return client;
    }
    private sealed class Repository : IBackupEvidencePort
    {
        private readonly Guid run = Guid.NewGuid();
        public BackupEvidenceRequest? Last { get; private set; }
        public int Calls { get; private set; }
        public bool Stale { get; set; }
        public ValueTask<BackupEvidencePage> ReadAsync(BackupEvidenceRequest request, CancellationToken token)
        {
            Calls++; Last = request; if (Stale) throw new BackupEvidenceCursorStaleException();
            var items = new List<BackupDatabaseEvidence>(); DateTimeOffset at = request.AtUtc;
            int after = request.Cursor?.DatabaseOrdinal ?? 0;
            for (int ordinal = after + 1; ordinal <= Math.Min(2, after + request.Limit); ordinal++)
            {
                string db = new((char)('a' + ordinal), 64), fork = new('f', 64);
                var identity = new BackupV2RunIdentity(request.TargetId, new(1), new(run));
                var context = new BackupV2DatabaseContext(db, fork, fork, null, null, null, true, BackupRecoveryModel.Simple,
                    "ONLINE", BackupFreshnessScope.Standalone, null, null, at, "UTC", at.DateTime.AddDays(-35), false);
                var sample = new BackupV2SampleRead(new(BackupKind.Full, false, at.AddMinutes(-1), at.DateTime.AddMinutes(-1), false, true, true),
                    at.DateTime.AddMinutes(-2), 0, long.MaxValue, fork, fork, new(BackupFinishTimeState.Resolved, at.AddMinutes(-1), 1));
                BackupFreshnessPolicy? policy = ordinal == 1 ? null : new(request.TargetId, new(1), db, 1, true,
                    TimeSpan.FromDays(1), null, TimeSpan.FromMinutes(5), FullBackupRequirement.FullImage, true);
                items.Add(new(ordinal, new(context, [sample], identity, BackupFreshnessCoverage.Complete), policy, [false]));
            }
            var header = new BackupEvidenceHeader(request.TargetId, at, 1, run, at, at, at, "UTC", at.DateTime.AddDays(-35),
                BackupFreshnessCoverage.Complete, BackupEvidenceStatus.Retained, 4, 4, 4, 2, true, 1, 1);
            BackupEvidenceCursor? next = items[^1].DatabaseOrdinal < 2 ? new(request.TargetId, at, 1, run, items[^1].DatabaseOrdinal, 1, 1) : null;
            return ValueTask.FromResult(new BackupEvidencePage(header, items.AsReadOnly(), next));
        }
    }
}
