using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class OverviewLatestStatusBoundaryTests(PostgreSql18Fixture fixture)
{
    [Fact]
    public async Task OneStaleEnabledRuleMakesLatestAlertEvidenceStale()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(241,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Guid target = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
                (instance_id, instance_key, display_name, host_name, tcp_port,
                 connect_timeout, authentication_mode, transport_security_mode,
                 lifecycle_state, revision, created_at, updated_at, discovery_requested_at)
            VALUES (@target, @key, 'Alert freshness', 'test.invalid', 1433,
                    interval '5 seconds', 'windows_integrated_service_identity',
                    'mandatory_validated', 'active', 1,
                    statement_timestamp(), statement_timestamp(), statement_timestamp());
            INSERT INTO alerting.rule
                (rule_id,instance_id,name,kind,metric_id,comparison,threshold,
                 confirmation_count,confirmation_window,evaluation_interval)
            SELECT gen_random_uuid(),@target,'Freshness rule ' || n::text,
                   1,'engine.user_connections',2,10,1,
                   interval '0 seconds',interval '15 seconds'
            FROM generate_series(1,2) AS n;
            INSERT INTO alerting.rule_state
                (instance_id,rule_id,alert_id,state,consecutive_matches,
                 first_match_at,last_observed_at,fired_at,last_value,reason)
            SELECT @target,r.rule_id,gen_random_uuid(),3,1,
                   CASE WHEN r.name = 'Freshness rule 1'
                        THEN statement_timestamp() - interval '3 minutes'
                        ELSE statement_timestamp() END,
                   CASE WHEN r.name = 'Freshness rule 1'
                        THEN statement_timestamp() - interval '3 minutes'
                        ELSE statement_timestamp() END,
                   CASE WHEN r.name = 'Freshness rule 1'
                        THEN statement_timestamp() - interval '3 minutes'
                        ELSE statement_timestamp() END,
                   15,'Threshold exceeded'
            FROM alerting.rule AS r WHERE r.instance_id = @target;
            """))
        {
            seed.Parameters.AddWithValue("target", target);
            seed.Parameters.AddWithValue("key", $"overview-alert-freshness-{target:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        long token;
        await using (var lease = collector.CreateCommand("""
            SELECT acquired,fencing_token FROM control.acquire_worker_lease(
                'overview/latest-status',@owner,interval '5 minutes');
            """))
        {
            lease.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader row = await lease.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            token = row.GetInt64(1);
        }
        var identity = new WorkerLeaseIdentity(new WorkerLeaseKey("overview/latest-status"),
            new WorkerExecutionId(owner), new FencingToken(token));
        var materializer = new PostgreSqlOverviewLatestMaterializationPort(collector);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlOverviewLatestStatusPort(server);
        OverviewLatestStatus beforeUpgrade = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.Equal("current", beforeUpgrade.ActiveAlerts.State);
        await using (var storedBefore = database.DataSource.CreateCommand("""
            SELECT active_alert_state FROM reporting.overview_latest_status
            WHERE instance_id = @target;
            """))
        {
            storedBefore.Parameters.AddWithValue("target", target);
            Assert.Equal("current", await storedBefore.ExecuteScalarAsync());
        }

        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(242, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus status = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
                new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow,
                CancellationToken.None));
        Assert.Equal(2, status.ActiveAlerts.Value);
        Assert.Equal("stale", status.ActiveAlerts.State);
        Assert.DoesNotContain(status.Issues, issue => issue.Destination == "alerts");
        await using (var storedAfter = database.DataSource.CreateCommand("""
            SELECT active_alert_state FROM reporting.overview_latest_status
            WHERE instance_id = @target;
            """))
        {
            storedAfter.Parameters.AddWithValue("target", target);
            Assert.Equal("stale", await storedAfter.ExecuteScalarAsync());
        }

        await using (var catchUp = database.DataSource.CreateCommand("""
            UPDATE alerting.rule_state AS state
            SET last_observed_at = statement_timestamp()
            FROM alerting.rule AS rule
            WHERE rule.instance_id = @target AND rule.name = 'Freshness rule 1'
              AND state.instance_id = rule.instance_id
              AND state.rule_id = rule.rule_id;
            """))
        {
            catchUp.Parameters.AddWithValue("target", target);
            Assert.Equal(1, await catchUp.ExecuteNonQueryAsync());
        }
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus current = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.Equal("current", current.ActiveAlerts.State);
        Assert.Equal(2, current.ActiveAlerts.Value);

        await using (var unevaluated = database.DataSource.CreateCommand("""
            INSERT INTO alerting.rule
                (rule_id,instance_id,name,kind,metric_id,comparison,threshold,
                 confirmation_count,confirmation_window,evaluation_interval)
            VALUES (gen_random_uuid(),@target,'Unevaluated rule',1,
                    'engine.user_connections',2,10,1,
                    interval '0 seconds',interval '15 seconds');
            """))
        {
            unevaluated.Parameters.AddWithValue("target", target);
            await unevaluated.ExecuteNonQueryAsync();
        }
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus partial = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.Equal("partial", partial.ActiveAlerts.State);
        Assert.Equal(2, partial.ActiveAlerts.Value);
    }

    [Fact]
    public async Task FailedUnpublishedTargetDoesNotStarveNextDueTarget()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Guid first = Guid.Parse("00000000-0000-4000-8000-000000000001");
        Guid second = Guid.Parse("00000000-0000-4000-8000-000000000002");
        foreach (Guid id in new[] { first, second })
        {
            await using var seed = database.DataSource.CreateCommand("""
                INSERT INTO control.observation_target
                    (instance_id, instance_key, display_name, host_name, tcp_port,
                     connect_timeout, authentication_mode, transport_security_mode,
                     lifecycle_state, revision, created_at, updated_at, discovery_requested_at)
                VALUES (@id, @key, 'Due target', 'test.invalid', 1433,
                        interval '5 seconds', 'windows_integrated_service_identity',
                        'mandatory_validated', 'active', 1,
                        statement_timestamp(), statement_timestamp(), statement_timestamp());
                """);
            seed.Parameters.AddWithValue("id", id);
            seed.Parameters.AddWithValue("key", $"overview-due-{id:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        long token;
        await using (var lease = collector.CreateCommand("""
            SELECT acquired,fencing_token FROM control.acquire_worker_lease(
                'overview/latest-status',@owner,interval '5 minutes');
            """))
        {
            lease.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader row = await lease.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            token = row.GetInt64(1);
        }
        var identity = new WorkerLeaseIdentity(new WorkerLeaseKey("overview/latest-status"),
            new WorkerExecutionId(owner), new FencingToken(token));
        var materializer = new PostgreSqlOverviewLatestMaterializationPort(collector);
        Assert.Equal(first, Assert.Single(await materializer.ListDueAsync(identity, 1,
            CancellationToken.None)).TargetId.Value);
        Assert.Equal(second, Assert.Single(await materializer.ListDueAsync(identity, 1,
            CancellationToken.None)).TargetId.Value);
        await using var privilege = database.DataSource.CreateCommand("""
            SELECT has_table_privilege('sqlobserver_collector',
                       'reporting.overview_refresh_attempt','SELECT'),
                   has_function_privilege('sqlobserver_collector',
                       'reporting.list_due_overview_latest_status(uuid,bigint,integer)','EXECUTE');
            """);
        await using NpgsqlDataReader permissions = await privilege.ExecuteReaderAsync();
        Assert.True(await permissions.ReadAsync());
        Assert.False(permissions.GetBoolean(0));
        Assert.True(permissions.GetBoolean(1));
    }

    [Fact]
    public async Task CoreMaterializerPublishesMissingEvidenceAndDefersFreshTargets()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);
        Guid target = Guid.NewGuid();
        await using (var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
                (instance_id, instance_key, display_name, host_name, tcp_port,
                 connect_timeout, authentication_mode, transport_security_mode,
                 lifecycle_state, revision, created_at, updated_at, discovery_requested_at)
            VALUES (@id, @key, 'Materializer test', 'test.invalid', 1433,
                    interval '5 seconds', 'windows_integrated_service_identity',
                    'mandatory_validated', 'active', 1,
                    statement_timestamp(), statement_timestamp(), statement_timestamp());
            """))
        {
            seed.Parameters.AddWithValue("id", target);
            seed.Parameters.AddWithValue("key", $"overview-materializer-{target:N}");
            await seed.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        long token;
        await using (var lease = collector.CreateCommand("""
            SELECT acquired,fencing_token FROM control.acquire_worker_lease(
                'overview/latest-status',@owner,interval '5 minutes');
            """))
        {
            lease.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader row = await lease.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            token = row.GetInt64(1);
        }
        var materializer = new PostgreSqlOverviewLatestMaterializationPort(collector);
        var identity = new WorkerLeaseIdentity(new WorkerLeaseKey("overview/latest-status"),
            new WorkerExecutionId(owner), new FencingToken(token));
        Assert.Equal(target, Assert.Single(await materializer.ListDueAsync(identity, 1,
            CancellationToken.None)).TargetId.Value);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() =>
            RefreshAsync(collector, target, Guid.NewGuid(), owner, token))).SqlState);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        Assert.Empty(await materializer.ListDueAsync(identity, 1, CancellationToken.None));

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var port = new PostgreSqlOverviewLatestStatusPort(server);
        OverviewLatestStatus status = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(1, status.Generation);
        Assert.Equal("pending", status.CollectionState);
        Assert.Null(status.ActiveAlerts.Value);
        Assert.Null(status.BlockedSessions.Value);
        Assert.Equal("unavailable", status.Sources["availabilityGroups"].State);
        Assert.Contains("AG evidence unavailable", status.Gaps);

        Guid rule = Guid.NewGuid();
        await using (var alert = database.DataSource.CreateCommand("""
            INSERT INTO alerting.rule
                (rule_id,instance_id,name,kind,metric_id,comparison,threshold,
                 confirmation_count,confirmation_window,evaluation_interval)
            VALUES (@rule,@target,'Connections high',1,'engine.user_connections',2,10,
                    1,interval '0 seconds',interval '15 seconds');
            INSERT INTO alerting.rule_state
                (instance_id,rule_id,alert_id,state,consecutive_matches,
                 first_match_at,last_observed_at,fired_at,last_value,reason)
            VALUES (@target,@rule,@alert,3,1,clock_timestamp(),clock_timestamp(),
                    clock_timestamp(),15,'Connection threshold exceeded');
            """))
        {
            alert.Parameters.AddWithValue("rule", rule);
            alert.Parameters.AddWithValue("target", target);
            alert.Parameters.AddWithValue("alert", Guid.NewGuid());
            await alert.ExecuteNonQueryAsync();
        }
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus active = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(2, active.Generation);
        Assert.Equal(1, active.ActiveAlerts.Value);
        Assert.Equal("current", active.ActiveAlerts.State);
        Assert.Contains(active.Issues, issue => issue.Title == "Connections high" &&
            issue.TargetId == target);

        Guid run = Guid.NewGuid();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        await using (var blocking = database.DataSource.CreateCommand("""
            SELECT control.ensure_blocking_monthly_partition(@at::date);
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,
                 target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,
                 request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'blocking.current',1,1,1,1,@work,@owner,1,
                    decode(repeat('aa',32),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                 source_row_count,output_item_count,inserted_item_count,duplicate_item_count,
                 rejected_item_count,response_bytes,output_bytes,persisted_bytes,truncated,
                 loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,
                 completion_digest,completed_at)
            VALUES (@run,'succeeded','completed',1,0,10,2,2,2,0,0,128,128,128,
                    false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),@at);
            INSERT INTO events.blocking_edge
                (observed_at,edge_id,collection_run_id,instance_id,target_revision,
                 blocked_session_id,blocker_kind,blocker_session_id,wait_type,
                 waiting_task_count,wait_duration_ms,root_blocker_session_id,
                 chain_depth,chain_state,collected_at)
            VALUES (@at,@edge1,@run,@target,1,82,'session',99,'LCK_M_S',1,10,99,1,'resolved',@at),
                   (@at,@edge2,@run,@target,1,82,'session',98,'LCK_M_X',1,20,98,1,'resolved',@at);
            """))
        {
            blocking.Parameters.AddWithValue("at", observedAt);
            blocking.Parameters.AddWithValue("run", run);
            blocking.Parameters.AddWithValue("target", target);
            blocking.Parameters.AddWithValue("work", $"collector/run/blocking.current/{target:N}");
            blocking.Parameters.AddWithValue("owner", Guid.NewGuid());
            blocking.Parameters.AddWithValue("edge1", Guid.NewGuid());
            blocking.Parameters.AddWithValue("edge2", Guid.NewGuid());
            await blocking.ExecuteNonQueryAsync();
        }
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus blocked = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(3, blocked.Generation);
        Assert.Equal(1, blocked.BlockedSessions.Value);
        Assert.Equal("current", blocked.BlockedSessions.State);
        Assert.Contains(blocked.Issues, issue => issue.Title == "Sessions blocked" &&
            issue.TargetId == target);

        var overview = new OverviewQueryService(
            new ObservationTargetStatusQueryService(
                new PostgreSqlObservationTargetPort(server),
                new PostgreSqlCapabilityProfilePort(server)),
            new HealthProjectionQueryService(new PostgreSqlHealthProjectionPort(server)),
            new AlertQueryService(new PostgreSqlAlertRepositoryPort(server)),
            new ActivityProjectionQueryService(new PostgreSqlActivityProjectionPort(server)),
            new DeadlockProjectionQueryService(new PostgreSqlDeadlockProjectionPort(server)),
            new OperationalHealthQueryService(new PostgreSqlOperationalHealthProjectionPort(server)),
            new MetricSeriesQueryService(new PostgreSqlAnalyticsRepositoryPort(
                server, new IdentityFingerprintKey(new byte[32]))),
            new PostgreSqlOverviewHistoryPort(server), port,
            new PostgreSqlObservationTargetGroupingPort(server));
        var authorization = new AuthorizationContext(
            new ActorSecurityIdentifier("S-1-5-21-100"), AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(target)]));
        DateTimeOffset toUtc = DateTimeOffset.UtcNow;
        OverviewTargetEvidence direct = Assert.Single((await overview.ReadAsync(
            new(authorization, target, toUtc.AddHours(-1), toUtc),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence staged = Assert.Single((await overview.ReadAsync(
            new(authorization, target, toUtc.AddHours(-1), toUtc, true),
            CancellationToken.None)).Evidence);
        Assert.Equal(1, direct.ActiveAlerts.Value);
        Assert.Equal(1, direct.BlockedSessions.Value);
        Assert.Contains(direct.Issues, issue => issue.Title == "Connections high");
        Assert.Contains(direct.Issues, issue => issue.Title == "Sessions blocked");
        Assert.Equal(direct.ActiveAlerts.Value, staged.ActiveAlerts.Value);
        Assert.Equal(direct.ActiveAlerts.State, staged.ActiveAlerts.State);
        Assert.Equal(direct.BlockedSessions.Value, staged.BlockedSessions.Value);
        Assert.Equal(direct.BlockedSessions.State, staged.BlockedSessions.State);
        Assert.Equal(direct.Issues.Where(issue => issue.Destination is "alerts" or "activity")
                .Select(issue => (issue.Title, issue.Detail)),
            staged.Issues.Where(issue => issue.Destination is "alerts" or "activity")
                .Select(issue => (issue.Title, issue.Detail)));

        Guid inventoryRun = Guid.NewGuid();
        DateTimeOffset inventoryAt = DateTimeOffset.UtcNow;
        await using (var inventory = database.DataSource.CreateCommand("""
            INSERT INTO control.collector_schedule
                (instance_id,collector_id,collector_version,target_revision,
                 schedule_revision,enabled,collection_interval,next_due_at,
                 circuit_state,consecutive_failure_count,last_completed_at,
                 last_succeeded_at,created_at,updated_at)
            VALUES (@target,'database.inventory',1,1,1,true,interval '1 hour',
                    @at,'closed',0,@at,@at,@at,@at)
            ON CONFLICT (instance_id,collector_id) DO UPDATE SET
                target_revision=1,schedule_revision=1,enabled=true,
                collection_interval=interval '1 hour',next_due_at=EXCLUDED.next_due_at,
                circuit_state='closed',consecutive_failure_count=0,
                last_completed_at=EXCLUDED.last_completed_at,
                last_succeeded_at=EXCLUDED.last_succeeded_at;
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,
                 target_revision,schedule_revision,work_key,owner_execution_id,
                 fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'database.inventory',1,1,1,1,@work,@owner,1,
                    decode(repeat('cc',32),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                 source_row_count,output_item_count,inserted_item_count,
                 duplicate_item_count,rejected_item_count,response_bytes,
                 output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                 loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                 completed_at)
            VALUES (@run,'succeeded','completed',1,0,10,7,7,7,0,0,128,128,128,
                    false,false,'none',true,0,0,
                    decode(repeat('dd',32),'hex'),@at);
            INSERT INTO telemetry.database_inventory_snapshot
                (observed_at,snapshot_id,collection_run_id,instance_id,
                 target_revision,database_id,database_name,state_code,
                 is_read_only,recovery_model,user_access,compatibility_level,collected_at)
            SELECT @at,gen_random_uuid(),@run,@target,1,n,
                   'Database ' || n::text,
                   CASE WHEN n=1 THEN 'online'
                        WHEN n=2 THEN 'recovery_pending'
                        WHEN n=3 THEN 'emergency'
                        ELSE 'suspect' END,
                   false,'full','multi_user',160,@at
            FROM generate_series(1,7) AS n;
            INSERT INTO alerting.rule
                (rule_id,instance_id,name,kind,metric_id,comparison,threshold,
                 confirmation_count,confirmation_window,evaluation_interval)
            SELECT gen_random_uuid(),@target,'Extra alert ' || n::text,
                   1,'engine.user_connections',2,10,1,
                   interval '0 seconds',interval '15 seconds'
            FROM generate_series(1,4000) AS n;
            INSERT INTO alerting.rule_state
                (instance_id,rule_id,alert_id,state,consecutive_matches,
                 first_match_at,last_observed_at,fired_at,last_value,reason)
            SELECT @target,r.rule_id,gen_random_uuid(),3,1,@at,@at,@at,15,
                   'Extra alert threshold exceeded'
            FROM alerting.rule AS r
            WHERE r.instance_id=@target AND r.name LIKE 'Extra alert %';
            """))
        {
            inventory.Parameters.AddWithValue("target", target);
            inventory.Parameters.AddWithValue("at", inventoryAt);
            inventory.Parameters.AddWithValue("run", inventoryRun);
            inventory.Parameters.AddWithValue("work", $"collector/run/database.inventory/{target:N}");
            inventory.Parameters.AddWithValue("owner", Guid.NewGuid());
            await inventory.ExecuteNonQueryAsync();
        }
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus databases = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(4001, databases.ActiveAlerts.Value);
        Assert.Equal("current", databases.ActiveAlerts.State);
        Assert.Equal("partial", databases.Sources["databaseStates"].State);
        Assert.Equal(5, databases.Issues.Count(issue => issue.Title == "Database state exception"));
        Assert.Equal(10, databases.Issues.Count);
        Assert.Contains(databases.Issues, issue => issue.Detail == "Database 2: RecoveryPending");
        Assert.Contains("Database state issue list partial", databases.Gaps);
        Assert.Contains("Overview issue list partial", databases.Gaps);
        DateTimeOffset largeAlertWindowEnd = DateTimeOffset.UtcNow;
        OverviewTargetEvidence directLargeAlerts = Assert.Single((await overview.ReadAsync(
            new(authorization, target, largeAlertWindowEnd.AddHours(-1), largeAlertWindowEnd),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence latestLargeAlerts = Assert.Single((await overview.ReadAsync(
            new(authorization, target, largeAlertWindowEnd.AddHours(-1), largeAlertWindowEnd, true),
            CancellationToken.None)).Evidence);
        Assert.Equal(4001, latestLargeAlerts.ActiveAlerts.Value);
        Assert.Equal("current", latestLargeAlerts.ActiveAlerts.State);
        Assert.True(directLargeAlerts.ActiveAlerts.Value is null or <= 4000);
        Assert.NotEqual("current", directLargeAlerts.ActiveAlerts.State);
        Assert.Contains(directLargeAlerts.Gaps, gap =>
            gap is "Alert count is a lower bound" or "Alerts unavailable");

        await using (var disable = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET enabled=false
            WHERE instance_id=@target AND collector_id='database.inventory';
            """))
        {
            disable.Parameters.AddWithValue("target", target);
            await disable.ExecuteNonQueryAsync();
        }
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus disabled = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("disabled", disabled.Sources["databaseStates"].State);
        Assert.DoesNotContain(disabled.Issues, issue => issue.Title == "Database state exception");
        Assert.Contains(disabled.Issues, issue => issue.Title == "Sessions blocked");

        DateTimeOffset waitAt = DateTimeOffset.UtcNow;
        await SeedWaitRunAsync(database.DataSource, target, Guid.NewGuid(),
            waitAt.AddSeconds(-20), 10, 100, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus noBaseline = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("unavailable", noBaseline.Sources["waits"].State);
        Assert.DoesNotContain(noBaseline.Resources, resource => resource.Label.StartsWith("Wait · ", StringComparison.Ordinal));
        Assert.Contains("Comparable wait evidence unavailable", noBaseline.Gaps);

        await SeedWaitRunAsync(database.DataSource, target, Guid.NewGuid(),
            waitAt.AddSeconds(-10), 20, 1300, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus comparable = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("current", comparable.Sources["waits"].State);
        OverviewResource lockWait = Assert.Single(comparable.Resources,
            resource => resource.Label == "Wait · Lock");
        Assert.Equal(1.2, lockWait.Value);
        DateTimeOffset waitWindowEnd = DateTimeOffset.UtcNow;
        OverviewTargetEvidence directWait = Assert.Single((await overview.ReadAsync(
            new(authorization, target, waitWindowEnd.AddHours(-1), waitWindowEnd),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence latestWait = Assert.Single((await overview.ReadAsync(
            new(authorization, target, waitWindowEnd.AddHours(-1), waitWindowEnd, true),
            CancellationToken.None)).Evidence);
        OverviewResource directLock = Assert.Single(directWait.Resources,
            resource => resource.Label == "Wait · LCK_M_S");
        OverviewResource cachedLock = Assert.Single(latestWait.Resources,
            resource => resource.Label == "Wait · Lock");
        Assert.Equal(1.2, directLock.Value);
        Assert.Equal(directLock.Value, cachedLock.Value);

        await SeedWaitRunAsync(database.DataSource, target, Guid.NewGuid(),
            waitAt, 30, 1800, true);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus mixed = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("partial", mixed.Sources["waits"].State);
        Assert.Equal(0.5, Assert.Single(mixed.Resources,
            resource => resource.Label == "Wait · Lock").Value);
        Assert.DoesNotContain(mixed.Resources, resource => resource.Label == "Wait · Log");
        Assert.Contains("Comparable wait evidence partial", mixed.Gaps);

        await SeedTempDbRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 1, 1000, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus tempComplete = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("current", tempComplete.Sources["tempDb"].State);
        Assert.Equal(50, Assert.Single(tempComplete.Resources,
            resource => resource.Label == "TempDB used").Value);
        DateTimeOffset validTempWindowEnd = DateTimeOffset.UtcNow;
        OverviewTargetEvidence directValidTemp = Assert.Single((await overview.ReadAsync(
            new(authorization, target, validTempWindowEnd.AddHours(-1), validTempWindowEnd),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence latestValidTemp = Assert.Single((await overview.ReadAsync(
            new(authorization, target, validTempWindowEnd.AddHours(-1), validTempWindowEnd, true),
            CancellationToken.None)).Evidence);
        OverviewResource directValidUsage = Assert.Single(directValidTemp.Resources,
            resource => resource.Label == "TempDB used");
        OverviewResource cachedValidUsage = Assert.Single(latestValidTemp.Resources,
            resource => resource.Label == "TempDB used");
        Assert.Equal(50, directValidUsage.Value);
        Assert.Equal(directValidUsage.Value, cachedValidUsage.Value);

        await SeedTempDbRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 2, 1500, true);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus tempPartial = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("partial", tempPartial.Sources["tempDb"].State);
        Assert.Equal(75, Assert.Single(tempPartial.Resources,
            resource => resource.Label == "TempDB used").Value);
        Assert.Contains("TempDB evidence partial", tempPartial.Gaps);

        await SeedTempDbRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 1, 2500, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus tempInvalid = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("degraded", tempInvalid.Sources["tempDb"].State);
        Assert.Null(Assert.Single(tempInvalid.Resources,
            resource => resource.Label == "TempDB used").Value);
        Assert.Contains("TempDB evidence degraded", tempInvalid.Gaps);
        DateTimeOffset tempWindowEnd = DateTimeOffset.UtcNow;
        OverviewTargetEvidence directInvalidTemp = Assert.Single((await overview.ReadAsync(
            new(authorization, target, tempWindowEnd.AddHours(-1), tempWindowEnd),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence latestInvalidTemp = Assert.Single((await overview.ReadAsync(
            new(authorization, target, tempWindowEnd.AddHours(-1), tempWindowEnd, true),
            CancellationToken.None)).Evidence);
        OverviewResource cachedTemp = Assert.Single(latestInvalidTemp.Resources,
            resource => resource.Label == "TempDB used");
        Assert.Null(cachedTemp.Value);
        Assert.Equal("degraded", cachedTemp.State);
        Assert.DoesNotContain(directInvalidTemp.Resources,
            resource => resource.Label == "TempDB used");
        Assert.Contains("TempDB unavailable", directInvalidTemp.Gaps);

        await SeedBackupRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 0, false, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus noBackups = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("unavailable", noBackups.Sources["backups"].State);
        Assert.DoesNotContain(noBackups.Resources, resource => resource.Label == "Backup evidence");

        await SeedBackupRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 6, true, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus damagedBackups = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("partial", damagedBackups.Sources["backups"].State);
        Assert.Equal(6, Assert.Single(damagedBackups.Resources,
            resource => resource.Label == "Backup evidence").Value);
        Assert.Equal(5, damagedBackups.Issues.Count(issue => issue.Title == "Damaged backup"));
        Assert.Equal(10, damagedBackups.Issues.Count);
        Assert.Contains("Damaged backup issue list partial", damagedBackups.Gaps);
        Assert.Contains("Overview issue list partial", damagedBackups.Gaps);

        await using (var ageBackup = database.DataSource.CreateCommand("""
            UPDATE reporting.overview_latest_status
            SET source_states = jsonb_set(source_states,
                '{backups,observedAtUtc}',
                to_jsonb(clock_timestamp() - interval '3 minutes'))
            WHERE instance_id = @target;
            """))
        {
            ageBackup.Parameters.AddWithValue("target", target);
            await ageBackup.ExecuteNonQueryAsync();
        }
        OverviewLatestStatus staleBackup = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("stale", staleBackup.Sources["backups"].State);
        Assert.Equal("stale", Assert.Single(staleBackup.Resources,
            resource => resource.Label == "Backup evidence").State);
        Assert.DoesNotContain(staleBackup.Issues,
            issue => issue.Title == "Damaged backup");

        await SeedBackupRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 1, false, true);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus unknownBackupTime = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("current", unknownBackupTime.Sources["backups"].State);
        Assert.Equal(1, Assert.Single(unknownBackupTime.Resources,
            resource => resource.Label == "Backup evidence").Value);
        Assert.DoesNotContain(unknownBackupTime.Issues, issue => issue.Title == "Damaged backup");
        Assert.Contains("Backup age unavailable: source timezone unknown", unknownBackupTime.Gaps);
        DateTimeOffset backupWindowEnd = DateTimeOffset.UtcNow;
        OverviewTargetEvidence directBackup = Assert.Single((await overview.ReadAsync(
            new(authorization, target, backupWindowEnd.AddHours(-1), backupWindowEnd),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence latestBackup = Assert.Single((await overview.ReadAsync(
            new(authorization, target, backupWindowEnd.AddHours(-1), backupWindowEnd, true),
            CancellationToken.None)).Evidence);
        OverviewResource directBackupCount = Assert.Single(directBackup.Resources,
            resource => resource.Label == "Backup evidence");
        OverviewResource cachedBackupCount = Assert.Single(latestBackup.Resources,
            resource => resource.Label == "Backup evidence");
        Assert.Equal(1, directBackupCount.Value);
        Assert.Equal(directBackupCount.Value, cachedBackupCount.Value);
        Assert.Contains("Backup age unavailable: source timezone unknown", directBackup.Gaps);
        Assert.Contains("Backup age unavailable: source timezone unknown", latestBackup.Gaps);

        await SeedAgRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 3, 2, 1, false);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus localAg = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("degraded", localAg.Sources["availabilityGroups"].State);
        Assert.Equal(1, Assert.Single(localAg.Resources,
            resource => resource.Label == "Availability replicas").Value);
        Assert.DoesNotContain(localAg.Issues,
            issue => issue.Title == "Availability replica disconnected");
        Assert.Contains("AG visibility is local-only", localAg.Gaps);

        await SeedAgRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 1, 1, 6, true);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus disconnectedAg = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("partial", disconnectedAg.Sources["availabilityGroups"].State);
        Assert.Equal(6, Assert.Single(disconnectedAg.Resources,
            resource => resource.Label == "Availability replicas").Value);
        Assert.Equal(5, disconnectedAg.Issues.Count(
            issue => issue.Title == "Availability replica disconnected"));
        Assert.Equal(10, disconnectedAg.Issues.Count);
        Assert.Contains("Disconnected AG issue list partial", disconnectedAg.Gaps);
        Assert.Contains("Overview issue list partial", disconnectedAg.Gaps);

        await using (var ageSources = database.DataSource.CreateCommand("""
            UPDATE reporting.overview_latest_status
            SET source_states = (
                SELECT jsonb_object_agg(key,
                    CASE WHEN key IN ('waits','tempDb','backups','availabilityGroups')
                         THEN jsonb_set(value, '{observedAtUtc}',
                             to_jsonb(clock_timestamp() - interval '3 minutes'))
                         ELSE value END)
                FROM jsonb_each(source_states)
            )
            WHERE instance_id = @target;
            """))
        {
            ageSources.Parameters.AddWithValue("target", target);
            await ageSources.ExecuteNonQueryAsync();
        }
        OverviewLatestStatus agedSources = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        foreach (string source in new[] { "waits", "tempDb", "backups", "availabilityGroups" })
            Assert.Equal("stale", agedSources.Sources[source].State);
        Assert.All(agedSources.Resources.Where(resource =>
            resource.Label.StartsWith("Wait · ", StringComparison.Ordinal) ||
            resource.Label is "TempDB used" or "Backup evidence" or "Availability replicas"),
            resource => Assert.Equal("stale", resource.State));
        Assert.DoesNotContain(agedSources.Issues,
            issue => issue.Title == "Availability replica disconnected");
        Assert.Contains("AG evidence stale", agedSources.Gaps);

        await SeedAgRunAsync(database.DataSource, target, Guid.NewGuid(),
            DateTimeOffset.UtcNow, 1, 1, 0, true);
        Assert.True(await materializer.RefreshCoreAsync(
            new OverviewRefreshTarget(new MonitoredInstanceId(target), 1), identity,
            CancellationToken.None));
        OverviewLatestStatus noAg = Assert.IsType<OverviewLatestStatus>(await port.ReadAsync(
            new MonitoredInstanceId(target), 1, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("current", noAg.Sources["availabilityGroups"].State);
        Assert.Equal(0, Assert.Single(noAg.Resources,
            resource => resource.Label == "Availability replicas").Value);
        Assert.DoesNotContain(noAg.Issues,
            issue => issue.Title == "Availability replica disconnected");
        DateTimeOffset agWindowEnd = DateTimeOffset.UtcNow;
        OverviewTargetEvidence directAg = Assert.Single((await overview.ReadAsync(
            new(authorization, target, agWindowEnd.AddHours(-1), agWindowEnd),
            CancellationToken.None)).Evidence);
        OverviewTargetEvidence latestAg = Assert.Single((await overview.ReadAsync(
            new(authorization, target, agWindowEnd.AddHours(-1), agWindowEnd, true),
            CancellationToken.None)).Evidence);
        OverviewResource directReplicaCount = Assert.Single(directAg.Resources,
            resource => resource.Label == "Availability replicas");
        OverviewResource cachedReplicaCount = Assert.Single(latestAg.Resources,
            resource => resource.Label == "Availability replicas");
        Assert.Equal(0, directReplicaCount.Value);
        Assert.Equal(directReplicaCount.Value, cachedReplicaCount.Value);
    }

    [Fact]
    public async Task PublishesOnlyNewerFencedTargetStatusAndReadsOnlyItsCurrentRevision()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrations = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults,
                new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrations.HasFailures);

        Guid target = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        {
            foreach (Guid id in new[] { target, other })
            {
                await using var seed = new NpgsqlCommand("""
                    INSERT INTO control.observation_target
                        (instance_id, instance_key, display_name, host_name, tcp_port,
                         connect_timeout, authentication_mode, transport_security_mode,
                         lifecycle_state, revision, created_at, updated_at, discovery_requested_at)
                    VALUES (@id, @key, 'Overview boundary test', 'test.invalid', 1433,
                            interval '5 seconds', 'windows_integrated_service_identity',
                            'mandatory_validated', 'active', 1,
                            statement_timestamp(), statement_timestamp(), statement_timestamp());
                    """, admin);
                seed.Parameters.AddWithValue("id", id);
                seed.Parameters.AddWithValue("key", $"overview-latest-{id:N}");
                await seed.ExecuteNonQueryAsync();
            }
        }

        await using var privilege = database.DataSource.CreateCommand("""
            SELECT has_table_privilege('sqlobserver_server',
                       'reporting.overview_latest_status','SELECT'),
                   has_function_privilege('sqlobserver_server',
                       'reporting.get_overview_latest_status(uuid,bigint)','EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                       'reporting.publish_overview_latest_status(uuid,bigint,bigint,text,timestamptz,integer,text,integer,text,jsonb,jsonb,jsonb,jsonb,uuid,bigint)','EXECUTE'),
                   has_function_privilege('sqlobserver_collector',
                       'reporting.publish_overview_latest_status(uuid,bigint,bigint,text,timestamptz,integer,text,integer,text,jsonb,jsonb,jsonb,jsonb,uuid,bigint)','EXECUTE');
            """);
        await using (NpgsqlDataReader permissions = await privilege.ExecuteReaderAsync())
        {
            Assert.True(await permissions.ReadAsync());
            Assert.False(permissions.GetBoolean(0));
            Assert.True(permissions.GetBoolean(1));
            Assert.False(permissions.GetBoolean(2));
            Assert.True(permissions.GetBoolean(3));
        }

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        Guid owner = Guid.NewGuid();
        long token;
        await using (var lease = collector.CreateCommand("""
            SELECT acquired,fencing_token
            FROM control.acquire_worker_lease('overview/latest-status',@owner,interval '5 minutes');
            """))
        {
            lease.Parameters.AddWithValue("owner", owner);
            await using NpgsqlDataReader row = await lease.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0));
            token = row.GetInt64(1);
        }
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => PublishAsync(collector, target, target, Guid.NewGuid(), token, 1))).SqlState);
        Assert.True(await PublishAsync(collector, target, target, owner, token, 1));
        Assert.False(await PublishAsync(collector, target, target, owner, token, 1));
        Assert.True(await PublishAsync(collector, target, target, owner, token, 2));
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(
            () => PublishAsync(collector, target, other, owner, token, 3))).SqlState);

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Equal(2, await ReadGenerationAsync(server, target, target, 1));
        Assert.Null(await ReadGenerationAsync(server, target, target, 2));
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
            () => ReadGenerationAsync(server, target, other, 1))).SqlState);

        var latest = new PostgreSqlOverviewLatestStatusPort(server);
        OverviewLatestStatus status = Assert.IsType<OverviewLatestStatus>(
            await latest.ReadAsync(new MonitoredInstanceId(target), 1,
                DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(target, status.TargetId);
        Assert.Equal(2, status.Generation);
        Assert.Equal(3, status.ActiveAlerts.Value);
        Assert.Equal(2, status.BlockedSessions.Value);
        Assert.Equal("current", status.Sources["alerts"].State);
        Assert.Equal(2, status.Issues.Count);
        Assert.Null(await latest.ReadAsync(new MonitoredInstanceId(target), 2,
            DateTimeOffset.UtcNow, CancellationToken.None));

        OverviewLatestStatus expired = Assert.IsType<OverviewLatestStatus>(
            await latest.ReadAsync(new MonitoredInstanceId(target), 1,
                status.PublishedAtUtc.AddMinutes(3), CancellationToken.None));
        Assert.Equal(3, expired.ActiveAlerts.Value);
        Assert.Equal(2, expired.BlockedSessions.Value);
        Assert.Equal("stale", expired.CollectionState);
        Assert.Equal("stale", expired.ActiveAlerts.State);
        Assert.Equal("stale", expired.BlockedSessions.State);
        Assert.Equal("stale", expired.Sources["health"].State);
        Assert.Empty(expired.Issues);
        Assert.Contains("Latest status projection is stale", expired.Gaps);

        await using (var ageSource = database.DataSource.CreateCommand("""
            UPDATE reporting.overview_latest_status
            SET source_states = jsonb_set(source_states,
                '{alerts,observedAtUtc}',
                to_jsonb(clock_timestamp() - interval '3 minutes'))
            WHERE instance_id = @target;
            """))
        {
            ageSource.Parameters.AddWithValue("target", target);
            await ageSource.ExecuteNonQueryAsync();
        }
        OverviewLatestStatus staleAlert = Assert.IsType<OverviewLatestStatus>(
            await latest.ReadAsync(new MonitoredInstanceId(target), 1,
                DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("current", staleAlert.CollectionState);
        Assert.Equal("stale", staleAlert.ActiveAlerts.State);
        Assert.Equal("current", staleAlert.BlockedSessions.State);
        Assert.Single(staleAlert.Issues);
        Assert.Equal("activity", staleAlert.Issues[0].Destination);
        Assert.Contains("Active alert evidence stale", staleAlert.Gaps);

        await using (var storedStale = database.DataSource.CreateCommand("""
            UPDATE reporting.overview_latest_status
            SET active_alert_state = 'stale',
                source_states = jsonb_set(jsonb_set(source_states,
                    '{alerts,state}', '"stale"'::jsonb),
                    '{alerts,observedAtUtc}', to_jsonb(clock_timestamp()))
            WHERE instance_id = @target;
            """))
        {
            storedStale.Parameters.AddWithValue("target", target);
            await storedStale.ExecuteNonQueryAsync();
        }
        OverviewLatestStatus stored = Assert.IsType<OverviewLatestStatus>(
            await latest.ReadAsync(new MonitoredInstanceId(target), 1,
                DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("stale", stored.ActiveAlerts.State);
        Assert.Single(stored.Issues);
        Assert.Equal("activity", stored.Issues[0].Destination);

        await using (var corrupt = database.DataSource.CreateCommand("""
            UPDATE reporting.overview_latest_status
            SET issues = jsonb_build_array(jsonb_build_object(
                'targetId', @other, 'server', 'Foreign server', 'title', 'Foreign issue',
                'detail', 'Outside target', 'destination', 'health',
                'priority', 1, 'observedAtUtc', NULL))
            WHERE instance_id = @target;
            """))
        {
            corrupt.Parameters.AddWithValue("target", target);
            corrupt.Parameters.AddWithValue("other", other);
            await corrupt.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            latest.ReadAsync(new MonitoredInstanceId(target), 1,
                DateTimeOffset.UtcNow, CancellationToken.None));
    }

    private static async Task<bool> PublishAsync(
        NpgsqlDataSource source, Guid target, Guid scope, Guid owner, long token, long generation)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand("""
            SELECT reporting.publish_overview_latest_status(
                @target,1,@generation,'current',NULL::timestamptz,
                3,'current',2,'current',
                jsonb_build_object('schemaVersion',1,
                    'health',jsonb_build_object('state','current','observedAtUtc',clock_timestamp()),
                    'alerts',jsonb_build_object('state','current','observedAtUtc',clock_timestamp()),
                    'blocking',jsonb_build_object('state','current','observedAtUtc',clock_timestamp())),
                jsonb_build_array(
                    jsonb_build_object('targetId',@target,'server','Overview boundary test',
                        'title','Active alert','detail','Alert detail',
                        'destination','alerts','priority',1,'observedAtUtc',clock_timestamp()),
                    jsonb_build_object('targetId',@target,'server','Overview boundary test',
                        'title','Sessions blocked','detail','Blocking detail',
                        'destination','activity','priority',1,'observedAtUtc',clock_timestamp())),
                '[]'::jsonb,'[]'::jsonb,@owner,@token);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        bool published = (bool)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return published;
    }

    private static async Task SeedWaitRunAsync(NpgsqlDataSource source, Guid target,
        Guid run, DateTimeOffset at, long tasks, long waitMilliseconds, bool includeLog)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,
                 target_revision,schedule_revision,work_key,owner_execution_id,
                 fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'waits.server',1,1,1,1,@work,@owner,1,
                    decode(repeat('ee',32),'hex'),@at,@at);
            INSERT INTO telemetry.server_wait_snapshot
                (observed_at,collection_run_id,instance_id,target_revision,wait_type,
                 waiting_tasks_count,wait_time_ms,maximum_wait_time_ms,
                 signal_wait_time_ms,collected_at)
            VALUES (@at,@run,@target,1,'LCK_M_S',@tasks,@wait,@wait,0,@at),
                   (@at,@run,@target,1,'SLEEP_TASK',1,100,100,0,@at);
            INSERT INTO telemetry.server_wait_snapshot
                (observed_at,collection_run_id,instance_id,target_revision,wait_type,
                 waiting_tasks_count,wait_time_ms,maximum_wait_time_ms,
                 signal_wait_time_ms,collected_at)
            SELECT @at,@run,@target,1,'WRITELOG',1,500,500,0,@at
            WHERE @includeLog;
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                 source_row_count,output_item_count,inserted_item_count,
                 duplicate_item_count,rejected_item_count,response_bytes,
                 output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                 loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                 completed_at)
            VALUES (@run,'succeeded','completed',1,0,10,
                    CASE WHEN @includeLog THEN 3 ELSE 2 END,
                    CASE WHEN @includeLog THEN 3 ELSE 2 END,
                    CASE WHEN @includeLog THEN 3 ELSE 2 END,
                    0,0,128,128,128,false,false,'none',true,0,0,
                    decode(repeat('ff',32),'hex'),@at);
            """);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("work", $"collector/run/waits.server/{target:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("tasks", tasks);
        command.Parameters.AddWithValue("wait", waitMilliseconds);
        command.Parameters.AddWithValue("includeLog", includeLog);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedTempDbRunAsync(NpgsqlDataSource source, Guid target,
        Guid run, DateTimeOffset at, short state, long usedBytes, bool truncated)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,
                 target_revision,schedule_revision,work_key,owner_execution_id,
                 fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'tempdb.health',1,1,1,1,@work,@owner,1,
                    decode(repeat('a1',32),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                 source_row_count,output_item_count,inserted_item_count,
                 duplicate_item_count,rejected_item_count,response_bytes,
                 output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                 loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                 completed_at)
            VALUES (@run,CASE WHEN @state=2 THEN 'partial' ELSE 'succeeded' END,
                    CASE WHEN @state=2 THEN 'source_row_limit' ELSE 'completed' END,
                    1,0,10,1,1,1,0,0,128,128,128,
                    @truncated,@truncated,
                    CASE WHEN @truncated THEN 'source_row_limit' ELSE 'none' END,
                    true,CASE WHEN @truncated THEN 1 ELSE 0 END,0,
                    decode(repeat('a2',32),'hex'),@at);
            INSERT INTO telemetry.m9_commit_replay
                (run_id,instance_id,target_revision,collector_id,collector_version,
                 output_schema_version,request_digest,payload_digest,
                 m9_observation_state)
            VALUES (@run,@target,1,'tempdb.health',1,1,
                    decode(repeat('a1',32),'hex'),decode(repeat('a2',32),'hex'),@state);
            INSERT INTO telemetry.tempdb_snapshot
                (instance_id,target_revision,run_id,observed_at,state,
                 total_bytes,used_bytes,log_total_bytes,log_used_bytes,truncated)
            VALUES (@target,1,@run,@at,@state,2000,@used,1000,500,@truncated);
            """);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("work", $"collector/run/tempdb.health/{target:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("used", usedBytes);
        command.Parameters.AddWithValue("truncated", truncated);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedBackupRunAsync(NpgsqlDataSource source, Guid target,
        Guid run, DateTimeOffset at, int rows, bool damaged, bool unknownTime)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,
                 target_revision,schedule_revision,work_key,owner_execution_id,
                 fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'backups.status',1,1,1,1,@work,@owner,1,
                    decode(repeat('b1',32),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                 source_row_count,output_item_count,inserted_item_count,
                 duplicate_item_count,rejected_item_count,response_bytes,
                 output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                 loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                 completed_at)
            VALUES (@run,'succeeded','completed',1,0,10,@rows,@rows,@rows,
                    0,0,128,128,128,false,false,'none',true,0,0,
                    decode(repeat('b2',32),'hex'),@at);
            INSERT INTO telemetry.m9_commit_replay
                (run_id,instance_id,target_revision,collector_id,collector_version,
                 output_schema_version,request_digest,payload_digest,
                 m9_observation_state)
            VALUES (@run,@target,1,'backups.status',1,1,
                    decode(repeat('b1',32),'hex'),decode(repeat('b2',32),'hex'),1);
            INSERT INTO telemetry.backup_status_snapshot
                (instance_id,target_revision,run_id,observed_at,
                 database_fingerprint,backup_kind,backup_set_id,last_finish_utc,
                 source_local_finish,source_time_unknown,size_bytes,copy_only,
                 has_checksum,is_damaged,coverage)
            SELECT @target,1,@run,@at,decode(lpad(to_hex(n),64,'0'),'hex'),
                   1,n,
                   CASE WHEN @unknown THEN NULL ELSE @at END,
                   CASE WHEN @unknown THEN @at AT TIME ZONE 'UTC' ELSE NULL END,
                   @unknown,1000,false,true,@damaged,1
            FROM generate_series(1,@rows) AS n;
            """);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("work", $"collector/run/backups.status/{target:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("rows", rows);
        command.Parameters.AddWithValue("damaged", damaged);
        command.Parameters.AddWithValue("unknown", unknownTime);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedAgRunAsync(NpgsqlDataSource source, Guid target,
        Guid run, DateTimeOffset at, short state, short visibility,
        int replicas, bool stateAvailable)
    {
        await using var command = source.CreateCommand("""
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,
                 target_revision,schedule_revision,work_key,owner_execution_id,
                 fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'availability-groups.health',1,1,1,1,
                    @work,@owner,1,decode(repeat('c1',32),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome
                (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
                 source_row_count,output_item_count,inserted_item_count,
                 duplicate_item_count,rejected_item_count,response_bytes,
                 output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,
                 loss_count_exact,lost_row_count,lost_byte_count,completion_digest,
                 completed_at)
            VALUES (@run,'succeeded','completed',1,0,10,@replicas,@replicas,
                    @replicas,0,0,128,128,128,false,false,'none',true,0,0,
                    decode(repeat('c2',32),'hex'),@at);
            INSERT INTO telemetry.m9_commit_replay
                (run_id,instance_id,target_revision,collector_id,collector_version,
                 output_schema_version,request_digest,payload_digest,
                 m9_observation_state,m9_visibility_scope)
            VALUES (@run,@target,1,'availability-groups.health',1,1,
                    decode(repeat('c1',32),'hex'),decode(repeat('c2',32),'hex'),
                    @state,@visibility);
            INSERT INTO telemetry.availability_group_replica_snapshot
                (instance_id,target_revision,run_id,observed_at,
                 group_fingerprint,replica_fingerprint,role,operational_state,
                 connected_state,visibility_scope,state_available)
            SELECT @target,1,@run,@at,decode(lpad(to_hex(1),64,'0'),'hex'),
                   decode(lpad(to_hex(n+100),64,'0'),'hex'),
                   CASE WHEN @visibility=1 AND n=1 THEN 'PRIMARY'
                        ELSE 'SECONDARY' END,
                   'ONLINE','DISCONNECTED',@visibility,@stateAvailable
            FROM generate_series(1,@replicas) AS n;
            """);
        command.Parameters.AddWithValue("run", run);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("work", $"collector/run/availability-groups.health/{target:N}");
        command.Parameters.AddWithValue("owner", Guid.NewGuid());
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("visibility", visibility);
        command.Parameters.AddWithValue("replicas", replicas);
        command.Parameters.AddWithValue("stateAvailable", stateAvailable);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> RefreshAsync(
        NpgsqlDataSource source, Guid target, Guid scope, Guid owner, long token)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand("""
            SELECT reporting.refresh_overview_latest_core(@target,1,@owner,@token);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("token", token);
        bool refreshed = (bool)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return refreshed;
    }

    private static async Task<long?> ReadGenerationAsync(
        NpgsqlDataSource source, Guid target, Guid scope, long revision)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await SetScopeAsync(connection, transaction, scope);
        await using var command = new NpgsqlCommand("""
            SELECT generation
            FROM reporting.get_overview_latest_status(@target,@revision);
            """, connection, transaction);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("revision", revision);
        object? result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result is null or DBNull ? null : (long)result;
    }

    private static async Task SetScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid scope)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
        command.Parameters.AddWithValue("scope", scope.ToString("D"));
        await command.ExecuteScalarAsync();
    }
}
