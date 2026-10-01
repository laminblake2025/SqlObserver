using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class QueryPerformanceV2CommitPostgreSqlTests
{
    [Fact]
    public async Task RepositoryPortRejectsOtherVersionsAndForeignCacheScopeBeforeOpeningAConnection()
    {
        await using Scene scene = await Prepare();
        Frame frame = await Start(scene);
        await using var unavailable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Timeout=1");
        var port = new PostgreSqlCollectorRuntimeRepositoryPort(unavailable);
        foreach ((string id, int manifest, int schema) in new[]
        {
            ("queries.performance",1,1), ("queries.performance",2,1),
            ("queries.performance",1,2), ("queries.performance",3,3), ("queries.performance-groups",2,2),
        })
        {
            CollectorDueWorkItem work = frame.Work;
            var other = new CollectorDueWorkItem(work.TargetId, work.TargetRevision, work.ConnectionPolicy, new(id),
                manifest, schema, work.ScheduleRevision, work.ScheduledAtUtc, work.RepositoryTimeUtc, work.Circuit, work.CapabilityProfile);
            CollectorPayload payload = PortPayload(frame);
            var summary = new CollectorRunSummary(frame.Run, work.TargetId, work.TargetRevision, new(id), manifest, schema,
                CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.Zero, 1,
                new(3, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
            await Assert.ThrowsAsync<InvalidDataException>(async () => await port.CommitRunAsync(new(other, summary, payload,
                CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), frame.Lease, Timeout), default));
            if (id == "queries.performance" && (manifest is not (1 or 2) || manifest != schema))
            {
                var queryStoreOnly = new CollectorPayload(queryPerformance: payload.QueryPerformance,
                    queryPerformanceStatuses: payload.QueryPerformanceStatuses);
                var queryStoreSummary = new CollectorRunSummary(frame.Run, work.TargetId, work.TargetRevision, new(id),
                    manifest, schema, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed, TimeSpan.Zero, 1,
                    new(1, queryStoreOnly.ItemCount, queryStoreOnly.EstimatedSizeBytes, queryStoreOnly.EstimatedSizeBytes),
                    CollectorLossEvidence.None);
                await Assert.ThrowsAsync<InvalidDataException>(async () => await port.CommitRunAsync(
                    new(other, queryStoreSummary, queryStoreOnly, CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc),
                        frame.Lease, Timeout), default));
            }
        }
        Frame foreign = frame with { Cache = CacheBatch(Guid.NewGuid(), 0, false) };
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await port.CommitRunAsync(PortRequest(foreign, PortPayload(foreign)), default));
        CollectorPayload valid = PortPayload(frame);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await port.CommitRunAsync(PortRequest(frame, valid, accounting:
                new(1, valid.ItemCount, valid.EstimatedSizeBytes, valid.EstimatedSizeBytes)), default));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await port.CommitRunAsync(PortRequest(frame, valid, accounting:
                new(QueryPerformanceV2OutputValidation.MaximumSourceRows + 1, valid.ItemCount,
                    valid.EstimatedSizeBytes, valid.EstimatedSizeBytes)), default));
    }

    [Fact]
    public async Task RepositoryPortRoutesCombinedPayloadSidecarsReplayAndLeaseRollbackAtomically()
    {
        await using Scene scene = await Prepare();
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Frame first = await Start(scene);
        Dictionary<string, object> links = await WithSidecars(scene, first);
        CollectorPayload payload = PortPayload(first, links);
        Assert.Equal(3, payload.ItemCount);
        Assert.Equal(5400, payload.EstimatedSizeBytes);
        // Compare the producer's five-input budget with PostgreSQL's actual
        // JSONB textual representation, including both opaque link families.
        await using (var measure = scene.Database.DataSource.CreateCommand("SELECT octet_length(@query::jsonb::text)+octet_length(@cache::jsonb::text)+octet_length(@text::jsonb::text)+octet_length(@plan::jsonb::text)+octet_length(@waits::jsonb::text);"))
        {
            measure.Parameters.AddWithValue("query", Encoding.UTF8.GetString(QueryPerformancePersistencePayload.Serialize(
                payload.QueryPerformance.Items, payload.QueryPerformanceStatuses, null, contentLinksCommittedWithRun: true)));
            measure.Parameters.AddWithValue("cache", Encoding.UTF8.GetString(PlanCacheRuntimeCommitPayload.Serialize(payload.PlanCacheRuntime!)));
            measure.Parameters.AddWithValue("text", (string)links["text"]);
            measure.Parameters.AddWithValue("plan", (string)links["plan"]);
            measure.Parameters.AddWithValue("waits", "[{\"database_id\":5,\"query_fingerprint\":\"" + Digest('1') +
                "\",\"plan_fingerprint\":\"" + Digest('2') + "\",\"categories\":[{\"category\":1,\"waitMilliseconds\":17}]}]");
            Assert.Equal(QueryPerformanceV2PersistenceBudget.Measure(payload.QueryPerformance.Items,
                payload.QueryPerformanceStatuses, payload.PlanCacheRuntime), Assert.IsType<int>(await measure.ExecuteScalarAsync()));
        }
        CommitCollectorRunRequest request = PortRequest(first, payload);
        CollectorRunCommitResult committed = await runtime.CommitRunAsync(request, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
        Assert.Equal(3, committed.InsertedCount); Assert.Equal(5400, committed.PersistedBytes);
        string after = await Snapshot(scene);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(request, default)).Status);
        Assert.Equal(after, await Snapshot(scene));
        Frame changed = first with { Cache = CacheBatch(scene.Target, 1, false) };
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(PortRequest(changed, PortPayload(changed, links)), default))).SqlState);
        Assert.Equal(after, await Snapshot(scene));
        await using (var evidence = scene.Database.DataSource.CreateCommand("""
            SELECT (SELECT count(*)=1 FROM events.query_performance_content_link WHERE collection_run_id=@run),
                (SELECT count(*)=1 FROM events.query_performance_plan_content_link WHERE collection_run_id=@run),
                (SELECT categories='[{"category":1,"waitMilliseconds":17}]'::jsonb FROM events.query_performance_wait_snapshot WHERE collection_run_id=@run);
            """))
        {
            evidence.Parameters.AddWithValue("run", first.Run.Value); await using var reader = await evidence.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); for (int index = 0; index < 3; index++) Assert.True(reader.GetBoolean(index));
        }

        Frame second = await Start(scene, increment: 2);
        string before = await Snapshot(scene);
        CollectorPayload missingPlan = PortPayload(second, missingPlan: true);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(PortRequest(second, missingPlan), default))).SqlState);
        Assert.Equal(before, await Snapshot(scene));
        await Sql(scene, $"""
            CREATE FUNCTION control.expire_cache_port_fixture() RETURNS trigger LANGUAGE plpgsql AS $trigger$
            BEGIN UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key='{second.Lease.Key.Value}';
            RETURN NEW; END $trigger$;
            CREATE TRIGGER expire_cache_port_fixture BEFORE INSERT ON telemetry.collection_run_outcome
                FOR EACH ROW EXECUTE FUNCTION control.expire_cache_port_fixture();
            """);
        CollectorRunCommitResult lost = await runtime.CommitRunAsync(PortRequest(second, PortPayload(second)), default);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, lost.Status);
        Assert.Equal(0, lost.InsertedCount); Assert.Equal(before, await Snapshot(scene));
        await Sql(scene, "DROP TRIGGER expire_cache_port_fixture ON telemetry.collection_run_outcome; DROP FUNCTION control.expire_cache_port_fixture();");
        Assert.Equal(3, (await runtime.CommitRunAsync(PortRequest(second, PortPayload(second)), default)).InsertedCount);
        await using var deltas = scene.Database.DataSource.CreateCommand("""
            SELECT count(*)=2 AND bool_and(transition='comparable' AND cpu_us=2 AND duration_us=2
                AND execution_count=2 AND logical_reads=2 AND writes=2 AND rows_processed=2)
            FROM events.plan_cache_runtime_delta WHERE run_id=@run;
            """);
        deltas.Parameters.AddWithValue("run", second.Run.Value);
        Assert.True(Assert.IsType<bool>(await deltas.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RepositoryPortAccountsPartialFailureAndRejectedOutputWithoutActivatingRuntimeGrant()
    {
        await using Scene scene = await Prepare();
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Frame partial = await Start(scene, capped: true);
        CommitCollectorRunRequest partialRequest = PortRequest(partial, PortPayload(partial));
        string before = await Snapshot(scene);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await new PostgreSqlCollectorRuntimeRepositoryPort(scene.Collector).CommitRunAsync(partialRequest, default))).SqlState);
        Assert.Equal(before, await Snapshot(scene));
        Assert.Equal(3, (await runtime.CommitRunAsync(partialRequest, default)).InsertedCount);

        Frame rejected = await Start(scene, cache: false);
        var invalidAccounting = new CollectorRunAccounting(3, 3, 5376, 5376);
        var loss = new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 3, true, 5376);
        var invalid = PortRequest(rejected, CollectorPayload.Empty, CollectorRunOutcome.OutputInvalid, invalidAccounting, loss);
        CollectorRunCommitResult invalidResult = await runtime.CommitRunAsync(invalid, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, invalidResult.Status);
        Assert.Equal(3, invalidResult.RejectedCount); Assert.Equal(0, invalidResult.InsertedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(invalid, default)).Status);
        await using (var counts = scene.Database.DataSource.CreateCommand("""
            SELECT output_item_count=3 AND inserted_item_count=0 AND duplicate_item_count=0 AND rejected_item_count=3
                AND output_bytes=0 AND lost_row_count=3 AND lost_byte_count=5376 AND loss_count_exact,
                (SELECT count(*)=0 FROM events.plan_cache_runtime_receipt WHERE run_id=@run)
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """))
        {
            counts.Parameters.AddWithValue("run", rejected.Run.Value); await using var reader = await counts.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0)); Assert.True(reader.GetBoolean(1));
        }
        Frame failed = await Start(scene, cache: false);
        var failure = PortRequest(failed, CollectorPayload.Empty, CollectorRunOutcome.TimedOut);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(failure, default)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(failure, default)).Status);
        Frame queryStoreOnly = await Start(scene, cache: false);
        Assert.Equal(1, (await runtime.CommitRunAsync(PortRequest(queryStoreOnly, PortPayload(queryStoreOnly)), default)).InsertedCount);
    }

    [Fact]
    public async Task RepositoryPortCommitsSelectedCacheTuplesWhenQueryStoreIsDisabled()
    {
        await using Scene scene = await Prepare();
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Frame frame = await Start(scene);
        var payload = new CollectorPayload(queryPerformanceStatuses:
            [new(6, QueryPerformanceReadStatus.QueryStoreDisabled, "query_store_disabled", false, false,
                0, 0, QueryStoreState.Disabled)], planCacheRuntime: frame.Cache);
        CollectorRunCommitResult result = await runtime.CommitRunAsync(PortRequest(frame, payload), default);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(2, result.InsertedCount); Assert.Equal(payload.EstimatedSizeBytes, result.PersistedBytes);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(PortRequest(frame, payload), default)).Status);
        await using var evidence = scene.Database.DataSource.CreateCommand("""
            SELECT source='query_store' AND source_state='disabled' AND coverage='unavailable' AND NOT freshness,
                (SELECT count(*)=0 FROM events.query_performance_observation WHERE collection_run_id=@run),
                (SELECT count(*)=2 FROM events.plan_cache_runtime_delta WHERE run_id=@run),
                (SELECT output_item_count=2 AND inserted_item_count=2 FROM telemetry.collection_run_outcome WHERE run_id=@run)
            FROM events.query_performance_run WHERE collection_run_id=@run;
            """);
        evidence.Parameters.AddWithValue("run", frame.Run.Value); await using var reader = await evidence.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync()); for (int index = 0; index < 4; index++) Assert.True(reader.GetBoolean(index));
    }

    private static NpgsqlDataSource MigratorDataSource(Scene scene)
    {
        // Qualification uses the private migration identity. No fixture grant
        // enables the staged function for the actual collector role.
        var builder = new NpgsqlDataSourceBuilder(scene.Database.ConnectionString);
        builder.UsePhysicalConnectionInitializer(connection =>
        {
            using var command = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection); command.ExecuteNonQuery();
        }, async connection =>
        {
            await using var command = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", connection); await command.ExecuteNonQueryAsync();
        });
        return builder.Build();
    }

    private static CollectorPayload PortPayload(Frame frame, Dictionary<string, object>? links = null, bool missingPlan = false)
    {
        var query = new QueryOpaqueIdentity(5, Digest('1')); var plan = new PlanOpaqueIdentity(query, Digest('2'));
        SensitivePayloadReference? textReference = links is null ? null : Reference(links, "text", SensitivePayloadKind.QueryText);
        SensitivePayloadReference? planReference = links is null ? null : Reference(links, "plan", SensitivePayloadKind.ExecutionPlan);
        if (missingPlan) planReference = new(new(Guid.NewGuid()), SensitivePayloadKind.ExecutionPlan, new(Convert.FromHexString(Digest('7'))));
        DateTimeOffset at = frame.Work.RepositoryTimeUtc;
        var observation = new QueryPerformanceObservation(frame.Work.TargetId, frame.Work.TargetRevision, query, plan,
            QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite, QueryMetricSemantics.QueryStoreInterval,
            new(300, 400, 1, 2, 3, 4), at.AddMinutes(-5), at, at, QueryCoverage.Complete, true, false,
            contentReference: textReference, planContentReference: planReference,
            waitSnapshot: links is null ? null : new([new(1, 17)]));
        return new(queryPerformance: new([observation]), queryPerformanceStatuses:
            [new(5, QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false, 1, 256, QueryStoreState.ReadWrite)],
            planCacheRuntime: frame.Cache);
    }

    private static SensitivePayloadReference Reference(Dictionary<string, object> links, string field, SensitivePayloadKind kind)
    {
        JsonNode item = JsonNode.Parse((string)links[field])![0]!;
        return new(new(Guid.Parse(item["payload_id"]!.GetValue<string>())), kind,
            new(Convert.FromHexString(item["fingerprint"]!.GetValue<string>())));
    }

    private static CommitCollectorRunRequest PortRequest(Frame frame, CollectorPayload payload,
        CollectorRunOutcome? outcome = null, CollectorRunAccounting? accounting = null, CollectorLossEvidence? loss = null)
    {
        CollectorRunOutcome state = outcome ?? (frame.Capped ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded);
        loss ??= frame.Capped ? new(CollectorLossKind.SourceRowLimit, 1, false) : CollectorLossEvidence.None;
        accounting ??= new(payload.QueryPerformanceStatuses.Sum(status => status.SourceRowsRead) + (frame.Cache?.CandidateRows ?? 0),
            payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(frame.Run, frame.Work.TargetId, frame.Work.TargetRevision, frame.Work.CollectorId,
            2, 2, state, state switch
            {
                CollectorRunOutcome.Partial => CollectorRunReason.SourceRowLimit,
                CollectorRunOutcome.OutputInvalid => CollectorRunReason.OutputValidationFailed,
                CollectorRunOutcome.TimedOut => CollectorRunReason.DeadlineExceeded,
                _ => CollectorRunReason.Completed,
            }, TimeSpan.FromMilliseconds(10), 1, accounting, loss);
        return new(frame.Work, summary, payload, state == CollectorRunOutcome.TimedOut
            ? new(CollectorCircuitState.Closed, 1, frame.Work.RepositoryTimeUtc)
            : CollectorCircuitSnapshot.Closed(frame.Work.RepositoryTimeUtc), frame.Lease, Timeout);
    }
}
