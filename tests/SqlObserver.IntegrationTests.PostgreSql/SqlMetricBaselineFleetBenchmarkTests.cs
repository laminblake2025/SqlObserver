using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector;
using SqlObserver.Domain.Analytics;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class SqlMetricAnalyticsSourceTests
{
    [Fact]
    [Trait("Category", "RequiresFleetBenchmark")]
    public async Task SqlBaselineFleetBenchmarkDrainsEveryTargetAndPreservesFullAndPartialHistory()
    {
        const int targets = 129, keys = 7, jobsExpected = targets * keys;
        await using RepositoryTestDatabase database = await Database();
        Guid[] ids = Enumerable.Range(0, targets).Select(_ => Guid.NewGuid()).ToArray();
        string[] metrics = MetricCatalogV2.All.Where(d => d.Source == AnalyticsMetricSource.SqlEngine)
            .Select(d => d.Metric.Key).Order(StringComparer.Ordinal).ToArray(); Assert.Equal(keys, metrics.Length);
        var seedTimer = Stopwatch.StartNew();
        await using (var seed = database.DataSource.CreateCommand("""
            CREATE TABLE public.sql_baseline_fleet_targets(ordinal integer PRIMARY KEY,instance_id uuid UNIQUE NOT NULL);
            INSERT INTO public.sql_baseline_fleet_targets SELECT ordinal::integer,id FROM unnest(@ids::uuid[]) WITH ORDINALITY AS u(id,ordinal);
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            SELECT instance_id,instance_id::text,'SQL baseline fleet target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now() FROM public.sql_baseline_fleet_targets;
            DO $$ DECLARE d date; BEGIN
             FOR d IN SELECT generate_series((date_trunc('day',clock_timestamp())-interval '28 days')::date,
               (date_trunc('day',clock_timestamp())-interval '1 day')::date,interval '1 day')::date LOOP
              EXECUTE format('CREATE TABLE IF NOT EXISTS analytics.%I PARTITION OF analytics.metric_rollup_v2 FOR VALUES FROM (%L) TO (%L)',
                'metric_rollup_v2_p'||to_char(d,'YYYYMMDD'),d::timestamptz,(d+1)::timestamptz);
             END LOOP;
            END $$;
            INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,
              dimension_hash,generation,sample_count,expected_count,value,visibility_state,computed_at,source_cutoff_utc,
              catalog_version,algorithm_version,catalog_checksum,last_value,weight_sum,weighted_value_sum,minimum_value,maximum_value,sample_value_sum)
            SELECT h,t.instance_id,1,'hour',m.key,'avg',sha256(convert_to('{}','UTF8')),1,120,120,v.mean,'complete',@to-interval '1 second',
              h+interval '1 hour',2,'sql-rollup-v2',@checksum,v.mean+1,w.weight,v.mean*w.weight,v.mean-1,v.mean+1,v.mean*120
            FROM public.sql_baseline_fleet_targets t CROSS JOIN unnest(@metrics::text[]) WITH ORDINALITY m(key,ordinal)
            CROSS JOIN generate_series(@from,@to-interval '1 hour',interval '1 hour') h
            CROSS JOIN LATERAL (SELECT (20+t.ordinal::double precision/4+m.ordinal+floor(extract(epoch FROM h-@from)/86400)/2)::double precision AS mean) v
            CROSS JOIN LATERAL (SELECT CASE WHEN m.key IN ('engine.batch_requests_per_second','engine.sql_scheduler_cpu_percent')
              THEN 3600::double precision ELSE 120::double precision END AS weight) w
            WHERE t.ordinal<129 OR h>=@to-interval '7 days';
            ANALYZE analytics.metric_rollup_v2;
            """))
        {
            seed.CommandTimeout = 120; seed.Parameters.AddWithValue("ids", ids); seed.Parameters.AddWithValue("metrics", metrics);
            seed.Parameters.AddWithValue("from", BaselineEnd.AddDays(-28)); seed.Parameters.AddWithValue("to", BaselineEnd);
            seed.Parameters.AddWithValue("checksum", MetricCatalogV2.Checksum); await seed.ExecuteNonQueryAsync();
        }
        seedTimer.Stop();
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource(); var repository = RollupRepository(collector);
        WorkerLeaseIdentity lease = await RollupLease(collector); var scheduleTimer = Stopwatch.StartNew();
        await using (var schedule = collector.CreateCommand("SELECT control.schedule_sql_metric_baseline_jobs(@owner,@fence);"))
        {
            schedule.Parameters.AddWithValue("owner", lease.Owner.Value); schedule.Parameters.AddWithValue("fence", lease.FencingToken.Value);
            Assert.Equal(896, await schedule.ExecuteScalarAsync()); Assert.Equal(7, await schedule.ExecuteScalarAsync());
            Assert.Equal(0, await schedule.ExecuteScalarAsync());
        }
        scheduleTimer.Stop();
        await new PostgreSqlWorkerLeasePort(collector).ReleaseAsync(new(lease, RollupTimeout), default);
        await using (var freeze = database.DataSource.CreateCommand("UPDATE control.collector_schedule SET enabled=false WHERE collector_id='engine.core';"))
            await freeze.ExecuteNonQueryAsync();
        using var process = Process.GetCurrentProcess(); long allocationStart = GC.GetTotalAllocatedBytes();
        long peakWorkingSet = process.WorkingSet64; int maximumRunning = 0;
        using var worker = new AnalyticsDerivationWorker(repository, new PostgreSqlWorkerLeasePort(collector),
            new WorkerExecutionId(Guid.NewGuid()), repository, NullLogger<AnalyticsDerivationWorker>.Instance);
        var workTimer = Stopwatch.StartNew(); await worker.StartAsync(default);
        try
        {
            while (workTimer.Elapsed < TimeSpan.FromMinutes(4))
            {
                (long completed, long running, long failed) = await Counts();
                maximumRunning = Math.Max(maximumRunning, (int)running); process.Refresh(); peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
                Assert.Equal(0, failed); Assert.InRange(running, 0, 2);
                if (completed == jobsExpected) break;
                await Task.Delay(250);
            }
            (long completed, long running, long failed) final = await Counts();
            Assert.Equal(jobsExpected, final.completed); Assert.Equal(0, final.running); Assert.Equal(0, final.failed);
        }
        finally { workTimer.Stop(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await worker.StopAsync(stop.Token); }
        long allocations = GC.GetTotalAllocatedBytes() - allocationStart;
        await using (var coverage = database.DataSource.CreateCommand("""
            SELECT count(*),count(DISTINCT instance_id),count(*) FILTER(WHERE result->>'visibilityState'='complete'),
              count(*) FILTER(WHERE result->>'visibilityState'='partial'),
              bool_and((result->>'completeDays')::integer=CASE WHEN t.ordinal=129 THEN 7 ELSE 28 END
                AND (result->>'sampleCount')::integer=CASE WHEN t.ordinal=129 THEN 1 ELSE 4 END
                AND (result->>'coverage')::double precision=CASE WHEN t.ordinal=129 THEN .25 ELSE 1 END
                AND (result->>'confidence')::double precision=CASE WHEN t.ordinal=129 THEN .03125 ELSE .5 END),
              NOT EXISTS(SELECT 1 FROM analytics.sql_metric_baseline GROUP BY instance_id,metric_key HAVING count(*)<>168 OR count(DISTINCT hour_of_week)<>168)
            FROM analytics.sql_metric_baseline b JOIN public.sql_baseline_fleet_targets t USING(instance_id);
            """))
        {
            await using NpgsqlDataReader rows = await coverage.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync());
            Assert.Equal(jobsExpected * 168L, rows.GetInt64(0)); Assert.Equal(targets, rows.GetInt64(1));
            Assert.Equal(128 * keys * 168L, rows.GetInt64(2)); Assert.Equal(keys * 168L, rows.GetInt64(3));
            Assert.True(rows.GetBoolean(4)); Assert.True(rows.GetBoolean(5));
        }
        await using (var statistics = database.DataSource.CreateCommand("""
            SELECT bool_and(abs((result->>'median')::double precision-expected.mean)<1e-8
                AND abs((result->>'mad')::double precision-CASE WHEN t.ordinal=129 THEN 0 ELSE 3.5 END)<1e-8
                AND abs((result->>'p10')::double precision-(expected.mean-CASE WHEN t.ordinal=129 THEN 0 ELSE 4.2 END))<1e-8
                AND abs((result->>'p90')::double precision-(expected.mean+CASE WHEN t.ordinal=129 THEN 0 ELSE 4.2 END))<1e-8)
            FROM analytics.sql_metric_baseline b JOIN public.sql_baseline_fleet_targets t USING(instance_id)
            JOIN unnest(@metrics::text[]) WITH ORDINALITY m(key,ordinal) ON m.key=b.metric_key
            CROSS JOIN LATERAL(SELECT 20+t.ordinal::double precision/4+m.ordinal
              +((b.hour_of_week/24-extract(dow FROM @from)::integer+7)%7)::double precision/2
              +CASE WHEN t.ordinal=129 THEN 10.5 ELSE 5.25 END AS mean) expected;
            """))
        {
            statistics.Parameters.AddWithValue("metrics", metrics); statistics.Parameters.AddWithValue("from", BaselineEnd.AddDays(-28));
            Assert.Equal(true, await statistics.ExecuteScalarAsync());
        }
        await using var sizes = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM analytics.metric_rollup_v2),
              (SELECT sum(pg_total_relation_size(relid)) FROM pg_partition_tree('analytics.metric_rollup_v2') WHERE isleaf),
              (SELECT sum(pg_total_relation_size(relid)) FROM pg_partition_tree('analytics.sql_metric_baseline') WHERE isleaf);
            """);
        await using NpgsqlDataReader sizeRows = await sizes.ExecuteReaderAsync(); Assert.True(await sizeRows.ReadAsync());
        Assert.Equal(603288L, sizeRows.GetInt64(0));
        output.WriteLine(JsonSerializer.Serialize(new
        {
            workload = "sql-baseline-v2-synthetic-hourly-history", targets, metrics = keys, windowDays = 28, partialTargetDays = 7,
            inputRows = sizeRows.GetInt64(0), jobs = jobsExpected, resultRows = jobsExpected * 168,
            seedSeconds = seedTimer.Elapsed.TotalSeconds, scheduleSeconds = scheduleTimer.Elapsed.TotalSeconds,
            workerSeconds = workTimer.Elapsed.TotalSeconds, jobsPerSecond = jobsExpected / workTimer.Elapsed.TotalSeconds,
            maximumObservedRunning = maximumRunning, allocatedBytes = allocations, peakWorkingSetBytes = peakWorkingSet,
            inputStorageBytes = sizeRows.GetDecimal(1), baselineStorageBytes = sizeRows.GetDecimal(2),
            logicalProcessors = Environment.ProcessorCount, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        }));

        async Task<(long Completed, long Running, long Failed)> Counts()
        {
            await using var count = database.DataSource.CreateCommand("""
                SELECT count(*) FILTER(WHERE status='succeeded'),count(*) FILTER(WHERE status='running'),
                  count(*) FILTER(WHERE status IN ('failed','partial')) FROM control.analytics_job WHERE source_catalog_version=2 AND job_kind='baseline';
                """);
            await using NpgsqlDataReader rows = await count.ExecuteReaderAsync(); Assert.True(await rows.ReadAsync());
            return (rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2));
        }
    }
}
