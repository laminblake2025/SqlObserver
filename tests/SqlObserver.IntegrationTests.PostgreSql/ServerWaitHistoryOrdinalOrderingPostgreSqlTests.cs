extern alias ServerAssembly;

using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M5ActivityPostgreSqlIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitHistoryUpgradePreservesOrdinalPagingOnEnglishRepositories(bool partitionedRoute)
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync(englishCollation: true);
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var baseline = await migrations.ApplyPendingAsync(new(374, new(TimeSpan.FromMinutes(2))), default);
        Assert.False(baseline.HasFailures);
        Assert.Equal(374, baseline.Results.Count);
        await using (var locale = database.DataSource.CreateCommand("SELECT 'WRITELOG' > 'WRITE_COMPLETION', 'WRITELOG' COLLATE \"C\" > 'WRITE_COMPLETION' COLLATE \"C\";"))
        await using (var row = await locale.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            Assert.False(row.GetBoolean(1));
        }
        DateTimeOffset at = (await ReadRepositoryClockAsync(database)).AddMinutes(-3);
        MonitoredInstanceId target = new(Guid.NewGuid()), other = new(Guid.NewGuid());
        Guid first = Guid.NewGuid(), normal = Guid.NewGuid(), reset = Guid.NewGuid();
        string[] types = ["WRITELOG", "WRITE_COMPLETION", "WAITFOR", "WAIT_XTP_HOST_WAIT"];
        foreach (var run in new[] { (Id: first, At: at), (Id: normal, At: at.AddMinutes(1)), (Id: reset, At: at.AddMinutes(2)) })
        {
            await SeedTargetAndRunAsync(database, target, run.Id, "waits.server", run.At, outputItemCount: types.Length);
            foreach (string type in types)
                await InsertWaitAsync(database, target, run.Id, run.At, type,
                    run.Id == reset ? 1 : run.Id == first ? 10 : 14,
                    run.Id == reset ? 10 : run.Id == first ? 100 : 160,
                    run.Id == reset ? 5 : 30, run.Id == reset ? 1 : 20);
        }
        await InsertWaitAsync(database, target, normal, at.AddMinutes(1), "NEW_WAIT_TYPE", 1, 10, 5, 1);
        Guid foreign = Guid.NewGuid();
        await SeedTargetAndRunAsync(database, other, foreign, "waits.server", at.AddMinutes(2));
        await InsertWaitAsync(database, other, foreign, at.AddMinutes(2), "WRITE_COMPLETION", 99, 999, 10, 1);
        if (partitionedRoute)
        {
            await using NpgsqlConnection setup = await database.DataSource.OpenConnectionAsync();
            foreach (var instance in new[] { target, other })
            {
                await using NpgsqlTransaction transaction = await setup.BeginTransactionAsync();
                await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target::text,true);", setup, transaction))
                {
                    scope.Parameters.AddWithValue("target", instance.Value);
                    await scope.ExecuteScalarAsync();
                }
                foreach (string function in new[] { "backfill_collection_run_v2", "backfill_collection_run_identity" })
                {
                    await using var copy = new NpgsqlCommand($"SELECT complete FROM control.{function}(@target,100);", setup, transaction);
                    copy.Parameters.AddWithValue("target", instance.Value);
                    Assert.True((bool)(await copy.ExecuteScalarAsync())!);
                }
                for (DateTimeOffset day = new(at.UtcDateTime.Date, TimeSpan.Zero); day <= at.AddMinutes(2); day = day.AddDays(1))
                {
                    await using var parity = new NpgsqlCommand("SELECT parity FROM control.record_collection_run_v2_parity_window(@target,'waits.server',@day,@end,100);", setup, transaction);
                    parity.Parameters.AddWithValue("target", instance.Value);
                    parity.Parameters.AddWithValue("day", day);
                    parity.Parameters.AddWithValue("end", day.AddDays(1));
                    Assert.True((bool)(await parity.ExecuteScalarAsync())!);
                }
                await transaction.CommitAsync();
            }
            await ExecuteAsync(database, "SELECT control.set_collection_run_v2_wait_read_route(true,10000);");
        }

        string connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        using var host = new WaitHistoryHttpFactory(connection, target.Value);
        using HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Snooze-Identity", "viewer");
        string window = $"fromUtc={Uri.EscapeDataString(at.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture))}&toUtc={Uri.EscapeDataString(at.AddMinutes(3).ToString("O", CultureInfo.InvariantCulture))}";
        string route = $"/api/v1/observation-targets/{target.Value:D}/activity/waits/history?{window}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(route + "&limit=25")).StatusCode);
        async Task<string> SecurityMetadataAsync()
        {
            await using var metadata = database.DataSource.CreateCommand("""
                SELECT jsonb_agg(jsonb_build_array(p.oid,p.proowner,p.prosecdef,p.proacl,p.proconfig) ORDER BY p.oid)::text
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='reporting' AND p.proname IN
                  ('list_server_wait_history','list_server_wait_history_legacy','list_server_wait_history_v2_shadow');
                """);
            return (string)(await metadata.ExecuteScalarAsync())!;
        }
        string security = await SecurityMetadataAsync();
        var upgrade = await migrations.ApplyPendingAsync(new(1, new(TimeSpan.FromMinutes(2))), default);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(security, await SecurityMetadataAsync());

        var expected = new List<string>();
        foreach (int pageSize in new[] { 1, 2, 25 })
        {
            string? cursor = null;
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                using var response = await client.GetAsync(route + $"&limit={pageSize}" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(target.Value, document.RootElement.GetProperty("instanceId").GetGuid());
                var items = document.RootElement.GetProperty("items").EnumerateArray().ToArray();
                Assert.InRange(items.Length, 1, pageSize);
                foreach (var item in items)
                {
                    var wait = item.GetProperty("wait");
                    Guid run = item.GetProperty("evidence").GetProperty("runId").GetGuid();
                    string type = wait.GetProperty("waitType").GetString()!;
                    string key = $"{wait.GetProperty("observedAtUtc").GetDateTimeOffset().UtcTicks:D19}|{run:N}|{type}";
                    Assert.True(seen.Add(key), "A historical wait must occur once across pages.");
                    keys.Add(key);
                    if (run == reset)
                    {
                        Assert.True(wait.GetProperty("resetDetected").GetBoolean());
                        Assert.Equal(JsonValueKind.Null, wait.GetProperty("waitTimeMillisecondsDelta").ValueKind);
                    }
                    else if (run == normal && type != "NEW_WAIT_TYPE")
                        Assert.Equal("60", wait.GetProperty("waitTimeMillisecondsDelta").GetString());
                    else
                    {
                        Assert.False(wait.GetProperty("baselineAvailable").GetBoolean());
                        Assert.Equal(JsonValueKind.Null, wait.GetProperty("waitTimeMillisecondsDelta").ValueKind);
                    }
                }
                cursor = document.RootElement.GetProperty("nextCursor").GetString();
            } while (cursor is not null);
            Assert.Equal(13, keys.Count);
            Assert.Equal(keys.OrderDescending(StringComparer.Ordinal), keys);
            if (expected.Count == 0) expected.AddRange(keys); else Assert.Equal(expected, keys);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/observation-targets/{other.Value:D}/activity/waits/history?{window}")).StatusCode);
        using HttpClient anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        Assert.Equal(375, Assert.Single(upgrade.Results).Migration.Number.Value);
    }
}

internal sealed class WaitHistoryHttpFactory(string connection, Guid target) : WebApplicationFactory<ServerAssembly::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("ConnectionStrings:SqlObserverRepository", connection);
        builder.UseSetting("SqlObserver:IdentityFingerprintKey", Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:GroupSid", SnoozeHttpAuthenticationHandler.ViewerGroup);
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:AllTargets", "false");
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:TargetIds:0", target.ToString("D"));
        builder.UseSetting("SqlObserver:Authorization:Bindings:0:Roles:0", "Viewer");
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = SnoozeHttpAuthenticationHandler.SchemeName;
            options.DefaultChallengeScheme = SnoozeHttpAuthenticationHandler.SchemeName;
        }).AddScheme<AuthenticationSchemeOptions, SnoozeHttpAuthenticationHandler>(SnoozeHttpAuthenticationHandler.SchemeName, _ => { }));
    }
}
