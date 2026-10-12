using System.Globalization;
using System.Text.Json;
using Npgsql;
using SqlObserver.Analytics;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class SqlMetricAnalyticsSourceTests
{
    [Fact]
    public async Task SqlBaselineRetentionRequiresVerifiedRecoveryAndKeepsFencesHoldsAndPolicyReattachment()
    {
        await using RepositoryTestDatabase database = await Database(); Guid target = await Target(database);
        DateTimeOffset oldEnd = BaselineEnd.AddDays(-100);
        string partition = "sql_metric_baseline_p" + oldEnd.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var writer = RollupRepository(collector);
        WorkerLeaseIdentity derivation = await RollupLease(collector);
        foreach (DateTimeOffset end in new[] { oldEnd, BaselineEnd.AddDays(-2), BaselineEnd.AddDays(-1), BaselineEnd })
        {
            await using (var enqueue = database.DataSource.CreateCommand("""
                INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,
                  generation,status,work_key,requested_at,dimensions_hash,source_catalog_version)
                VALUES(gen_random_uuid(),'baseline',@target,1,@from,@to,@metric,@to,1,'queued','analytics/derivation',@from,
                  sha256(convert_to('{}','UTF8')),2);
                """))
            {
                enqueue.Parameters.AddWithValue("target", target); enqueue.Parameters.AddWithValue("from", end.AddDays(-28));
                enqueue.Parameters.AddWithValue("to", end); enqueue.Parameters.AddWithValue("metric", BatchMetric); await enqueue.ExecuteNonQueryAsync();
            }
            AnalyticsDerivationJob job = Assert.Single(await writer.ClaimAsync(derivation, 1, default));
            await writer.StoreSqlBaselinesAsync(RollupRequest(job, derivation), SqlMetricBaselineV2.Compute([], BatchMetric, end, end), default);
            await writer.CompleteAsync(job, derivation, AnalyticsDerivationCompletion.Succeeded, null, default);
        }
        string original = await BaselineContents(database, target);
        Assert.Equal(168, (await BaselineRead(database, target, oldEnd)).Items.Count);
        Assert.Equal("recovery_attestation_missing", await Reason());
        var leaseResult = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(new(new("retention/maintenance"),
            new(Guid.NewGuid()), new(TimeSpan.FromSeconds(90)), RollupTimeout), default);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, leaseResult.Status); WorkerLeaseIdentity retention = leaseResult.Lease!.Identity;
        var maintenance = new PostgreSqlPartitionMaintenancePort(collector);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));

        // Exercise actual pg_dump/pg_restore before attesting recovery. The archive
        // is retained and restored again after the original partition is dropped.
        await using RepositoryTestBackup backup = await database.CreateBackupAsync();
        await using (RepositoryTestDatabase restored = await backup.RestoreAsync())
        {
            Assert.Equal(original, await BaselineContents(restored, target));
            SqlMetricBaselinePage page = await BaselineRead(restored, target, oldEnd);
            Assert.Equal(168, page.Items.Count); Assert.Equal("unavailable", page.State);
            Assert.All(page.Items, row => { Assert.Null(row.Statistics.Median); Assert.Equal(MetricCatalogV2.Checksum, row.CatalogChecksum); });
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var admin = RollupRepository(server);
        JsonElement attestation = JsonSerializer.SerializeToElement(new
        {
            attestedBy = "S-1-5-21-1-2-3-1001", backupSetReference = backup.Reference,
            expiresAtUtc = DateTimeOffset.UtcNow.AddHours(2).ToString("O", CultureInfo.InvariantCulture), digest = backup.Digest,
        });
        var request = new AttestationMutationRequest(attestation, Guid.NewGuid(), "S-1-5-21-1-2-3-1001", Guid.NewGuid(),
            "Fixture full PostgreSQL archive restored and SQL baseline identity verified", McpParameterCanonicalizer.Digest(attestation).Value);
        Assert.Equal("committed", (await admin.RecordAttestationAsync(request, default)).State);
        Assert.Equal("replayed", (await admin.RecordAttestationAsync(request, default)).State);
        await using (var identity = database.DataSource.CreateCommand("SELECT encode(attestation_digest,'hex') FROM system.recovery_attestation WHERE attestation_id=@id;"))
        { identity.Parameters.AddWithValue("id", request.OperationId); Assert.Equal(backup.Digest, await identity.ExecuteScalarAsync()); }

        // An active job in a disjoint window must not become a fleet-wide gate.
        Guid disjointTarget = await Target(database);
        await Hold(disjointTarget, BaselineEnd.AddDays(-1), BaselineEnd);
        Guid overlap = await Hold(target, oldEnd.AddHours(1), oldEnd.AddHours(2));
        Assert.Equal("dependency_pending", await Reason()); Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await using (var completed = database.DataSource.CreateCommand("UPDATE control.analytics_job SET status='succeeded' WHERE job_id=@job;"))
        { completed.Parameters.AddWithValue("job", overlap); await completed.ExecuteNonQueryAsync(); }

        await Sql("""
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),clock_timestamp()+interval '1 hour'
            FROM system.partition_registry WHERE parent_table='sql_metric_baseline' AND range_start=@old;
            """);
        Assert.Equal("reader_lease_active", await Reason()); Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await Sql("UPDATE system.partition_reader_lease SET released_at=clock_timestamp() WHERE parent_table='sql_metric_baseline';");
        await Sql("""
            INSERT INTO system.retention_backfill_state(parent_schema,parent_table,partition_name,range_start,range_end,state)
            SELECT parent_schema,parent_table,partition_name,range_start,range_end,'pending'
            FROM system.partition_registry WHERE parent_table='sql_metric_baseline' AND range_start=@old;
            """);
        Assert.Equal("backfill_incomplete", await Reason()); Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await Sql("UPDATE system.retention_backfill_state SET state='complete' WHERE parent_table='sql_metric_baseline';");
        await Sql("UPDATE system.retention_policy SET minimum_partitions_to_keep=4,policy_revision=policy_revision+1 WHERE data_class='sql_metric_baselines';");
        Assert.Equal("minimum_partition_floor", await Reason()); Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await Sql("UPDATE system.retention_policy SET minimum_partitions_to_keep=3,policy_revision=policy_revision+1 WHERE data_class='sql_metric_baselines';");
        Assert.Equal("eligible", await Reason());
        var stale = new WorkerLeaseIdentity(retention.Key, retention.Owner, new(retention.FencingToken.Value + 1));
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => Step(stale).AsTask())).SqlState);
        Assert.Equal(SystemRetentionStepOutcome.Detached, await Step(retention));
        Assert.Equal("no_data", (await BaselineRead(database, target, oldEnd)).State);
        await using (var preserved = database.DataSource.CreateCommand($"SELECT count(*) FROM analytics.{partition};"))
            Assert.Equal(168L, await preserved.ExecuteScalarAsync());
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await Sql("UPDATE system.retention_policy SET enabled=false,retain_for=NULL,policy_revision=policy_revision+1 WHERE data_class='sql_metric_baselines';");
        Assert.Equal(SystemRetentionStepOutcome.Reattached, await Step(retention));
        Assert.Equal(original, await BaselineContents(database, target));
        await Sql("UPDATE system.retention_policy SET enabled=true,retain_for=interval '90 days',policy_revision=policy_revision+1 WHERE data_class='sql_metric_baselines';");
        Assert.Equal(SystemRetentionStepOutcome.Detached, await Step(retention));
        await Sql("""
            UPDATE system.retention_execution SET drop_after=clock_timestamp()-interval '1 second' WHERE data_class='sql_metric_baselines' AND state='grace';
            INSERT INTO system.partition_reader_lease(lease_id,parent_schema,parent_table,partition_name,owner_execution_id,expires_at)
            SELECT gen_random_uuid(),parent_schema,parent_table,partition_name,gen_random_uuid(),clock_timestamp()+interval '1 hour'
            FROM system.partition_registry WHERE parent_table='sql_metric_baseline' AND lifecycle_state='detached';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await Sql("UPDATE system.partition_reader_lease SET released_at=clock_timestamp() WHERE parent_table='sql_metric_baseline' AND released_at IS NULL;");
        await Sql($"CREATE VIEW public.sql_baseline_drop_failure AS SELECT * FROM analytics.{partition};");
        Assert.Equal(SystemRetentionStepOutcome.Retry, await Step(retention));
        await using (var preserved = database.DataSource.CreateCommand($"SELECT count(*) FROM analytics.{partition};"))
            Assert.Equal(168L, await preserved.ExecuteScalarAsync());
        Assert.Equal(SystemRetentionStepOutcome.Idle, await Step(retention));
        await Sql("""
            DROP VIEW public.sql_baseline_drop_failure;
            INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at)
            SELECT execution_id,'2BP01','Fixture elapsed retry after dependent view removal',clock_timestamp()-interval '1 second'
            FROM system.retention_execution WHERE data_class='sql_metric_baselines' AND state='retry';
            """);
        Assert.Equal(SystemRetentionStepOutcome.Dropped, await Step(retention));
        Assert.Equal("no_data", (await BaselineRead(database, target, oldEnd)).State);
        await using (var final = database.DataSource.CreateCommand("""
            SELECT count(*) FILTER(WHERE lifecycle_state='dropped'), count(*) FILTER(WHERE lifecycle_state='attached'),
              to_regclass(@partition) IS NULL FROM system.partition_registry WHERE parent_table='sql_metric_baseline';
            """))
        {
            final.Parameters.AddWithValue("partition", "analytics." + partition); await using NpgsqlDataReader rows = await final.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync()); Assert.Equal(1L, rows.GetInt64(0)); Assert.Equal(3L, rows.GetInt64(1)); Assert.True(rows.GetBoolean(2));
        }
        await using RepositoryTestDatabase recovered = await backup.RestoreAsync();
        Assert.Equal(original, await BaselineContents(recovered, target));
        Assert.Equal(168, (await BaselineRead(recovered, target, oldEnd)).Items.Count);
        await using var audits = database.DataSource.CreateCommand("""
            SELECT count(*) FROM audit.activity WHERE action_name IN ('retention.partition.detach','retention.partition.reattach','retention.partition.drop')
                AND outcome='succeeded' AND safe_details->>'dataClass'='sql_metric_baselines';
            """);
        Assert.Equal(4L, await audits.ExecuteScalarAsync());

        ValueTask<SystemRetentionStepOutcome> Step(WorkerLeaseIdentity lease) => maintenance.RunSystemRetentionStepAsync(lease, RollupTimeout, default);
        async Task<string> Reason()
        {
            await using var command = database.DataSource.CreateCommand("SELECT reason FROM system.preview_m10_retention(clock_timestamp(),NULL) WHERE data_class='sql_metric_baselines' AND range_start=@old;");
            command.Parameters.AddWithValue("old", oldEnd); return (string)(await command.ExecuteScalarAsync())!;
        }
        async Task Sql(string sql)
        { await using var command = database.DataSource.CreateCommand(sql); command.Parameters.AddWithValue("old", oldEnd); await command.ExecuteNonQueryAsync(); }
        async Task<Guid> Hold(Guid instance, DateTimeOffset from, DateTimeOffset to)
        {
            Guid id = Guid.NewGuid(); await using var command = database.DataSource.CreateCommand("""
                INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,work_key,from_utc,to_utc,status)
                VALUES(@id,'baseline',@target,1,'analytics/derivation',@from,@to,'queued');
                """);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("target", instance);
            command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to); await command.ExecuteNonQueryAsync(); return id;
        }
    }

    private static async Task<string> BaselineContents(RepositoryTestDatabase database, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_agg(to_jsonb(b) ORDER BY b.window_end,b.hour_of_week)::text FROM analytics.sql_metric_baseline b WHERE b.instance_id=@target;
            """);
        command.Parameters.AddWithValue("target", target); return (string)(await command.ExecuteScalarAsync())!;
    }
    private static async Task<SqlMetricBaselinePage> BaselineRead(RepositoryTestDatabase database, Guid target, DateTimeOffset end)
    {
        await using NpgsqlDataSource server = database.CreateServerDataSource(); var service = new SqlMetricBaselineQueryService(RollupRepository(server));
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1001"), AuthorizationPrincipalState.Active,
            [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([new(target)]))]);
        return await service.GetAsync(authorization, new(new(target), BatchMetric, end.AddDays(-28), end, 168, RollupTimeout, new(1)), 2, 1, default);
    }
}
