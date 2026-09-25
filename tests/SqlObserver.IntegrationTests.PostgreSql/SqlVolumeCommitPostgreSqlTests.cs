using Npgsql;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Server;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class SqlVolumeCommitPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task ExactCapacityReplayAndLeaseLossAreAtomic()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout),
                CancellationToken.None);
        Assert.False(migrated.HasFailures,
            string.Join(", ", migrated.Results.Where(static result => result.Outcome == MigrationOutcome.Failed)
                .Select(static result => $"{result.Migration.Number.Value}:{result.FailureCode}")));

        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lease = await AcquireAsync(leases, work);
        var runId = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, runId, lease, Timeout),
                CancellationToken.None)).Status);

        CollectorPayload payload = Volumes(work, long.MaxValue - 1);
        CommitCollectorRunRequest request = Success(work, runId, lease, payload);
        Assert.Equal(CollectorRunCommitStatus.Committed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        Assert.Equal(CollectorRunCommitStatus.Replayed,
            (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
        await using (var count = database.DataSource.CreateCommand("""
            SELECT count(*),max(total_bytes),max(available_bytes),
                   count(*) FILTER (WHERE total_bytes IS NULL AND available_bytes IS NULL)
            FROM telemetry.sql_volume_snapshot WHERE run_id=@run;
            """))
        {
            count.Parameters.AddWithValue("run", runId.Value);
            await using NpgsqlDataReader reader = await count.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(long.MaxValue, reader.GetInt64(1));
            Assert.Equal(long.MaxValue - 1, reader.GetInt64(2));
            Assert.Equal(1L, reader.GetInt64(3));
        }

        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() =>
            runtime.CommitRunAsync(Success(work, runId, lease, Volumes(work, long.MaxValue - 2)),
                CancellationToken.None).AsTask());
        Assert.Equal("40001", divergent.SqlState);
        Assert.Equal(2L, await CountRowsAsync(database, runId));

        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        await using (NpgsqlConnection readConnection = await server.OpenConnectionAsync())
        {
            await using (var scope = new NpgsqlCommand(
                "SELECT set_config('sqlobserver.target_scope',@scope,false);", readConnection))
            {
                scope.Parameters.AddWithValue("scope", work.TargetId.Value.ToString());
                await scope.ExecuteNonQueryAsync();
            }
            await using (var first = new NpgsqlCommand("""
                SELECT * FROM reporting.list_sql_volume_snapshot(
                    @target,NULL::uuid,NULL::bigint,NULL::bytea,1);
                """, readConnection))
            {
                first.Parameters.AddWithValue("target", work.TargetId.Value);
                await using NpgsqlDataReader reader = await first.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(15, reader.FieldCount);
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(runId.Value, reader.GetGuid(1));
                Assert.Equal("current", reader.GetString(2));
                Assert.Equal(new string('a', 64), reader.GetString(7));
                Assert.Equal(long.MaxValue, reader.GetInt64(10));
                Assert.Equal(long.MaxValue - 1, reader.GetInt64(11));
                Assert.True(reader.GetBoolean(13));
                Assert.False(await reader.ReadAsync());
            }
            await using (var second = new NpgsqlCommand("""
                SELECT * FROM reporting.list_sql_volume_snapshot(
                    @target,@run,1,@after_key,1);
                """, readConnection))
            {
                second.Parameters.AddWithValue("target", work.TargetId.Value);
                second.Parameters.AddWithValue("run", runId.Value);
                second.Parameters.AddWithValue("after_key", Convert.FromHexString(new string('a', 64)));
                await using NpgsqlDataReader reader = await second.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(new string('b', 64), reader.GetString(7));
                Assert.True(reader.IsDBNull(10));
                Assert.True(reader.IsDBNull(11));
                Assert.False(reader.GetBoolean(13));
                Assert.False(await reader.ReadAsync());
            }
            await using (var staleRevision = new NpgsqlCommand("""
                SELECT * FROM reporting.list_sql_volume_snapshot(
                    @target,@run,2,@after_key,1);
                """, readConnection))
            {
                staleRevision.Parameters.AddWithValue("target", work.TargetId.Value);
                staleRevision.Parameters.AddWithValue("run", runId.Value);
                staleRevision.Parameters.AddWithValue("after_key", Convert.FromHexString(new string('a', 64)));
                Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(
                    () => staleRevision.ExecuteNonQueryAsync())).SqlState);
            }
            await using (var wrongTarget = new NpgsqlCommand("""
                SELECT * FROM reporting.list_sql_volume_snapshot(
                    @target,NULL::uuid,NULL::bigint,NULL::bytea,1);
                """, readConnection))
            {
                wrongTarget.Parameters.AddWithValue("target", Guid.NewGuid());
                Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(
                    () => wrongTarget.ExecuteNonQueryAsync())).SqlState);
            }
        }

        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var service = new SqlVolumeReadService(new PostgreSqlSqlVolumeReadPort(server));
            var authorized = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-711"),
                AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([work.TargetId]))]);
            SqlVolumeReadPage first = Assert.IsType<SqlVolumeReadPage>(await service.ReadAsync(
                authorized, new SqlVolumeReadRequest(work.TargetId, 1, null, Timeout),
                CancellationToken.None));
            Assert.Equal(SqlVolumeEvidenceState.Current, first.State);
            Assert.Equal(long.MaxValue, Assert.Single(first.Items).TotalBytes);
            SqlVolumeReadCursor cursor = Assert.IsType<SqlVolumeReadCursor>(first.NextCursor);
            var protector = new SqlVolumeCursorProtector(new EphemeralDataProtectionProvider());
            string token = protector.Protect(cursor);
            SqlVolumeReadCursor decoded = protector.Unprotect(token);
            Assert.Equal(cursor.TargetId, decoded.TargetId);
            Assert.Equal(cursor.RunId, decoded.RunId);
            Assert.Equal(cursor.AfterVolumeKey, decoded.AfterVolumeKey);
            Assert.Throws<ArgumentException>(() => protector.Unprotect(token[..^1] + "!"));
            SqlVolumeReadPage second = Assert.IsType<SqlVolumeReadPage>(await service.ReadAsync(
                authorized, new SqlVolumeReadRequest(work.TargetId, 1, decoded, Timeout),
                CancellationToken.None));
            Assert.Null(Assert.Single(second.Items).TotalBytes);
            Assert.Null(second.NextCursor);
            var other = new MonitoredInstanceId(Guid.NewGuid());
            var mixedGrant = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-712"),
                AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([other])),
                 new RoleAuthorizationGrant(ApplicationRole.Auditor,
                    TargetAuthorizationScope.ForTargets([work.TargetId]))]);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(
                mixedGrant, new SqlVolumeReadRequest(work.TargetId, 1, null, Timeout),
                CancellationToken.None).AsTask());
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
            string path = $"/api/v1/observation-targets/{work.TargetId.Value:D}/resources/volumes";
            using HttpResponseMessage firstResponse = await http.GetAsync(path + "?limit=1");
            Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
            using JsonDocument firstBody = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            JsonElement firstRoot = firstBody.RootElement;
            Assert.Equal("current", firstRoot.GetProperty("state").GetString());
            JsonElement firstItem = Assert.Single(firstRoot.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.String, firstItem.GetProperty("totalBytes").ValueKind);
            Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                firstItem.GetProperty("totalBytes").GetString());
            Assert.False(firstItem.TryGetProperty("volumePath", out _));
            string next = firstRoot.GetProperty("nextCursor").GetString()!;
            using HttpResponseMessage secondResponse = await http.GetAsync(
                path + "?limit=1&cursor=" + Uri.EscapeDataString(next));
            Assert.Equal(System.Net.HttpStatusCode.OK, secondResponse.StatusCode);
            using JsonDocument secondBody = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
            JsonElement secondItem = Assert.Single(secondBody.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, secondItem.GetProperty("totalBytes").ValueKind);
            Assert.Equal(JsonValueKind.Null, secondBody.RootElement.GetProperty("nextCursor").ValueKind);
            using HttpResponseMessage wrongTarget = await http.GetAsync(
                $"/api/v1/observation-targets/{Guid.NewGuid():D}/resources/volumes?cursor=" +
                Uri.EscapeDataString(next));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrongTarget.StatusCode);
            using HttpResponseMessage tampered = await http.GetAsync(
                path + "?cursor=" + Uri.EscapeDataString(next[..^1] + "!"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, tampered.StatusCode);
        }

        CollectorDueWorkItem lostWork = await PrepareWorkAsync(database, runtime);
        WorkerLeaseIdentity lostLease = await AcquireAsync(leases, lostWork);
        var lostRun = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started,
            (await runtime.BeginRunAsync(new BeginCollectorRunRequest(lostWork, lostRun, lostLease, Timeout),
                CancellationToken.None)).Status);
        await leases.ReleaseAsync(new ReleaseWorkerLeaseRequest(lostLease, Timeout), CancellationToken.None);
        Assert.Equal(CollectorRunCommitStatus.LeaseLost,
            (await runtime.CommitRunAsync(Success(lostWork, lostRun, lostLease,
                Volumes(lostWork, 10)), CancellationToken.None)).Status);
        Assert.Equal(0L, await CountRowsAsync(database, lostRun));
        await using var outcome = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run;");
        outcome.Parameters.AddWithValue("run", lostRun.Value);
        Assert.Equal(0L, await outcome.ExecuteScalarAsync());
        await using (NpgsqlDataSource server = database.CreateServerDataSource())
        {
            var service = new SqlVolumeReadService(new PostgreSqlSqlVolumeReadPort(server));
            var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-713"),
                AuthorizationPrincipalState.Active,
                [new RoleAuthorizationGrant(ApplicationRole.Viewer,
                    TargetAuthorizationScope.ForTargets([lostWork.TargetId]))]);
            SqlVolumeReadPage unavailable = Assert.IsType<SqlVolumeReadPage>(await service.ReadAsync(
                authorization, new SqlVolumeReadRequest(lostWork.TargetId, 25, null, Timeout),
                CancellationToken.None));
            Assert.Equal(SqlVolumeEvidenceState.Unavailable, unavailable.State);
            Assert.Equal("not_collected", unavailable.Reason);
            Assert.Empty(unavailable.Items);
            Assert.True(unavailable.HasVisibilityGap);
        }
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
            VALUES (@target,@key,'Volume commit target','sql01',1433,interval '5 seconds',
              'windows_integrated_service_identity','mandatory_validated','active',1,
              statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET enabled=(collector_id IN ('storage.volume','engine.core','database.files')),
                last_outcome=CASE WHEN collector_id IN ('engine.core','database.files')
                                  THEN 'succeeded' ELSE last_outcome END
            WHERE instance_id=@target;
            """);
        seed.Parameters.AddWithValue("target", target);
        seed.Parameters.AddWithValue("key", $"volume.commit.{target:N}");
        await seed.ExecuteNonQueryAsync();
        return Assert.Single((await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(16, Timeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target && item.CollectorId.Value == "storage.volume");
    }

    private static async Task<WorkerLeaseIdentity> AcquireAsync(
        PostgreSqlWorkerLeasePort leases, CollectorDueWorkItem work)
    {
        LeaseAcquisitionResult acquisition = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/storage.volume/{work.TargetId.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        Assert.Equal(LeaseAcquisitionStatus.Acquired, acquisition.Status);
        return Assert.IsType<WorkerLease>(acquisition.Lease).Identity;
    }

    private static CollectorPayload Volumes(CollectorDueWorkItem work, long available) =>
        new(sqlVolumes: new SqlVolumeObservationBatch([
            new SqlVolumeObservation(work.TargetId, work.TargetRevision, new string('a', 64),
                SqlVolumeIdentityKind.VolumeId, 3, long.MaxValue, available, work.RepositoryTimeUtc),
            new SqlVolumeObservation(work.TargetId, work.TargetRevision, new string('b', 64),
                SqlVolumeIdentityKind.FileScopedUnknown, 1, null, null, work.RepositoryTimeUtc),
        ]));

    private static CommitCollectorRunRequest Success(
        CollectorDueWorkItem work, CollectorRunId runId, WorkerLeaseIdentity lease, CollectorPayload payload)
    {
        var summary = new CollectorRunSummary(runId, work.TargetId, work.TargetRevision,
            work.CollectorId, work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(5), 1,
            new CollectorRunAccounting(4, payload.ItemCount,
                payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
    }

    private static async Task<long> CountRowsAsync(RepositoryTestDatabase database, CollectorRunId runId)
    {
        await using var count = database.DataSource.CreateCommand(
            "SELECT count(*) FROM telemetry.sql_volume_snapshot WHERE run_id=@run;");
        count.Parameters.AddWithValue("run", runId.Value);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
