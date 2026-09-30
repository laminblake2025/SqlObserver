using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class BackupEvidenceReadTests
{
    [Fact]
    public async Task BackupCatalogCreatesUpdatesAndDisablesRulesThroughProductionRepository()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var admin = new PostgreSqlAlertRepositoryPort(server); var evaluator = new PostgreSqlAlertRepositoryPort(collector);
        var rules = new List<AlertRuleDefinition>();
        foreach (AlertCatalogEntry entry in AlertCatalog.Entries.Where(entry => BackupAlertRuleContract.IsBackup(entry.Kind)))
        {
            var rule = new AlertRuleDefinition(Guid.NewGuid(), entry.Name, entry.Kind, null, entry.Comparison,
                entry.Threshold, entry.Hysteresis, 1, entry.ConfirmationWindow, TimeSpan.FromSeconds(15), clearConfirmationCount: 1);
            var request = new AlertRuleWriteRequest(rule, Guid.NewGuid().ToString("D"),
                BackupRuleAudit(target, AdministrativeAuditAction.CreateAlertRule), Timeout);
            AdministrativeAuditReceipt first = await admin.UpsertRuleAsync(request, CancellationToken.None);
            AdministrativeAuditReceipt replay = await admin.UpsertRuleAsync(request, CancellationToken.None);
            Assert.Equal(first.AuditId.Value,replay.AuditId.Value); Assert.Equal(first.RecordedAtUtc,replay.RecordedAtUtc); rules.Add(rule);
        }
        AlertRuleListPage page = await admin.ListRulesForAdminAsync(new(target), 100, null, Timeout, CancellationToken.None);
        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, item => Assert.True(BackupAlertRuleContract.IsBackup(item.Kind)));
        await SeedBackupCommitDestinationAsync(database,target);
        await CollectAsync(database,target,databaseCount:1,withLog:true); await SetPolicyAsync(server,target);
        await using (var policy = server.CreateCommand("""
            SELECT result_status FROM control.set_backup_freshness_policy(@target,1,decode(@database,'hex'),1,gen_random_uuid(),
                true,30,900,300,1,false,'S-1-5-21-1-2-3-1001',gen_random_uuid());
            """))
        { policy.Parameters.AddWithValue("target",target); policy.Parameters.AddWithValue("database",Fingerprint); Assert.Equal("applied",await policy.ExecuteScalarAsync()); }
        WorkerLeaseIdentity overview = await OverviewLeaseAsync(collector);
        BackupFleetSummary summary = await new PostgreSqlBackupFleetSummaryPort(collector).ReadAsync(new(new(target),1),overview,Timeout,CancellationToken.None);
        Assert.True(await new PostgreSqlBackupFleetCachePort(collector).PublishAsync(summary,overview,Timeout,CancellationToken.None));
        WorkerLeaseIdentity lease = await BackupAlertLeaseAsync(collector);
        AlertEvaluationBatch batch = await BackupCommitBatchAsync(evaluator,lease,
            await evaluator.ClaimDueEvaluationsAsync(lease,100,Timeout,CancellationToken.None));
        Assert.Equal(3,(await evaluator.EvaluateAndPersistAsync(batch,CancellationToken.None)).Evaluated);
        AlertRuleDefinition full = rules.Single(rule => rule.Kind==AlertRuleKind.BackupFullPolicy);
        Assert.Equal(AlertState.Firing,(await evaluator.GetStateAsync(new(target),full.RuleId,Timeout,CancellationToken.None))!.State);
        await AssertBackupCommitCountsAsync(database,target,3,3,1,0);
        var retagged = new AlertRuleDefinition(full.RuleId,full.Name,AlertRuleKind.BackupLogPolicy,null,full.Comparison,
            0,0,1,full.ConfirmationWindow,full.EvaluationInterval,clearConfirmationCount:1);
        Assert.Equal(400,(await Assert.ThrowsAsync<AlertRepositoryOperationException>(async () =>
            await admin.UpsertRuleAsync(new(retagged,Guid.NewGuid().ToString("D"),
                BackupRuleAudit(target,AdministrativeAuditAction.UpdateAlertRule),Timeout,1),CancellationToken.None))).StatusCode);
        var changed = new AlertRuleDefinition(full.RuleId,"backup.custom.full",full.Kind,null,full.Comparison,
            0,0,2,TimeSpan.FromMinutes(10),TimeSpan.FromSeconds(60),clearConfirmationCount:3);
        await admin.UpsertRuleAsync(new(changed,Guid.NewGuid().ToString("D"),
            BackupRuleAudit(target,AdministrativeAuditAction.UpdateAlertRule),Timeout,1),CancellationToken.None);
        await Assert.ThrowsAsync<PostgresException>(async () => await evaluator.EvaluateAndPersistAsync(batch,CancellationToken.None));
        var disabled = new AlertRuleDefinition(changed.RuleId,changed.Name,changed.Kind,null,changed.Comparison,
            0,0,2,changed.ConfirmationWindow,changed.EvaluationInterval,false,3);
        await admin.UpsertRuleAsync(new(disabled,Guid.NewGuid().ToString("D"),
            BackupRuleAudit(target,AdministrativeAuditAction.RetireAlertRule),Timeout,2),CancellationToken.None);
        AlertRuleState retired = (await evaluator.GetStateAsync(new(target),full.RuleId,Timeout,CancellationToken.None))!;
        Assert.Equal(AlertState.Resolved,retired.State); Assert.Equal("rule_disabled",retired.Reason);
        await using var verify = database.DataSource.CreateCommand("""
            SELECT (SELECT revision=3 AND NOT enabled FROM alerting.rule WHERE rule_id=@rule),
              (SELECT bool_and(cancelled_at IS NOT NULL AND cancel_reason='rule_revision_changed')
                 FROM alerting.delivery_outbox WHERE rule_id=@rule),
              (SELECT catalog_digest=@digest AND contract_version=3 FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts');
            """);
        verify.Parameters.AddWithValue("rule",full.RuleId); verify.Parameters.AddWithValue("digest",AlertCatalog.Digest);
        await using NpgsqlDataReader row = await verify.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        for(int i=0;i<3;i++) Assert.True(row.GetBoolean(i));
    }

    [Fact]
    public async Task BackupCatalogSqlRejectsForgedSourceMetricAndTimingBindings()
    {
        await using RepositoryTestDatabase database = await PrepareAsync(); Guid target = await SeedAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var wire = new Dictionary<string,object?> {
            ["Action"]="CreateAlertRule",["RuleId"]=Guid.NewGuid(),["TargetId"]=target,["Name"]="backup.native",
            ["Kind"]=3,["MetricId"]=null,["Comparison"]=1,["Threshold"]=0,["Hysteresis"]=0,["ConfirmationCount"]=2,
            ["ClearConfirmationCount"]=2,["ConfirmationWindow"]="00:05:00",["EvaluationInterval"]="00:00:30",
            ["Enabled"]=true,["SourceCollector"]="backups.status",["SourceSchemaVersion"]=2,["CatalogDigest"]=AlertCatalog.Digest,
            ["ExpectedRevision"]=null,["RequestDigest"]=null,
        };
        foreach ((string field,object? value) in new (string,object?)[] {
            ("SourceCollector","engine.core"),("SourceSchemaVersion",1),("MetricId","engine.user_connections"),
            ("MetricId",""),("Comparison",2),("Threshold",1),("EvaluationInterval","00:00:14"),
            ("ConfirmationWindow","00:00:00"),("SourceCollector",null),
        })
        {
            var forged = new Dictionary<string,object?>(wire) { [field]=value };
            await using NpgsqlConnection connection = await server.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT set_config('sqlobserver.target_scope',@scope,true);
                SELECT * FROM alerting.upsert_rule(@rule,@operation,'S-1-5-21-1-2-3-1001',gen_random_uuid());
                """,connection,transaction);
            command.Parameters.AddWithValue("scope",target.ToString("D"));
            command.Parameters.AddWithValue("rule",NpgsqlDbType.Jsonb,JsonSerializer.Serialize(forged));
            command.Parameters.AddWithValue("operation",Guid.NewGuid().ToString("D"));
            Assert.Equal("22023",(await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync())).SqlState);
        }
        await using var privileges = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM alerting.rule WHERE instance_id=@target),
              has_function_privilege('sqlobserver_server','alerting.upsert_rule(jsonb,text,text,uuid)','EXECUTE'),
              has_function_privilege('sqlobserver_collector','alerting.upsert_rule(jsonb,text,text,uuid)','EXECUTE');
            """);
        privileges.Parameters.AddWithValue("target",target); await using NpgsqlDataReader row = await privileges.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync()); Assert.Equal(0L,row.GetInt64(0)); Assert.True(row.GetBoolean(1)); Assert.False(row.GetBoolean(2));
    }

    [Fact]
    public async Task BackupCatalogMigrationRejectsDriftWithoutReplacingAdministrativeFunction()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrator = new PostgreSqlMigrationPort(database.DataSource);
        Assert.False((await migrator.ApplyPendingAsync(new(286,new(TimeSpan.FromMinutes(2))),CancellationToken.None)).HasFailures);
        string oldDefinition;
        await using (var read = database.DataSource.CreateCommand("SELECT pg_get_functiondef('alerting.upsert_rule(jsonb,text,text,uuid)'::regprocedure);"))
            oldDefinition = Assert.IsType<string>(await read.ExecuteScalarAsync());
        await using (var drift = database.DataSource.CreateCommand("UPDATE alerting.catalog_registry SET catalog_digest=repeat('f',64) WHERE catalog_id='sqlobserver.m8.alerts';"))
            await drift.ExecuteNonQueryAsync();
        Assert.True((await migrator.ApplyPendingAsync(new(1,Timeout),CancellationToken.None)).HasFailures);
        await using var verify = database.DataSource.CreateCommand("""
            SELECT pg_get_functiondef('alerting.upsert_rule(jsonb,text,text,uuid)'::regprocedure),
              (SELECT contract_version=2 AND catalog_digest=repeat('f',64) FROM alerting.catalog_registry WHERE catalog_id='sqlobserver.m8.alerts');
            """);
        await using NpgsqlDataReader row = await verify.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        Assert.Equal(oldDefinition,row.GetString(0)); Assert.True(row.GetBoolean(1));
    }

    private static AdministrativeAuditEnvelope BackupRuleAudit(Guid target,AdministrativeAuditAction action) =>
        new(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"),new(Guid.NewGuid()),action,new(target));
}
