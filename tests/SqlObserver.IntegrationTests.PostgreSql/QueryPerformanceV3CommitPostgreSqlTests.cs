using System.Security.Cryptography;
using System.Text;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class QueryPerformanceV2CommitPostgreSqlTests
{
    [Fact]
    public async Task V3RepositoryCommitsCountersContentOutcomeAndScheduleAsOneFencedReplayableUnit()
    {
        await using Scene scene = await Prepare(protocolVersion: 3);
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Frame first = await Start(scene);
        var sidecars = await WithSidecars(scene, first);
        CollectorPayload protectedPayload = V3Payload(first, V3Content(first), sidecars);
        var committer = new QueryPerformanceProtectedContentCommitter(new PostgreSqlSensitivePayloadPort(scene.Collector));
        CollectorPayload linked = await committer.WriteAsync(protectedPayload, first.Work.TargetId, first.Lease, Timeout, default);
        Assert.Equal(protectedPayload.ItemCount, linked.ItemCount);
        Assert.Equal(protectedPayload.EstimatedSizeBytes, linked.EstimatedSizeBytes);
        Assert.Equal(QueryPerformanceV3PersistenceBudget.Measure(protectedPayload.QueryPerformance.Items,
            protectedPayload.QueryPerformanceStatuses, protectedPayload.PlanCacheRuntime, protectedPayload.PlanCacheContent),
            QueryPerformanceV3PersistenceBudget.Measure(linked.QueryPerformance.Items, linked.QueryPerformanceStatuses,
                linked.PlanCacheRuntime, linked.PlanCacheContent));
        await using (var measure = scene.Database.DataSource.CreateCommand("SELECT octet_length(@content::jsonb::text);"))
        {
            measure.Parameters.AddWithValue("content", Encoding.UTF8.GetString(PlanCacheContentPersistencePayload.Serialize(linked.PlanCacheContent!)));
            Assert.Equal(PlanCacheContentPersistencePayload.Measure(linked.PlanCacheContent!), Assert.IsType<int>(await measure.ExecuteScalarAsync()));
        }
        var request = PortRequest(first, linked);
        var result = await runtime.CommitRunAsync(request, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(5, result.InsertedCount); Assert.Equal(linked.EstimatedSizeBytes, result.PersistedBytes);
        string after = await Snapshot(scene);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(request, default)).Status);
        Assert.Equal(after, await Snapshot(scene));
        var content = linked.PlanCacheContent!;
        var changedContent = new PlanCacheContentOutputBatch(first.Cache!, 2, 2, 0, 98, content.Items);
        Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(PortRequest(first, V3Payload(first, changedContent, sidecars)), default))).SqlState);
        Assert.Equal(after, await Snapshot(scene));
        await using (var evidence = scene.Database.DataSource.CreateCommand("""
            SELECT output_item_count=5 AND inserted_item_count=5 AND rejected_item_count=0,
                (SELECT count(*)=4 AND bool_and(content_state='available') FROM events.plan_cache_content_link WHERE run_id=@run),
                (SELECT retained_entries=2 AND ciphertext_bytes=48 FROM events.plan_cache_content_receipt WHERE run_id=@run),
                (SELECT active_run_id IS NULL AND last_outcome='succeeded' FROM control.collector_schedule WHERE instance_id=@target AND collector_id='queries.performance'),
                (SELECT count(*)=1 FROM events.query_performance_wait_snapshot WHERE collection_run_id=@run)
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """))
        {
            evidence.Parameters.AddWithValue("run", first.Run.Value); evidence.Parameters.AddWithValue("target", scene.Target);
            await using var reader = await evidence.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            for (int i = 0; i < 5; i++) Assert.True(reader.GetBoolean(i));
        }

        Frame second = await Start(scene, increment: 2);
        CollectorPayload secondLinked = await committer.WriteAsync(V3Payload(second, V3Content(second)), second.Work.TargetId, second.Lease, Timeout, default);
        var items = secondLinked.PlanCacheContent!.Items.ToArray();
        var last = items[^1]; var reference = last.Plan.Reference!;
        var invalidPlan = PlanCacheContentValue.Protected(new(reference.Kind, reference.Fingerprint, "AES-256-GCM", "fixture",
            new byte[12], new byte[16], new byte[last.Plan.CiphertextBytes]))
            .WithReference(new(new(Guid.NewGuid()), reference.Kind, reference.Fingerprint));
        items[^1] = new(last.Statement, last.Text, invalidPlan);
        var invalidContent = new PlanCacheContentOutputBatch(second.Cache!, 2, 2, 0, 96, items);
        string before = await Snapshot(scene);
        Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await runtime.CommitRunAsync(PortRequest(second, V3Payload(second, invalidContent)), default))).SqlState);
        Assert.Equal(before, await Snapshot(scene));
        await Sql(scene, $"""
            CREATE FUNCTION control.expire_v3_fixture() RETURNS trigger LANGUAGE plpgsql AS $trigger$
            BEGIN UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key='{second.Lease.Key.Value}';
            RETURN NEW; END $trigger$;
            CREATE TRIGGER expire_v3_fixture BEFORE INSERT ON telemetry.collection_run_outcome
                FOR EACH ROW EXECUTE FUNCTION control.expire_v3_fixture();
            """);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost, (await runtime.CommitRunAsync(PortRequest(second, secondLinked), default)).Status);
        Assert.Equal(before, await Snapshot(scene));
        await Sql(scene, "DROP TRIGGER expire_v3_fixture ON telemetry.collection_run_outcome; DROP FUNCTION control.expire_v3_fixture();");
        Assert.Equal(5, (await runtime.CommitRunAsync(PortRequest(second, secondLinked), default)).InsertedCount);
    }

    [Theory]
    [InlineData(PlanCacheContentCaptureState.Disabled)]
    [InlineData(PlanCacheContentCaptureState.ProtectionUnavailable)]
    public async Task V3NoContentReceiptPreservesCountersAndCaptureReason(PlanCacheContentCaptureState state)
    {
        await using Scene scene = await Prepare(protocolVersion: 3);
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Frame first = await Start(scene);
        var content = new PlanCacheContentOutputBatch(first.Cache!, 0, 0, 0, 0, [], state);
        CollectorPayload payload = V3Payload(first, content);
        Assert.Equal(3, (await runtime.CommitRunAsync(PortRequest(first, payload), default)).InsertedCount);
        await using var receipt = scene.Database.DataSource.CreateCommand("""
            SELECT capture_state,retained_entries,(SELECT count(*) FROM events.plan_cache_content_link WHERE run_id=@run),
                (SELECT count(*) FROM events.plan_cache_runtime_delta WHERE run_id=@run)
            FROM events.plan_cache_content_receipt WHERE run_id=@run;
            """);
        receipt.Parameters.AddWithValue("run", first.Run.Value);
        await using var reader = await receipt.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal(state == PlanCacheContentCaptureState.Disabled ? "disabled" : "protection_unavailable", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1)); Assert.Equal(0L, reader.GetInt64(2)); Assert.Equal(2L, reader.GetInt64(3));
    }

    [Fact]
    public async Task V3FailureAndQueryStoreOnlyRunsPreserveAccountingWithoutCacheContent()
    {
        await using Scene scene = await Prepare(protocolVersion: 3);
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
        Frame rejected = await Start(scene, cache: false);
        var accounting = new CollectorRunAccounting(5, 22_008, 128, 128);
        var loss = new CollectorLossEvidence(CollectorLossKind.OutputValidationFailure, 22_008, true, 128);
        var invalid = PortRequest(rejected, CollectorPayload.Empty, CollectorRunOutcome.OutputInvalid, accounting, loss);
        var result = await runtime.CommitRunAsync(invalid, default);
        Assert.Equal(CollectorRunCommitStatus.Committed, result.Status);
        Assert.Equal(22_008, result.RejectedCount); Assert.Equal(0, result.InsertedCount);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(invalid, default)).Status);
        await using (var row = scene.Database.DataSource.CreateCommand("""
            SELECT output_item_count=22008 AND rejected_item_count=22008 AND lost_byte_count=128 AND output_bytes=0,
                (SELECT count(*)=0 FROM events.plan_cache_content_receipt),
                (SELECT count(*)=0 FROM events.plan_cache_runtime_receipt)
            FROM telemetry.collection_run_outcome WHERE run_id=@run;
            """))
        {
            row.Parameters.AddWithValue("run", rejected.Run.Value);
            await using var reader = await row.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
            for (int i = 0; i < 3; i++) Assert.True(reader.GetBoolean(i));
        }
        Frame queryStoreOnly = await Start(scene, cache: false);
        Assert.Equal(1, (await runtime.CommitRunAsync(PortRequest(queryStoreOnly, PortPayload(queryStoreOnly)), default)).InsertedCount);
        Frame timedOut = await Start(scene, cache: false);
        var timeoutRequest = PortRequest(timedOut, CollectorPayload.Empty, CollectorRunOutcome.TimedOut,
            new(0, 0, 0, 0), CollectorLossEvidence.None);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(timeoutRequest, default)).Status);
    }

    [Fact]
    public async Task V3CanonicalWriterRejectsAChangedImmutableManifestPin()
    {
        await using Scene scene = await Prepare(badContract: true, protocolVersion: 3);
        await using NpgsqlDataSource migrator = MigratorDataSource(scene);
        Frame frame = await Start(scene);
        var content = new PlanCacheContentOutputBatch(frame.Cache!, 0, 0, 0, 0, [], PlanCacheContentCaptureState.Disabled);
        string before = await Snapshot(scene);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(async () =>
            await new PostgreSqlCollectorRuntimeRepositoryPort(migrator).CommitRunAsync(PortRequest(frame, V3Payload(frame, content)), default))).SqlState);
        Assert.Equal(before, await Snapshot(scene));
    }

    [Fact]
    public async Task V3OutputBoundaryRejectsMissingContentUnderstatedSourceAndUnlinkedPayloadBeforeRepositoryIo()
    {
        await using Scene scene = await Prepare(protocolVersion: 3);
        Frame frame = await Start(scene);
        await using var unavailable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Timeout=1");
        var port = new PostgreSqlCollectorRuntimeRepositoryPort(unavailable);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await port.CommitRunAsync(PortRequest(frame, PortPayload(frame)), default));
        var payload = V3Payload(frame, V3Content(frame));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await port.CommitRunAsync(PortRequest(frame, payload,
            accounting: new(3, payload.ItemCount, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes)), default));
        Assert.Throws<InvalidOperationException>(() => PlanCacheContentPersistencePayload.Serialize(payload.PlanCacheContent!));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await port.CommitRunAsync(PortRequest(frame, payload), default));
        await using var grants = scene.Database.DataSource.CreateCommand("""
            SELECT NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='control' AND p.proname='commit_query_performance_v3';
            """);
        Assert.True(Assert.IsType<bool>(await grants.ExecuteScalarAsync()));
    }

    private static PlanCacheContentOutputBatch V3Content(Frame frame)
    {
        ProtectedSensitivePayload Protected(SensitivePayloadKind kind, int bytes) =>
            new(kind, new(SHA256.HashData(Guid.NewGuid().ToByteArray())), "AES-256-GCM", "v3-fixture", new byte[12], new byte[16], new byte[bytes]);
        var text = Protected(SensitivePayloadKind.QueryText, 8); var plan = Protected(SensitivePayloadKind.ExecutionPlan, 16);
        return new(frame.Cache!, 2, 2, 0, 96, frame.Cache!.Items.Select(statement =>
            new PlanCacheStatementContentObservation(statement, PlanCacheContentValue.Protected(text), PlanCacheContentValue.Protected(plan))).ToArray());
    }

    private static CollectorPayload V3Payload(Frame frame, PlanCacheContentOutputBatch content, Dictionary<string, object>? sidecars = null)
    {
        var queryStore = PortPayload(frame, sidecars);
        return new(queryPerformance: queryStore.QueryPerformance, queryPerformanceStatuses: queryStore.QueryPerformanceStatuses,
            planCacheRuntime: frame.Cache, planCacheContent: content);
    }
}
