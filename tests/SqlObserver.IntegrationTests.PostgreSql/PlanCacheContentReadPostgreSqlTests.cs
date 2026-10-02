using System.Text;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collectors;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class QueryPerformanceV2CommitPostgreSqlTests
{
    [Fact]
    public async Task CacheContentReadReturnsOnlyExactCanonicalEvidenceAndAuditsEveryCompletedAttempt()
    {
        await using Scene scene = await Prepare(protocolVersion: 3);
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        await using NpgsqlDataSource server = scene.Database.CreateServerDataSource();
        using var protector = new RoundtripProtector();
        Frame frame = await Start(scene);
        var items = new List<PlanCacheStatementContentObservation>(); int sourceBytes = 0;
        foreach (var statement in frame.Cache!.Items)
        {
            string text = $"SELECT '{statement.Watermark.Key.CacheEntryFingerprint}' -- <script>inert</script>";
            string xml = $"<ShowPlanXML><StmtSimple StatementText=\"{statement.Watermark.Key.CacheEntryFingerprint}\" /></ShowPlanXML>";
            sourceBytes += (text.Length + xml.Length) * 2;
            var encryptedText = await protector.ProtectAsync(scene.Target, SensitivePayloadKind.QueryText, Encoding.UTF8.GetBytes(text), default);
            var encryptedPlan = await protector.ProtectAsync(scene.Target, SensitivePayloadKind.ExecutionPlan, Encoding.UTF8.GetBytes(xml), default);
            items.Add(new(statement, PlanCacheContentValue.Protected(encryptedText!), PlanCacheContentValue.Protected(encryptedPlan!)));
        }
        var content = new PlanCacheContentOutputBatch(frame.Cache, 2, 2, 0, sourceBytes, items);
        var linked = await new QueryPerformanceProtectedContentCommitter(new PostgreSqlSensitivePayloadPort(scene.Collector))
            .WriteAsync(V3Payload(frame, content), frame.Work.TargetId, frame.Lease, Timeout, default);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        await runtime.CommitRunAsync(PortRequest(frame, linked), default);
        var repository = new PostgreSqlPlanCacheContentReadRepositoryPort(server);
        var service = new PlanCacheContentReadService(repository, protector);
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForAllTargets()), new(ApplicationRole.QueryTextReader, TargetAuthorizationScope.ForAllTargets())]);
        var key = frame.Cache.Items[0].Watermark.Key;
        foreach (var item in items)
            foreach (var kind in new[] { SensitivePayloadKind.QueryText, SensitivePayloadKind.ExecutionPlan })
            {
                var result = await service.ReadAsync(authorization, Request(entry: item.Statement.Watermark.Key.CacheEntryFingerprint, kind: kind), default);
                Assert.Equal("available", result.Status); Assert.Equal("available", result.Reason);
                Assert.Contains(item.Statement.Watermark.Key.CacheEntryFingerprint, result.Content);
                Assert.Equal(frame.Cache.ReadCompletedUtc, result.ObservedAtUtc);
            }
        foreach (var request in new[] { Request(run: Guid.NewGuid()), Request(database: 5), Request(inc: Digest('0')),
            Request(query: Digest('0')), Request(plan: Digest('0')), Request(entry: Digest('0')), Request(target: Guid.NewGuid()) })
        {
            var result = await service.ReadAsync(authorization, request, default);
            Assert.Equal("unavailable", result.Status); Assert.Equal("evidence_unavailable", result.Reason); Assert.Null(result.Content);
        }
        var mixed = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active,
            [new(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([frame.Work.TargetId])),
             new(ApplicationRole.QueryTextReader, TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(Guid.NewGuid())]))]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.ReadAsync(mixed, Request(), default));
        await using (var audit = scene.Database.DataSource.CreateCommand("""
            SELECT count(*)=12,
                count(*) FILTER(WHERE safe_details->>'accessOutcome'='opened')=4,
                count(*) FILTER(WHERE safe_details->>'accessOutcome'='unavailable')=7,
                count(*) FILTER(WHERE safe_details->>'accessOutcome'='denied')=1,
                bool_and(NOT safe_details::text LIKE '%<script>%' AND NOT safe_details::text LIKE '%ShowPlanXML%'
                    AND NOT safe_details ? 'payloadId' AND NOT safe_details ? 'keyIdentifier')
            FROM audit.activity WHERE action_name='query.cache_content.read';
            """))
        {
            await using var reader = await audit.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            for (int i = 0; i < 5; i++) Assert.True(reader.GetBoolean(i));
        }
        await using (var revise = scene.Database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=2 WHERE instance_id=@target;"))
        {
            revise.Parameters.AddWithValue("target", scene.Target); await revise.ExecuteNonQueryAsync();
        }
        Assert.Equal("evidence_unavailable", (await service.ReadAsync(authorization, Request(), default)).Reason);
        await using (var restore = scene.Database.DataSource.CreateCommand("UPDATE control.observation_target SET revision=1 WHERE instance_id=@target;"))
        {
            restore.Parameters.AddWithValue("target", scene.Target); await restore.ExecuteNonQueryAsync();
        }
        // Retiring counter evidence must make a still-retained content link inaccessible.
        await using (var retire = scene.Database.DataSource.CreateCommand("""
            SELECT partition_name::text FROM system.partition_registry WHERE parent_table='plan_cache_runtime_delta'
             AND lifecycle_state='attached' AND range_start=date_trunc('day',clock_timestamp(),'UTC');
            """))
        {
            string partition = Assert.IsType<string>(await retire.ExecuteScalarAsync());
            Assert.Matches("^plan_cache_runtime_delta_p[0-9]{8}$", partition);
            await using var drop = scene.Database.DataSource.CreateCommand($"ALTER TABLE events.plan_cache_runtime_delta DETACH PARTITION events.{partition};");
            await drop.ExecuteNonQueryAsync();
        }
        var retired = await service.ReadAsync(authorization, Request(), default);
        Assert.Equal("evidence_unavailable", retired.Reason); Assert.Null(retired.Content);
        await using var retained = scene.Database.DataSource.CreateCommand("SELECT count(*) FROM events.plan_cache_content_link;");
        Assert.Equal(4L, await retained.ExecuteScalarAsync());

        PlanCacheContentReadRequest Request(Guid? target = null, Guid? run = null, int database = 6, string? inc = null,
            string? query = null, string? plan = null, string? entry = null, SensitivePayloadKind kind = SensitivePayloadKind.QueryText) =>
            new(new(target ?? scene.Target), run ?? frame.Run.Value, database, inc ?? key.DatabaseIncarnation,
                query ?? key.Query.QueryFingerprint, plan ?? key.Plan.PlanFingerprint, entry ?? key.CacheEntryFingerprint, kind, Timeout);
    }

    [Theory]
    [InlineData("disabled", "capture_disabled")]
    [InlineData("no_key", "protection_unavailable")]
    [InlineData("not_selected", "not_selected")]
    [InlineData("unavailable", "source_unavailable")]
    [InlineData("oversized", "oversized")]
    [InlineData("byte_limit", "response_byte_limit")]
    [InlineData("protection_unavailable", "protection_unavailable")]
    [InlineData("protection_failed", "protection_failed")]
    public async Task CacheContentReadPreservesMissingReasonsAndRejectsUnfinishedEvidence(string state, string reason)
    {
        await using Scene scene = await Prepare(protocolVersion: 3);
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        await using NpgsqlDataSource server = scene.Database.CreateServerDataSource();
        Frame frame = await Start(scene);
        var repository = new PostgreSqlPlanCacheContentReadRepositoryPort(server);
        var key = frame.Cache!.Items[^1].Watermark.Key;
        var request = new PlanCacheContentReadRequest(frame.Work.TargetId, frame.Run.Value, 6, key.DatabaseIncarnation,
            key.Query.QueryFingerprint, key.Plan.PlanFingerprint, key.CacheEntryFingerprint, SensitivePayloadKind.QueryText, Timeout);
        // An allocated run and its source identities cannot expose content before canonical completion.
        Assert.Equal("evidence_unavailable", (await repository.ReadAsync(request, default)).Reason);
        bool disabled = state is "disabled" or "no_key";
        var missing = state switch
        {
            "oversized" => PlanCacheContentState.Oversized,
            "byte_limit" => PlanCacheContentState.ResponseByteLimit,
            "protection_unavailable" => PlanCacheContentState.ProtectionUnavailable,
            "protection_failed" => PlanCacheContentState.ProtectionFailed,
            _ => PlanCacheContentState.Unavailable,
        };
        var selected = disabled ? [] : frame.Cache.Items.Take(state == "not_selected" ? 1 : 2).Select(statement =>
            new PlanCacheStatementContentObservation(statement, PlanCacheContentValue.Missing(SensitivePayloadKind.QueryText, missing),
                PlanCacheContentValue.Missing(SensitivePayloadKind.ExecutionPlan, missing))).ToArray();
        var content = new PlanCacheContentOutputBatch(frame.Cache, selected.Length, selected.Length, 0, 0, selected,
            state == "no_key" ? PlanCacheContentCaptureState.ProtectionUnavailable : null);
        await new PostgreSqlCollectorRuntimeRepositoryPort(migrator).CommitRunAsync(PortRequest(frame, V3Payload(frame, content)), default);
        var result = await repository.ReadAsync(request, default);
        Assert.Equal(reason, result.Reason); Assert.Null(result.Payload);
        Assert.Equal(frame.Cache.ReadCompletedUtc, result.ObservedAtUtc);

        await using var catalog = server.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_server','control.get_plan_cache_content_payload(uuid,uuid,integer,text,text,text,text,text)','EXECUTE'),
                NOT has_function_privilege('sqlobserver_collector','control.get_plan_cache_content_payload(uuid,uuid,integer,text,text,text,text,text)','EXECUTE'),
                NOT has_table_privilege('sqlobserver_server','events.plan_cache_content_link','SELECT'),
                NOT has_table_privilege('sqlobserver_server',(SELECT oid FROM pg_class WHERE relname='protected_diagnostic_payload'),'SELECT'),
                current_setting('sqlobserver.target_scope',true) IS NULL OR current_setting('sqlobserver.target_scope',true)='',
                current_setting('sqlobserver.role',true) IS NULL OR current_setting('sqlobserver.role',true)='';
            """);
        await using (var reader = await catalog.ExecuteReaderAsync())
        { Assert.True(await reader.ReadAsync()); for (int i = 0; i < 6; i++) Assert.True(reader.GetBoolean(i)); }
        await using var noScope = server.CreateCommand("""
            SELECT * FROM control.get_plan_cache_content_payload(@target,@run,6,@inc,@query,@plan,@entry,'query_text');
            """);
        noScope.Parameters.AddWithValue("target", scene.Target); noScope.Parameters.AddWithValue("run", frame.Run.Value);
        noScope.Parameters.AddWithValue("inc", key.DatabaseIncarnation); noScope.Parameters.AddWithValue("query", key.Query.QueryFingerprint);
        noScope.Parameters.AddWithValue("plan", key.Plan.PlanFingerprint); noScope.Parameters.AddWithValue("entry", key.CacheEntryFingerprint);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () => await noScope.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task CacheContentReadReportsLegacyCounterOnlyCapturesAsMissingContent()
    {
        await using Scene scene = await Prepare();
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        await using NpgsqlDataSource server = scene.Database.CreateServerDataSource();
        Frame frame = await Start(scene);
        await new PostgreSqlCollectorRuntimeRepositoryPort(migrator).CommitRunAsync(PortRequest(frame, PortPayload(frame)), default);
        var key = frame.Cache!.Items[0].Watermark.Key;
        var result = await new PostgreSqlPlanCacheContentReadRepositoryPort(server).ReadAsync(new(frame.Work.TargetId,
            frame.Run.Value, 6, key.DatabaseIncarnation, key.Query.QueryFingerprint, key.Plan.PlanFingerprint,
            key.CacheEntryFingerprint, SensitivePayloadKind.QueryText, Timeout), default);
        Assert.Equal("capture_missing", result.Reason); Assert.Null(result.Payload);
        Assert.Equal(frame.Cache.ReadCompletedUtc, result.ObservedAtUtc);
    }
}
