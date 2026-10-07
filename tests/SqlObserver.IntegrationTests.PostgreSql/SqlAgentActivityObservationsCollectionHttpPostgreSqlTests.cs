using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentActivityObservationsCollectionHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task RealScopedHttpRequiresEnrollmentAndScheduleCasAndNeverLeaksPrivatePins()
    {
        await using var db = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await SqlAgentActivityObservationsCollectionPostgreSqlTests.TargetAsync(db);
        Guid other = await SqlAgentActivityObservationsCollectionPostgreSqlTests.TargetAsync(db);
        using var host = new ServerLifecycleNativeHttpFactory(db.ConnectionString, target, other);
        using var admin = host.Client("admin"); using var viewer = host.Client("viewer");
        string url = $"/api/v1/observation-targets/{target:D}/sql-agent/activity-observations/collection";
        var state = await ReadAsync(viewer, url);
        Assert.False(state.Enabled);
        var body = new { expectedTargetRevision = 1, expectedScheduleRevision = state.Revision, expectedEnabled = false, enabled = true };
        using var denied = await viewer.PutAsJsonAsync(url, body); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var foreign = await admin.PutAsJsonAsync($"/api/v1/observation-targets/{other:D}/sql-agent/activity-observations/collection", body);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var noEnrollment = await admin.PutAsJsonAsync(url, body);
        Assert.Equal(HttpStatusCode.Conflict, noEnrollment.StatusCode);
        Assert.Contains("agent_activity_enrollment_unavailable", await noEnrollment.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var server = db.CreateServerDataSource();
        var enrollment = new PostgreSqlSqlAgentModuleEnrollmentPort(server);
        await enrollment.SetAsync(SqlAgentActivityObservationsCollectionPostgreSqlTests.Enrollment(target, 0), CancellationToken.None);
        state = await ReadAsync(admin, url); Assert.False(state.Enabled);
        using var enabled = await admin.PutAsJsonAsync(url,
            new { expectedTargetRevision = 1, expectedScheduleRevision = state.Revision, expectedEnabled = false, enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode); Assert.True(enabled.Headers.CacheControl!.NoStore);
        var optIn = await ReadAsync(admin, url); Assert.True(optIn.Enabled); Assert.Equal(state.Revision + 1, optIn.Revision);
        await enrollment.SetAsync(SqlAgentActivityObservationsCollectionPostgreSqlTests.Enrollment(target, 1, revoke: true), CancellationToken.None);
        var revoked = await ReadAsync(admin, url); Assert.False(revoked.Enabled); Assert.Equal(optIn.Revision + 1, revoked.Revision);
        await enrollment.SetAsync(SqlAgentActivityObservationsCollectionPostgreSqlTests.Enrollment(target, 2), CancellationToken.None);
        var reenrolled = await ReadAsync(admin, url); Assert.False(reenrolled.Enabled); Assert.Equal(revoked.Revision + 1, reenrolled.Revision);
        using var stale = await admin.PutAsJsonAsync(url,
            new { expectedTargetRevision = 1, expectedScheduleRevision = state.Revision, expectedEnabled = false, enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var targetStale = await admin.GetAsync(url + "?targetRevision=2"); Assert.Equal(HttpStatusCode.Conflict, targetStale.StatusCode);
    }

    private static async Task<(long Revision, bool Enabled)> ReadAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url + "?targetRevision=1"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private); Assert.True(response.Headers.CacheControl.NoStore);
        string text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        foreach (string privateField in new[] { "sql01", "expectedWindowsSid", "definitionSha256", "publicCertificateSha256", "enrollmentId", "evidenceSha256" })
            Assert.DoesNotContain(privateField, text, StringComparison.Ordinal);
        return (json.RootElement.GetProperty("scheduleRevision").GetInt64(), json.RootElement.GetProperty("collectorEnabled").GetBoolean());
    }
}
