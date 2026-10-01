using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class QueryPerformanceV2ManifestTests
{
    internal static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    internal const string ManifestDigest = "1e6a93c2771abea9beb682b177a3904ebca1cfb49c3e2a2b8772a6acd5e00ee3";
    internal const string BundleDigest = "2674c820b6b5e2ff6c2485e06b313b4b11eed446b216ca2cfa8e09c9ea310c4c";

    [Fact]
    public void StagedManifestPinsBothSourcesAndPreservesProtectedSidecarsWithoutActivation()
    {
        byte[] manifest = File.ReadAllBytes(Path.Combine(Root, "collectors/manifests/queries.performance.v2.json"));
        byte[] index = File.ReadAllBytes(Path.Combine(Root, "collectors/manifests/m7-query-performance-v2.assets.sha256"));
        Assert.Equal(ManifestDigest, Convert.ToHexStringLower(SHA256.HashData(manifest)));
        Assert.Equal(BundleDigest, Convert.ToHexStringLower(SHA256.HashData(index)));
        string[] lines = Encoding.UTF8.GetString(index).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        foreach (string line in lines)
        {
            string[] fields = line.Split("  ", StringSplitOptions.None);
            Assert.Equal(2, fields.Length);
            string file = Path.GetFullPath(fields[1], Root);
            Assert.StartsWith(Root, file, StringComparison.OrdinalIgnoreCase);
            byte[] content = File.ReadAllBytes(file);
            Assert.DoesNotContain((byte)'\r', content);
            Assert.Equal(fields[0], Convert.ToHexStringLower(SHA256.HashData(content)));
        }
        _ = SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded();
        _ = PlanCacheRuntimeSourceBundle.LoadEmbedded();
        _ = PlanCacheDatabaseIdentitySource.LoadEmbedded();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "collectors/manifests/queries.performance.v2.schema.json")));
        JsonSchema validator = JsonSchema.Build(schema.RootElement.Clone());
        using JsonDocument document = JsonDocument.Parse(manifest);
        Assert.True(validator.Evaluate(document.RootElement).IsValid);
        Assert.False(document.RootElement.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("collectorVersion").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("outputSchemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("queryResources").GetProperty("cacheRuntime").GetProperty("queryStorePreferred").GetBoolean());
        foreach (string field in new[] { "defaultEnabled", "collectorVersion", "outputSchemaVersion", "unexpected" })
        {
            var changed = Assert.IsType<JsonObject>(JsonNode.Parse(manifest));
            changed[field] = field == "defaultEnabled" ? JsonValue.Create(true) : JsonValue.Create(1);
            Assert.False(validator.Evaluate(JsonSerializer.SerializeToElement(changed)).IsValid);
        }
    }
}

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class QueryPerformanceV2CommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private static readonly char[] CacheEntries = ['e', 'f'];
    private static readonly string[] JsonArguments = ["query", "cache", "text", "plan", "waits"];
    private const string CommitSql = """
        SELECT * FROM control.commit_query_performance_v2(@run,@target,@revision,@schedule,@scheduled,@key,@owner,@fence,
            @request,@outcome,@reason,@duration,@attempts,@source_rows,@output_items,@response_bytes,@output_bytes,
            @loss,@lost_items,@exact,@lost_bytes,@circuit,@failures,@completion,@from,@to,
            @source::events.query_performance_source,@state,@coverage,@fresh,@truncated,@query,@cache,@text,@plan,@waits);
        """;

    [Fact]
    public async Task QueryStoreAndCacheCommitTogetherWithExactReplayComparisonAndV3Routing()
    {
        await using Scene scene = await Prepare();
        Frame first = await Start(scene);
        Dictionary<string, object> firstParameters = await WithSidecars(scene, first);
        Assert.Equal(("committed", 3), await Commit(scene, first, firstParameters));
        string committed = await Snapshot(scene);
        Assert.Equal(("replayed", 0), await Commit(scene, first, firstParameters));
        Assert.Equal(committed, await Snapshot(scene));
        await Reject(scene, first, "40001", parameters => parameters["circuit"] = "open", original: firstParameters);
        await Reject(scene, first, "40001", parameters => parameters["duration"] = 11L, original: firstParameters);
        await Reject(scene, first, "40001", parameters => parameters["text"] = "[{}]", original: firstParameters);
        await using (var evidence = scene.Database.DataSource.CreateCommand("""
            SELECT output_item_count,inserted_item_count,duplicate_item_count,
                (SELECT count(*) FROM events.query_performance_observation WHERE collection_run_id=@run),
                (SELECT count(*) FROM events.plan_cache_runtime_delta WHERE run_id=@run),
                (SELECT count(*) FROM events.plan_cache_runtime_receipt WHERE run_id=@run),
                (SELECT active_run_id IS NULL AND last_outcome='succeeded' FROM control.collector_schedule
                    WHERE instance_id=@target AND collector_id='queries.performance')
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """))
        {
            evidence.Parameters.AddWithValue("run", first.Run.Value); evidence.Parameters.AddWithValue("target", scene.Target);
            await using var reader = await evidence.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            Assert.Equal(3, reader.GetInt32(0)); Assert.Equal(3, reader.GetInt32(1)); Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(1L, reader.GetInt64(3)); Assert.Equal(2L, reader.GetInt64(4)); Assert.Equal(1L, reader.GetInt64(5));
            Assert.True(reader.GetBoolean(6));
        }
        await using (var links = scene.Database.DataSource.CreateCommand("""
            SELECT (SELECT count(*)=1 AND bool_and(content_available) FROM events.query_performance_content_link WHERE collection_run_id=@run),
                (SELECT count(*)=1 AND bool_and(content_available) FROM events.query_performance_plan_content_link WHERE collection_run_id=@run),
                (SELECT categories='[{"category":1,"waitMilliseconds":17}]'::jsonb FROM events.query_performance_wait_snapshot WHERE collection_run_id=@run);
            """))
        {
            links.Parameters.AddWithValue("run", first.Run.Value); await using var reader = await links.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); for (int index = 0; index < 3; index++) Assert.True(reader.GetBoolean(index));
        }
        await Sql(scene, """
            SET ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.target_scope',@target::text,false);
            SELECT complete FROM control.backfill_collection_run_v2(@target,100);
            SELECT complete FROM control.backfill_collection_run_identity(@target,100);
            SELECT parity FROM control.record_collection_run_v2_parity_window(@target,'queries.performance',
                date_trunc('day',(SELECT min(started_at) FROM telemetry.collection_run WHERE instance_id=@target)),
                date_trunc('day',(SELECT max(started_at) FROM telemetry.collection_run WHERE instance_id=@target))+interval '1 day',100);
            SELECT control.set_collection_run_v2_scheduler_read_route(true,10000);
            RESET ROLE;
            """);
        Assert.Equal(("replayed", 0), await Commit(scene, first, firstParameters));
        await Sql(scene, """
            SET ROLE sqlobserver_migrator;
            SELECT set_config('sqlobserver.target_scope',@target::text,false);
            SELECT complete FROM control.backfill_query_observation_v3(@target,100);
            SELECT parity FROM control.record_next_query_observation_v3_parity_window(@target,100);
            SELECT control.set_query_observation_v3_read_route(true);
            SELECT control.activate_query_observation_v3_writer();
            RESET ROLE;
            """);
        Frame second = await Start(scene, increment: 2);
        Assert.Equal(("committed", 3), await Commit(scene, second));
        await using (var deltas = scene.Database.DataSource.CreateCommand("""
            SELECT count(*)=2 AND bool_and(transition='comparable' AND cpu_us=2 AND duration_us=2
                AND execution_count=2 AND logical_reads=2 AND writes=2 AND rows_processed=2)
            FROM events.plan_cache_runtime_delta WHERE run_id=@run;
            """))
        { deltas.Parameters.AddWithValue("run", second.Run.Value); Assert.True(Assert.IsType<bool>(await deltas.ExecuteScalarAsync())); }
        await using (var route = scene.Database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_observation WHERE collection_run_id=@run),
                (SELECT count(*) FROM events.query_performance_observation_v3 WHERE collection_run_id=@run);
            """))
        {
            route.Parameters.AddWithValue("run", second.Run.Value); await using var reader = await route.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.Equal(0L, reader.GetInt64(0)); Assert.Equal(1L, reader.GetInt64(1));
        }
        Frame queryStoreOnly = await Start(scene, cache: false);
        Assert.Equal(("committed", 1), await Commit(scene, queryStoreOnly));
        await using var noCache = scene.Database.DataSource.CreateCommand("SELECT count(*) FROM events.plan_cache_runtime_receipt WHERE run_id=@run;");
        noCache.Parameters.AddWithValue("run", queryStoreOnly.Run.Value); Assert.Equal(0L, await noCache.ExecuteScalarAsync());
    }

    [Fact]
    public async Task InvalidLaterEvidenceAndLeaseLossAfterWritesRollBackEveryContributionAndSchedule()
    {
        await using Scene scene = await Prepare();
        Frame baseline = await Start(scene); Assert.Equal(("committed", 3), await Commit(scene, baseline));
        Frame current = await Start(scene, increment: 2);
        await Reject(scene, current, "42501", parameters => parameters["target"] = Guid.NewGuid());
        await Reject(scene, current, "55000", parameters => parameters["fence"] = current.Lease.FencingToken.Value + 1);
        await Reject(scene, current, "55000", parameters => parameters["request"] = new byte[32]);
        await Reject(scene, current, "22023", parameters => parameters["source_rows"] = 2);
        await Reject(scene, current, "22023", parameters => parameters["output_bytes"] = 1L);
        await Reject(scene, current, "22023", parameters => parameters["circuit"] = "open");
        await Reject(scene, current, "55000", mutation: """
            UPDATE control.collector_schedule SET schedule_revision=schedule_revision+1
            WHERE instance_id=@target AND collector_id='queries.performance';
            """);
        await Reject(scene, current, "55000", mutation: """
            ALTER TABLE control.observation_target DISABLE TRIGGER USER;
            UPDATE control.observation_target SET lifecycle_state='disabled' WHERE instance_id=@target;
            """);
        await Reject(scene, current, "22023", parameters =>
        {
            var corrupted = Assert.IsType<JsonObject>(JsonNode.Parse((string)parameters["cache"]));
            corrupted["items"]![1]!["cpuUs"] = -1L;
            parameters["cache"] = corrupted.ToJsonString();
        });
        await Reject(scene, current, "23503", parameters => parameters["plan"] = new JsonArray(new JsonObject
        {
            ["database_id"] = 5,
            ["query_fingerprint"] = Digest('1'),
            ["plan_fingerprint"] = Digest('2'),
            ["payload_id"] = Guid.NewGuid().ToString(),
            ["fingerprint"] = Digest('3'),
        }).ToJsonString());
        await Reject(scene, current, "55000", mutation: $"""
            CREATE FUNCTION control.expire_cache_commit_fixture() RETURNS trigger LANGUAGE plpgsql AS $trigger$
            BEGIN UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key='{current.Lease.Key.Value}';
            RETURN NEW; END $trigger$;
            CREATE TRIGGER expire_cache_commit_fixture BEFORE INSERT ON telemetry.collection_run_outcome
                FOR EACH ROW EXECUTE FUNCTION control.expire_cache_commit_fixture();
            """);
        Assert.Equal(("committed", 3), await Commit(scene, current));
        await using var unchanged = scene.Database.DataSource.CreateCommand("""
            SELECT count(*)=2 AND bool_and(cpu_us=2 AND transition='comparable')
            FROM events.plan_cache_runtime_delta WHERE run_id=@run;
            """);
        unchanged.Parameters.AddWithValue("run", current.Run.Value); Assert.True(Assert.IsType<bool>(await unchanged.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task PartialAndFailureRunsPreserveCoverageAndCircuitWithoutFabricatedCacheWrites()
    {
        await using Scene scene = await Prepare();
        Frame partial = await Start(scene, capped: true);
        Assert.Equal(("committed", 3), await Commit(scene, partial));
        await using (var loss = scene.Database.DataSource.CreateCommand("""
            SELECT outcome='partial' AND output_item_count=3 AND inserted_item_count=3 AND lost_row_count=1 AND NOT loss_count_exact,
                (SELECT count(*)=1 FROM telemetry.visibility_gap WHERE run_id=@run),
                (SELECT candidate_rows=3 AND candidate_limit=2 AND NOT source_exhaustive FROM events.plan_cache_runtime_receipt WHERE run_id=@run)
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """))
        {
            loss.Parameters.AddWithValue("run", partial.Run.Value); await using var reader = await loss.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); for (int index = 0; index < 3; index++) Assert.True(reader.GetBoolean(index));
        }
        Frame failed = await Start(scene, cache: false);
        Dictionary<string, object> failure = Parameters(failed);
        failure["query"] = new JsonObject
        {
            ["observations"] = new JsonArray(),
            ["databaseStatuses"] = new JsonArray(),
            ["targetStatus"] = new JsonObject { ["status"] = "deadline_exceeded", ["reason"] = "deadline_exceeded" }
        }.ToJsonString();
        failure["outcome"] = "timed_out"; failure["reason"] = "deadline_exceeded";
        failure["source_rows"] = 0; failure["output_items"] = 0; failure["response_bytes"] = 0L;
        failure["output_bytes"] = 0L; failure["failures"] = 1; failure["coverage"] = "unavailable"; failure["fresh"] = false; failure["state"] = "unavailable"; failure["source"] = "unavailable";
        Assert.Equal(("committed", 0), await Commit(scene, failed, failure));
        Assert.Equal(("replayed", 0), await Commit(scene, failed, failure));
        await using (var evidence = scene.Database.DataSource.CreateCommand("""
            SELECT (SELECT count(*)=0 FROM events.plan_cache_runtime_receipt WHERE run_id=@run),
                (SELECT count(*)=0 FROM events.plan_cache_runtime_delta WHERE run_id=@run),
                (SELECT count(*)=1 AND bool_and(NOT count_is_exact) FROM telemetry.visibility_gap WHERE run_id=@run),
                (SELECT active_run_id IS NULL AND circuit_state='closed' AND consecutive_failure_count=1 AND last_outcome='timed_out'
                    FROM control.collector_schedule WHERE instance_id=@target AND collector_id='queries.performance');
            """))
        {
            evidence.Parameters.AddWithValue("run", failed.Run.Value); evidence.Parameters.AddWithValue("target", scene.Target);
            await using var result = await evidence.ExecuteReaderAsync(); Assert.True(await result.ReadAsync());
            for (int index = 0; index < 4; index++) Assert.True(result.GetBoolean(index));
        }
        Frame orphan = await Start(scene);
        var empty = Assert.IsType<JsonObject>(JsonNode.Parse((string)Parameters(orphan)["cache"]));
        empty["candidateRows"] = 0; empty["emittedRows"] = 0; empty["items"] = new JsonArray();
        await using (var connection = await scene.Database.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Scope(connection, transaction, scene.Target);
            await using var seed = new NpgsqlCommand("SELECT events.apply_plan_cache_runtime_batch(@run,@target,1,@cache);", connection, transaction);
            seed.Parameters.AddWithValue("run", orphan.Run.Value); seed.Parameters.AddWithValue("target", scene.Target);
            seed.Parameters.AddWithValue("cache", NpgsqlDbType.Jsonb, empty.ToJsonString());
            Assert.Equal(0, await seed.ExecuteScalarAsync()); await transaction.CommitAsync();
        }
        var incomplete = Parameters(orphan); incomplete["cache"] = empty.ToJsonString();
        incomplete["source_rows"] = 1; incomplete["output_items"] = 1;
        incomplete["output_bytes"] = 1280L; incomplete["response_bytes"] = 1280L;
        await Reject(scene, orphan, "55000", original: incomplete);
    }

    [Fact]
    public async Task StagedLifecycleRejectsChangedImmutablePinsAndHasNoRuntimeGrant()
    {
        await using Scene scene = await Prepare(badContract: true);
        Frame frame = await Start(scene);
        await Reject(scene, frame, "55000");
    }

    private async Task<Scene> Prepare(bool badContract = false)
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new(MigrationBatchResult.MaximumResults, new(TimeSpan.FromMinutes(2))), default);
            Assert.False(migrated.HasFailures, string.Join(",", migrated.Results.Where(row => row.Outcome == MigrationOutcome.Failed)
                .Select(row => $"{row.Migration.Number.Value}:{row.FailureCode}")));
            Guid target = Guid.NewGuid();
            await using (var grants = database.DataSource.CreateCommand("""
                SELECT NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                    AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                    AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
                    (SELECT count(*)=0 FROM control.collector_contract WHERE collector_id='queries.performance' AND collector_version=2)
                FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='control' AND p.proname='commit_query_performance_v2';
                """))
            {
                await using var reader = await grants.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0)); Assert.True(reader.GetBoolean(1)); Assert.False(await reader.ReadAsync());
            }
            await using (var seed = database.DataSource.CreateCommand("""
                INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                    authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
                VALUES(@target,@target::text,'v2 query commit fixture','sql01',1433,interval '5 seconds',
                    'windows_integrated_service_identity','mandatory_validated','active',1,now(),now(),now());
                INSERT INTO control.collector_contract
                SELECT (jsonb_populate_record(NULL::control.collector_contract,to_jsonb(c)||jsonb_build_object(
                    'collector_version',2,'manifest_schema_version',8,'output_schema_version',2,
                    'manifest_sha256',decode(@manifest,'hex'),'asset_bundle_sha256',decode(@bundle,'hex'),
                    'execution_timeout',interval '2 minutes','maximum_rows',22000))).*
                FROM control.collector_contract c WHERE c.collector_id='queries.performance' AND c.collector_version=1;
                UPDATE control.collector_schedule SET enabled=collector_id='queries.performance',
                    collector_version=CASE WHEN collector_id='queries.performance' THEN 2 ELSE collector_version END,
                    last_outcome=CASE WHEN collector_id IN ('engine.core','database.inventory') THEN 'succeeded' ELSE last_outcome END
                WHERE instance_id=@target;
                """))
            {
                seed.Parameters.AddWithValue("target", target);
                seed.Parameters.AddWithValue("manifest", badContract ? Digest('0') : QueryPerformanceV2ManifestTests.ManifestDigest);
                seed.Parameters.AddWithValue("bundle", QueryPerformanceV2ManifestTests.BundleDigest);
                await seed.ExecuteNonQueryAsync();
            }
            return new(database, database.CreateCollectorDataSource(), target);
        }
        catch { await database.DisposeAsync(); throw; }
    }

    private static async Task<Frame> Start(Scene scene, long increment = 0, bool cache = true, bool capped = false)
    {
        if (scene.PreviousLease is { } previous)
            await new PostgreSqlWorkerLeasePort(scene.Collector).ReleaseAsync(new(previous, Timeout), default);
        await Sql(scene, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp()-interval '1 second' WHERE instance_id=@target AND collector_id='queries.performance';");
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(scene.Collector);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new(16, Timeout), default)).Items,
            item => item.CollectorId.Value == "queries.performance" && item.TargetId.Value == scene.Target);
        Assert.Equal(2, work.CollectorManifestVersion); Assert.Equal(2, work.OutputSchemaVersion);
        LeaseAcquisitionResult acquired = await new PostgreSqlWorkerLeasePort(scene.Collector).AcquireAsync(
            new(new($"collector/run/queries.performance/{scene.Target:N}"), new(Guid.NewGuid()), new(TimeSpan.FromMinutes(5)), Timeout), default);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new(work, run, lease, Timeout), default)).Status);
        await using var request = scene.Database.DataSource.CreateCommand("SELECT request_digest FROM telemetry.collection_run WHERE run_id=@run;");
        request.Parameters.AddWithValue("run", run.Value); byte[] digest = Assert.IsType<byte[]>(await request.ExecuteScalarAsync());
        scene.PreviousLease = lease;
        return new(work, lease, run, digest, QueryPayload(work.RepositoryTimeUtc), cache ? CacheBatch(scene.Target, increment, capped) : null, capped);
    }

    private static JsonObject QueryPayload(DateTimeOffset end) => new()
    {
        ["observations"] = new JsonArray(new JsonObject
        {
            ["databaseId"] = 5,
            ["queryFingerprint"] = Digest('1'),
            ["planFingerprint"] = Digest('2'),
            ["source"] = "query_store",
            ["sourceState"] = "read_write",
            ["semantics"] = "query_store_interval",
            ["intervalStartUtc"] = Time(end.AddMinutes(-5)),
            ["intervalEndUtc"] = Time(end),
            ["observedAtUtc"] = Time(end),
            ["cpuMs"] = 300,
            ["durationMs"] = 400,
            ["executions"] = 1,
            ["logicalReads"] = 2,
            ["writes"] = 3,
            ["rows"] = 4,
            ["coverage"] = "complete",
            ["fresh"] = true,
            ["truncated"] = false,
        }),
        ["databaseStatuses"] = new JsonArray(new JsonObject
        {
            ["databaseId"] = 5,
            ["status"] = "query_store_rows",
            ["sourceState"] = "read_write",
            ["reason"] = "query_store_read",
            ["fallbackAttempted"] = false,
            ["truncated"] = false,
            ["lossKind"] = "none",
            ["sourceRowsRead"] = 1,
            ["responseBytes"] = 256,
            ["minimumLostItems"] = 0,
            ["lossCountIsExact"] = true,
            ["minimumLostBytes"] = 0,
        }),
    };

    private static PlanCacheRuntimeOutputBatch CacheBatch(Guid target, long increment, bool capped)
    {
        var id = new MonitoredInstanceId(target); var revision = new ObservationTargetRevision(1);
        DateTimeOffset at = DateTimeOffset.UtcNow; var query = new QueryOpaqueIdentity(6, Digest('a'));
        var before = new PlanCacheDatabaseIdentityEvidence(id, revision, 6, Digest('c'), Digest('d'), at.AddMilliseconds(-20), at.AddMilliseconds(-10));
        var after = new PlanCacheDatabaseIdentityEvidence(id, revision, 6, Digest('c'), Digest('d'), at.AddMilliseconds(30), at.AddMilliseconds(40));
        var counters = new PlanCacheRuntimeCounters(1001 + increment, 2002 + increment, 7 + increment, 20 + increment, 3 + increment, 5 + increment);
        PlanCacheRuntimeStatementObservation[] rows = CacheEntries.Select(entry => new PlanCacheRuntimeStatementObservation(
            new(new(id, revision, query, new(query, Digest('b')), Digest('d'), Digest('c'), Digest(entry)), counters, at.AddMilliseconds(20)), before, after)).ToArray();
        return new(id, revision, Digest('c'), at, at.AddMilliseconds(20), capped ? 2 : 2000, capped ? 3 : 2, 0, 0, 0, capped ? 3 : 2, 0, rows);
    }

    private static async Task<Dictionary<string, object>> WithSidecars(Scene scene, Frame frame)
    {
        Guid text = Guid.NewGuid(), plan = Guid.NewGuid();
        await using (var seed = scene.Database.DataSource.CreateCommand("""
            INSERT INTO security.protected_diagnostic_payload(payload_id,payload_kind,fingerprint,protection_algorithm,key_identifier,
                nonce,authentication_tag,ciphertext,instance_id)
            VALUES(@text,'query_text',decode(repeat('3',64),'hex'),'AES-GCM','test',decode(repeat('4',24),'hex'),
                    decode(repeat('5',32),'hex'),decode('66','hex'),@target),
                (@plan,'execution_plan',decode(repeat('7',64),'hex'),'AES-GCM','test',decode(repeat('8',24),'hex'),
                    decode(repeat('9',32),'hex'),decode('aa','hex'),@target);
            """))
        {
            seed.Parameters.AddWithValue("text", text); seed.Parameters.AddWithValue("plan", plan);
            seed.Parameters.AddWithValue("target", scene.Target); await seed.ExecuteNonQueryAsync();
        }
        var parameters = Parameters(frame);
        parameters["text"] = new JsonArray(new JsonObject
        {
            ["database_id"] = 5,
            ["query_fingerprint"] = Digest('1'),
            ["payload_id"] = text.ToString(),
            ["fingerprint"] = Digest('3')
        }).ToJsonString();
        parameters["plan"] = new JsonArray(new JsonObject
        {
            ["database_id"] = 5,
            ["query_fingerprint"] = Digest('1'),
            ["plan_fingerprint"] = Digest('2'),
            ["payload_id"] = plan.ToString(),
            ["fingerprint"] = Digest('7')
        }).ToJsonString();
        parameters["waits"] = new JsonArray(new JsonObject
        {
            ["database_id"] = 5,
            ["query_fingerprint"] = Digest('1'),
            ["plan_fingerprint"] = Digest('2'),
            ["categories"] = new JsonArray(new JsonObject { ["category"] = 1, ["waitMilliseconds"] = 17 })
        }).ToJsonString();
        parameters["output_bytes"] = (long)parameters["output_bytes"] + 24;
        parameters["response_bytes"] = (long)parameters["response_bytes"] + 24;
        parameters["source_rows"] = (int)parameters["source_rows"] + 1;
        return parameters;
    }

    private static Dictionary<string, object> Parameters(Frame frame) => new()
    {
        ["run"] = frame.Run.Value,
        ["target"] = frame.Work.TargetId.Value,
        ["revision"] = frame.Work.TargetRevision.Value,
        ["schedule"] = frame.Work.ScheduleRevision.Value,
        ["scheduled"] = frame.Work.ScheduledAtUtc.UtcDateTime,
        ["key"] = frame.Lease.Key.Value,
        ["owner"] = frame.Lease.Owner.Value,
        ["fence"] = frame.Lease.FencingToken.Value,
        ["request"] = frame.RequestDigest,
        ["outcome"] = frame.Capped ? "partial" : "succeeded",
        ["reason"] = frame.Capped ? "source_row_limit" : "completed",
        ["duration"] = 10L,
        ["attempts"] = 1,
        ["source_rows"] = 1 + (frame.Cache?.CandidateRows ?? 0),
        ["output_items"] = 1 + (frame.Cache?.Items.Count ?? 0),
        ["response_bytes"] = 256L + (frame.Cache?.EstimatedSizeBytes ?? 0),
        ["output_bytes"] = 256L + (frame.Cache?.EstimatedSizeBytes ?? 0),
        ["loss"] = frame.Capped ? "source_row_limit" : "none",
        ["lost_items"] = frame.Capped ? 1 : 0,
        ["exact"] = !frame.Capped,
        ["lost_bytes"] = 0,
        ["circuit"] = "closed",
        ["failures"] = 0,
        ["completion"] = SHA256.HashData(Encoding.UTF8.GetBytes("fixture-completion")),
        ["from"] = frame.Work.RepositoryTimeUtc.AddMinutes(-5).UtcDateTime,
        ["to"] = frame.Work.RepositoryTimeUtc.UtcDateTime,
        ["source"] = frame.Cache is null ? "query_store" : "mixed",
        ["state"] = "read_write",
        ["coverage"] = frame.Capped ? "truncated" : "complete",
        ["fresh"] = true,
        ["truncated"] = frame.Capped,
        ["query"] = frame.Query.ToJsonString(),
        ["cache"] = frame.Cache is null ? DBNull.Value : Encoding.UTF8.GetString(PlanCacheRuntimeCommitPayload.Serialize(frame.Cache)),
        ["text"] = "[]",
        ["plan"] = "[]",
        ["waits"] = "[]",
    };

    private static async Task<(string Status, int Inserted)> Commit(Scene scene, Frame frame, Dictionary<string, object>? supplied = null)
    {
        await using var connection = await scene.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(); await Scope(connection, transaction, scene.Target);
        var result = await Invoke(connection, transaction, supplied ?? Parameters(frame)); await transaction.CommitAsync(); return result;
    }

    private static async Task Reject(Scene scene, Frame frame, string expected, Action<Dictionary<string, object>>? change = null, string? mutation = null, Dictionary<string, object>? original = null)
    {
        string before = await Snapshot(scene); var parameters = new Dictionary<string, object>(original ?? Parameters(frame)); change?.Invoke(parameters);
        await using var connection = await scene.Database.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(); await Scope(connection, transaction, scene.Target);
        if (mutation is not null)
        {
            await using var setup = new NpgsqlCommand(mutation, connection, transaction);
            setup.Parameters.AddWithValue("target", scene.Target); await setup.ExecuteNonQueryAsync();
        }
        Assert.Equal(expected, (await Assert.ThrowsAsync<PostgresException>(() => Invoke(connection, transaction, parameters))).SqlState);
        await transaction.RollbackAsync(); Assert.Equal(before, await Snapshot(scene));
    }

    private static async Task<(string Status, int Inserted)> Invoke(NpgsqlConnection connection, NpgsqlTransaction transaction, Dictionary<string, object> parameters)
    {
        await using var command = new NpgsqlCommand(CommitSql, connection, transaction) { CommandTimeout = 30 };
        foreach ((string name, object value) in parameters)
            if (JsonArguments.Contains(name, StringComparer.Ordinal)) command.Parameters.AddWithValue(name, NpgsqlDbType.Jsonb, value);
            else command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        var result = (reader.GetString(0), reader.GetInt32(1)); Assert.False(await reader.ReadAsync()); return result;
    }

    private static async Task Scope(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid target)
    {
        await using var command = new NpgsqlCommand("SET LOCAL ROLE sqlobserver_migrator; SELECT set_config('sqlobserver.target_scope',@target::text,true);", connection, transaction);
        command.Parameters.AddWithValue("target", target); await command.ExecuteNonQueryAsync();
    }

    private static async Task Sql(Scene scene, string sql)
    {
        await using var command = scene.Database.DataSource.CreateCommand(sql); command.Parameters.AddWithValue("target", scene.Target);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Snapshot(Scene scene)
    {
        await using var command = scene.Database.DataSource.CreateCommand("""
            SELECT jsonb_build_array(
                (SELECT jsonb_agg(to_jsonb(w) ORDER BY observed_day,cache_entry_fingerprint) FROM events.plan_cache_runtime_watermark w),
                (SELECT jsonb_agg(to_jsonb(d) ORDER BY committed_at,cache_entry_fingerprint) FROM events.plan_cache_runtime_delta d),
                (SELECT jsonb_agg(to_jsonb(r) ORDER BY committed_at,run_id) FROM events.plan_cache_runtime_receipt r),
                (SELECT jsonb_agg(to_jsonb(q) ORDER BY collection_run_id) FROM events.query_performance_run q),
                (SELECT jsonb_agg(to_jsonb(q) ORDER BY collection_run_id) FROM events.query_performance_observation q),
                (SELECT jsonb_agg(to_jsonb(q) ORDER BY collection_run_id) FROM events.query_performance_observation_v3 q),
                (SELECT jsonb_agg(to_jsonb(o) ORDER BY run_id) FROM telemetry.collection_run_outcome o),
                (SELECT jsonb_agg(to_jsonb(g) ORDER BY run_id) FROM telemetry.visibility_gap g),
                (SELECT to_jsonb(s) FROM control.collector_schedule s WHERE instance_id=@target AND collector_id='queries.performance'),
                (SELECT to_jsonb(l) FROM control.worker_lease l WHERE work_key='collector/run/queries.performance/'||replace(@target::text,'-','')),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY collection_run_id) FROM events.query_performance_content_link l),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY collection_run_id) FROM events.query_performance_plan_content_link l),
                (SELECT jsonb_agg(to_jsonb(l) ORDER BY collection_run_id) FROM events.query_performance_wait_snapshot l))::text;
            """);
        command.Parameters.AddWithValue("target", scene.Target); return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static string Digest(char value) => new(value, 64);
    private static string Time(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private sealed record Frame(CollectorDueWorkItem Work, WorkerLeaseIdentity Lease, CollectorRunId Run, byte[] RequestDigest,
        JsonObject Query, PlanCacheRuntimeOutputBatch? Cache, bool Capped);
    private sealed class Scene(RepositoryTestDatabase database, NpgsqlDataSource collector, Guid target) : IAsyncDisposable
    {
        public RepositoryTestDatabase Database { get; } = database;
        public NpgsqlDataSource Collector { get; } = collector;
        public Guid Target { get; } = target;
        public WorkerLeaseIdentity? PreviousLease { get; set; }
        public async ValueTask DisposeAsync() { await Collector.DisposeAsync(); await Database.DisposeAsync(); }
    }
}
