using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Alerting;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Alerting;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M8AlertingPostgreSqlIntegrationTests
{
    private const string TemplateActor = "S-1-5-18";
    private static readonly int[] TemplateRaceOutcomes = [200, 409];

    [Fact]
    public async Task RuleTemplateChildIdentifiersAreDeterministicAndAcceptedByNativeRuleWriter()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using (var identities = database.DataSource.CreateCommand("""
            SELECT alerting.require_canonical_operation_uuid(alerting.rule_template_id('child-operation-'||i)::text)
            FROM generate_series(1,256) i;
            SELECT alerting.rule_template_id('same-child')=alerting.rule_template_id('same-child'),
              alerting.rule_template_id('same-child')<>alerting.rule_template_id('different-child');
            """))
        {
            await using NpgsqlDataReader rows = await identities.ExecuteReaderAsync();
            int validated = 0;
            while (await rows.ReadAsync()) validated++;
            Assert.Equal(256, validated);
            Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync());
            Assert.True(rows.GetBoolean(0)); Assert.True(rows.GetBoolean(1));
        }
        Guid target = Guid.NewGuid(), template = Guid.NewGuid(), deployment = Guid.NewGuid();
        await SeedTemplateTargetAsync(database, target);
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        using JsonDocument preview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, deployment, Guid.NewGuid()), [target]);
        JsonElement member = preview.RootElement.GetProperty("deployment").GetProperty("items")[0];
        await using (var identities = database.DataSource.CreateCommand("SELECT alerting.require_canonical_operation_uuid(@operation),alerting.require_canonical_operation_uuid(@rule);"))
        {
            identities.Parameters.AddWithValue("operation", member.GetProperty("operationId").GetString()!);
            identities.Parameters.AddWithValue("rule", member.GetProperty("ruleId").GetString()!);
            await identities.ExecuteNonQueryAsync();
        }
        using JsonDocument applied = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = deployment, expectedRevision = 1, action = "apply", reason = "Verify native child identifiers" }, [target]);
        await AssertTemplateMemberAppliedAsync(database, deployment, applied.RootElement);
        await using var audit = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.admin_idempotency WHERE action_name='upsert_rule' AND operation_id=@operation; SELECT result_sqlstate FROM alerting.rule_deployment_member WHERE deployment_id=@job;");
        audit.Parameters.AddWithValue("operation", member.GetProperty("operationId").GetGuid()); audit.Parameters.AddWithValue("job", deployment);
        await using NpgsqlDataReader evidence = await audit.ExecuteReaderAsync(); Assert.True(await evidence.ReadAsync()); Assert.Equal(1L, evidence.GetInt64(0));
        Assert.True(await evidence.NextResultAsync()); Assert.True(await evidence.ReadAsync()); Assert.True(evidence.IsDBNull(0));
    }

    [Fact]
    public async Task RuleTemplateVersionsAreImmutableAndReplayIsActorBoundBeforeCatalogApproval()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid template = Guid.NewGuid();
        object command = TemplateWrite(template, Guid.NewGuid());
        using JsonDocument first = await TemplateCallAsync(database, "rule_template_write", command, []);
        using JsonDocument replay = await TemplateCallAsync(database, "rule_template_write", command, [], approval: false);
        Assert.Equal(first.RootElement.GetRawText(), replay.RootElement.GetRawText());
        await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_template_write", command, [], actor: "S-1-5-19"));
        using JsonDocument second = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid(), 1, threshold: 2), []);
        Assert.Equal(2, second.RootElement.GetProperty("version").GetInt32());
        await using var read = database.DataSource.CreateCommand("SELECT alerting.rule_template_get(@id,1,true),count(*) FROM alerting.rule_template_version WHERE template_id=@id;");
        read.Parameters.AddWithValue("id", template);
        await using NpgsqlDataReader rows = await read.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        using JsonDocument old = JsonDocument.Parse(rows.GetString(0));
        Assert.Equal(1, old.RootElement.GetProperty("values").GetProperty("threshold").GetInt32());
        Assert.Equal(2L, rows.GetInt64(1));
    }

    [Fact]
    public async Task RuleTemplatePreviewRequiresEntireNativeGroupAndFreezesMembership()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), template = Guid.NewGuid(), deployment = Guid.NewGuid();
        await SeedTemplateTargetAsync(database, a); await SeedTemplateTargetAsync(database, b);
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        object preview = TemplatePreview(template, deployment, Guid.NewGuid());
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_deployment_preview", preview, [a]));
        Assert.Equal("42501", denied.SqlState);
        await using (var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule_deployment;")) Assert.Equal(0L, await count.ExecuteScalarAsync());
        using JsonDocument accepted = await TemplateCallAsync(database, "rule_deployment_preview", preview, [a, b]);
        Assert.Equal(2, accepted.RootElement.GetProperty("deployment").GetProperty("items").GetArrayLength());
        Guid c = Guid.NewGuid(); await SeedTemplateTargetAsync(database, c);
        using JsonDocument replay = await TemplateCallAsync(database, "rule_deployment_preview", preview, [a, b]);
        Assert.Equal(accepted.RootElement.GetRawText(), replay.RootElement.GetRawText());
        await using var changed = database.DataSource.CreateCommand("UPDATE control.observation_target_grouping SET revision=revision+1 WHERE instance_id=@id;");
        changed.Parameters.AddWithValue("id", b); await changed.ExecuteNonQueryAsync();
        using JsonDocument batch = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = deployment, expectedRevision = 1, action = "apply", reason = "Apply frozen group" }, [a, b]);
        string[] statuses = batch.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("status").GetString()!).ToArray();
        Assert.Contains("applied", statuses); Assert.Contains("conflict", statuses);
        Assert.True(batch.RootElement.GetProperty("sealed").GetBoolean());
    }

    [Fact]
    public async Task RuleTemplateBatchesAreBoundedImmutableAndRollbackProtectsLaterManualEdits()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid template = Guid.NewGuid(), deployment = Guid.NewGuid(); var targets = new List<Guid>();
        for (int i = 0; i < 11; i++) { Guid id = Guid.NewGuid(); targets.Add(id); await SeedTemplateTargetAsync(database, id); }
        using JsonDocument library = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        using JsonDocument preview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, deployment, Guid.NewGuid()), targets);
        object apply = new { operationId = Guid.NewGuid(), deploymentId = deployment, expectedRevision = 1, action = "apply", reason = "First bounded batch" };
        using JsonDocument first = await TemplateCallAsync(database, "rule_deployment_batch", apply, targets);
        Assert.Equal(10, first.RootElement.GetProperty("items").GetArrayLength()); Assert.Equal(1, first.RootElement.GetProperty("pendingCount").GetInt32());
        using JsonDocument replay = await TemplateCallAsync(database, "rule_deployment_batch", apply, targets, approval: false);
        Assert.Equal(first.RootElement.GetRawText(), replay.RootElement.GetRawText());
        using JsonDocument stop = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = deployment, expectedRevision = 2, action = "stop", reason = "Stop pending member" }, targets);
        Assert.True(stop.RootElement.GetProperty("sealed").GetBoolean());
        Guid rollback = Guid.NewGuid();
        using JsonDocument proposal = await TemplateCallAsync(database, "rule_deployment_rollback_preview", new { operationId = Guid.NewGuid(), deploymentId = rollback, sourceDeploymentId = deployment, reason = "Restore applied group" }, targets);
        Assert.Equal(10, proposal.RootElement.GetProperty("deployment").GetProperty("items").GetArrayLength());
        Guid changedTarget = first.RootElement.GetProperty("items")[0].GetProperty("targetId").GetGuid();
        await using var changed = database.DataSource.CreateCommand("UPDATE alerting.rule SET revision=revision+1 WHERE instance_id=@id;"); changed.Parameters.AddWithValue("id", changedTarget); await changed.ExecuteNonQueryAsync();
        using JsonDocument reverted = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = rollback, expectedRevision = 1, action = "apply", reason = "Apply rollback" }, targets);
        Assert.Equal(1, reverted.RootElement.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("status").GetString() == "conflict"));
        await using var verify = database.DataSource.CreateCommand("SELECT count(*) FILTER(WHERE enabled),count(*) FROM alerting.rule WHERE instance_id=ANY(@ids);"); verify.Parameters.AddWithValue("ids", targets.ToArray());
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0)); Assert.Equal(10L, reader.GetInt64(1));
    }

    [Fact]
    public async Task RuleTemplateOwnerPublicationRequiresOwnerOrAllTargetAdministrator()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid template = Guid.NewGuid();
        using JsonDocument created = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        object update = TemplateWrite(template, Guid.NewGuid(), 1, threshold: 2);
        PostgresException denied = await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_template_write", update, [], actor: "S-1-5-19")); Assert.Equal("42501", denied.SqlState);
        using JsonDocument approved = await TemplateCallAsync(database, "rule_template_write", update, [], actor: "S-1-5-19", all: true); Assert.Equal(2, approved.RootElement.GetProperty("version").GetInt32());
        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_template_write", update, [], actor: TemplateActor, all: true)); Assert.Equal("40001", divergent.SqlState);
        await using var immutable = database.DataSource.CreateCommand("UPDATE alerting.rule_template_version SET payload='{}' WHERE template_id=@id;"); immutable.Parameters.AddWithValue("id", template);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task RuleTemplateOverflowIsRefusedAfterWholeGroupAuthorizationWithoutPersistingCohort()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid template = Guid.NewGuid(); var targets = new List<Guid>();
        for (int i = 0; i < 101; i++) { Guid id = Guid.NewGuid(); targets.Add(id); await SeedTemplateTargetAsync(database, id); }
        using JsonDocument created = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        object request = TemplatePreview(template, Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_deployment_preview", request, targets.Take(100).ToArray()))).SqlState);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_deployment_preview", request, targets))).SqlState);
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule_deployment;"); Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RuleTemplateSourceUnavailableBlocksEnabledProposalButPermitsExistingRuleCleanup()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(), job = Guid.NewGuid();
        await SeedTemplateTargetAsync(database, target);
        using JsonDocument created = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        using JsonDocument firstPreview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, job, Guid.NewGuid()), [target]);
        using JsonDocument firstApply = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = job, expectedRevision = 1, action = "apply", reason = "Initial rule" }, [target]);
        await AssertTemplateMemberAppliedAsync(database, job, firstApply.RootElement);
        await using var unavailable = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET enabled=false WHERE instance_id=@id AND collector_id='engine.core';"); unavailable.Parameters.AddWithValue("id", target); await unavailable.ExecuteNonQueryAsync();
        using JsonDocument disabled = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid(), 1, enabled: false), []);
        Guid cleanup = Guid.NewGuid();
        using JsonDocument cleanupPreview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, cleanup, Guid.NewGuid(), 2), [target]);
        JsonElement candidate = cleanupPreview.RootElement.GetProperty("deployment").GetProperty("items")[0]; Assert.Equal("unavailable", candidate.GetProperty("compatibility").GetString()); Assert.Equal("update", candidate.GetProperty("decision").GetString());
        using JsonDocument cleanupApply = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = cleanup, expectedRevision = 1, action = "apply", reason = "Disable unavailable rule" }, [target]); Assert.Equal("applied", cleanupApply.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        Guid blocked = Guid.NewGuid();
        using JsonDocument blockedPreview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, blocked, Guid.NewGuid()), [target]); Assert.Equal("incompatible", blockedPreview.RootElement.GetProperty("deployment").GetProperty("items")[0].GetProperty("decision").GetString());
    }

    [Fact]
    public async Task RuleTemplateRollbackRestoresValidPriorBindingAndLeavesOtherSameSignalRulesAlone()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        await SeedTemplateTargetAsync(database, target);
        await using (var other = database.DataSource.CreateCommand("INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval) VALUES(@rule,@target,'other.connections',1,'engine.user_connections',2,9,0,1,interval '0 seconds',interval '15 seconds');"))
        { other.Parameters.AddWithValue("rule", Guid.NewGuid()); other.Parameters.AddWithValue("target", target); await other.ExecuteNonQueryAsync(); }
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        using JsonDocument p1 = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, first, Guid.NewGuid()), [target]);
        using JsonDocument a1 = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = first, expectedRevision = 1, action = "apply", reason = "Apply version one" }, [target]);
        using JsonDocument v2 = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid(), 1, threshold: 2), []);
        using JsonDocument p2 = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, second, Guid.NewGuid(), 2), [target]);
        using JsonDocument a2 = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = second, expectedRevision = 1, action = "apply", reason = "Apply version two" }, [target]);
        Guid rollback = Guid.NewGuid();
        using JsonDocument rp = await TemplateCallAsync(database, "rule_deployment_rollback_preview", new { operationId = Guid.NewGuid(), deploymentId = rollback, sourceDeploymentId = second, reason = "Restore version one" }, [target]);
        using JsonDocument ra = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = rollback, expectedRevision = 1, action = "apply", reason = "Apply restored values" }, [target]);
        Assert.Equal("applied", ra.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        await using var verify = database.DataSource.CreateCommand("SELECT b.template_version,b.deployment_id,b.rule_revision,r.revision,r.threshold,(SELECT threshold FROM alerting.rule WHERE instance_id=@target AND name='other.connections') FROM alerting.rule_template_binding b JOIN alerting.rule r USING(instance_id,rule_id) WHERE b.instance_id=@target;"); verify.Parameters.AddWithValue("target", target);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0)); Assert.Equal(first, reader.GetGuid(1)); Assert.Equal(reader.GetInt64(3), reader.GetInt64(2)); Assert.Equal(1d, reader.GetDouble(4)); Assert.Equal(9d, reader.GetDouble(5));
    }

    [Fact]
    public async Task RuleTemplateApplyAndStopRaceHasOneRevisionWinnerAndPrivateHistoryAnchors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(), job = Guid.NewGuid();
        await SeedTemplateTargetAsync(database, target); using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        using JsonDocument preview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, job, Guid.NewGuid()), [target]);
        async Task<int> Race(string action)
        {
            try { using JsonDocument result = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = job, expectedRevision = 1, action, reason = "Race one revision" }, [target]); return 200; }
            catch (PostgresException error) when (error.SqlState == "40001") { return 409; }
        }
        int[] outcomes = await Task.WhenAll(Race("apply"), Race("stop")); Assert.Equal(TemplateRaceOutcomes, outcomes.Order().ToArray());
        await using var history = database.DataSource.CreateCommand("SELECT alerting.rule_deployment_history(@id,1,NULL,false,ARRAY[@target],true);"); history.Parameters.AddWithValue("id", job); history.Parameters.AddWithValue("target", target);
        using JsonDocument page = JsonDocument.Parse((string)(await history.ExecuteScalarAsync())!); Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());
        await using var foreign = database.DataSource.CreateCommand("SELECT alerting.rule_deployment_history(@id,1,@before,false,ARRAY[@target],true);"); foreign.Parameters.AddWithValue("id", job); foreign.Parameters.AddWithValue("target", target); foreign.Parameters.AddWithValue("before", Guid.NewGuid()); Assert.Equal("P0002", (await Assert.ThrowsAsync<PostgresException>(() => foreign.ExecuteScalarAsync())).SqlState);
    }

    [Fact]
    public async Task RuleTemplateTablesAreForcedRlsAndOnlyBoundedServerEntryPointsAreGranted()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        await using var check = database.DataSource.CreateCommand("""
            SELECT count(*),bool_and(relrowsecurity AND relforcerowsecurity) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='alerting' AND c.relname IN('rule_template','rule_template_version','rule_deployment','rule_deployment_member','rule_template_binding','rule_template_activity','rule_template_operation');
            SELECT has_table_privilege('sqlobserver_server','alerting.rule_template_operation','SELECT'),
             has_function_privilege('sqlobserver_collector','alerting.rule_template_write(jsonb,text,uuid,boolean,uuid[],boolean,jsonb)','EXECUTE'),
             has_function_privilege('sqlobserver_auditor','alerting.rule_deployment_get(uuid,boolean,uuid[],boolean)','EXECUTE'),
             has_function_privilege('sqlobserver_server','alerting.rule_template_admit(jsonb,text,text,jsonb)','EXECUTE'),
             has_sequence_privilege('sqlobserver_server','alerting.rule_template_operation_change_sequence_seq','USAGE');
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace CROSS JOIN LATERAL aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) acl
             WHERE n.nspname='alerting' AND (p.proname LIKE 'rule_template_%' OR p.proname LIKE 'rule_deployment_%') AND acl.grantee=0 AND acl.privilege_type='EXECUTE';
            """);
        await using NpgsqlDataReader rows = await check.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync()); Assert.Equal(7L, rows.GetInt64(0)); Assert.True(rows.GetBoolean(1)); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); for (int i = 0; i < 5; i++) Assert.False(rows.GetBoolean(i)); Assert.True(await rows.NextResultAsync()); Assert.True(await rows.ReadAsync()); Assert.Equal(0L, rows.GetInt64(0));
    }

    [Fact]
    public async Task RuleTemplateProductionAdapterReplaysConcurrentExactPreviewsAcrossRepeatableReadSnapshots()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(); await SeedTemplateTargetAsync(database, target);
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlAlertRuleTemplateRepositoryPort(server);
        var request = TemplatePreviewRequest(template, Guid.NewGuid(), target);
        await using NpgsqlConnection gate = await database.DataSource.OpenConnectionAsync();
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended('rule-deployment-jobs',0));", gate)) await acquire.ExecuteNonQueryAsync();
        Task<AlertRuleDeploymentPreviewReceipt> first = repository.PreviewAsync(request, Timeout(), CancellationToken.None).AsTask();
        Task<AlertRuleDeploymentPreviewReceipt> second = repository.PreviewAsync(request, Timeout(), CancellationToken.None).AsTask();
        await WaitForTemplateLockWaitersAsync(database, 2);
        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended('rule-deployment-jobs',0));", gate)) await release.ExecuteNonQueryAsync();
        AlertRuleDeploymentPreviewReceipt[] results = await Task.WhenAll(first, second);
        Assert.Equal(JsonSerializer.Serialize(results[0]), JsonSerializer.Serialize(results[1]));
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule_deployment;"); Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RuleTemplatePreviewKeepsCoherentGroupingSnapshotWhileConcurrentLabelEditWaits()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(); await SeedTemplateTargetAsync(database, target);
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var repository = new PostgreSqlAlertRuleTemplateRepositoryPort(server);
        await using NpgsqlConnection edit = await database.DataSource.OpenConnectionAsync(); await using NpgsqlTransaction transaction = await edit.BeginTransactionAsync();
        await using (var lockTarget = new NpgsqlCommand("SELECT 1 FROM control.observation_target WHERE instance_id=@target FOR UPDATE;", edit, transaction)) { lockTarget.Parameters.AddWithValue("target", target); await lockTarget.ExecuteNonQueryAsync(); }
        var request = TemplatePreviewRequest(template, Guid.NewGuid(), target);
        Task<AlertRuleDeploymentPreviewReceipt> preview = repository.PreviewAsync(request, Timeout(), CancellationToken.None).AsTask();
        await WaitForTemplateLockWaitersAsync(database, 1);
        await using (var labels = new NpgsqlCommand("UPDATE control.observation_target_grouping SET environment_name='left-group',revision=revision+1 WHERE instance_id=@target;", edit, transaction)) { labels.Parameters.AddWithValue("target", target); await labels.ExecuteNonQueryAsync(); }
        await transaction.CommitAsync();
        AlertRuleDeploymentPreviewReceipt frozen = await preview; AlertRuleDeploymentMember member = Assert.Single(frozen.Deployment.Items); Assert.Equal(1, member.GroupingRevision);
        using JsonDocument batch = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = request.DeploymentId, expectedRevision = 1, action = "apply", reason = "Check stale grouping fence" }, [target]);
        Assert.Equal("conflict", batch.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task RuleTemplateNativeDisableWaitsForDispatchThenResolvesStateAndCancelsOwnedDelivery()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(), firstJob = Guid.NewGuid(); await SeedTemplateTargetAsync(database, target);
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        using JsonDocument preview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, firstJob, Guid.NewGuid()), [target]);
        using JsonDocument applied = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = firstJob, expectedRevision = 1, action = "apply", reason = "Install dispatch rule" }, [target]);
        Guid rule = applied.RootElement.GetProperty("items")[0].GetProperty("ruleId").GetGuid();
        var lease = new WorkerLeaseIdentity(new WorkerLeaseKey("alerts/templates/dispatch"), new WorkerExecutionId(Guid.NewGuid()), new FencingToken(1));
        await SeedTargetAndLeaseAsync(database, target, lease); await SeedActiveRuleStateAsync(database, target, rule);
        Guid destination = Guid.NewGuid(), delivery = Guid.NewGuid(); await SeedApprovedDestinationAsync(database, target, destination); await SeedOutboxAsync(database, target, rule, destination, delivery, Guid.NewGuid());
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var alerts = new PostgreSqlAlertRepositoryPort(collector);
        AlertDeliveryWork claimed = Assert.Single(await alerts.ClaimDueDeliveriesAsync(lease, 10, Timeout(), CancellationToken.None));
        await using IAlertDeliveryDispatchPermit permit = await alerts.AcquireDeliveryDispatchPermitAsync(claimed, Timeout(), CancellationToken.None);
        using JsonDocument disabled = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid(), 1, enabled: false), []);
        Guid cleanup = Guid.NewGuid(); using JsonDocument cleanupPreview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, cleanup, Guid.NewGuid(), 2), [target]);
        Task<JsonDocument> transition = TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = cleanup, expectedRevision = 1, action = "apply", reason = "Disable through dispatch fence" }, [target]);
        await WaitForTemplateLockWaitersAsync(database, 1); Assert.False(transition.IsCompleted); await permit.DisposeAsync();
        using JsonDocument result = await transition; Assert.Equal("applied", result.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        await using var verify = database.DataSource.CreateCommand("SELECT s.state,d.cancel_reason,d.cancelled_at IS NOT NULL,d.completed_at IS NOT NULL,d.lease_work_key IS NULL FROM alerting.rule_state s JOIN alerting.delivery_outbox d USING(instance_id,rule_id) WHERE d.delivery_id=@id;"); verify.Parameters.AddWithValue("id", delivery);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal((short)5, reader.GetInt16(0)); Assert.Equal("rule_disabled", reader.GetString(1)); Assert.True(reader.GetBoolean(2)); Assert.True(reader.GetBoolean(3)); Assert.True(reader.GetBoolean(4));
    }

    [Fact]
    public async Task RuleTemplateNamedReplacementRejectsDifferentSignalWhileCreateMissingSkipsIt()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(), existing = Guid.NewGuid(); await SeedTemplateTargetAsync(database, target);
        await using (var seed = database.DataSource.CreateCommand("INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval) VALUES(@id,@target,'group.connections',3,NULL,1,0,0,1,interval '0 seconds',interval '15 seconds');")) { seed.Parameters.AddWithValue("id", existing); seed.Parameters.AddWithValue("target", target); await seed.ExecuteNonQueryAsync(); }
        using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", TemplateWrite(template, Guid.NewGuid()), []);
        Guid replace = Guid.NewGuid(); using JsonDocument replaced = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, replace, Guid.NewGuid()), [target]); Assert.Equal("incompatible", replaced.RootElement.GetProperty("deployment").GetProperty("items")[0].GetProperty("decision").GetString());
        Guid create = Guid.NewGuid(); using JsonDocument skipped = await TemplateCallAsync(database, "rule_deployment_preview", new { operationId = Guid.NewGuid(), deploymentId = create, templateId = template, templateVersion = 1, group = new { kind = "tag", value = "production" }, mode = "create_missing", reason = "Respect existing named rule" }, [target]); Assert.Equal("skip_existing", skipped.RootElement.GetProperty("deployment").GetProperty("items")[0].GetProperty("decision").GetString());
        using JsonDocument applied = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = create, expectedRevision = 1, action = "apply", reason = "Record existing rule skip" }, [target]); Assert.Equal("skipped", applied.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        await using var verify = database.DataSource.CreateCommand("SELECT rule_id,kind,revision FROM alerting.rule WHERE instance_id=@id;"); verify.Parameters.AddWithValue("id", target); await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(existing, reader.GetGuid(0)); Assert.Equal((short)3, reader.GetInt16(1)); Assert.Equal(1L, reader.GetInt64(2)); Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task RuleTemplateFreshCatalogDriftAndPreviewExpiryNeverOverwriteRulesOrBreakHistoricalReplay()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync(); Guid target = Guid.NewGuid(), template = Guid.NewGuid(), deployment = Guid.NewGuid(); await SeedTemplateTargetAsync(database, target);
        object library = TemplateWrite(template, Guid.NewGuid()); using JsonDocument saved = await TemplateCallAsync(database, "rule_template_write", library, []);
        using JsonDocument preview = await TemplateCallAsync(database, "rule_deployment_preview", TemplatePreview(template, deployment, Guid.NewGuid()), [target]);
        await using (var drift = database.DataSource.CreateCommand("UPDATE alerting.catalog_registry SET catalog_digest=repeat('e',64) WHERE catalog_id='sqlobserver.m8.alerts';")) await drift.ExecuteNonQueryAsync();
        using JsonDocument conflicts = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = deployment, expectedRevision = 1, action = "apply", reason = "Require current catalog" }, [target]); Assert.Equal("conflict", conflicts.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        using JsonDocument replay = await TemplateCallAsync(database, "rule_template_write", library, [], approval: false); Assert.Equal(saved.RootElement.GetRawText(), replay.RootElement.GetRawText());
        await using (var restore = database.DataSource.CreateCommand("UPDATE alerting.catalog_registry SET catalog_digest=@digest WHERE catalog_id='sqlobserver.m8.alerts';")) { restore.Parameters.AddWithValue("digest", AlertCatalog.Digest); await restore.ExecuteNonQueryAsync(); }
        Guid expired = Guid.NewGuid(); object command = TemplatePreview(template, expired, Guid.NewGuid()); using JsonDocument original = await TemplateCallAsync(database, "rule_deployment_preview", command, [target]);
        await using (var expire = database.DataSource.CreateCommand("UPDATE alerting.rule_deployment SET expires_at=clock_timestamp()-interval '1 second' WHERE deployment_id=@id;")) { expire.Parameters.AddWithValue("id", expired); await expire.ExecuteNonQueryAsync(); }
        using JsonDocument old = await TemplateCallAsync(database, "rule_deployment_preview", command, [target], approval: false); Assert.Equal(original.RootElement.GetRawText(), old.RootElement.GetRawText());
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() => TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = expired, expectedRevision = 1, action = "apply", reason = "Expired preview cannot apply" }, [target]))).SqlState);
        using JsonDocument stopped = await TemplateCallAsync(database, "rule_deployment_batch", new { operationId = Guid.NewGuid(), deploymentId = expired, expectedRevision = 1, action = "stop", reason = "Seal expired pending work" }, [target]); Assert.True(stopped.RootElement.GetProperty("sealed").GetBoolean());
        await using var count = database.DataSource.CreateCommand("SELECT count(*) FROM alerting.rule WHERE instance_id=@id;"); count.Parameters.AddWithValue("id", target); Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    private static AlertRuleDeploymentPreviewRequest TemplatePreviewRequest(Guid template, Guid deployment, Guid target) =>
        new(Guid.NewGuid(), deployment, template, 1, new AlertRuleTemplateGroup("environment", "template-test"), "replace_named", "Native coherent preview")
        { ActorSid = TemplateActor, CorrelationId = Guid.NewGuid(), AdministratorScope = TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]), CurrentApproval = new("metric.threshold", AlertCatalog.Digest, "engine.core", 1, "count") };

    private static async Task WaitForTemplateLockWaitersAsync(RepositoryTestDatabase database, int minimum)
    {
        for (int i = 0; i < 100; i++)
        {
            await using var query = database.DataSource.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%alerting.rule_deployment_%';");
            if ((long)(await query.ExecuteScalarAsync())! >= minimum) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("The native deployment did not reach its expected lock fence.");
    }

    private static object TemplateValues(double threshold = 1, bool enabled = true) => new { name = "group.connections", kind = "metric_threshold", metricId = "engine.user_connections", comparison = "greater_than_or_equal", threshold, hysteresis = 0, confirmationCount = 1, confirmationSeconds = 0, evaluationSeconds = 15, clearConfirmationCount = 2, enabled };
    private static object TemplateWrite(Guid template, Guid operation, int? expectedVersion = null, double threshold = 1, bool enabled = true) => new { operationId = operation, templateId = template, expectedVersion, title = "Group connections", sourceTemplateName = "metric.threshold", catalogDigest = AlertCatalog.Digest, values = TemplateValues(threshold, enabled), reason = "Synthetic shared template" };
    private static object TemplatePreview(Guid template, Guid deployment, Guid operation, int version = 1) => new { operationId = operation, deploymentId = deployment, templateId = template, templateVersion = version, group = new { kind = "environment", value = "template-test" }, mode = "replace_named", reason = "Synthetic complete group" };

    private static async Task AssertTemplateMemberAppliedAsync(RepositoryTestDatabase database, Guid deployment, JsonElement receipt)
    {
        await using var diagnostics = database.DataSource.CreateCommand("SELECT coalesce(jsonb_agg(jsonb_build_object('targetId',instance_id,'status',status,'code',result_code,'sqlstate',result_sqlstate)),'[]'::jsonb)::text FROM alerting.rule_deployment_member WHERE deployment_id=@id;");
        diagnostics.Parameters.AddWithValue("id", deployment);
        string states = (string)(await diagnostics.ExecuteScalarAsync())!;
        Assert.True(receipt.GetProperty("items").EnumerateArray().All(item => item.GetProperty("status").GetString() == "applied"), $"Native target results: {states}. Immutable receipt: {receipt.GetRawText()}");
    }

    private static async Task<JsonDocument> TemplateCallAsync(RepositoryTestDatabase database, string function, object command, IReadOnlyList<Guid> scope, bool approval = true, string actor = TemplateActor, bool all = false)
    {
        string connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=sqlobserver_server" }.ConnectionString;
        await using NpgsqlConnection connection = new(connectionString); await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(function is "rule_deployment_preview" or "rule_deployment_rollback_preview" ? IsolationLevel.RepeatableRead : IsolationLevel.ReadCommitted);
        await using var query = new NpgsqlCommand($"SELECT alerting.{function}(@command,@actor,@correlation,@all,@scope,true,@approval);", connection, transaction);
        query.Parameters.AddWithValue("all", all);
        query.Parameters.Add("command", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(command); query.Parameters.AddWithValue("actor", actor); query.Parameters.AddWithValue("correlation", Guid.NewGuid()); query.Parameters.AddWithValue("scope", scope.ToArray());
        query.Parameters.Add("approval", NpgsqlDbType.Jsonb).Value = approval ? JsonSerializer.Serialize(new { sourceTemplateName = "metric.threshold", catalogDigest = AlertCatalog.Digest, sourceCollector = "engine.core", sourceSchemaVersion = 1, unit = "count" }) : DBNull.Value;
        JsonDocument receipt = JsonDocument.Parse((string)(await query.ExecuteScalarAsync() ?? throw new InvalidDataException("Missing template receipt.")));
        await transaction.CommitAsync(); return receipt;
    }

    private static async Task SeedTemplateTargetAsync(RepositoryTestDatabase database, Guid id)
    {
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,instance_name,connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@id,@key,'Template target','template.example','SQL',interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            INSERT INTO control.observation_target_grouping(instance_id,environment_name,tags) VALUES(@id,'template-test',ARRAY['production']);
            INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
            VALUES(@id,'engine.core',1,1,1,true,interval '30 seconds',statement_timestamp(),'closed',0,statement_timestamp(),statement_timestamp())
            ON CONFLICT(instance_id,collector_id) DO UPDATE SET target_revision=1,enabled=true;
            """);
        seed.Parameters.AddWithValue("id", id); seed.Parameters.AddWithValue("key", "template-" + id.ToString("N")); await seed.ExecuteNonQueryAsync();
    }
}
