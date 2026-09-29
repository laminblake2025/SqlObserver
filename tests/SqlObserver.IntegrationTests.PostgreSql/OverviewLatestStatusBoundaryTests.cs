using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class OverviewLatestStatusBoundaryTests(PostgreSql18Fixture fixture)
{
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
        Assert.Contains(status.Gaps, gap => gap.Contains("not materialized", StringComparison.Ordinal));

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
            FROM generate_series(1,4) AS n;
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
        Assert.Equal("partial", databases.Sources["databaseStates"].State);
        Assert.Equal(5, databases.Issues.Count(issue => issue.Title == "Database state exception"));
        Assert.Equal(10, databases.Issues.Count);
        Assert.Contains(databases.Issues, issue => issue.Detail == "Database 2: RecoveryPending");
        Assert.Contains("Database state issue list partial", databases.Gaps);
        Assert.Contains("Overview issue list partial", databases.Gaps);

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
