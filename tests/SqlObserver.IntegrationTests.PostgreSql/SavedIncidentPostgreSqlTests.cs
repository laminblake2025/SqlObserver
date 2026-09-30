using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category","RequiresPostgreSql")]
public sealed class SavedIncidentPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));
    private const string Actor = "S-1-5-21-1001";

    [Fact]
    public async Task ConcurrentRetriesPreserveOneSnapshotAndAuditAndMcpReadsSurviveSourceRetention()
    {
        await using RepositoryTestDatabase database = await Database();
        SaveIncidentRequest request = Request(); await Target(database,request.TargetId);
        await Seed(database,request,3);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = new PostgreSqlSavedIncidentPort(server);
        SavedIncident[] results = await Task.WhenAll(
            repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default).AsTask(),
            repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default).AsTask());
        SavedIncident saved=results[0];
        Assert.Equal(saved.ThreadId,results[1].ThreadId); Assert.Equal(saved.SavedAtUtc,results[1].SavedAtUtc);
        Assert.Equal(3,saved.Evidence.Count); Assert.False(saved.EvidenceTruncated);
        Assert.Equal(request.Notes,saved.Notes);
        await Seed(database,request,1,offset:10);
        Assert.Equal(3,(await repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default)).Evidence.Count);
        await Assert.ThrowsAsync<SavedIncidentConflictException>(() => repository.SaveAsync(request with { Notes="Changed" },Actor,Guid.NewGuid(),Timeout,default).AsTask());
        await Assert.ThrowsAsync<SavedIncidentConflictException>(() => repository.SaveAsync(request,"S-1-5-21-1002",Guid.NewGuid(),Timeout,default).AsTask());
        Assert.Equal(1L,await Scalar(database,"SELECT count(*) FROM audit.activity WHERE action_name='incident.save';"));
        await RetireSourcePartitions(database,request.TargetId);
        SavedIncident read=Assert.IsType<SavedIncident>(await repository.ReadAsync(request.TargetId,saved.ThreadId,Timeout,default));
        Assert.Equal(3,read.Evidence.Count); Assert.Equal(saved.Evidence[0].Evidence.GetRawText(),read.Evidence[0].Evidence.GetRawText());
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await Scope(connection,request.TargetId);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM reporting.get_m10_incident_evidence(@target,1,@thread,257,clock_timestamp(),NULL,NULL);
            """,connection);
        command.Parameters.AddWithValue("target",request.TargetId); command.Parameters.AddWithValue("thread",saved.ThreadId);
        Assert.Equal(3L,await command.ExecuteScalarAsync());
        command.CommandText="SELECT count(*) FROM reporting.get_m10_incident_evidence(@target,1,@thread,256,clock_timestamp());";
        Assert.Equal(3L,await command.ExecuteScalarAsync());
        command.CommandText="SELECT count(*) FROM reporting.list_incidents_metadata(@target,1,@from,@to,101,clock_timestamp(),control.read_incident_publication_revision(@target,1,NULL),NULL,NULL);";
        command.Parameters.AddWithValue("from",request.FromUtc); command.Parameters.AddWithValue("to",request.ToUtc);
        Assert.Equal(1L,await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CaptureReportsRowAndByteTruncationAndNeverAcceptsAStaleTargetRevision()
    {
        await using RepositoryTestDatabase database = await Database();
        SaveIncidentRequest request = Request(); await Target(database,request.TargetId); await Seed(database,request,201);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlSavedIncidentPort(server);
        SavedIncident saved=await repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default);
        Assert.Equal(200,saved.Evidence.Count); Assert.True(saved.EvidenceTruncated);
        SaveIncidentRequest second=Request(); await Target(database,second.TargetId); await Seed(database,second,10,large:true);
        SavedIncident bounded=await repository.SaveAsync(second,Actor,Guid.NewGuid(),Timeout,default);
        Assert.True(bounded.EvidenceTruncated); Assert.InRange(bounded.Evidence.Count,1,9);
        Assert.True(bounded.Evidence.Sum(row => System.Text.Encoding.UTF8.GetByteCount(row.Evidence.GetRawText()))<=1048576);
        await Assert.ThrowsAsync<SavedIncidentConflictException>(() => repository.SaveAsync(request with { ExpectedTargetRevision=2,OperationId=Guid.NewGuid() },Actor,Guid.NewGuid(),Timeout,default).AsTask());
        Assert.Equal(2L,await Scalar(database,"SELECT count(*) FROM analytics.saved_incident;"));
    }

    [Fact]
    public async Task NoEvidenceStillCreatesAnExplicitEmptySavedWindowAndGeneration()
    {
        await using RepositoryTestDatabase database = await Database();
        SaveIncidentRequest request = Request(); await Target(database,request.TargetId);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlSavedIncidentPort(server);
        SavedIncident saved=await repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default);
        Assert.Empty(saved.Evidence); Assert.False(saved.EvidenceTruncated);
        Assert.Equal(request.FromUtc,saved.FromUtc); Assert.Equal(request.ToUtc,saved.ToUtc);
        Assert.Equal(1L,await Scalar(database,"SELECT count(*) FROM analytics.incident_generation;"));
        Assert.Null(await repository.ReadAsync(request.TargetId,Guid.NewGuid(),Timeout,default));
    }

    [Fact]
    public async Task ServerOnlyFunctionsAndTablesRejectCrossTargetScopeAndInvalidDirectCommands()
    {
        await using RepositoryTestDatabase database = await Database(); SaveIncidentRequest request = Request();
        await Target(database,request.TargetId); Guid other=Guid.NewGuid(); await Target(database,other);
        await using NpgsqlConnection admin=await database.DataSource.OpenConnectionAsync();
        foreach (string signature in new[] {"reporting.read_saved_incident(uuid,uuid)","control.save_incident(uuid,bigint,uuid,timestamptz,timestamptz,text,text,text,uuid)"})
        {
            await using var privileges = new NpgsqlCommand("""
                SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator' AND p.prosecdef
                  AND has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
                  AND NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
                FROM pg_proc p WHERE p.oid=to_regprocedure(@signature);
                """,admin);
            privileges.Parameters.AddWithValue("signature",signature); Assert.Equal(true,await privileges.ExecuteScalarAsync());
        }
        Assert.Equal(0L,await Scalar(database,"""
            SELECT count(*) FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
            WHERE n.nspname='analytics' AND t.relname IN ('saved_incident','saved_incident_evidence')
              AND (NOT t.relrowsecurity OR NOT t.relforcerowsecurity
                OR has_table_privilege('sqlobserver_server',t.oid,'SELECT,INSERT,UPDATE,DELETE'));
            """));
        await using NpgsqlDataSource server=database.CreateServerDataSource(); await using NpgsqlConnection connection=await server.OpenConnectionAsync();
        await Scope(connection,other);
        await using var denied=new NpgsqlCommand("SELECT reporting.read_saved_incident(@target,@thread);",connection);
        denied.Parameters.AddWithValue("target",request.TargetId); denied.Parameters.AddWithValue("thread",Guid.NewGuid());
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteScalarAsync())).SqlState);
        await Scope(connection,request.TargetId);
        await using var invalid=new NpgsqlCommand("SELECT control.save_incident(@target,1,@operation,clock_timestamp(),clock_timestamp()-interval '1 hour','Title','',@actor,@correlation);",connection);
        invalid.Parameters.AddWithValue("target",request.TargetId); invalid.Parameters.AddWithValue("operation",Guid.NewGuid());
        invalid.Parameters.AddWithValue("actor",Actor); invalid.Parameters.AddWithValue("correlation",Guid.NewGuid());
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteScalarAsync())).SqlState);
        Assert.Equal(0L,await Scalar(database,"SELECT count(*) FROM analytics.incident_thread;"));
    }

    [Fact]
    public async Task AuditFailureRollsBackTheEntireSavedIncident()
    {
        await using RepositoryTestDatabase database=await Database(); SaveIncidentRequest request=Request();
        await Target(database,request.TargetId); await Seed(database,request,1);
        await Execute(database,"""
            CREATE FUNCTION public.reject_incident_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.action_name='incident.save' THEN RAISE EXCEPTION 'test audit unavailable'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_incident_audit BEFORE INSERT ON audit.activity FOR EACH ROW EXECUTE FUNCTION public.reject_incident_audit();
            """);
        await using NpgsqlDataSource server=database.CreateServerDataSource(); var repository=new PostgreSqlSavedIncidentPort(server);
        await Assert.ThrowsAsync<PostgresException>(() => repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default).AsTask());
        foreach (string table in new[] {"analytics.incident_thread","analytics.incident_generation","analytics.saved_incident","analytics.saved_incident_evidence"})
            Assert.Equal(0L,await Scalar(database,$"SELECT count(*) FROM {table};"));
    }

    private async Task<RepositoryTestDatabase> Database()
    {
        RepositoryTestDatabase database=await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result=await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new(MigrationBatchResult.MaximumResults,new(TimeSpan.FromSeconds(30))),default);
            Assert.False(result.HasFailures,result.Results.FirstOrDefault(row => row.Outcome==MigrationOutcome.Failed)?.FailureCode);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
    private static SaveIncidentRequest Request()
    {
        DateTimeOffset to=DateTimeOffset.UtcNow.AddMinutes(-1); to=to.AddTicks(-(to.Ticks%TimeSpan.TicksPerMicrosecond));
        return new(Guid.NewGuid(),1,Guid.NewGuid(),to.AddHours(-1),to,"Saved investigation","Notes\nkept exactly");
    }
    private static Task Target(RepositoryTestDatabase database,Guid target) => Execute(database,"""
        INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
          authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
        VALUES(@target,@key,'Saved incident target','sql01',1433,interval '5 seconds','windows_integrated_service_identity',
          'mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
        """,("target",target),("key","saved-incident-"+target.ToString("N")));
    private static Task Seed(RepositoryTestDatabase database,SaveIncidentRequest request,int count,int offset=0,bool large=false) => Execute(database,"""
        INSERT INTO analytics.evidence_packet_v2(occurred_at,packet_id,instance_id,target_revision,evidence_kind,
            source_digest,identity_digest,source_cutoff_digest,evidence,confidence,visibility_state)
        SELECT @from+((i+@offset)*interval '1 second'),gen_random_uuid(),@target,1,'metric',
            sha256(convert_to(i::text,'UTF8')),sha256(convert_to(i::text,'UTF8')),sha256(convert_to(i::text,'UTF8')),
            jsonb_build_object('count',i,'testPayload',CASE WHEN @large THEN repeat('x',200000) ELSE '' END),.9,'complete'
        FROM generate_series(1,@count) i;
        """,("from",request.FromUtc),("target",request.TargetId),("count",count),("offset",offset),("large",large));
    private static async Task Scope(NpgsqlConnection connection,Guid target)
    {
        await using var command=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target,false);",connection);
        command.Parameters.AddWithValue("target",target.ToString("D")); await command.ExecuteNonQueryAsync();
    }
    private static async Task RetireSourcePartitions(RepositoryTestDatabase database,Guid target)
    {
        // Production source rows are append-only; retention retires their whole
        // partitions. Use that storage operation in this disposable database.
        await using var lookup=database.DataSource.CreateCommand("""
            SELECT DISTINCT n.nspname,c.relname FROM analytics.evidence_packet_v2 e
            JOIN pg_class c ON c.oid=e.tableoid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE e.instance_id=@target;
            """);
        lookup.Parameters.AddWithValue("target",target);
        var partitions=new List<(string Schema,string Name)>();
        await using (NpgsqlDataReader reader=await lookup.ExecuteReaderAsync())
            while (await reader.ReadAsync()) partitions.Add((reader.GetString(0),reader.GetString(1)));
        Assert.NotEmpty(partitions);
        using var quoting=new NpgsqlCommandBuilder();
        foreach (var partition in partitions)
            await Execute(database,$"DROP TABLE {quoting.QuoteIdentifier(partition.Schema)}.{quoting.QuoteIdentifier(partition.Name)};");
    }
    private static async Task Execute(RepositoryTestDatabase database,string sql,params (string Key,object Value)[] parameters)
    {
        await using var command=database.DataSource.CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key,parameter.Value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<long> Scalar(RepositoryTestDatabase database,string sql)
    { await using var command=database.DataSource.CreateCommand(sql); return (long)(await command.ExecuteScalarAsync())!; }
}
