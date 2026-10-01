using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class PlanCacheRuntimeStoragePostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly string[] CounterFields = ["cpuUs", "durationUs", "executionCount", "logicalReads", "writes", "rowsProcessed"];
    private static readonly string[] RejectedSqlStates = ["22023", "22003", "42501"];
    [Fact]
    public async Task StorageAndPartitionsArePrivateScopedAppendOnlyAndRegisteredForBoundedRetention()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var catalog = new NpgsqlCommand("""
            SELECT count(*)=3,
                bool_and(c.relkind='p' AND c.relrowsecurity AND c.relforcerowsecurity
                    AND pg_get_userbyid(c.relowner)='sqlobserver_migrator'),
                bool_and(NOT has_table_privilege('sqlobserver_collector',c.oid,'SELECT,INSERT,UPDATE,DELETE')),
                bool_and(NOT has_table_privilege('sqlobserver_server',c.oid,'SELECT,INSERT,UPDATE,DELETE')),
                bool_and(NOT has_table_privilege('sqlobserver_auditor',c.oid,'SELECT,INSERT,UPDATE,DELETE')),
                (SELECT count(*)=30 AND bool_and(child.relrowsecurity AND child.relforcerowsecurity
                    AND NOT has_table_privilege('sqlobserver_collector',child.oid,'SELECT,INSERT,UPDATE,DELETE'))
                    FROM pg_inherits i JOIN pg_class child ON child.oid=i.inhrelid
                    WHERE i.inhparent IN ('events.plan_cache_runtime_watermark'::regclass,
                        'events.plan_cache_runtime_delta'::regclass,'events.plan_cache_runtime_receipt'::regclass)),
                (SELECT count(*)=30 FROM system.partition_registry WHERE parent_schema='events'
                    AND parent_table IN ('plan_cache_runtime_watermark','plan_cache_runtime_delta','plan_cache_runtime_receipt')
                    AND lifecycle_state='attached'),
                (SELECT count(*)=3 AND bool_and(enabled AND minimum_partitions_to_keep=3
                    AND retain_for=CASE WHEN data_class='m7_cache_watermarks' THEN interval '8 days' ELSE interval '30 days' END)
                    FROM system.retention_policy WHERE data_class IN ('m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts')),
                NOT has_function_privilege('sqlobserver_collector','events.apply_plan_cache_runtime_batch(uuid,uuid,bigint,jsonb)','EXECUTE'),
                NOT has_function_privilege('sqlobserver_server','events.apply_plan_cache_runtime_batch(uuid,uuid,bigint,jsonb)','EXECUTE'),
                NOT has_function_privilege('sqlobserver_collector','control.ensure_plan_cache_runtime_partitions(date)','EXECUTE')
            FROM pg_class c WHERE c.oid IN ('events.plan_cache_runtime_watermark'::regclass,
                'events.plan_cache_runtime_delta'::regclass,'events.plan_cache_runtime_receipt'::regclass);
            """, connection))
        await using (var reader = await catalog.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            for (int column = 0; column < 11; column++) Assert.True(reader.GetBoolean(column), $"Catalog invariant {column} failed.");
        }
        await using var transaction = await connection.BeginTransactionAsync();
        Guid target = Guid.NewGuid();
        await Scope(connection, transaction, target);
        Assert.Equal(0, await Scalar(connection, transaction, "SELECT control.ensure_plan_cache_runtime_partitions(current_date);"));
        var payload = Payload(target, DateTimeOffset.UtcNow.AddSeconds(-3), Row(1));
        Assert.Equal(1, await Apply(connection, transaction, target, payload));
        Assert.Equal(3L, await Scalar(connection, transaction, """
            SELECT (SELECT count(*) FROM events.plan_cache_runtime_watermark)
                +(SELECT count(*) FROM events.plan_cache_runtime_delta)+(SELECT count(*) FROM events.plan_cache_runtime_receipt);
            """));
        await Scope(connection, transaction, Guid.NewGuid());
        Assert.Equal(0L, await Scalar(connection, transaction, """
            SELECT (SELECT count(*) FROM events.plan_cache_runtime_watermark)
                +(SELECT count(*) FROM events.plan_cache_runtime_delta)+(SELECT count(*) FROM events.plan_cache_runtime_receipt);
            """));
        string childName;
        await using (var child = new NpgsqlCommand("""
            SELECT partition_name::text FROM system.partition_registry
            WHERE parent_schema='events' AND parent_table='plan_cache_runtime_watermark'
                AND range_start=date_trunc('day',clock_timestamp(),'UTC');
            """, connection, transaction))
            childName = Assert.IsType<string>(await child.ExecuteScalarAsync());
        Assert.Matches("^plan_cache_runtime_watermark_p[0-9]{8}$", childName);
        Assert.Equal(0L, await Scalar(connection, transaction, $"SELECT count(*) FROM events.{childName};"));
        await Scope(connection, transaction, target);
        Assert.Equal(1L, await Scalar(connection, transaction, $"SELECT count(*) FROM events.{childName};"));
        await Execute(connection, transaction, "SAVEPOINT poisoned_partition_policy;");
        await Execute(connection, transaction, $"ALTER POLICY cache_runtime_scope ON events.{childName} USING(true) WITH CHECK(true);");
        var poisoned = await Assert.ThrowsAsync<PostgresException>(() => Scalar(connection, transaction,
            "SELECT control.ensure_plan_cache_runtime_partitions(current_date);"));
        Assert.Equal("55000", poisoned.SqlState);
        await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT poisoned_partition_policy;");
        foreach (string relation in new[] { "plan_cache_runtime_delta", "plan_cache_runtime_receipt" })
        {
            await Execute(connection, transaction, "SAVEPOINT immutable_history;");
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, transaction, $"DELETE FROM events.{relation};"));
            Assert.Equal("55000", error.SqlState);
            await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT immutable_history;");
        }
        await Execute(connection, transaction, "SAVEPOINT invalid_nullable_delta;");
        var nullable = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, transaction, """
            INSERT INTO events.plan_cache_runtime_delta
                (committed_at,run_id,instance_id,target_revision,database_id,database_incarnation,source_epoch,
                 query_fingerprint,plan_fingerprint,cache_entry_fingerprint,source_read_started_at,source_read_completed_at,
                 identity_before_started_at,identity_before_completed_at,identity_after_started_at,identity_after_completed_at,
                 transition,delta_from,delta_to,cpu_us,duration_us,execution_count,logical_reads,writes,rows_processed)
            SELECT committed_at,gen_random_uuid(),instance_id,target_revision,database_id,database_incarnation,source_epoch,
                query_fingerprint,plan_fingerprint,cache_entry_fingerprint,source_read_started_at,source_read_completed_at,
                identity_before_started_at,identity_before_completed_at,identity_after_started_at,identity_after_completed_at,
                'comparable',source_read_completed_at-interval '1 minute',source_read_completed_at,NULL,NULL,NULL,NULL,NULL,NULL
            FROM events.plan_cache_runtime_delta;
            """));
        Assert.Equal("23514", nullable.SqlState);
        await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT invalid_nullable_delta;");
        await transaction.RollbackAsync();

        await using var policy = await connection.BeginTransactionAsync();
        await Execute(connection, policy, """
            SET LOCAL ROLE sqlobserver_server;
            SELECT set_config('sqlobserver.role','SecurityAdministrator',true);
            SELECT set_config('sqlobserver.authorization_scope','global',true);
            """);
        foreach (string dataClass in new[] { "m7_cache_watermarks", "m7_cache_deltas", "m7_cache_receipts" })
        {
            await using var command = new NpgsqlCommand("""
                SELECT system.update_m10_retention_policy(@class,true,interval '31 days',3,1,'cache-storage-test','bounded policy update');
                """, connection, policy);
            command.Parameters.AddWithValue("class", dataClass);
            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }
        await Execute(connection, policy, "SAVEPOINT invalid_retention_floor;");
        var floor = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, policy, """
            SELECT system.update_m10_retention_policy('m7_cache_watermarks',true,interval '7 days',3,2,'cache-storage-test','too short');
            """));
        Assert.Equal("22023", floor.SqlState);
        await Execute(connection, policy, "ROLLBACK TO SAVEPOINT invalid_retention_floor;");
        await policy.RollbackAsync();
    }

    [Fact]
    public async Task PerEntryTransitionsMatchDomainContractAndReceiptsPreservePartialCoverage()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Guid target = Guid.NewGuid(); await Scope(connection, transaction, target);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddMinutes(-3); int step = 0;
        var previous = new Dictionary<string, PlanCacheRuntimeWatermark>(StringComparer.Ordinal);
        await Compare(Row(1)); await Compare(Row(1));
        var precision = Row(1); precision["cpuUs"] = 1002L; await Compare(precision);
        await Compare(precision, Row(2)); // Separate entries for the same query have separate baselines.
        foreach ((string field, int index) in CounterFields
            .Select((field, index) => (field, index)))
        {
            var baseline = Row(10 + index); await Compare(baseline);
            var falling = (JsonObject)baseline.DeepClone(); falling[field] = falling[field]!.GetValue<long>() - 1;
            await Compare(falling);
        }
        foreach (string field in new[] { "databaseIncarnation", "planFingerprint" })
        {
            var changed = Row(1, 100); changed[field] = Digest(88);
            if (field == "planFingerprint") changed["databaseIncarnation"] = Digest(88);
            await Compare(changed);
        }
        var newEntry = Row(90, 200); await Compare(newEntry);
        var epoch = Payload(target, at.AddSeconds(++step), newEntry); SetEpoch(epoch, Digest(99));
        await ComparePayload(epoch);
        // Same-time and older captures record a stale observation without advancing state.
        var stale = Payload(target, ParseTime(epoch["readStartedUtc"]!), Row(90, 500)); SetEpoch(stale, Digest(99));
        await ComparePayload(stale);
        var afterStale = Payload(target, at.AddSeconds(++step), Row(90, 201)); SetEpoch(afterStale, Digest(99));
        await ComparePayload(afterStale);

        var capped = Payload(target, at.AddSeconds(++step), Row(2, 5));
        capped["candidateLimit"] = 1; capped["candidateRows"] = 2; capped["emittedRows"] = 2; capped["sourceExhaustive"] = false;
        await ComparePayload(capped);
        var missing = Payload(target, at.AddSeconds(++step));
        missing["candidateRows"] = 1; missing["emittedRows"] = 1; missing["unqualifiedRows"] = 1;
        int watermarks = previous.Count;
        await ComparePayload(missing);
        Assert.Equal(watermarks, previous.Count);
        Assert.Equal(2L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_runtime_receipt WHERE NOT qualified_exhaustive;"));

        var replay = Payload(target, at.AddSeconds(++step), Row(1, 600)); Guid replayRun = Guid.NewGuid();
        await ComparePayload(replay, replayRun);
        Assert.Equal(0, await Apply(connection, transaction, target, replay, replayRun));
        var divergent = (JsonObject)replay.DeepClone(); ((JsonObject)divergent["items"]![0]!)["cpuUs"] = 9999L;
        await Execute(connection, transaction, "SAVEPOINT divergent_replay;");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Apply(connection, transaction, target, divergent, replayRun));
        Assert.Equal("40001", error.SqlState);
        await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT divergent_replay;");
        await transaction.RollbackAsync();

        async Task Compare(params JsonObject[] rows) => await ComparePayload(Payload(target, at.AddSeconds(++step), rows));
        async Task ComparePayload(JsonObject payload, Guid? suppliedRun = null)
        {
            Guid run = suppliedRun ?? Guid.NewGuid();
            var expected = new Dictionary<string, PlanCacheRuntimeTransition>(StringComparer.Ordinal);
            foreach (JsonObject row in payload["items"]!.AsArray().Cast<JsonObject>())
            {
                var current = Watermark(payload, row);
                string key = current.Key.CacheEntryFingerprint;
                previous.TryGetValue(key, out var old);
                expected.Add(key, PlanCacheRuntimeWatermarkCalculator.Compare(old, current, true));
            }
            Assert.Equal(expected.Count, await Apply(connection, transaction, target, payload, run));
            await using var read = new NpgsqlCommand("""
                SELECT encode(cache_entry_fingerprint,'hex'),transition,delta_from,delta_to,
                    cpu_us,duration_us,execution_count,logical_reads,writes,rows_processed
                FROM events.plan_cache_runtime_delta WHERE run_id=@run;
                """, connection, transaction);
            read.Parameters.AddWithValue("run", run);
            await using var reader = await read.ExecuteReaderAsync(); int seen = 0;
            while (await reader.ReadAsync())
            {
                seen++; var transition = expected[reader.GetString(0)];
                Assert.Equal(TransitionName(transition.Kind), reader.GetString(1));
                if (transition.Delta is { } delta)
                {
                    Assert.Equal(transition.DeltaFromUtc!.Value.UtcDateTime, reader.GetDateTime(2));
                    Assert.Equal(transition.DeltaToUtc!.Value.UtcDateTime, reader.GetDateTime(3));
                    Assert.Equal(new[] { delta.CpuMicroseconds, delta.DurationMicroseconds, delta.Executions,
                        delta.LogicalReads, delta.Writes, delta.Rows }, Enumerable.Range(4, 6).Select(reader.GetInt64).ToArray());
                }
                else Assert.All(Enumerable.Range(2, 8), index => Assert.True(reader.IsDBNull(index)));
                if (transition.NextWatermark is { } next) previous[reader.GetString(0)] = next;
            }
            Assert.Equal(expected.Count, seen);
        }
    }

    [Fact]
    public async Task InvalidOrUnqualifiedFramesCannotPartiallyChangeBaselinesOrPublishDeltas()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Guid target = Guid.NewGuid(); await Scope(connection, transaction, target);
        DateTimeOffset at = DateTimeOffset.UtcNow.AddSeconds(-10);
        Assert.Equal(1, await Apply(connection, transaction, target, Payload(target, at, Row(1))));
        foreach (string invalid in new[] { "unknown_field", "missing_field", "null_counter", "negative_counter", "string_counter",
            "overflow_counter", "system_database", "foreign_target", "foreign_scope", "wrong_revision", "inconsistent_counts",
            "exhaustive_cap", "duplicate_entry", "changed_identity", "changed_epoch", "overlap_before", "overlap_after",
            "expired_bracket", "non_utc", "old_clock", "future_clock" })
        {
            var payload = Payload(target, at.AddSeconds(1), Row(1, 10), Row(2));
            var second = (JsonObject)payload["items"]![1]!;
            switch (invalid)
            {
                case "unknown_field": second["sqlText"] = "unexpected sensitive content"; break;
                case "missing_field": second.Remove("cpuUs"); break;
                case "null_counter": second["cpuUs"] = null; break;
                case "negative_counter": second["cpuUs"] = -1L; break;
                case "string_counter": second["cpuUs"] = "1"; break;
                case "overflow_counter": second["cpuUs"] = JsonNode.Parse("9223372036854775808"); break;
                case "system_database": second["databaseId"] = 4; break;
                case "foreign_target": payload["targetId"] = Guid.NewGuid().ToString(); break;
                case "wrong_revision": payload["targetRevision"] = 2; break;
                case "inconsistent_counts": payload["unqualifiedRows"] = 1; break;
                case "exhaustive_cap": payload["candidateLimit"] = 1; payload["items"]!.AsArray().RemoveAt(1); break;
                case "duplicate_entry": second["cacheEntryFingerprint"] = Digest(1); break;
                case "changed_identity": second["identityAfterIncarnation"] = Digest(100); break;
                case "changed_epoch": second["identityAfterEpoch"] = Digest(100); break;
                case "overlap_before": second["identityBeforeCompletedUtc"] = Time(at.AddSeconds(1.001)); break;
                case "overlap_after": second["identityAfterStartedUtc"] = Time(at.AddSeconds(1.001)); break;
                case "expired_bracket": second["identityBeforeStartedUtc"] = Time(at.AddSeconds(-30)); second["identityBeforeCompletedUtc"] = Time(at.AddSeconds(-29)); break;
                case "non_utc": second["identityAfterCompletedUtc"] = "2026-10-01T12:00:00+00:00"; break;
                case "old_clock": payload = Payload(target, DateTimeOffset.UtcNow.AddMinutes(-6), Row(1, 10), Row(2)); break;
                case "future_clock": payload = Payload(target, DateTimeOffset.UtcNow.AddMinutes(6), Row(1, 10), Row(2)); break;
            }
            await Execute(connection, transaction, "SAVEPOINT rejected_batch;");
            if (invalid == "foreign_scope") await Scope(connection, transaction, Guid.NewGuid());
            var error = await Assert.ThrowsAsync<PostgresException>(() => Apply(connection, transaction, target, payload));
            Assert.Contains(error.SqlState, RejectedSqlStates);
            await Execute(connection, transaction, "ROLLBACK TO SAVEPOINT rejected_batch;");
            Assert.Equal(1L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_runtime_watermark;"));
            Assert.Equal(1001L, await Scalar(connection, transaction, "SELECT cpu_us FROM events.plan_cache_runtime_watermark;"));
            Assert.Equal(1L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_runtime_delta;"));
            Assert.Equal(1L, await Scalar(connection, transaction, "SELECT count(*) FROM events.plan_cache_runtime_receipt;"));
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SevenDayComparisonBoundaryUsesActualCaptureTimesAcrossDailyPartitions()
    {
        await using var database = await Migrate();
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        Guid target = Guid.NewGuid(); await Scope(connection, transaction, target);
        DateTimeOffset currentStarted = ParseTime(JsonValue.Create(Time(DateTimeOffset.UtcNow.AddSeconds(-10)))!);
        foreach (int extraMicroseconds in new[] { 0, 1 })
        {
            int entry = 120 + extraMicroseconds;
            var initial = Payload(target, currentStarted.AddSeconds(-20), Row(entry));
            Assert.Equal(1, await Apply(connection, transaction, target, initial));
            var oldPayload = Payload(target, currentStarted.AddDays(-7).AddTicks(-10 * extraMicroseconds), Row(entry));
            var old = Watermark(oldPayload, (JsonObject)oldPayload["items"]![0]!);
            // A committed historical baseline is state, not a new skewed source read.
            await using (var move = new NpgsqlCommand("""
                UPDATE events.plan_cache_runtime_watermark SET
                    observed_day=date_trunc('day',@completed,'UTC'),source_read_started_at=@started,source_read_completed_at=@completed,
                    identity_before_started_at=@started-interval '20 milliseconds',
                    identity_before_completed_at=@started-interval '10 milliseconds',
                    identity_after_started_at=@started+interval '30 milliseconds',
                    identity_after_completed_at=@started+interval '40 milliseconds'
                WHERE cache_entry_fingerprint=@entry;
                """, connection, transaction))
            {
                move.Parameters.AddWithValue("started", ParseTime(oldPayload["readStartedUtc"]!));
                move.Parameters.AddWithValue("completed", old.ObservedAtUtc);
                move.Parameters.AddWithValue("entry", Convert.FromHexString(Digest(entry)));
                Assert.Equal(1, await move.ExecuteNonQueryAsync());
            }
            var current = Payload(target, currentStarted, Row(entry, 1)); Guid run = Guid.NewGuid();
            var expected = PlanCacheRuntimeWatermarkCalculator.Compare(old, Watermark(current, (JsonObject)current["items"]![0]!), true);
            Assert.Equal(1, await Apply(connection, transaction, target, current, run));
            await using var read = new NpgsqlCommand("SELECT transition,cpu_us,delta_from,delta_to FROM events.plan_cache_runtime_delta WHERE run_id=@run;", connection, transaction);
            read.Parameters.AddWithValue("run", run);
            await using var reader = await read.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(TransitionName(expected.Kind), reader.GetString(0));
            if (extraMicroseconds == 0)
            {
                Assert.Equal("comparable", reader.GetString(0)); Assert.Equal(1L, reader.GetInt64(1));
                Assert.Equal(TimeSpan.FromDays(7), reader.GetDateTime(3) - reader.GetDateTime(2));
            }
            else
            {
                Assert.Equal("comparison_window_expired", reader.GetString(0));
                Assert.True(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); Assert.True(reader.IsDBNull(3));
            }
        }
        await transaction.RollbackAsync();
    }

    private async Task<RepositoryTestDatabase> Migrate(int maximum = MigrationBatchResult.MaximumResults)
    {
        var database = await fixture.CreateDatabaseAsync();
        try
        {
            var result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new MigrationApplyRequest(maximum, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
            Assert.False(result.HasFailures, string.Join(",", result.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
                .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task Scope(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid target)
    {
        await using var command = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator; SELECT set_config('sqlobserver.target_scope',@target,true);", connection, transaction);
        command.Parameters.AddWithValue("target", target.ToString()); await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> Apply(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid target, JsonObject payload, Guid? run = null)
    {
        Guid runId = run ?? Guid.NewGuid();
        await SeedCanonicalRun(connection, transaction, target, runId, ParseTime(payload["readStartedUtc"]!));
        await using var command = new NpgsqlCommand("SELECT events.apply_plan_cache_runtime_batch(@run,@target,1,@payload);", connection, transaction) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("run", runId); command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload.ToJsonString());
        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }

    // Standalone transition tests use real identity anchors rather than orphan
    // run UUIDs. Their synthetic v1 header is not a v2 activation or lease proof.
    private static async Task SeedCanonicalRun(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid target, Guid run, DateTimeOffset started)
    {
        await using var seed = new NpgsqlCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'private cache identity fixture','cache.fixture',1433,interval '5 seconds',
                'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now()) ON CONFLICT DO NOTHING;
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,
                target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,'queries.performance',1,1,1,1,'fixture/cache-identity',gen_random_uuid(),1,
                decode(repeat('aa',32),'hex'),@started,@started) ON CONFLICT DO NOTHING;
            """, connection, transaction);
        seed.Parameters.AddWithValue("target", target); seed.Parameters.AddWithValue("run", run); seed.Parameters.AddWithValue("started", started);
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 30 }; return await command.ExecuteScalarAsync();
    }

    private static string Digest(int value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"cache-test:{value}"))).ToLowerInvariant();
    private static string Time(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(JsonNode node) => DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture);

    private static JsonObject Row(int entry, long increment = 0) => new()
    {
        ["databaseId"] = 6,
        ["databaseIncarnation"] = Digest(51),
        ["queryFingerprint"] = Digest(52),
        ["planFingerprint"] = Digest(53),
        ["cacheEntryFingerprint"] = Digest(entry),
        ["cpuUs"] = 1001L + increment,
        ["durationUs"] = 2002L + increment,
        ["executionCount"] = 7L + increment,
        ["logicalReads"] = 20L + increment,
        ["writes"] = 3L + increment,
        ["rowsProcessed"] = 5L + increment,
    };

    private static JsonObject Payload(Guid target, DateTimeOffset started, params JsonObject[] items)
    {
        var rows = new JsonArray();
        foreach (var source in items)
        {
            var row = (JsonObject)source.DeepClone();
            row["identityBeforeStartedUtc"] = Time(started.AddMilliseconds(-20));
            row["identityBeforeCompletedUtc"] = Time(started.AddMilliseconds(-10));
            row["identityAfterStartedUtc"] = Time(started.AddMilliseconds(30));
            row["identityAfterCompletedUtc"] = Time(started.AddMilliseconds(40));
            row["identityBeforeIncarnation"] = row["databaseIncarnation"]!.GetValue<string>();
            row["identityAfterIncarnation"] = row["databaseIncarnation"]!.GetValue<string>();
            row["identityBeforeEpoch"] = Digest(54); row["identityAfterEpoch"] = Digest(54); rows.Add(row);
        }
        return new()
        {
            ["schemaVersion"] = 1,
            ["targetId"] = target.ToString(),
            ["targetRevision"] = 1,
            ["sourceEpoch"] = Digest(54),
            ["readStartedUtc"] = Time(started),
            ["readCompletedUtc"] = Time(started.AddMilliseconds(20)),
            ["candidateLimit"] = 2000,
            ["candidateRows"] = rows.Count,
            ["unresolvedRows"] = 0,
            ["invisibleDatabaseRows"] = 0,
            ["excludedRows"] = 0,
            ["emittedRows"] = rows.Count,
            ["unqualifiedRows"] = 0,
            ["sourceExhaustive"] = true,
            ["items"] = rows,
        };
    }

    private static PlanCacheRuntimeWatermark Watermark(JsonObject payload, JsonObject row)
    {
        var query = new QueryOpaqueIdentity(row["databaseId"]!.GetValue<int>(), row["queryFingerprint"]!.GetValue<string>());
        return new(new(new MonitoredInstanceId(Guid.Parse(payload["targetId"]!.GetValue<string>())), new(1), query,
            new(query, row["planFingerprint"]!.GetValue<string>()), row["databaseIncarnation"]!.GetValue<string>(),
            payload["sourceEpoch"]!.GetValue<string>(), row["cacheEntryFingerprint"]!.GetValue<string>()),
            new(row["cpuUs"]!.GetValue<long>(), row["durationUs"]!.GetValue<long>(), row["executionCount"]!.GetValue<long>(),
                row["logicalReads"]!.GetValue<long>(), row["writes"]!.GetValue<long>(), row["rowsProcessed"]!.GetValue<long>()),
            ParseTime(payload["readCompletedUtc"]!));
    }

    private static void SetEpoch(JsonObject payload, string epoch)
    {
        payload["sourceEpoch"] = epoch;
        foreach (JsonObject row in payload["items"]!.AsArray().Cast<JsonObject>())
        {
            row["identityBeforeEpoch"] = epoch; row["identityAfterEpoch"] = epoch;
        }
    }

    private static string TransitionName(PlanCacheRuntimeTransitionKind kind) => kind switch
    {
        PlanCacheRuntimeTransitionKind.BaselineUnavailable => "baseline_unavailable",
        PlanCacheRuntimeTransitionKind.Comparable => "comparable",
        PlanCacheRuntimeTransitionKind.Reset => "reset",
        PlanCacheRuntimeTransitionKind.Stale => "stale",
        PlanCacheRuntimeTransitionKind.ComparisonWindowExpired => "comparison_window_expired",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
