using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BackupV2PersistenceTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Signature = "control.commit_backups_v2_collection_run(" +
        "uuid,uuid,bigint,text,integer,integer,bigint,timestamp with time zone,text,uuid,bigint,bytea," +
        "text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,integer,text,integer,jsonb,bytea)";

    [Fact]
    public async Task PrivateWriterPreservesRawAndQualifiedTimesAndRejectsDivergentReplay()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        WorkerLeaseIdentity lease = await LeaseAsync(database, target);
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        CommitCollectorRunRequest request = Evidence(work, run, lease, qualified: true);
        await using (NpgsqlDataSource collector = database.CreateCollectorDataSource())
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await new PostgreSqlCollectorRuntimeRepositoryPort(collector).CommitRunAsync(request, CancellationToken.None))).SqlState);
        Assert.Equal(2, (await runtime.CommitRunAsync(request, CancellationToken.None)).InsertedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(Evidence(work, run, lease, qualified: true, size: 1), CancellationToken.None))).SqlState);
        Assert.Equal((1L, 1L, 1L, 1L, 0L), await CountsAsync(database, target));
        await using (var read = database.DataSource.CreateCommand("""
            SELECT scan.received_at,scan.source_observed_at,sample.source_local_finish,sample.finish_utc,
              sample.qualification_revision,sample.size_bytes,sample.has_checksum,sample.is_damaged
            FROM telemetry.backup_v2_scan scan JOIN telemetry.backup_v2_sample sample
              USING(instance_id,target_revision,received_at,run_id) WHERE run_id=@run;
            """))
        {
            read.Parameters.AddWithValue("run", run.Value);
            await using NpgsqlDataReader row = await read.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(work.RepositoryTimeUtc, row.GetFieldValue<DateTimeOffset>(0));
            // Source clock skew is evidence, not the partition key or a retry loop.
            Assert.Equal(work.RepositoryTimeUtc.AddDays(60), row.GetFieldValue<DateTimeOffset>(1));
            Assert.Equal(DateTimeKind.Unspecified, row.GetDateTime(2).Kind);
            Assert.Equal(work.RepositoryTimeUtc.AddHours(-1), row.GetFieldValue<DateTimeOffset>(3));
            Assert.Equal(1L, row.GetInt64(4)); Assert.Equal(long.MaxValue, row.GetInt64(5));
            Assert.True(row.IsDBNull(6)); Assert.False(row.GetBoolean(7));
        }
        await using var stale = database.DataSource.CreateCommand("UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key=@key;");
        stale.Parameters.AddWithValue("key", lease.Key.Value); await stale.ExecuteNonQueryAsync();
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task PartialUnknownIdentityTruncationAndFailureReceiptsNeverFabricateBackupEvidence()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        WorkerLeaseIdentity lease = await LeaseAsync(database, target);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        Assert.Equal(2, (await runtime.CommitRunAsync(Evidence(work, run, lease), CancellationToken.None)).InsertedCount);
        await using (var unresolved = database.DataSource.CreateCommand("SELECT finish_utc,qualification_revision,time_state FROM telemetry.backup_v2_sample WHERE run_id=@run;"))
        {
            unresolved.Parameters.AddWithValue("run", run.Value);
            await using NpgsqlDataReader row = await unresolved.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync()); Assert.True(row.IsDBNull(0)); Assert.True(row.IsDBNull(1)); Assert.Equal((short)4, row.GetInt16(2));
        }
        work = await NextDueAsync(database, runtime, target); run = await BeginAsync(runtime, work, lease);
        Assert.Equal(3, (await runtime.CommitRunAsync(Evidence(work, run, lease, unknown: true), CancellationToken.None)).InsertedCount);
        foreach (CollectorRunReason reason in new[] { CollectorRunReason.SourceRowLimit, CollectorRunReason.ResponseByteLimit })
        {
            work = await NextDueAsync(database, runtime, target); run = await BeginAsync(runtime, work, lease);
            var snapshot = new BackupV2ReadSnapshot(new(work.TargetId, work.TargetRevision, run), work.RepositoryTimeUtc,
                work.RepositoryTimeUtc, null, work.RepositoryTimeUtc.DateTime.AddDays(-35), [], 3073, BackupFreshnessCoverage.Truncated);
            var loss = new CollectorLossEvidence(reason == CollectorRunReason.SourceRowLimit ? CollectorLossKind.SourceRowLimit : CollectorLossKind.ResponseByteLimit, 3073, false);
            Assert.Equal(0, (await runtime.CommitRunAsync(Request(work, run, lease, snapshot, CollectorRunOutcome.Partial, reason, loss), CancellationToken.None)).InsertedCount);
        }
        work = await NextDueAsync(database, runtime, target); run = await BeginAsync(runtime, work, lease);
        var empty = new BackupV2ReadSnapshot(new(work.TargetId, work.TargetRevision, run), work.RepositoryTimeUtc, null, null, null, [], 0, BackupFreshnessCoverage.Partial);
        await runtime.CommitRunAsync(Request(work, run, lease, empty, CollectorRunOutcome.Partial, CollectorRunReason.VisibilityIncomplete,
            new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false)), CancellationToken.None);
        foreach (CollectorRunOutcome outcome in new[] { CollectorRunOutcome.PermissionDenied, CollectorRunOutcome.OutputInvalid })
        {
            work = await NextDueAsync(database, runtime, target); run = await BeginAsync(runtime, work, lease);
            bool invalid = outcome == CollectorRunOutcome.OutputInvalid;
            var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 2, 2, outcome,
                invalid ? CollectorRunReason.OutputValidationFailed : CollectorRunReason.RequiredPermissionMissing,
                TimeSpan.FromMilliseconds(5), 1, invalid ? new(3, 3, 8192, 8192) : new(0, 0, 0, 0),
                invalid ? new(CollectorLossKind.OutputValidationFailure, 3, false, 8192) : CollectorLossEvidence.None);
            var failure = new CommitCollectorRunRequest(work, summary, CollectorPayload.Empty, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
            Assert.Equal(0, (await runtime.CommitRunAsync(failure, CancellationToken.None)).InsertedCount);
            Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(failure, CancellationToken.None)).Status);
        }
        Assert.Equal((7L, 3L, 2L, 7L, 6L), await CountsAsync(database, target));
        await using var rejected = database.DataSource.CreateCommand("""
            SELECT scan.rejected_items,scan.rejected_bytes,outcome.output_item_count,outcome.rejected_item_count,outcome.lost_byte_count
            FROM telemetry.backup_v2_scan scan JOIN telemetry.collection_run_outcome outcome USING(run_id) WHERE scan.run_id=@run;
            """);
        rejected.Parameters.AddWithValue("run", run.Value);
        await using NpgsqlDataReader rejectedRow = await rejected.ExecuteReaderAsync();
        Assert.True(await rejectedRow.ReadAsync()); Assert.Equal(3, rejectedRow.GetInt32(0)); Assert.Equal(8192, rejectedRow.GetInt32(1));
        Assert.Equal(3, rejectedRow.GetInt32(2)); Assert.Equal(3, rejectedRow.GetInt32(3)); Assert.Equal(8192L, rejectedRow.GetInt64(4));
    }

    [Fact]
    public async Task SqlBoundaryRejectsMalformedEvidenceAndRollsBackEveryWriteOnPartitionOrFenceFailure()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        WorkerLeaseIdentity lease = await LeaseAsync(database, target);
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        CommitCollectorRunRequest request = Evidence(work, run, lease);
        string valid = JsonSerializer.Serialize(BackupV2PersistencePayload.Build(request, "succeeded", "completed"), JsonOptions);
        Action<JsonObject>[] mutations =
        [
            root => root["extra"] = true,
            root => root.Remove("sourceZoneId"),
            root => root["schemaVersion"] = "2",
            root => root["receivedAtUtc"] = "2026-02-30T00:00:00.000000Z",
            root => root["sourceObservedAtUtc"] = "2026-09-30T00:00:00+00:00",
            root => root["targetRevision"] = 2,
            root => root["coverage"] = 3,
            root => root["databases"]![0]!["ordinal"] = 2,
            root => root["databases"]![0]!["recoveryModel"] = "2",
            root => root["databases"]![0]!["databaseFingerprint"] = null,
            root => root["databases"]![0]!["localReplicaPreferred"] = true,
            root => root["databases"]![0]!["samples"]![0]!["copyOnly"] = "false",
            root => root["databases"]![0]!["samples"]![0]!["sizeBytes"] = -1,
            root => root["databases"]![0]!["samples"]![0]!["sizeBytes"] = JsonNode.Parse("9223372036854775808"),
            root => root["databases"]![0]!["samples"]![0]!["recordedStartOffsetMinutes"] = 1,
            root => root["databases"]![0]!["samples"]![0]!["lastRecoveryForkFingerprint"] = new string('b', 64),
            root => root["databases"]![0]!["samples"]![0]!["qualificationRevision"] = 1,
            root => { var sample = root["databases"]![0]!["samples"]![0]!; sample["timeState"] = 1; sample["finishUtc"] = work.RepositoryTimeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture); },
        ];
        foreach (Action<JsonObject> mutate in mutations)
        {
            var root = JsonNode.Parse(valid)!.AsObject(); mutate(root);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(database, request, root.ToJsonString()))).SqlState);
            Assert.Equal((0L, 0L, 0L, 0L, 0L), await CountsAsync(database, target));
        }
        var changed = JsonNode.Parse(valid)!.AsObject(); changed["databases"]![0]!["samples"]![0]!["sizeBytes"] = 1;
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(database, request, changed.ToJsonString(), valid))).SqlState);
        string qualified = JsonSerializer.Serialize(BackupV2PersistencePayload.Build(Evidence(work, run, lease, qualified: true), "succeeded", "completed"), JsonOptions);
        Action<JsonObject>[] qualificationMutations =
        [
            root => root["sourceZoneId"] = "Wrong zone",
            root => root["databases"]![0]!["samples"]![0]!["qualificationRevision"] = 999,
            root => root["databases"]![0]!["samples"]![0]!["finishUtc"] = work.RepositoryTimeUtc.AddDays(2).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            root => root["databases"]![0]!["samples"]![0]!["finishUtc"] = work.RepositoryTimeUtc.AddHours(-3).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            root => root["databases"]![0]!["samples"]![0]!["finishUtc"] = work.RepositoryTimeUtc.AddHours(-1).AddSeconds(1).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
        ];
        foreach (Action<JsonObject> mutate in qualificationMutations)
        {
            var root = JsonNode.Parse(qualified)!.AsObject(); mutate(root);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() => RawCommitAsync(database, request, root.ToJsonString()))).SqlState);
            Assert.Equal((0L, 0L, 0L, 0L, 0L), await CountsAsync(database, target));
        }
        await ExecuteAsync(database, "ALTER TABLE telemetry.backup_v2_sample DETACH PARTITION telemetry.backup_sample_test;");
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(async () => await runtime.CommitRunAsync(request, CancellationToken.None))).SqlState);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await CountsAsync(database, target));
        await ExecuteAsync(database, """
            DO $p$ BEGIN EXECUTE format('ALTER TABLE telemetry.backup_v2_sample ATTACH PARTITION telemetry.backup_sample_test FOR VALUES FROM (%L) TO (%L)',current_date::timestamptz,(current_date+1)::timestamptz); END $p$;
            UPDATE control.collector_schedule SET schedule_revision=schedule_revision+1 WHERE collector_id='backups.status';
            """);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () => await runtime.CommitRunAsync(request, CancellationToken.None))).SqlState);
        Assert.Equal((0L, 0L, 0L, 0L, 0L), await CountsAsync(database, target));
        await ExecuteAsync(database, "UPDATE control.collector_schedule SET schedule_revision=schedule_revision-1 WHERE collector_id='backups.status';");
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task TablesAndWriterArePrivateAppendOnlyAndForceTargetScope()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        WorkerLeaseIdentity lease = await LeaseAsync(database, target);
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        await runtime.CommitRunAsync(Evidence(work, run, lease), CancellationToken.None);
        await using (var catalog = database.DataSource.CreateCommand("""
            SELECT has_function_privilege('sqlobserver_collector',@function,'EXECUTE'),
              has_function_privilege('sqlobserver_server',@function,'EXECUTE'),
              has_function_privilege('sqlobserver_auditor',@function,'EXECUTE'),pg_get_userbyid(proowner),prosecdef
            FROM pg_proc WHERE oid=@function::regprocedure;
            """))
        {
            catalog.Parameters.AddWithValue("function", Signature);
            await using NpgsqlDataReader row = await catalog.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.False(row.GetBoolean(0)); Assert.False(row.GetBoolean(1)); Assert.False(row.GetBoolean(2));
            Assert.Equal("sqlobserver_migrator", row.GetString(3)); Assert.True(row.GetBoolean(4));
        }
        foreach (string table in new[] { "telemetry.backup_v2_scan", "telemetry.backup_v2_database", "telemetry.backup_v2_sample", "control.backup_historical_zone_qualification" })
        {
            await using var check = database.DataSource.CreateCommand("SELECT relrowsecurity,relforcerowsecurity,has_table_privilege('sqlobserver_server',@table,'SELECT'),has_table_privilege('sqlobserver_collector',@table,'INSERT') FROM pg_class WHERE oid=@table::regclass;");
            check.Parameters.AddWithValue("table", table);
            await using NpgsqlDataReader row = await check.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
            Assert.True(row.GetBoolean(0)); Assert.True(row.GetBoolean(1)); Assert.False(row.GetBoolean(2)); Assert.False(row.GetBoolean(3));
        }
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database,
            "SET ROLE sqlobserver_migrator; UPDATE telemetry.backup_v2_scan SET coverage=2;"))).SqlState);
        await ExecuteAsync(database, "GRANT SELECT ON telemetry.backup_v2_scan,telemetry.backup_v2_database,telemetry.backup_v2_sample TO sqlobserver_server;");
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using var scoped = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_server; SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction);
        scoped.Parameters.AddWithValue("scope", Guid.NewGuid().ToString()); await scoped.ExecuteNonQueryAsync();
        foreach (string table in new[] { "backup_v2_scan", "backup_v2_database", "backup_v2_sample" })
        {
            scoped.Parameters["scope"].Value = Guid.NewGuid().ToString(); await scoped.ExecuteNonQueryAsync();
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM telemetry.{table};", connection, transaction);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
            scoped.Parameters["scope"].Value = target.ToString(); await scoped.ExecuteNonQueryAsync();
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
    }

    private async Task<RepositoryTestDatabase> PrepareAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
            new(MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(migrated.HasFailures, string.Join(", ", migrated.Results.Where(item => item.Outcome == MigrationOutcome.Failed)
            .Select(item => $"{item.Migration.Number.Value}:{item.FailureCode}")));
        return database;
    }

    private static async Task<Guid> SeedAsync(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            -- Fixture scheduling only. Production catalog/activation remains v1.
            INSERT INTO control.collector_contract(collector_id,collector_version,execution_order,manifest_schema_version,
              output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,minimum_interval,execution_timeout,
              maximum_rows,maximum_response_bytes,estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
            SELECT collector_id,2,execution_order,7,2,manifest_sha256,asset_bundle_sha256,default_interval,minimum_interval,
              execution_timeout,3072,4194304,estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval
            FROM control.collector_contract WHERE collector_id='backups.status' AND collector_version=1
            ON CONFLICT(collector_id,collector_version) DO NOTHING;
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Backup v2 commit','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule SET enabled=(collector_id IN ('engine.core','backups.status')),
              collector_version=CASE WHEN collector_id='backups.status' THEN 2 ELSE collector_version END,
              next_due_at=CASE WHEN collector_id='engine.core' THEN clock_timestamp()+interval '1 day' ELSE next_due_at END,
              last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded' ELSE last_outcome END,
              last_succeeded_at=CASE WHEN collector_id='engine.core' THEN clock_timestamp() ELSE last_succeeded_at END
            WHERE instance_id=@target;
            SET ROLE sqlobserver_migrator;
            INSERT INTO control.backup_historical_zone_qualification(instance_id,target_revision,qualification_revision,
              source_zone_id,serialized_zone_rules,rules_sha256,qualified_from,qualified_to)
            VALUES(@target,1,1,'UTC',@rules,sha256(convert_to(@rules,'UTF8')),clock_timestamp()-interval '40 days',clock_timestamp()+interval '1 day');
            DO $partitions$ BEGIN
              EXECUTE format('CREATE TABLE telemetry.backup_scan_test PARTITION OF telemetry.backup_v2_scan FOR VALUES FROM (%L) TO (%L)',current_date::timestamptz,(current_date+1)::timestamptz);
              EXECUTE format('CREATE TABLE telemetry.backup_database_test PARTITION OF telemetry.backup_v2_database FOR VALUES FROM (%L) TO (%L)',current_date::timestamptz,(current_date+1)::timestamptz);
              EXECUTE format('CREATE TABLE telemetry.backup_sample_test PARTITION OF telemetry.backup_v2_sample FOR VALUES FROM (%L) TO (%L)',current_date::timestamptz,(current_date+1)::timestamptz);
            END $partitions$;
            RESET ROLE;
            """);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("key", $"backup.commit.{target:N}");
        seed.Parameters.AddWithValue("rules", TimeZoneInfo.Utc.ToSerializedString()); await seed.ExecuteNonQueryAsync();
        return target;
    }

    private static async Task<WorkerLeaseIdentity> LeaseAsync(RepositoryTestDatabase database, Guid target)
    {
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(database.DataSource).AcquireAsync(new(
            new($"collector/run/backups.status/{target:N}"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        return Assert.IsType<WorkerLease>(acquired.Lease).Identity;
    }

    private static async Task<CollectorDueWorkItem> DueAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        CollectorDueWorkItem due = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), CancellationToken.None)).Items, item => item.TargetId.Value == target);
        DateTimeOffset now = due.RepositoryTimeUtc;
        var profile = new CapabilityProfile(due.TargetId, due.TargetRevision, new("capability.connection"), 1, 1,
            new(new(16, 0, 1000, 1), new("Fixture"), SqlServerEngineEdition.Enterprise, SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Supported, CapabilityDiscoveryReason.Verified, SqlServerAuthenticationScheme.Kerberos,
            true, false, [], [], TimeSpan.FromMilliseconds(1), 64, now, now.AddMinutes(5));
        return new(due.TargetId, due.TargetRevision, due.ConnectionPolicy, due.CollectorId, 2, 2, due.ScheduleRevision, due.ScheduledAtUtc, now, due.Circuit, profile);
    }

    private static async Task<CollectorDueWorkItem> NextDueAsync(RepositoryTestDatabase database, PostgreSqlCollectorRuntimeRepositoryPort runtime, Guid target)
    {
        await using var command = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET next_due_at=statement_timestamp() WHERE instance_id=@target AND collector_id='backups.status';");
        command.Parameters.AddWithValue("target", target); await command.ExecuteNonQueryAsync(); return await DueAsync(runtime, target);
    }

    private static async Task<CollectorRunId> BeginAsync(PostgreSqlCollectorRuntimeRepositoryPort runtime, CollectorDueWorkItem work, WorkerLeaseIdentity lease)
    {
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), CancellationToken.None)).Status);
        return run;
    }

    private static CommitCollectorRunRequest Evidence(CollectorDueWorkItem work, CollectorRunId run, WorkerLeaseIdentity lease,
        bool qualified = false, bool unknown = false, long size = long.MaxValue)
    {
        DateTimeOffset now = work.RepositoryTimeUtc;
        var identity = new BackupV2RunIdentity(work.TargetId, work.TargetRevision, run);
        string db = new('d', 64), family = new('f', 64), fork = new('a', 64);
        DateTime start = now.DateTime.AddHours(-2), finish = now.DateTime.AddHours(-1), lower = now.DateTime.AddDays(-35);
        var sample = new BackupV2SampleRead(new(BackupKind.Full, false, qualified ? now.AddHours(-1) : null, finish,
            null, false, true), start, 0, size, fork, fork,
            new(qualified ? BackupFinishTimeState.Resolved : BackupFinishTimeState.QualificationMissing, qualified ? now.AddHours(-1) : null, qualified ? 1 : null));
        var context = new BackupV2DatabaseContext(db, family, fork, null, null, null, true, BackupRecoveryModel.Full,
            "ONLINE", BackupFreshnessScope.Standalone, null, null, now.AddDays(60), "UTC", lower, false);
        List<BackupV2DatabaseRead> databases = [new(context, [sample], identity, BackupFreshnessCoverage.Complete)];
        if (unknown) databases.Add(new(context with { Database = null, IdentityKnown = false }, [], identity, BackupFreshnessCoverage.Partial));
        var snapshot = new BackupV2ReadSnapshot(identity, now, now.AddDays(60), "UTC", lower, databases,
            unknown ? 3 : 2, unknown ? BackupFreshnessCoverage.Partial : BackupFreshnessCoverage.Complete);
        return Request(work, run, lease, snapshot, unknown ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
            unknown ? CollectorRunReason.VisibilityIncomplete : CollectorRunReason.Completed,
            unknown ? new(CollectorLossKind.VisibilityIncomplete, 1, false) : CollectorLossEvidence.None);
    }

    private static CommitCollectorRunRequest Request(CollectorDueWorkItem work, CollectorRunId run, WorkerLeaseIdentity lease,
        BackupV2ReadSnapshot snapshot, CollectorRunOutcome outcome, CollectorRunReason reason, CollectorLossEvidence loss)
    {
        var payload = new CollectorPayload(operationalHealth: new(snapshot, snapshot.ItemCount, snapshot.EstimatedSizeBytes));
        var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId, 2, 2, outcome, reason,
            TimeSpan.FromMilliseconds(5), 1, new(snapshot.SourceRowsRead, snapshot.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), loss);
        return new(work, summary, payload, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<(long Scans, long Databases, long Samples, long Outcomes, long Gaps)> CountsAsync(RepositoryTestDatabase database, Guid target)
    {
        await using var read = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.backup_v2_scan WHERE instance_id=@target),
              (SELECT count(*) FROM telemetry.backup_v2_database WHERE instance_id=@target),
              (SELECT count(*) FROM telemetry.backup_v2_sample WHERE instance_id=@target),
              (SELECT count(*) FROM telemetry.collection_run_outcome outcome JOIN telemetry.collection_run_v2 run USING(run_id) WHERE run.instance_id=@target AND run.collector_id='backups.status'),
              (SELECT count(*) FROM telemetry.visibility_gap WHERE instance_id=@target);
            """);
        read.Parameters.AddWithValue("target", target); await using NpgsqlDataReader row = await read.ExecuteReaderAsync(); Assert.True(await row.ReadAsync());
        return (row.GetInt64(0), row.GetInt64(1), row.GetInt64(2), row.GetInt64(3), row.GetInt64(4));
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql); await command.ExecuteNonQueryAsync();
    }

    private static async Task RawCommitAsync(RepositoryTestDatabase database, CommitCollectorRunRequest request, string payload, string? digestPayload = null)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT result.* FROM telemetry.collection_run_v2 run CROSS JOIN LATERAL control.commit_backups_v2_collection_run(
              run.run_id,run.instance_id,run.target_revision,run.collector_id,run.collector_version,run.output_schema_version,
              run.schedule_revision,run.scheduled_for,run.work_key,run.owner_execution_id,run.fencing_token,run.request_digest,
              'succeeded','completed',5::bigint,1,2,2,@bytes::bigint,@bytes::bigint,'none',0,true,0,'closed',0,@payload,
              sha256(convert_to(CAST(@digest AS jsonb)::text,'UTF8'))) result WHERE run.run_id=@run;
            """);
        command.Parameters.AddWithValue("run", request.Summary.RunId.Value); command.Parameters.AddWithValue("bytes", request.Payload.EstimatedSizeBytes);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload); command.Parameters.AddWithValue("digest", NpgsqlDbType.Jsonb, digestPayload ?? payload);
        await command.ExecuteNonQueryAsync();
    }
}
