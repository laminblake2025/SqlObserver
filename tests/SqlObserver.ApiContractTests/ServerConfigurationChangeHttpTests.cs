using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class ServerConfigurationChangeHttpTests(TargetHealthApiFactory factory)
    : IClassFixture<TargetHealthApiFactory>
{
    private const string Window = "fromUtc=2026-09-29T00%3A00%3A00Z&toUtc=2026-09-29T01%3A00%3A00Z&limit=2";

    [Fact]
    public async Task TargetHistoryPagesAndRejectsForeignOrStaleCursors()
    {
        var repository = new FakeServerConfigurationChangePort();
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IServerConfigurationChangeRepositoryPort>();
                services.AddSingleton<IServerConfigurationChangeRepositoryPort>(repository);
            }));
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "viewer");
        Guid target = FakeHealthProjectionRepository.ExistingTargetId;
        string route = $"/api/v1/observation-targets/{target:D}/health/server-configuration-changes?{Window}";

        using HttpResponseMessage first = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using JsonDocument firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(2, firstBody.RootElement.GetProperty("items").GetArrayLength());
        string cursor = firstBody.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        using HttpResponseMessage second = await client.GetAsync(
            $"{route}&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using JsonDocument secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(3, secondBody.RootElement.GetProperty("items")[0]
            .GetProperty("configurationId").GetInt32());
        Assert.Equal(JsonValueKind.Null, secondBody.RootElement.GetProperty("nextCursor").ValueKind);
        Assert.Equal(2, repository.Calls);

        using HttpClient scopedClient = host.CreateClient();
        scopedClient.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, "scoped");
        using HttpResponseMessage outOfScope = await scopedClient.GetAsync(route);
        Assert.Equal(HttpStatusCode.Forbidden, outOfScope.StatusCode);
        Assert.Equal(2, repository.Calls);

        using HttpResponseMessage malformed = await client.GetAsync($"{route}&cursor=invalid%21");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        string tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        using HttpResponseMessage altered = await client.GetAsync(
            $"{route}&cursor={Uri.EscapeDataString(tampered)}");
        Assert.Equal(HttpStatusCode.BadRequest, altered.StatusCode);
        var cursorCodec = host.Services.GetRequiredService<ServerConfigurationChangeCursorCodec>();
        string foreignCursor = cursorCodec.Encode(
            cursorCodec.Decode(cursor) with
            { TargetId = new MonitoredInstanceId(FakeHealthProjectionRepository.ScopedTargetId) });
        using HttpResponseMessage foreign = await client.GetAsync(
            $"{route}&cursor={Uri.EscapeDataString(foreignCursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        using HttpResponseMessage missingWindow = await client.GetAsync(
            $"/api/v1/observation-targets/{target:D}/health/server-configuration-changes?cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.BadRequest, missingWindow.StatusCode);
        Assert.Equal(2, repository.Calls);

        repository.Stale = true;
        using HttpResponseMessage stale = await client.GetAsync(
            $"{route}&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using JsonDocument staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
        Assert.Equal("cursor_stale", staleBody.RootElement.GetProperty("code").GetString());
    }

    private sealed class FakeServerConfigurationChangePort : IServerConfigurationChangeRepositoryPort
    {
        private static readonly Guid Run = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal int Calls { get; private set; }
        internal bool Stale { get; set; }

        public ValueTask<ServerConfigurationChangePage> ReadAsync(
            ServerConfigurationChangeRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Stale && request.Cursor is not null)
                throw new ServerConfigurationCursorStaleException(new InvalidOperationException());
            DateTimeOffset at = new(2026, 9, 29, 0, 30, 0, TimeSpan.Zero);
            ServerConfigurationChange Item(int id) => new(id, $"Option {id}", "int",
                "0", id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "1", "1", true, false, at.AddMinutes(-5), at.AddMinutes(-1), at, Run);
            ServerConfigurationChange[] items = request.Cursor is null
                ? [Item(1), Item(2)] : [Item(3)];
            ServerConfigurationChangeCursor? next = request.Cursor is null
                ? new ServerConfigurationChangeCursor(request.TargetId, request.FromUtc,
                    request.ToUtc, 1, 1, at, Run, 2) : null;
            return ValueTask.FromResult(new ServerConfigurationChangePage(request.TargetId,
                request.FromUtc, request.ToUtc, 1, 1, items, next));
        }
    }
}
