using Npgsql;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Server;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class BufferNodePleCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task NodeSamplesCommitOnceAndRespectRunAndLeaseFences()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout),
                CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result => result.Outcome == MigrationOutcome.Failed)
                .Select(static result => $"{result.Migration.Number.Value}:{result.FailureCode}")));
        await using (var partition = database.DataSource.CreateCommand(
            "SELECT control.ensure_daily_metric_partition(current_date);"))
            await partition.ExecuteNonQueryAsync();

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lease = await AcquireAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout),
                CancellationToken.None)).Status);

        CommitCollectorRunRequest request = Success(work, runId, lease, Nodes(work, 42));
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        CollectorPayload duplicateNode = new(metrics:
        [
            request.Payload.Metrics[0],
            new MetricSample(new MetricSampleId(Guid.NewGuid()), work.TargetId,
                new MetricId("engine.buffer_node_page_life_expectancy_seconds"),
                work.RepositoryTimeUtc, 84, [new MetricDimension("node_id", "0")]),
        ]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, duplicateNode),
                CancellationToken.None).AsTask());

        await using (var rows = database.DataSource.CreateCommand("""
            SELECT metric_key,dimensions->>'node_id',metric_value,collection_run_id
            FROM telemetry.raw_metric_sample WHERE collection_run_id=@run
            ORDER BY (dimensions->>'node_id')::integer;
            """))
        {
            rows.Parameters.AddWithValue("run", runId.Value);
            await using NpgsqlDataReader reader = await rows.ExecuteReaderAsync();
            for (int node = 0; node < 2; node++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("engine.buffer_node_page_life_expectancy_seconds", reader.GetString(0));
                Assert.Equal(node.ToString(System.Globalization.CultureInfo.InvariantCulture), reader.GetString(1));
                Assert.Equal(node == 0 ? 42d : 84d, reader.GetDouble(2));
                Assert.Equal(runId.Value, reader.GetGuid(3));
            }
            Assert.False(await reader.ReadAsync());
        }

        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, Nodes(work, 43)),
                CancellationToken.None).AsTask());
        Assert.Equal("40001", divergent.SqlState);
        Assert.Equal(2L, await CountRowsAsync(database, runId));
        await SeedCommittedAggregateAsync(database, work, 200);

        await using (NpgsqlConnection parity = await database.DataSource.OpenConnectionAsync())
        {
            await using (var role = new NpgsqlCommand("SET ROLE sqlobserver_migrator;", parity))
                await role.ExecuteNonQueryAsync();
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@target::text,false);", parity))
            {
                scope.Parameters.AddWithValue("target", work.TargetId.Value);
                await scope.ExecuteScalarAsync();
            }
            async Task<List<string>> Read(string name, string arguments)
            {
                await using var command = new NpgsqlCommand(
                    $"SELECT to_jsonb(row)::text FROM reporting.{name}({arguments}) AS row;",
                    parity);
                command.Parameters.AddWithValue("target", work.TargetId.Value);
                command.Parameters.AddWithValue("from", work.RepositoryTimeUtc.AddMinutes(-5));
                command.Parameters.AddWithValue("to", work.RepositoryTimeUtc.AddHours(1));
                var rows = new List<string>();
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
                return rows;
            }
            foreach ((string name, string arguments) in new[]
            {
                ("list_buffer_node_ple_nodes", "@target,@from,@to"),
                ("get_buffer_node_ple_trend", "@target,0,@from,@to"),
                ("get_buffer_node_ple_trend", "@target,1,@from,@to"),
                ("get_buffer_manager_ple_trend", "@target,@from,@to"),
            })
            {
                List<string> original = await Read(name, arguments);
                Assert.NotEmpty(original);
                Assert.Equal(original, await Read($"{name}_v2_shadow", arguments));
            }
            Assert.Equal(await Read("count_buffer_node_ple_gaps", "@target,@from,@to"),
                await Read("count_buffer_node_ple_gaps_v2_shadow", "@target,@from,@to"));

            await using var grants = new NpgsqlCommand("""
                SELECT count(*) FROM pg_proc AS p
                JOIN pg_namespace AS n ON n.oid=p.pronamespace
                WHERE n.nspname='reporting'
                  AND p.proname IN ('list_buffer_node_ple_nodes_v2_shadow',
                                    'get_buffer_node_ple_trend_v2_shadow',
                                    'get_buffer_manager_ple_trend_v2_shadow',
                                    'count_buffer_node_ple_gaps_v2_shadow')
                  AND has_function_privilege('sqlobserver_migrator',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_server',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE')
                  AND NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE');
                """, parity);
            Assert.Equal(4L, await grants.ExecuteScalarAsync());
        }

        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var service = new BufferNodePleWindowQueryService(
                new PostgreSqlBufferNodePleWindowPort(server));
            var authorized = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-721"),
                AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([work.TargetId]))]);
            var window = new BufferNodePleWindowRequest(work.TargetId, null,
                work.RepositoryTimeUtc.AddMinutes(-5), work.RepositoryTimeUtc.AddHours(1), Timeout);
            BufferNodePleWindowResult result = await service.ReadAsync(authorized, window,
                CancellationToken.None);
            Assert.Equal(0, result.SelectedNodeId);
            Assert.Equal([0, 1], result.Nodes.Select(static node => node.NodeId));
            Assert.Equal(0L, result.VisibilityGapCount);
            BufferNodePlePoint point = Assert.Single(result.Points);
            Assert.Equal(1L, point.SampleCount);
            Assert.Equal(42d, point.MinimumSeconds);
            Assert.Equal(42d, point.AverageSeconds);
            Assert.Equal(42d, point.MaximumSeconds);
            Assert.Equal(200d, Assert.Single(result.AggregatePoints).AverageSeconds);

            BufferNodePleWindowResult second = await service.ReadAsync(authorized,
                new BufferNodePleWindowRequest(work.TargetId, 1, window.FromUtc,
                    window.ToUtc, Timeout), CancellationToken.None);
            Assert.Equal(1, second.SelectedNodeId);
            Assert.Equal(84d, Assert.Single(second.Points).AverageSeconds);
            Assert.Equal(200d, Assert.Single(second.AggregatePoints).AverageSeconds);

            var mixedGrant = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-722"),
                AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([new MonitoredInstanceId(Guid.NewGuid())])),
                 new RoleAuthorizationGrant(ApplicationRole.Auditor,
                    TargetAuthorizationScope.ForTargets([work.TargetId]))]);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(
                mixedGrant, window, CancellationToken.None).AsTask());

            await using NpgsqlConnection connection = await server.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
            {
                scope.Parameters.AddWithValue("scope", Guid.NewGuid().ToString());
                await scope.ExecuteNonQueryAsync();
            }
            await using var crossTarget = new NpgsqlCommand("""
                SELECT * FROM reporting.list_buffer_node_ple_nodes(@target,@from_utc,@to_utc);
                """, connection, transaction);
            crossTarget.Parameters.AddWithValue("target", work.TargetId.Value);
            crossTarget.Parameters.AddWithValue("from_utc", window.FromUtc);
            crossTarget.Parameters.AddWithValue("to_utc", window.ToUtc);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                () => crossTarget.ExecuteNonQueryAsync())).SqlState);
        }
        string serverConnectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c role=sqlobserver_server",
        }.ConnectionString;
        await using (var factory = new McpProductionPipelineFactory(serverConnectionString,
            withCursorSigner: true))
        using (HttpClient http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false,
        }))
        {
            string path = $"/api/v1/observation-targets/{work.TargetId.Value:D}/resources/buffer-nodes/ple";
            using HttpResponseMessage response = await http.GetAsync(path + "?nodeId=1");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = body.RootElement;
            Assert.Equal("numa_node_page_life_expectancy_gauge",
                root.GetProperty("semantics").GetString());
            Assert.Equal(1, root.GetProperty("selectedNodeId").GetInt32());
            Assert.Equal(2, root.GetProperty("nodes").GetArrayLength());
            Assert.Equal(84d, Assert.Single(root.GetProperty("points").EnumerateArray())
                .GetProperty("averageSeconds").GetDouble());
            Assert.Equal(200d, Assert.Single(root.GetProperty("aggregatePoints").EnumerateArray())
                .GetProperty("averageSeconds").GetDouble());
            using HttpResponseMessage invalid = await http.GetAsync(path + "?nodeId=1024");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
            using HttpResponseMessage wrongTarget = await http.GetAsync(
                $"/api/v1/observation-targets/{Guid.NewGuid():D}/resources/buffer-nodes/ple");
            Assert.Equal(System.Net.HttpStatusCode.OK, wrongTarget.StatusCode);
            using JsonDocument wrongBody = JsonDocument.Parse(await wrongTarget.Content.ReadAsStringAsync());
            Assert.Empty(wrongBody.RootElement.GetProperty("nodes").EnumerateArray());
        }

        CollectorDueWorkItem lostWork = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lostLease = await AcquireAsync(leases, lostWork);
        var lostRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(lostWork, lostRun, lostLease, Timeout),
                CancellationToken.None)).Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lostLease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost,
            (await runtime.CommitRunAsync(Success(lostWork, lostRun, lostLease, Nodes(lostWork, 10)),
                CancellationToken.None)).Status);
        Assert.Equal(0L, await CountRowsAsync(database, lostRun));
        await using var outcome = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;");
        outcome.Parameters.AddWithValue("run", lostRun.Value);
        Assert.Equal(0L, await outcome.ExecuteScalarAsync());

        CollectorDueWorkItem partialWork = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity partialLease = await AcquireAsync(leases, partialWork);
        var partialRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(partialWork, partialRun,
                partialLease, Timeout), CancellationToken.None)).Status);
        var partialSummary = new CollectorRunSummary(partialRun, partialWork.TargetId,
            partialWork.TargetRevision, partialWork.CollectorId,
            partialWork.CollectorManifestVersion, partialWork.OutputSchemaVersion,
            CollectorRunOutcome.Partial, CollectorRunReason.SourceRowLimit,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(129, 0, 1000, 0),
            new CollectorLossEvidence(CollectorLossKind.SourceRowLimit, 1, countIsExact: false));
        var partialRequest = new CommitCollectorRunRequest(partialWork, partialSummary,
            CollectorPayload.Empty, CollectorCircuitSnapshot.Closed(partialWork.RepositoryTimeUtc),
            partialLease, Timeout);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(partialRequest, CancellationToken.None)).Status);
        Assert.Equal(0L, await CountRowsAsync(database, partialRun));
        await using (var gap = database.DataSource.CreateCommand("""
            SELECT count(*),max(lost_row_count) FROM telemetry.visibility_gap WHERE run_id=@run;
            """))
        {
            gap.Parameters.AddWithValue("run", partialRun.Value);
            await using NpgsqlDataReader reader = await gap.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var service = new BufferNodePleWindowQueryService(
                new PostgreSqlBufferNodePleWindowPort(server));
            var authorized = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-723"),
                AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([partialWork.TargetId]))]);
            BufferNodePleWindowResult partial = await service.ReadAsync(authorized,
                new BufferNodePleWindowRequest(partialWork.TargetId, null,
                    partialWork.ScheduledAtUtc.AddMinutes(-5),
                    partialWork.RepositoryTimeUtc.AddHours(1), Timeout), CancellationToken.None);
            Assert.Empty(partial.Nodes);
            Assert.Empty(partial.Points);
            Assert.Null(partial.SelectedNodeId);
            Assert.Equal(1L, partial.VisibilityGapCount);
        }

        await using var privileges = database.DataSource.CreateCommand("""
            SELECT has_table_privilege('sqlobserver_collector',
                'telemetry.raw_metric_sample','INSERT');
            """);
        Assert.False((bool)(await privileges.ExecuteScalarAsync())!);
    }

    private static async Task<CollectorDueWorkItem> PrepareWorkAsync(
        RepositoryTestDatabase database, PostgreSqlCollectorRuntimeRepositoryPort runtime)
    {
        Guid target = Guid.NewGuid();
        await using var seed = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
             (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,
              created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'Buffer Node commit target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET enabled=(collector_id IN ('engine.core','engine.buffer-nodes')),
                last_outcome=CASE WHEN collector_id='engine.core' THEN 'succeeded'
                                  ELSE last_outcome END
            WHERE instance_id=@target;
            UPDATE control.collector_schedule
            SET enabled=true,next_due_at=statement_timestamp(),updated_at=statement_timestamp()
            WHERE instance_id=@target AND collector_id='engine.buffer-nodes';
            """);
        seed.Parameters.AddWithValue("target", target);
        seed.Parameters.AddWithValue("key", $"buffer.node.commit.{target:N}");
        await seed.ExecuteNonQueryAsync();
        return Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target && item.CollectorId.Value == "engine.buffer-nodes");
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(
        PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/engine.buffer-nodes/{work.TargetId.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquisition.Status);
        return Assert.IsType<WorkerLease>(acquisition.Lease).Identity;
    }

    private static CollectorPayload Nodes(CollectorDueWorkItem work, double first) => new(metrics:
    [
        new MetricSample(new MetricSampleId(Guid.NewGuid()), work.TargetId,
            new MetricId("engine.buffer_node_page_life_expectancy_seconds"),
            work.RepositoryTimeUtc, first, [new MetricDimension("node_id", "0")]),
        new MetricSample(new MetricSampleId(Guid.NewGuid()), work.TargetId,
            new MetricId("engine.buffer_node_page_life_expectancy_seconds"),
            work.RepositoryTimeUtc, 84, [new MetricDimension("node_id", "1")]),
    ]);

    private static CommitCollectorRunRequest Success(CollectorDueWorkItem work, CollectorRunId runId,
        WorkerLeaseIdentity lease, CollectorPayload payload)
    {
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(payload.ItemCount, payload.ItemCount,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes), CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<long> CountRowsAsync(RepositoryTestDatabase database, CollectorRunId runId)
    {
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.raw_metric_sample WHERE collection_run_id=@run;");
        count.Parameters.AddWithValue("run", runId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task SeedCommittedAggregateAsync(RepositoryTestDatabase database,
        CollectorDueWorkItem work, double seconds)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand(
            "SELECT set_config('sqlobserver.target_scope',@scope,true);", connection, transaction))
        {
            scope.Parameters.AddWithValue("scope", work.TargetId.Value.ToString());
            await scope.ExecuteNonQueryAsync();
        }
        await using var seed = new NpgsqlCommand("""
            INSERT INTO telemetry.collection_run
             (run_id,instance_id,collector_id,collector_version,output_schema_version,
              target_revision,schedule_revision,work_key,owner_execution_id,fencing_token,
              request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'engine.core',1,1,1,1,'test/buffer-manager-ple',@owner,1,
              decode(repeat('a',64),'hex'),@at,@at);
            INSERT INTO telemetry.collection_run_outcome
             (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,
              source_row_count,output_item_count,inserted_item_count,
              duplicate_item_count,rejected_item_count,response_bytes,output_bytes,
              persisted_bytes,truncated,loss_detected,loss_kind,loss_count_exact,
              lost_row_count,lost_byte_count,completion_digest,completed_at)
            VALUES (@run,'succeeded','completed',1,0,1,1,1,1,0,0,200,200,200,
              false,false,'none',true,0,0,decode(repeat('b',64),'hex'),@at);
            INSERT INTO telemetry.raw_metric_sample
             (observed_at,sample_id,instance_id,metric_key,metric_value,
              dimensions,collected_at,collection_run_id)
            VALUES (@at,@sample,@target,'engine.page_life_expectancy_seconds',
              @seconds,'{}'::jsonb,@at,@run);
            """, connection, transaction);
        seed.Parameters.AddWithValue("run", Guid.NewGuid());
        seed.Parameters.AddWithValue("target", work.TargetId.Value);
        seed.Parameters.AddWithValue("owner", Guid.NewGuid());
        seed.Parameters.AddWithValue("at", work.RepositoryTimeUtc);
        seed.Parameters.AddWithValue("sample", Guid.NewGuid());
        seed.Parameters.AddWithValue("seconds", seconds);
        await seed.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}
