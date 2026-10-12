using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Reporting;

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
    [Fact]
    public async Task SummaryPagesRetainDenseOrderingHistoricalRevisionsAndRejectLatePublications()
    {
        await using RepositoryTestDatabase database = await Database(); SaveIncidentRequest request = Request(); await Target(database,request.TargetId);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlSavedIncidentPort(server);
        var saved = new List<SavedIncident>();
        for (int i=0;i<5;i++) saved.Add(await repository.SaveAsync(request with { OperationId=Guid.NewGuid(),Title="Investigation "+i },Actor,Guid.NewGuid(),Timeout,default));
        // Dense timestamps exercise the UUID tie-breaker, not timestamp-only paging.
        DateTimeOffset at = saved.Min(row => row.SavedAtUtc);
        await Execute(database,"UPDATE analytics.saved_incident SET saved_at=@at WHERE instance_id=@target;",("at",at),("target",request.TargetId));
        await Execute(database,"UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;",("target",request.TargetId));
        await repository.SaveAsync(request with { ExpectedTargetRevision=2,OperationId=Guid.NewGuid(),Title="New revision" },Actor,Guid.NewGuid(),Timeout,default);
        SavedIncidentListPage first = await repository.ListAsync(new(request.TargetId,2),Timeout,default);
        Assert.True(first.HasMore); Assert.Equal(2,first.Items.Count); Assert.Equal(2,first.Items[0].TargetRevision);
        var found = new List<SavedIncidentSummary>(first.Items); var page = first;
        while (page.HasMore)
        {
            SavedIncidentSummary last = page.Items[^1];
            page = await repository.ListAsync(new(request.TargetId,2,new(request.TargetId,first.PublicationRevision,first.SnapshotUtc,last.SavedAtUtc,last.ThreadId,2)),Timeout,default);
            Assert.Equal(first.SnapshotUtc,page.SnapshotUtc); found.AddRange(page.Items);
        }
        Assert.Equal(6,found.Count); Assert.Equal(6,found.Select(row=>row.ThreadId).Distinct().Count());
        Assert.Equal(saved.Select(row=>row.ThreadId.ToString("N")).OrderDescending(StringComparer.Ordinal),found.Skip(1).Select(row=>row.ThreadId.ToString("N")));
        Assert.All(found,row=>Assert.Equal(0,row.EvidenceCount));
        SavedIncidentSummary boundary = first.Items[^1];
        var cursor = new SavedIncidentListCursor(request.TargetId,first.PublicationRevision,first.SnapshotUtc,boundary.SavedAtUtc,boundary.ThreadId,2);
        await repository.SaveAsync(request with { ExpectedTargetRevision=2,OperationId=Guid.NewGuid(),Title="Late publication" },Actor,Guid.NewGuid(),Timeout,default);
        await Assert.ThrowsAsync<SavedIncidentConflictException>(()=>repository.ListAsync(new(request.TargetId,2,cursor),Timeout,default).AsTask());
        Assert.Equal(2,(await repository.ListAsync(new(request.TargetId,2),Timeout,default)).Items.Count);
        Assert.NotNull(await repository.ReadAsync(request.TargetId,saved[0].ThreadId,Timeout,default));
    }

    [Fact]
    public async Task SummaryFunctionRejectsCrossScopeIncompleteBoundsAndDirectTableAccess()
    {
        await using RepositoryTestDatabase database = await Database(); SaveIncidentRequest request = Request(); await Target(database,request.TargetId);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlSavedIncidentPort(server);
        Assert.Empty((await repository.ListAsync(new(request.TargetId,50),Timeout,default)).Items);
        await using NpgsqlConnection connection = await server.OpenConnectionAsync(); await Scope(connection,Guid.NewGuid());
        await using var command = new NpgsqlCommand("SELECT reporting.list_saved_incidents(@target,2,NULL,NULL,NULL,NULL);",connection);
        command.Parameters.AddWithValue("target",request.TargetId);
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteScalarAsync())).SqlState);
        await Scope(connection,request.TargetId);
        foreach (string args in new[] { "0,NULL,NULL,NULL,NULL", "51,NULL,NULL,NULL,NULL", "2,NULL,clock_timestamp(),NULL,NULL", "2,1,NULL,NULL,NULL" })
        {
            command.CommandText=$"SELECT reporting.list_saved_incidents(@target,{args});";
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteScalarAsync())).SqlState);
        }
        Assert.Equal(0L,await Scalar(database,"""
            SELECT count(*) FROM pg_proc p WHERE p.oid='reporting.list_saved_incidents(uuid,integer,bigint,timestamptz,timestamptz,uuid)'::regprocedure
            AND (pg_get_userbyid(p.proowner)<>'sqlobserver_migrator' OR NOT p.prosecdef
              OR has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE') OR has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
              OR EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'));
            """));
        command.CommandText="SELECT * FROM control.saved_incident_publication;";
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>command.ExecuteScalarAsync())).SqlState);
    }
    private static SaveIncidentRequest Request()
    {
        DateTimeOffset to=DateTimeOffset.UtcNow.AddMinutes(-1); to=to.AddTicks(-(to.Ticks%TimeSpan.TicksPerMicrosecond));
        return new(Guid.NewGuid(),1,Guid.NewGuid(),to.AddHours(-1),to,"Saved investigation","Notes\nkept exactly");
    }
    [Fact]
    public async Task EmptyListFenceSerializesTheFirstConcurrentSave()
    {
        await using RepositoryTestDatabase database=await Database(); SaveIncidentRequest request=Request(); await Target(database,request.TargetId);
        await using NpgsqlDataSource server=database.CreateServerDataSource(); var repository=new PostgreSqlSavedIncidentPort(server);
        await using NpgsqlConnection connection=await server.OpenConnectionAsync();
        await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync();
        await Scope(connection,request.TargetId);
        await using(var command=new NpgsqlCommand("SELECT reporting.list_saved_incidents(@target,20,NULL,NULL,NULL,NULL);",connection,transaction))
        {
            command.Parameters.AddWithValue("target",request.TargetId);
            using var page=System.Text.Json.JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
            Assert.Equal(0,page.RootElement.GetProperty("publicationRevision").GetInt64());
            Assert.Empty(page.RootElement.GetProperty("items").EnumerateArray());
        }
        Task<SavedIncident> save=repository.SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default).AsTask();
        bool waiting=false;
        for(int attempt=0;attempt<100&&!waiting;attempt++)
        {
            await using var observe=database.DataSource.CreateCommand("""
                SELECT EXISTS(SELECT FROM pg_stat_activity WHERE datname=current_database()
                  AND wait_event_type='Lock' AND query LIKE '%control.save_incident(@target%');
                """);
            // Npgsql sends positional placeholders in the backend query text.
            observe.CommandText=observe.CommandText.Replace("@target","$1",StringComparison.Ordinal);
            waiting=(bool)(await observe.ExecuteScalarAsync())!;
            if(!waiting)await Task.Delay(20);
        }
        Assert.True(waiting,"The first save should wait for the empty list's publication lock.");
        Assert.False(save.IsCompleted);
        await transaction.CommitAsync(); Assert.NotNull(await save);
        SavedIncidentListPage refreshed=await repository.ListAsync(new(request.TargetId,20),Timeout,default);
        Assert.Equal(1,refreshed.PublicationRevision); Assert.Single(refreshed.Items);
    }
    private static Task Target(RepositoryTestDatabase database,Guid target) => Execute(database,"""
        INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
          authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
        VALUES(@target,@key,'Saved incident target','sql01',1433,interval '5 seconds','windows_integrated_service_identity',
          'mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
        """,("target",target),("key","saved-incident-"+target.ToString("N")));
    [Fact]
    public async Task SavedReportPreservesDurableNotesAndAllJsonPartsAfterSourceExpiry()
    {
        await using RepositoryTestDatabase database=await Database(); SaveIncidentRequest request=Request() with
            {Notes="<script>Plain text</script>\r\n"+new string('界',2000),Title="=Safe investigation"};
        await Target(database,request.TargetId); await Seed(database,request,3,large:true);
        await using NpgsqlDataSource server=database.CreateServerDataSource();
        SavedIncident saved=await new PostgreSqlSavedIncidentPort(server).SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default);
        await RetireSourcePartitions(database,request.TargetId);
        await Execute(database,"UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;",("target",request.TargetId));
        var reports=new PostgreSqlReportRepository(server);
        var command=new ReportRequest("saved-investigation",Guid.NewGuid(),ThreadId:saved.ThreadId);
        ReportRun[] concurrent=await Task.WhenAll(reports.CreateAsync(request.TargetId,command,Actor,default).AsTask(),
            reports.CreateAsync(request.TargetId,command,Actor,default).AsTask());
        ReportRun run=concurrent[0]; Assert.Equal(run.RunId,concurrent[1].RunId);
        Assert.Equal(ReportKind.SavedInvestigation,run.Kind); Assert.Equal(1,run.TargetRevision);
        Assert.Equal(TimeSpan.FromHours(24),run.ExpiresAtUtc-run.SnapshotUtc);
        Assert.Equal(run.RunId,(await reports.CreateAsync(request.TargetId,command,Actor,default)).RunId);
        ReportSectionPage metadata=await reports.ReadPageAsync(request.TargetId,run.RunId,"investigation",0,200,default);
        Assert.Equal(request.Notes,string.Concat(metadata.Rows.Where(row=>row.Values["field"]=="notes").Select(row=>row.Values["value"])));
        var evidence=new List<ReportRow>(); long after=0;
        while(true)
        {
            var page=await reports.ReadPageAsync(request.TargetId,run.RunId,"saved-evidence",after,200,default);
            evidence.AddRange(page.Rows); if(!page.HasMore)break; after=page.Rows[^1].Ordinal;
        }
        Assert.True(evidence.Count>200);
        foreach(var packet in saved.Evidence)
        {
            string raw=string.Concat(evidence.Where(row=>row.Values["packetId"]==packet.PacketId.ToString("D"))
                .Select(row=>System.Text.Json.JsonSerializer.Deserialize<string>(row.Values["evidenceJson"]!)));
            using var parsed=System.Text.Json.JsonDocument.Parse(raw);
            Assert.Equal(packet.Evidence.GetRawText(),parsed.RootElement.GetRawText());
        }
        Assert.Equal(saved.Evidence.Count,evidence.Select(row=>row.Values["packetId"]).Distinct().Count());
        Assert.All(evidence,row=>Assert.All(row.Values.Values,value=>Assert.True(value is null||System.Text.Encoding.UTF8.GetByteCount(value)<=4096)));
        var definition=ReportCatalog.Get(ReportKind.SavedInvestigation);
        string csv=System.Text.Encoding.UTF8.GetString(ReportRenderer.RenderCsv(definition,definition.Sections[1],evidence));
        using(var parser=new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(csv)))
        {
            parser.TextFieldType=Microsoft.VisualBasic.FileIO.FieldType.Delimited;
            parser.SetDelimiters(",");parser.HasFieldsEnclosedInQuotes=true;parser.TrimWhiteSpace=false;
            Assert.Equal("evidenceJson",parser.ReadFields()![11]);
            var payloads=new Dictionary<string,List<string>>();
            while(!parser.EndOfData)
            {
                string[] values=parser.ReadFields()!;
                if(!payloads.TryGetValue(values[0],out var parts))payloads[values[0]]=parts=[];
                parts.Add(System.Text.Json.JsonSerializer.Deserialize<string>(values[11])!);
            }
            foreach(var packet in saved.Evidence)
                Assert.Equal(packet.Evidence.GetRawText(),string.Concat(payloads[packet.PacketId.ToString("D")]));
        }
        Assert.Equal(1L,await Scalar(database,"SELECT count(*) FROM audit.report_activity WHERE activity_kind='create' AND outcome='accepted';"));
        await Assert.ThrowsAsync<PostgresException>(()=>reports.CreateAsync(request.TargetId,command,"S-1-5-21-1002",default).AsTask());
        await Assert.ThrowsAsync<PostgresException>(()=>reports.CreateAsync(request.TargetId,command with {ThreadId=Guid.NewGuid()},Actor,default).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>reports.CreateAsync(request.TargetId,command with {OperationId=Guid.NewGuid(),ThreadId=Guid.NewGuid()},Actor,default).AsTask());
    }

    [Fact]
    public async Task SavedReportAuditFailureRollsBackAndLegacyProducerRejectsSavedKind()
    {
        await using RepositoryTestDatabase database=await Database(); SaveIncidentRequest request=Request(); await Target(database,request.TargetId);
        await using NpgsqlDataSource server=database.CreateServerDataSource();
        SavedIncident saved=await new PostgreSqlSavedIncidentPort(server).SaveAsync(request,Actor,Guid.NewGuid(),Timeout,default);
        await Execute(database,"""
            CREATE FUNCTION public.reject_saved_report_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.activity_kind='create' THEN RAISE EXCEPTION 'test report audit unavailable'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER reject_saved_report_audit BEFORE INSERT ON audit.report_activity FOR EACH ROW EXECUTE FUNCTION public.reject_saved_report_audit();
            """);
        var reports=new PostgreSqlReportRepository(server);
        await Assert.ThrowsAsync<PostgresException>(()=>reports.CreateAsync(request.TargetId,new("saved-investigation",Guid.NewGuid(),ThreadId:saved.ThreadId),Actor,default).AsTask());
        Assert.Equal(0L,await Scalar(database,"SELECT count(*) FROM reporting.report_run;"));
        Assert.Equal(0L,await Scalar(database,"SELECT count(*) FROM reporting.report_section_row;"));
        await using NpgsqlConnection connection=await server.OpenConnectionAsync(); await Scope(connection,request.TargetId);
        await using var invalid=new NpgsqlCommand("SELECT reporting.create_report_run(@target,'saved-investigation',gen_random_uuid(),@actor,clock_timestamp()-interval '1 hour',clock_timestamp(),decode(repeat('00',32),'hex'));",connection);
        invalid.Parameters.AddWithValue("target",request.TargetId); invalid.Parameters.AddWithValue("actor",Actor);
        Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(()=>invalid.ExecuteScalarAsync())).SqlState);
        Assert.Equal(0L,await Scalar(database,"""
            SELECT count(*) FROM pg_proc p WHERE p.oid='reporting.create_saved_investigation_report(uuid,uuid,uuid,text,bytea)'::regprocedure
            AND (pg_get_userbyid(p.proowner)<>'sqlobserver_migrator' OR NOT p.prosecdef
              OR has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE') OR has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
              OR EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE'));
            """));
        invalid.CommandText="SELECT reporting.create_window_report_run(@target,'saved-investigation',gen_random_uuid(),@actor,clock_timestamp()-interval '1 hour',clock_timestamp(),decode(repeat('00',32),'hex'));";
        Assert.Equal("42501",(await Assert.ThrowsAsync<PostgresException>(()=>invalid.ExecuteScalarAsync())).SqlState);
    }
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
