using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed partial class SqlMetricAnalyticsSourceTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout MigrationTimeout = new(TimeSpan.FromMinutes(2));
    private static readonly DateTimeOffset Day = new(DateTime.UtcNow.Date.AddDays(-1), TimeSpan.Zero);

    [Fact]
    public async Task UpgradePreservesBothOverviewReadersAndExposesElapsedWeights()
    {
        await using RepositoryTestDatabase database = await Database(291);
        Guid target = await Target(database);
        await Seed(database, target, Sample(Day.AddSeconds(60), 100, 100000));
        Snapshot second = Sample(Day.AddSeconds(90), 3100, 160000);
        Snapshot third = Sample(Day.AddSeconds(180), 4000, 448000);
        await Seed(database, target, second); await Seed(database, target, third);
        DateTimeOffset from = Day.AddSeconds(61), to = Day.AddMinutes(4);
        string legacyBefore = await Overview(database, target, from, to, false);
        string partitionedBefore = await Overview(database, target, from, to, true);
        Assert.Equal(legacyBefore, partitionedBefore);
        MigrationBatchResult upgrade = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, MigrationTimeout), default);
        Assert.False(upgrade.HasFailures);
        Assert.Equal(292, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(legacyBefore, await Overview(database, target, from, to, false));
        Assert.Equal(partitionedBefore, await Overview(database, target, from, to, true));

        Input[] rows = await Inputs(database, target, from, to);
        Assert.Equal(14, rows.Length);
        Assert.Equal(MetricCatalogV2.All.Where(definition => definition.Source == AnalyticsMetricSource.SqlEngine)
            .Select(definition => definition.Metric.Key).Order(StringComparer.Ordinal), rows.Select(row => row.Key).Distinct().Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.True(row.Complete));
        Assert.All(rows.Where(row => row.Key == "engine.user_connections"), row => Assert.Equal(1, row.Weight));
        Input[] batches = rows.Where(row => row.Key == "engine.batch_requests_per_second").ToArray();
        Assert.Equal(100, batches[0].Value); Assert.Equal(30, batches[0].Weight);
        Assert.Equal(10, batches[1].Value); Assert.Equal(90, batches[1].Weight);
        Assert.Equal(32.5, batches.Sum(row => row.Value!.Value * row.Weight!.Value) / batches.Sum(row => row.Weight!.Value));
        Input[] cpu = rows.Where(row => row.Key == "engine.sql_scheduler_cpu_percent").ToArray();
        Assert.Equal(50, cpu[0].Value); Assert.Equal(80, cpu[1].Value);
        Assert.Equal(72.5, cpu.Sum(row => row.Value!.Value * row.Weight!.Value) / cpu.Sum(row => row.Weight!.Value));
        Assert.Equal(second.Run, batches[0].Run); Assert.Equal(third.Run, batches[1].Run);

        // The same inputs are available on both physical run stores without
        // changing the persisted fleet rollout switches.
        await SetInputRoute(database, true);
        Assert.Equal(rows, await Inputs(database, target, from, to));
        Assert.False((await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(1, MigrationTimeout), default)).HasFailures);
    }

    [Fact]
    public async Task InvalidDeltaInputsRemainGapsAndIndependentMetricsStillWork()
    {
        await using RepositoryTestDatabase database = await Database();
        var cases = new (string Name, double Seconds, double? PriorBatch, double? Batch,
            double? PriorCpu, double? Cpu, double? PriorEpoch, double? Epoch,
            double? PriorSchedulers, double? Schedulers, double? ExpectedBatch, double? ExpectedCpu)[]
        {
            ("ordinary", 60, 1000, 1600, 100000, 220000, 1, 1, 4, 4, 10, 50),
            ("restart", 60, 1000, 1600, 100000, 220000, 1, 2, 4, 4, null, null),
            ("unknown epoch", 60, 1000, 1600, 100000, 220000, null, null, 4, 4, null, null),
            ("batch reset", 60, 1000, 10, 100000, 220000, 1, 1, 4, 4, null, 50),
            ("cpu reset", 60, 1000, 1600, 100000, 10, 1, 1, 4, 4, 10, null),
            ("scheduler change", 60, 1000, 1600, 100000, 220000, 1, 1, 4, 5, 10, null),
            ("zero schedulers", 60, 1000, 1600, 100000, 220000, 1, 1, 0, 0, 10, null),
            ("missing cpu", 60, 1000, 1600, 100000, null, 1, 1, 4, 4, 10, null),
            ("missing batch", 60, 1000, null, 100000, 220000, 1, 1, 4, 4, null, 50),
            ("two minutes", 120, 1000, 2200, 100000, 340000, 1, 1, 4, 4, 10, 50),
            ("past two minutes", 120.000001, 1000, 2200, 100000, 340000, 1, 1, 4, 4, null, null),
            ("rounded batch", 60, 9007199254740900, 9007199254740992, 100000, 220000, 1, 1, 4, 4, null, 50),
            ("rounded cpu", 60, 1000, 1600, 9007199254740900, 9007199254740992, 1, 1, 4, 4, 10, null),
            ("cpu tolerance", 60, 1000, 1600, 100000, 352000, 1, 1, 4, 4, 10, 100),
            ("impossible cpu", 60, 1000, 1600, 100000, 352001, 1, 1, 4, 4, 10, null),
            ("unchanged", 60, 1000, 1000, 100000, 100000, 1, 1, 4, 4, 0, 0),
        };
        foreach (var test in cases)
        {
            Guid target = await Target(database);
            Snapshot prior = Sample(Day.AddMinutes(10), test.PriorBatch, test.PriorCpu)
                with { Epoch = test.PriorEpoch, Schedulers = test.PriorSchedulers };
            Snapshot current = Sample(prior.At.AddTicks((long)Math.Round(test.Seconds * TimeSpan.TicksPerSecond)), test.Batch, test.Cpu)
                with { Epoch = test.Epoch, Schedulers = test.Schedulers };
            await Seed(database, target, prior); await Seed(database, target, current);
            Input[] rows = await Inputs(database, target, current.At, current.At.AddSeconds(1));
            Assert.Equal(7, rows.Length);
            Input batch = Assert.Single(rows, row => row.Key == "engine.batch_requests_per_second");
            Input cpu = Assert.Single(rows, row => row.Key == "engine.sql_scheduler_cpu_percent");
            Assert.True(test.ExpectedBatch == batch.Value, test.Name + ": batch");
            Assert.True(test.ExpectedCpu == cpu.Value, test.Name + ": CPU");
            Assert.Equal(test.ExpectedBatch.HasValue, batch.Complete);
            Assert.Equal(test.ExpectedCpu.HasValue, cpu.Complete);
            Assert.All(rows, row => Assert.Equal(current.Run, row.Run));
        }
    }

    [Fact]
    public async Task SnapshotWindowAndRunEligibilityExcludeLateForeignAndFailedEvidence()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database), other = await Target(database);
        Snapshot good = Sample(Day.AddMinutes(2), 1000, 100000) with { Memory = null };
        await Seed(database, target, good);
        DateTimeOffset cutoff = Day.AddMinutes(4), from = Day.AddMinutes(1), to = cutoff;
        await Seed(database, target, Sample(Day.AddMinutes(3), 1600, 220000) with { CollectedAt = cutoff.AddSeconds(1) });
        await Seed(database, target, Sample(Day.AddMinutes(3), 1600, 220000) with { CompletedAt = cutoff.AddSeconds(1) });
        await Seed(database, target, Sample(Day.AddMinutes(3), 1600, 220000) with { Success = false });
        await Seed(database, target, Sample(Day.AddMinutes(3), 1600, 220000) with { Revision = 2 });
        await Seed(database, target, Sample(Day.AddMinutes(3), 1600, 220000) with { Collector = "waits.server" });
        await Seed(database, target, Sample(Day.AddMinutes(3), 1600, 220000) with { Dimensions = "{\"database\":\"other\"}" });
        await Seed(database, target, Sample(to, 1600, 220000));
        await Seed(database, other, Sample(good.At, 1600, 220000));
        Input[] rows = await Inputs(database, target, from, to, cutoff: cutoff);
        Assert.Equal(7, rows.Length); Assert.All(rows, row => Assert.Equal(good.Run, row.Run));
        Input memory = Assert.Single(rows, row => row.Key == "engine.process_physical_memory_bytes");
        Assert.Null(memory.Value); Assert.False(memory.Complete); Assert.Equal(1, memory.Weight);
        Assert.Null(Assert.Single(rows, row => row.Key == "engine.batch_requests_per_second").Value);
        Assert.Single(await Inputs(database, target, from, to, "engine.user_connections", cutoff: cutoff));
        await SetInputRoute(database, true);
        Assert.Equal(rows, await Inputs(database, target, from, to, cutoff: cutoff));
    }

    [Fact]
    public async Task BoundsFailExplicitlyInsteadOfReturningATruncatedSuccess()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database);
        await Seed(database, target, Sample(Day.AddMinutes(2), 1000, 100000));
        DateTimeOffset from = Day.AddMinutes(1), to = Day.AddMinutes(3);
        Assert.Equal(7, (await Inputs(database, target, from, to, limit: 7)).Length);
        int exactBytes = await InputBytes(database, target, from, to);
        Assert.Equal(7, (await Inputs(database, target, from, to, bytes: exactBytes)).Length);
        Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(() =>
            Inputs(database, target, from, to, bytes: exactBytes - 1))).SqlState);
        foreach (var read in new Func<Task<Input[]>>[]
        {
            () => Inputs(database, target, from, to, limit: 6),
            () => Inputs(database, target, from, to, bytes: 1),
            () => Inputs(database, target, from, to, limit: 0),
            () => Inputs(database, target, from, to, limit: 100001),
            () => Inputs(database, target, from, to, bytes: 8388609),
            () => Inputs(database, target, from, to, "engine.batch_requests_total"),
            () => Inputs(database, target, from, to, "host.cpu.percent"),
            () => Inputs(database, target, from, from.AddDays(1).AddTicks(10)),
            () => Inputs(database, target, to, from),
            () => Inputs(database, target, from, to, cutoff: to.AddTicks(-10)),
        }) Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(read)).SqlState);
        Assert.Empty(await Inputs(database, target, from.AddHours(1), to.AddHours(1)));
    }

    [Fact]
    public async Task DuplicateTimestampsCannotCreateRatesAndNextDeltaUsesTheLastRun()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database);
        DateTimeOffset at = Day.AddMinutes(10);
        Snapshot first = Sample(at, 1000, 100000) with { Run = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        Snapshot second = Sample(at, 1600, 220000) with { Run = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        Snapshot third = Sample(at.AddSeconds(30), 1900, 280000) with { Run = Guid.Parse("00000000-0000-0000-0000-000000000003") };
        await Seed(database, target, first); await Seed(database, target, second); await Seed(database, target, third);
        Input[] rows = await Inputs(database, target, at, at.AddMinutes(1));
        Assert.Equal(21, rows.Length);
        Assert.All(rows.Where(row => row.Key is "engine.batch_requests_per_second" or "engine.sql_scheduler_cpu_percent" && row.Run != third.Run), row =>
        { Assert.Null(row.Value); Assert.False(row.Complete); });
        Assert.Equal(10, Assert.Single(rows, row => row.Run == third.Run && row.Key == "engine.batch_requests_per_second").Value);
        Assert.Equal(50, Assert.Single(rows, row => row.Run == third.Run && row.Key == "engine.sql_scheduler_cpu_percent").Value);
        await SetInputRoute(database, true);
        Assert.Equal(rows, await Inputs(database, target, at, at.AddMinutes(1)));
    }

    [Fact]
    public async Task CollectorCannotReadAnotherTargetOrBypassThePrivateSourceAndRls()
    {
        await using RepositoryTestDatabase database = await Database();
        Guid target = await Target(database), other = await Target(database);
        await Seed(database, target, Sample(Day.AddMinutes(2), 1000, 100000));
        DateTimeOffset from = Day.AddMinutes(1), to = Day.AddMinutes(3);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() =>
            Inputs(database, target, from, to, scope: other))).SqlState);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(() =>
            Inputs(database, target, from, to, revision: 2))).SqlState);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await using var direct = new NpgsqlCommand("""
            SELECT * FROM reporting.sql_core_metric_inputs(@target,1,@from,@to,@to,false);
            """, connection);
        direct.Parameters.AddWithValue("target", target); direct.Parameters.AddWithValue("from", from); direct.Parameters.AddWithValue("to", to);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync())).SqlState);
        direct.CommandText = "SELECT * FROM telemetry.raw_metric_sample;";
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => direct.ExecuteNonQueryAsync())).SqlState);
        await using var privileges = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator' AND p.prosecdef
             AND has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
             AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
             AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE')
             AND NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid='reporting.read_sql_metric_analytics_inputs(uuid,bigint,timestamptz,timestamptz,timestamptz,text,integer,integer)'::regprocedure;
            """);
        Assert.Equal(true, await privileges.ExecuteScalarAsync());
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using var denied = server.CreateCommand("""
            SELECT * FROM reporting.read_sql_metric_analytics_inputs(@target,1,@from,@to,@to,NULL,100,8388608);
            """);
        denied.Parameters.AddWithValue("target", target); denied.Parameters.AddWithValue("from", from); denied.Parameters.AddWithValue("to", to);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync())).SqlState);
    }

    private async Task<RepositoryTestDatabase> Database(int count = MigrationBatchResult.MaximumResults)
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(count, MigrationTimeout), default);
        Assert.False(result.HasFailures, result.Results.FirstOrDefault(row => row.Outcome == MigrationOutcome.Failed)?.FailureCode);
        return database;
    }

    private static async Task<Guid> Target(RepositoryTestDatabase database)
    {
        Guid target = Guid.NewGuid();
        await using var command = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,
             connect_timeout,authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@target::text,'SQL analytics target','sql01',1433,interval '5 seconds',
             'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
            """);
        command.Parameters.AddWithValue("target", target); await command.ExecuteNonQueryAsync(); return target;
    }

    private sealed record Snapshot(Guid Run, DateTimeOffset At, double? Batch, double? Cpu)
    {
        public double? Epoch { get; init; } = 1;
        public double? Schedulers { get; init; } = 4;
        public double? Memory { get; init; } = 1073741824;
        public DateTimeOffset? CollectedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public long Revision { get; init; } = 1;
        public bool Success { get; init; } = true;
        public string Collector { get; init; } = "engine.core";
        public string Dimensions { get; init; } = "{}";
    }

    private static Snapshot Sample(DateTimeOffset at, double? batch, double? cpu) => new(Guid.NewGuid(), at, batch, cpu);
    private sealed record Input(Guid Run, DateTimeOffset At, string Key, double? Value, double? Weight, bool Complete);

    private static async Task Seed(RepositoryTestDatabase database, Guid target, Snapshot sample)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT control.ensure_daily_metric_partition((@at AT TIME ZONE 'UTC')::date);
            INSERT INTO telemetry.collection_run(run_id,instance_id,collector_id,collector_version,output_schema_version,
             target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES(@run,@target,@collector,1,1,@revision,1,'test/sql-analytics',gen_random_uuid(),1,
             decode(repeat('aa',32),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome(run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
             source_row_count,output_item_count,inserted_item_count,duplicate_item_count,rejected_item_count,
             response_bytes,output_bytes,persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,
             lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES(@run,CASE WHEN @success THEN 'succeeded' ELSE 'transient_failure' END,
             CASE WHEN @success THEN 'completed' ELSE 'transient_target_failure' END,1,0,10,9,9,9,0,0,
             128,128,128,false,false,'none',true,0,0,decode(repeat('bb',32),'hex'),@completed);
            INSERT INTO telemetry.raw_metric_sample(observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
            SELECT @at,gen_random_uuid(),@target,metric.key,metric.value,@dimensions::jsonb,@collected,@run
            FROM (VALUES ('engine.user_connections',7::double precision),('engine.process_physical_memory_bytes',@memory),
             ('engine.os_available_memory_bytes',2147483648),('engine.memory_grants_pending',2),('engine.scheduler_runnable_tasks',3),
             ('engine.start_time_key',@epoch),('engine.visible_scheduler_count',@schedulers),
             ('engine.batch_requests_total',@batch),('engine.scheduler_cpu_milliseconds_total',@cpu)) metric(key,value)
            WHERE metric.value IS NOT NULL;
            """);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("run", sample.Run);
        command.Parameters.AddWithValue("at", sample.At); command.Parameters.AddWithValue("collected", sample.CollectedAt ?? sample.At);
        command.Parameters.AddWithValue("completed", sample.CompletedAt ?? sample.At); command.Parameters.AddWithValue("success", sample.Success);
        command.Parameters.AddWithValue("collector", sample.Collector); command.Parameters.AddWithValue("revision", sample.Revision);
        command.Parameters.AddWithValue("dimensions", sample.Dimensions);
        foreach ((string name, double? value) in new[] { ("memory", sample.Memory), ("epoch", sample.Epoch), ("schedulers", sample.Schedulers), ("batch", sample.Batch), ("cpu", sample.Cpu) })
            command.Parameters.AddWithValue(name, NpgsqlDbType.Double, (object?)value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Input[]> Inputs(RepositoryTestDatabase database, Guid target, DateTimeOffset from, DateTimeOffset to,
        string? key = null, int limit = 100000, int bytes = 8388608, DateTimeOffset? cutoff = null, Guid? scope = null, long revision = 1)
    {
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await using (var setScope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection))
        { setScope.Parameters.AddWithValue("scope", (scope ?? target).ToString("D")); await setScope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("""
            SELECT * FROM reporting.read_sql_metric_analytics_inputs(@target,@revision,@from,@to,@cutoff,@key,@limit,@bytes);
            """, connection);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to); command.Parameters.AddWithValue("cutoff", cutoff ?? to);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Text, (object?)key ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", limit); command.Parameters.AddWithValue("bytes", bytes);
        var result = new List<Input>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(new Input(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.IsDBNull(4) ? null : reader.GetDouble(4), reader.GetBoolean(5)));
        return result.ToArray();
    }

    private static async Task<string> Overview(RepositoryTestDatabase database, Guid target, DateTimeOffset from, DateTimeOffset to, bool partitioned)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target::text,false),set_config('sqlobserver.role','Viewer',false);", connection))
        { scope.Parameters.AddWithValue("target", target); await scope.ExecuteNonQueryAsync(); }
        string sql = partitioned
            ? "SELECT jsonb_agg(to_jsonb(v) ORDER BY v.metric_key,v.bucket_at)::text FROM reporting.overview_workload_history_v2_shadow(@target,1,@from,@to,@to) v;"
            : "SELECT jsonb_agg(to_jsonb(v) ORDER BY v.metric_key,v.bucket_at)::text FROM reporting.overview_workload_history(@target,1,@from,@to,@to) v;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task SetInputRoute(RepositoryTestDatabase database, bool partitioned)
    {
        // Fixture-only route selection; the production switch retains its
        // existing fenced fleet parity requirement.
        await using var command = database.DataSource.CreateCommand("UPDATE system.collection_run_v2_m10_read_route SET use_v2=@partitioned WHERE singleton;");
        command.Parameters.AddWithValue("partitioned", partitioned); await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> InputBytes(RepositoryTestDatabase database, Guid target, DateTimeOffset from, DateTimeOffset to)
    {
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@target::text,false);", connection))
        { scope.Parameters.AddWithValue("target", target); await scope.ExecuteNonQueryAsync(); }
        await using var command = new NpgsqlCommand("""
            SELECT sum(octet_length(row_to_json(v)::text))::integer
            FROM reporting.read_sql_metric_analytics_inputs(@target,1,@from,@to,@to,NULL,100000,8388608) v;
            """, connection);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("from", from); command.Parameters.AddWithValue("to", to);
        return Assert.IsType<int>(await command.ExecuteScalarAsync());
    }
}
