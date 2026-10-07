using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Server;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentModuleEnrollmentHttpPostgreSqlTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task ProductionHttpEnrollmentUsesRestrictedRepositoryAuditReplayAndRevocation()
    {
        await using var database = await SqlVolumeWindowHistoryFixture.CreateAsync(fixture);
        Guid target = await ServerLifecycleAdmissionPostgreSqlTests.TargetAsync(database);
        Guid other = await ServerLifecycleAdmissionPostgreSqlTests.TargetAsync(database);
        using var host = new ServerLifecycleNativeHttpFactory(database.ConnectionString, target, other);
        using var admin = host.Client("admin");
        using var viewer = host.Client("viewer");
        await using var collector = database.CreateCollectorDataSource();
        var resolver = new PostgreSqlSqlAgentModuleEnrollmentPort(collector);
        string route = $"/api/v1/observation-targets/{target:D}/sql-agent/module-enrollment";
        var initial = await ReadAsync(admin, route);
        Assert.Equal(0, initial.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("enrollment").ValueKind);
        var absent = await resolver.ResolveAsync(new(target), new(1), CancellationToken.None);
        Assert.Equal(0, absent!.Revision);
        Assert.Null(absent.Enrollment);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var body = new SqlAgentModuleEnrollmentBody(1, 0, Guid.NewGuid(), true,
            new(Guid.NewGuid(), "S-1-5-21-123-456-789-1001",
                new("replay.invalid", 1433, "replay.invalid", "SQL01", "SQL01", "", 42, 43,
                    SqlAgentModuleBinding.ApprovedDefinitionSha256, new string('a', 64), new string('b', 40)),
                now.AddDays(-1), now.AddDays(1), new string('c', 64)));

        using var deniedRead = await viewer.GetAsync(route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        using var deniedWrite = await viewer.PutAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.Forbidden, deniedWrite.StatusCode);
        using var foreignWrite = await admin.PutAsJsonAsync(
            $"/api/v1/observation-targets/{other:D}/sql-agent/module-enrollment", body);
        Assert.Equal(HttpStatusCode.Forbidden, foreignWrite.StatusCode);
        Assert.Equal(0L, await CountAsync(database,
            "SELECT count(*) FROM control.sql_agent_module_enrollment_command WHERE instance_id=@target", target));

        using var applied = await admin.PutAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.True(applied.Headers.CacheControl!.NoStore);
        var enrolled = await ReadAsync(admin, route);
        Assert.Equal(1, enrolled.GetProperty("enrollmentRevision").GetInt64());
        var enrollment = enrolled.GetProperty("enrollment");
        Assert.Equal(body.Enrollment!.EnrollmentId, enrollment.GetProperty("enrollmentId").GetGuid());
        Assert.Equal(body.Enrollment.EvidenceSha256, enrollment.GetProperty("evidenceSha256").GetString());
        var resolved = await resolver.ResolveAsync(new(target), new(1), CancellationToken.None);
        Assert.Equal(1, resolved!.Revision);
        Assert.Equal(body.Enrollment.EnrollmentId, resolved.Enrollment!.EnrollmentId);
        Assert.Equal(body.Enrollment.Binding.DefinitionSha256, resolved.Enrollment.Binding.DefinitionSha256);
        Assert.Equal(body.Enrollment.ExpectedWindowsSid, resolved.Enrollment.ExpectedWindowsSid);
        Assert.Equal(body.Enrollment.NotBeforeUtc, resolved.Enrollment.NotBeforeUtc);
        Assert.Equal(body.Enrollment.ExpiresAtUtc, resolved.Enrollment.ExpiresAtUtc);

        using var replay = await admin.PutAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var conflict = await admin.PutAsJsonAsync(route, body with { OperationId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(1, (await ReadAsync(admin, route)).GetProperty("enrollmentRevision").GetInt64());

        using var revoked = await admin.PutAsJsonAsync(route,
            new SqlAgentModuleEnrollmentBody(1, 1, Guid.NewGuid(), false, null));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var final = await ReadAsync(admin, route);
        Assert.Equal(2, final.GetProperty("enrollmentRevision").GetInt64());
        Assert.Equal(JsonValueKind.Null, final.GetProperty("enrollment").ValueKind);
        var deniedBinding = await resolver.ResolveAsync(new(target), new(1), CancellationToken.None);
        Assert.Equal(2, deniedBinding!.Revision);
        Assert.Null(deniedBinding.Enrollment);
        using var revokedReplay = await admin.PutAsJsonAsync(route, body);
        Assert.Equal(HttpStatusCode.Conflict, revokedReplay.StatusCode);
        using var staleTarget = await admin.GetAsync(route + "?targetRevision=2");
        Assert.Equal(HttpStatusCode.Conflict, staleTarget.StatusCode);
        Assert.Null(await resolver.ResolveAsync(new(target), new(2), CancellationToken.None));

        Assert.Equal(2L, await CountAsync(database, """
            SELECT count(*) FROM control.sql_agent_module_enrollment_command c
            JOIN audit.activity a ON a.activity_id=c.audit_activity_id AND a.occurred_at=c.audit_occurred_at
            WHERE c.instance_id=@target AND a.subject_identifier=@target::text
              AND a.action_name='observation_target.update' AND a.authorization_result='allowed' AND a.outcome='succeeded'
            """, target));
        Assert.Equal(2L, await CountAsync(database,
            "SELECT count(*) FROM audit.activity WHERE subject_identifier=@target::text AND action_name='observation_target.update' AND outcome='succeeded'", target));
        Assert.Equal(0L, await CountAsync(database,
            "SELECT count(*) FROM control.collector_schedule WHERE instance_id=@target AND collector_id IN('cpu.components','server.lifecycle') AND enabled", target));
        Assert.Equal(0L, await CountAsync(database,
            "SELECT count(*) FROM control.collector_contract WHERE collector_id='sql-agent.activity' AND @target IS NOT NULL", target));
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string route)
    {
        using var response = await client.GetAsync(route + "?targetRevision=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<object?> CountAsync(RepositoryTestDatabase database, string sql, Guid target)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("target", target);
        return await command.ExecuteScalarAsync();
    }
}
