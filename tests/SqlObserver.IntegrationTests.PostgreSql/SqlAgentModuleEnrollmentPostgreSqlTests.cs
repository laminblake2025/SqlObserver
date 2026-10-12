using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlAgentModuleEnrollmentPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(10));

    [Fact]
    public async Task EnrollmentIsExplicitAuditedRevocableAndExactReplayCannotUndoAba()
    {
        await using var db = await PrepareAsync(); var target = await SeedAsync(db);
        await using var server = db.CreateServerDataSource(); await using var collector = db.CreateCollectorDataSource();
        var admin = new PostgreSqlSqlAgentModuleEnrollmentPort(server); var resolver = new PostgreSqlSqlAgentModuleEnrollmentPort(collector);
        var initial = await admin.ReadAsync(target, new(1), CancellationToken.None);
        Assert.Equal(SqlAgentModuleEnrollmentReadStatus.Found, initial.Status);
        Assert.Equal(0, initial.Snapshot!.Revision); Assert.Null(initial.Snapshot.Enrollment);
        var defaultOff = await resolver.ResolveAsync(target, new(1), CancellationToken.None);
        Assert.Equal(0, defaultOff!.Revision); Assert.Null(defaultOff.Enrollment);
        var first = Request(target, 0);
        Assert.Equal(new(SqlAgentModuleEnrollmentMutationStatus.Applied, 1), await admin.SetAsync(first, CancellationToken.None));
        Assert.Equal(new(SqlAgentModuleEnrollmentMutationStatus.Replayed, 1), await admin.SetAsync(first, CancellationToken.None));
        var loaded = (await resolver.ResolveAsync(target, new(1), CancellationToken.None))!.Enrollment;
        Assert.True(first.Change.Enrollment!.SameAuthorization(loaded));
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.RevisionConflict,
            (await admin.SetAsync(Request(target, 0, operation: first.Change.OperationId), CancellationToken.None)).Status);
        var revoke = Request(target, 1, revoke: true);
        Assert.Equal(new(SqlAgentModuleEnrollmentMutationStatus.Applied, 2), await admin.SetAsync(revoke, CancellationToken.None));
        var revoked = await resolver.ResolveAsync(target, new(1), CancellationToken.None);
        Assert.Equal(2, revoked!.Revision); Assert.Null(revoked.Enrollment);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.RevisionConflict, (await admin.SetAsync(first, CancellationToken.None)).Status);
        var reenroll = Request(target, 2, enrollmentId: first.Change.Enrollment.EnrollmentId);
        Assert.Equal(new(SqlAgentModuleEnrollmentMutationStatus.Applied, 3), await admin.SetAsync(reenroll, CancellationToken.None));
        Assert.False(first.Change.Enrollment.SameAuthorization((await resolver.ResolveAsync(target, new(1), CancellationToken.None))!.Enrollment));
        Assert.Equal(3L, await ScalarAsync(db, """
            SELECT count(*) FROM control.sql_agent_module_enrollment_command c JOIN audit.activity a
              ON a.activity_id=c.audit_activity_id AND a.occurred_at=c.audit_occurred_at
            WHERE c.instance_id=@target AND a.subject_identifier=@target::text AND a.authorization_result='allowed' AND a.outcome='succeeded'
            """, target));
        // Enrollment does not enable either existing opt-in schedule or install an Agent collector contract.
        Assert.Equal(0L, await ScalarAsync(db, "SELECT count(*) FROM control.collector_schedule WHERE instance_id=@target AND collector_id IN('cpu.components','server.lifecycle') AND enabled", target));
        Assert.Equal(0L, await ScalarAsync(db, "SELECT count(*) FROM control.collector_contract WHERE collector_id='sql-agent.activity' AND @target IS NOT NULL", target));
    }

    [Fact]
    public async Task ContextStatusesDistinguishAbsenceStaleRevisionAndInactiveTargets()
    {
        await using var db = await PrepareAsync(); var target = await SeedAsync(db);
        await using var server = db.CreateServerDataSource(); await using var collector = db.CreateCollectorDataSource();
        var admin = new PostgreSqlSqlAgentModuleEnrollmentPort(server); var resolver = new PostgreSqlSqlAgentModuleEnrollmentPort(collector);
        var unknown = new MonitoredInstanceId(Guid.NewGuid());
        Assert.Equal(SqlAgentModuleEnrollmentReadStatus.NotFound, (await admin.ReadAsync(unknown, new(1), CancellationToken.None)).Status);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.NotFound, (await admin.SetAsync(Request(unknown, 0), CancellationToken.None)).Status);
        Assert.Null(await resolver.ResolveAsync(unknown, new(1), CancellationToken.None));
        await admin.SetAsync(Request(target, 0), CancellationToken.None);
        await ExecuteAsync(db, "UPDATE control.observation_target SET revision=2,updated_at=clock_timestamp() WHERE instance_id=@target", target);
        Assert.Equal(SqlAgentModuleEnrollmentReadStatus.RevisionConflict, (await admin.ReadAsync(target, new(1), CancellationToken.None)).Status);
        Assert.Null(await resolver.ResolveAsync(target, new(1), CancellationToken.None));
        var newRevision = await resolver.ResolveAsync(target, new(2), CancellationToken.None);
        Assert.Equal(0, newRevision!.Revision); Assert.Null(newRevision.Enrollment);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.RevisionConflict, (await admin.SetAsync(Request(target, 1), CancellationToken.None)).Status);
        await ExecuteAsync(db, "UPDATE control.observation_target SET lifecycle_state='retired',retired_at=clock_timestamp(),updated_at=clock_timestamp() WHERE instance_id=@target", target);
        Assert.Equal(SqlAgentModuleEnrollmentReadStatus.Inactive, (await admin.ReadAsync(target, new(2), CancellationToken.None)).Status);
        Assert.Equal(SqlAgentModuleEnrollmentMutationStatus.Inactive, (await admin.SetAsync(Request(target, 0, targetRevision: 2), CancellationToken.None)).Status);
        Assert.Null(await resolver.ResolveAsync(target, new(2), CancellationToken.None));
    }

    [Theory]
    [InlineData(-2, -1)]
    [InlineData(1, 2)]
    public async Task ResolverSuppressesOutOfWindowBindingButAdministratorRetainsEvidence(int fromDays, int toDays)
    {
        await using var db = await PrepareAsync(); var target = await SeedAsync(db);
        await using var server = db.CreateServerDataSource(); await using var collector = db.CreateCollectorDataSource();
        var admin = new PostgreSqlSqlAgentModuleEnrollmentPort(server);
        var request = Request(target, 0, from: DateTimeOffset.UtcNow.AddDays(fromDays), to: DateTimeOffset.UtcNow.AddDays(toDays));
        await admin.SetAsync(request, CancellationToken.None);
        Assert.True(request.Change.Enrollment!.SameAuthorization((await admin.ReadAsync(target, new(1), CancellationToken.None)).Snapshot!.Enrollment));
        var snapshot = await new PostgreSqlSqlAgentModuleEnrollmentPort(collector).ResolveAsync(target, new(1), CancellationToken.None);
        Assert.Equal(1, snapshot!.Revision); Assert.Null(snapshot.Enrollment);
    }

    [Fact]
    public async Task ConcurrentCommandsHaveOneWinnerAndRolesCannotBypassFunctionsOrRewriteHistory()
    {
        await using var db = await PrepareAsync(); var target = await SeedAsync(db);
        await using var server = db.CreateServerDataSource(); await using var collector = db.CreateCollectorDataSource();
        var admin = new PostgreSqlSqlAgentModuleEnrollmentPort(server);
        var results = await Task.WhenAll(admin.SetAsync(Request(target, 0), CancellationToken.None).AsTask(), admin.SetAsync(Request(target, 0), CancellationToken.None).AsTask());
        Assert.Single(results, r => r.Status == SqlAgentModuleEnrollmentMutationStatus.Applied);
        Assert.Single(results, r => r.Status == SqlAgentModuleEnrollmentMutationStatus.RevisionConflict);
        await using (var direct = server.CreateCommand("SELECT * FROM control.sql_agent_module_enrollment_command"))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await direct.ExecuteNonQueryAsync())).SqlState);
        await using (var rewrite = db.DataSource.CreateCommand("UPDATE control.sql_agent_module_enrollment_command SET target_revision=target_revision"))
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await rewrite.ExecuteNonQueryAsync())).SqlState);
        await using (var delete = db.DataSource.CreateCommand("DELETE FROM control.sql_agent_module_enrollment_command"))
            Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await delete.ExecuteNonQueryAsync())).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await new PostgreSqlSqlAgentModuleEnrollmentPort(collector).SetAsync(Request(target, 1), CancellationToken.None))).SqlState);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await new PostgreSqlSqlAgentModuleEnrollmentPort(collector).ReadAsync(target, new(1), CancellationToken.None))).SqlState);
        await using var security = db.DataSource.CreateCommand("""
            SELECT c.relrowsecurity AND c.relforcerowsecurity
              AND NOT has_function_privilege('sqlobserver_auditor','control.read_sql_agent_module_enrollment(uuid,bigint)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_auditor','control.resolve_sql_agent_module_enrollment(uuid,bigint)','EXECUTE')
              AND NOT has_function_privilege('sqlobserver_server','control.resolve_sql_agent_module_enrollment(uuid,bigint)','EXECUTE')
              AND NOT EXISTS(SELECT 1 FROM pg_proc p CROSS JOIN LATERAL aclexplode(p.proacl) a WHERE p.oid='control.set_sql_agent_module_enrollment(uuid,bigint,bigint,uuid,jsonb,text,uuid)'::regprocedure AND a.grantee=0)
            FROM pg_class c WHERE c.oid='control.sql_agent_module_enrollment_command'::regclass
            """);
        Assert.Equal(true, await security.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FailedFinalWriteRollsBackAuditAndEnrollmentTogether()
    {
        await using var db = await PrepareAsync(); var target = await SeedAsync(db);
        await using var server = db.CreateServerDataSource();
        await using (var inject = db.DataSource.CreateCommand("""
            CREATE FUNCTION control.fixture_reject_module_command() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'fixture failure' USING ERRCODE='23514'; END $$;
            CREATE TRIGGER fixture_reject_module_command BEFORE INSERT ON control.sql_agent_module_enrollment_command
             FOR EACH ROW EXECUTE FUNCTION control.fixture_reject_module_command();
            """)) await inject.ExecuteNonQueryAsync();
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () => await new PostgreSqlSqlAgentModuleEnrollmentPort(server).SetAsync(Request(target, 0), CancellationToken.None))).SqlState);
        Assert.Equal(0L, await ScalarAsync(db, "SELECT count(*) FROM control.sql_agent_module_enrollment_command WHERE instance_id=@target", target));
        Assert.Equal(0L, await ScalarAsync(db, "SELECT count(*) FROM audit.activity WHERE subject_identifier=@target::text AND action_name='observation_target.update'", target));
    }

    [Fact]
    public async Task DirectWriterRejectsMalformedBindingsAndUnknownVersionsWithoutAuditOrRows()
    {
        await using var db = await PrepareAsync(); var target = await SeedAsync(db);
        await using var server = db.CreateServerDataSource();
        var admin = new PostgreSqlSqlAgentModuleEnrollmentPort(server);
        await admin.SetAsync(Request(target, 0), CancellationToken.None);
        string valid;
        await using (var read = db.DataSource.CreateCommand("SELECT enrollment::text FROM control.sql_agent_module_enrollment_command WHERE instance_id=@target"))
        { read.Parameters.AddWithValue("target", target.Value); valid = (string)(await read.ExecuteScalarAsync())!; }
        var changes = new Action<JsonObject>[]
        {
            v => v["unexpected"]=true,
            v => v["binding"]!["moduleVersion"]=2,
            v => v["binding"]!["sourceVersion"]=1,
            v => v["binding"]!["probeVersion"]=2,
            v => v["binding"]!["installationPolicyVersion"]=1,
            v => v["binding"]!["definitionSha256"]=new string('a',64),
            v => v["binding"]!["publicCertificateSha256"]="bad",
            v => v["binding"]!["thumbprintHex"]=new string('A',40),
            v => v["binding"]!["tcpPort"]=65536,
            v => v["binding"]!["objectId"]=0,
            v => v["binding"]!["certificateId"]=-1,
            v => v["binding"]!["endpointHost"]="sql01;TrustServerCertificate=true",
            v => v["binding"]!["serverName"]=new string('x',129),
            v => v["binding"]!["machineName"]=null,
            v => v["expectedWindowsSid"]="S-1-5-021-1",
            v => v["expectedWindowsSid"]="S-1-5-4294967296",
            v => v["enrollmentId"]=Guid.Empty.ToString("D"),
            v => v["evidenceSha256"]=new string('0',64),
            v => v["notBeforeUtc"]="2026-01-01T00:00:00-04:00",
            v => v["expiresAtUtc"]=v["notBeforeUtc"]!.DeepClone(),
            v => v.Remove("binding"),
        };
        foreach (var change in changes)
        {
            var document = JsonNode.Parse(valid)!.AsObject(); change(document);
            await using var bad = server.CreateCommand("SELECT * FROM control.set_sql_agent_module_enrollment(@target,1,1,@op,@json,@actor,@correlation)");
            bad.Parameters.AddWithValue("target", target.Value); bad.Parameters.AddWithValue("op", Guid.NewGuid());
            bad.Parameters.Add("json",NpgsqlDbType.Jsonb).Value=document.ToJsonString();
            bad.Parameters.AddWithValue("actor", "S-1-5-21-1-2-3-1001"); bad.Parameters.AddWithValue("correlation", Guid.NewGuid());
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () => await bad.ExecuteNonQueryAsync())).SqlState);
        }
        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM control.sql_agent_module_enrollment_command WHERE instance_id=@target", target));
        Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM audit.activity WHERE subject_identifier=@target::text AND action_name='observation_target.update'", target));
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        var db = await fixture.CreateDatabaseAsync();
        var result = await new PostgreSqlMigrationPort(db.DataSource).ApplyPendingAsync(new(MigrationBatchResult.MaximumResults,new(TimeSpan.FromMinutes(2))),CancellationToken.None);
        Assert.False(result.HasFailures,string.Join(",",result.Results.Where(r=>r.Outcome==MigrationOutcome.Failed).Select(r=>$"{r.Migration.Number.Value}:{r.FailureCode}")));
        return db;
    }
    private static async Task<MonitoredInstanceId> SeedAsync(RepositoryTestDatabase db)
    {
        var target = new MonitoredInstanceId(Guid.NewGuid());
        await using var command=db.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,certificate_host_name,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Module enrollment fixture','sql01',1433,'sql01',interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now())
            """);
        command.Parameters.AddWithValue("target",target.Value); command.Parameters.AddWithValue("key",$"module.{target.Value:N}");
        await command.ExecuteNonQueryAsync(); return target;
    }
    private static SetSqlAgentModuleEnrollmentRequest Request(MonitoredInstanceId target,long expected,bool revoke=false,
        Guid? operation=null,Guid? enrollmentId=null,long targetRevision=1,DateTimeOffset? from=null,DateTimeOffset? to=null)
    {
        var revision=new ObservationTargetRevision(targetRevision);
        var binding=new SqlAgentModuleBinding("sql01",1433,"sql01","SQL01","SQL01","",42,43,
            SqlAgentModuleBinding.ApprovedDefinitionSha256,new string('a',64),new string('b',40));
        SqlAgentModuleEnrollment? enrollment=revoke?null:new(target,revision,expected+1,enrollmentId??Guid.NewGuid(),"S-1-5-21-1-2-3-1002",binding,
            from??DateTimeOffset.UtcNow.AddDays(-1),to??DateTimeOffset.UtcNow.AddDays(1),new string('c',64));
        return new(new(target,revision,expected,operation??Guid.NewGuid(),enrollment),
            new(new("S-1-5-21-1-2-3-1001"),new(Guid.NewGuid()),AdministrativeAuditAction.UpdateObservationTarget,target),Timeout);
    }
    private static async Task<object?> ScalarAsync(RepositoryTestDatabase db,string sql,MonitoredInstanceId target)
    { await using var command=db.DataSource.CreateCommand(sql); command.Parameters.AddWithValue("target",target.Value); return await command.ExecuteScalarAsync(); }
    private static async Task ExecuteAsync(RepositoryTestDatabase db,string sql,MonitoredInstanceId target)
    { await using var command=db.DataSource.CreateCommand(sql); command.Parameters.AddWithValue("target",target.Value); await command.ExecuteNonQueryAsync(); }
}
