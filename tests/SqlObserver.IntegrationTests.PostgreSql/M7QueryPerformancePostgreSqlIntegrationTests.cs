using System.Security.Cryptography;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Collectors;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class M7QueryPerformancePostgreSqlIntegrationTests
{
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(30));
    private readonly PostgreSql18Fixture fixture;
    public M7QueryPerformancePostgreSqlIntegrationTests(PostgreSql18Fixture fixture) => this.fixture = fixture;

    [Fact]
    [Trait("Category", "RequiresPostgreSql")]
    public async Task MigrationExposesFencedCommitAndBoundedProjectionWithoutTableGrants()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var runner = new PostgreSqlMigrationPort(database.DataSource, PostgreSqlMigrationCatalog.LoadEmbedded());
        MigrationBatchResult result = await runner.ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromMinutes(2))), CancellationToken.None);
        Assert.False(result.HasFailures);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('events.query_performance_observation') IS NOT NULL, EXISTS (SELECT 1 FROM pg_proc WHERE proname='commit_query_performance_collection_run_canonical'), EXISTS (SELECT 1 FROM pg_proc WHERE proname='get_top_queries_projection'), NOT EXISTS (SELECT 1 FROM pg_proc WHERE proname='list_query_performance_projection'), (SELECT relrowsecurity FROM pg_class WHERE oid='events.query_performance_run'::regclass), NOT has_table_privilege('sqlobserver_server','events.query_performance_observation','SELECT');", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        await reader.DisposeAsync();
        await using var privileges = new NpgsqlCommand("""
            SELECT proname,
                   has_function_privilege('sqlobserver_collector',oid,'EXECUTE')
            FROM pg_proc
            WHERE pronamespace='control'::regnamespace
              AND proname IN ('commit_query_performance_collection_run_canonical',
                              'commit_query_performance_with_text',
                              'commit_query_text_links');
            """, connection);
        await using NpgsqlDataReader privilegeRows = await privileges.ExecuteReaderAsync();
        var callable = new Dictionary<string, bool>(StringComparer.Ordinal);
        while (await privilegeRows.ReadAsync()) callable[privilegeRows.GetString(0)] = privilegeRows.GetBoolean(1);
        Assert.Equal(3, callable.Count);
        Assert.False(callable["commit_query_performance_collection_run_canonical"]);
        Assert.True(callable["commit_query_performance_with_text"]);
        Assert.False(callable["commit_query_text_links"]);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task QueryTextLinkCommitsWithRunOrRollsBackBoth(bool payloadExists, bool attachReference)
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(migrated.HasFailures);
        MonitoredInstanceId target = new(Guid.NewGuid());
        await using (var setup = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
                (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                 authentication_mode,transport_security_mode,lifecycle_state,revision,
                 created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'M7 content target','sql01',1433,interval '5 seconds',
                    'windows_integrated_service_identity','mandatory_validated','active',1,
                    statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET last_outcome='succeeded',last_completed_at=clock_timestamp()
            WHERE instance_id=@target AND collector_id IN ('engine.core','database.inventory');
            """))
        {
            setup.Parameters.AddWithValue("target", target.Value);
            setup.Parameters.AddWithValue("key", $"m7-content.{target.Value:N}");
            await setup.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(ListDueCollectorWorkRequest.MaximumItems, Timeout),
            CancellationToken.None)).Items.Single(item => item.CollectorId.Value == "queries.performance");
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/queries.performance/{target.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status);

        byte[] fingerprint = RandomNumberGenerator.GetBytes(32);
        var protectedPayload = new ProtectedSensitivePayload(SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(fingerprint), "AES-256-GCM", "m7-content-test",
            RandomNumberGenerator.GetBytes(12), RandomNumberGenerator.GetBytes(16),
            RandomNumberGenerator.GetBytes(64));
        SensitivePayloadReference missingReference = new(new SensitivePayloadId(Guid.NewGuid()),
            SensitivePayloadKind.QueryText, protectedPayload.Fingerprint);
        DateTimeOffset end = work.RepositoryTimeUtc.AddSeconds(-1);
        QueryOpaqueIdentity query = new(5, new string('a', 64));
        QueryPerformanceObservation observation = new(target, work.TargetRevision, query, null,
            QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite,
            QueryMetricSemantics.QueryStoreInterval, new QueryPerformanceMetricSet(1, 2, 1, 3, 0, 1),
            end.AddMinutes(-1), end, end, QueryCoverage.Complete, true, false,
            attachReference && !payloadExists ? missingReference : null,
            protectedContent: attachReference && payloadExists ? protectedPayload : null);
        CollectorPayload payload = new(queryPerformance: new QueryPerformanceObservationBatch([observation]),
            queryPerformanceStatuses: [new QueryPerformanceDatabaseStatus(5,
                QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false, false,
                1, 256, QueryStoreState.ReadWrite)]);
        CollectorPayload unlinkedPayload = payload;
        if (payloadExists && attachReference)
        {
            payload = await new QueryPerformanceProtectedContentCommitter(
                new PostgreSqlSensitivePayloadPort(collector)).WriteAsync(
                    payload, target, lease, Timeout, CancellationToken.None);
            Assert.NotNull(Assert.Single(payload.QueryPerformance.Items).ContentReference);
            Assert.Null(Assert.Single(payload.QueryPerformance.Items).ProtectedContent);
        }
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount,
            payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(run, target, work.TargetRevision, work.CollectorId,
            work.CollectorManifestVersion, work.OutputSchemaVersion, CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed, TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);

        if (payloadExists && attachReference)
            await Assert.ThrowsAsync<InvalidDataException>(() => runtime.CommitRunAsync(
                new CommitCollectorRunRequest(work, summary, unlinkedPayload,
                    CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout),
                CancellationToken.None).AsTask());

        if (payloadExists || !attachReference)
        {
            Assert.Equal(CollectorRunCommitStatus.Committed,
                (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            Assert.Equal(CollectorRunCommitStatus.Replayed,
                (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            if (attachReference)
            {
                await using NpgsqlDataSource server = database.CreateServerDataSource();
                var queryText = new PostgreSqlQueryTextReadRepositoryPort(server);
                var readRequest = new QueryTextReadRequest(target, run.Value, query, Timeout);
                ProtectedSensitivePayload? read = await queryText.ReadAsync(readRequest, CancellationToken.None);
                Assert.NotNull(read);
                Assert.Equal(protectedPayload.Fingerprint.ToArray(), read.Fingerprint.ToArray());
                Assert.Equal(protectedPayload.GetCiphertext(), read.GetCiphertext());
                Assert.Null(await queryText.ReadAsync(readRequest with { CollectionRunId = Guid.NewGuid() }, CancellationToken.None));
                Assert.Null(await queryText.ReadAsync(readRequest with { TargetId = new MonitoredInstanceId(Guid.NewGuid()) }, CancellationToken.None));
                Assert.Null(await queryText.ReadAsync(readRequest with { Query = new QueryOpaqueIdentity(5, new string('b', 64)) }, CancellationToken.None));
                await queryText.AuditAsync(readRequest, "S-1-5-21-100", "opened", CancellationToken.None);
                await using NpgsqlConnection auditConnection = await database.DataSource.OpenConnectionAsync();
                await using var audit = new NpgsqlCommand("SELECT count(*) FROM events.query_text_access_audit WHERE instance_id=@target AND collection_run_id=@run AND outcome='opened';", auditConnection);
                audit.Parameters.AddWithValue("target", target.Value);
                audit.Parameters.AddWithValue("run", run.Value);
                Assert.Equal(1L, (long)(await audit.ExecuteScalarAsync())!);
                await using var privileges = new NpgsqlCommand("SELECT has_function_privilege('sqlobserver_server','control.get_query_text_payload(uuid,uuid,integer,bytea)','EXECUTE'), has_function_privilege('sqlobserver_collector','control.get_query_text_payload(uuid,uuid,integer,bytea)','EXECUTE'), has_table_privilege('sqlobserver_server','events.query_text_access_audit','SELECT');", auditConnection);
                await using NpgsqlDataReader privilegeRows = await privileges.ExecuteReaderAsync();
                Assert.True(await privilegeRows.ReadAsync());
                Assert.True(privilegeRows.GetBoolean(0));
                Assert.False(privilegeRows.GetBoolean(1));
                Assert.False(privilegeRows.GetBoolean(2));
                await privilegeRows.DisposeAsync();

                var divergentReference = new SensitivePayloadReference(
                    new SensitivePayloadId(Guid.NewGuid()), SensitivePayloadKind.QueryText,
                    protectedPayload.Fingerprint);
                QueryPerformanceObservation divergent = new(target, work.TargetRevision, query, null,
                    QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite,
                    QueryMetricSemantics.QueryStoreInterval, observation.Metrics,
                    observation.IntervalStartUtc, observation.IntervalEndUtc,
                    observation.ObservedAtUtc, QueryCoverage.Complete, true, false,
                    divergentReference);
                CollectorPayload divergentPayload = new(
                    queryPerformance: new QueryPerformanceObservationBatch([divergent]),
                    queryPerformanceStatuses: payload.QueryPerformanceStatuses);
                await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(
                    new CommitCollectorRunRequest(work, summary, divergentPayload,
                        CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout),
                    CancellationToken.None).AsTask());

                await using NpgsqlConnection scoped = await collector.OpenConnectionAsync();
                await using (var setScope = new NpgsqlCommand(
                    "SELECT set_config('sqlobserver.target_scope',@target,false);", scoped))
                {
                    setScope.Parameters.AddWithValue("target", target.Value.ToString("D"));
                    await setScope.ExecuteNonQueryAsync();
                }
                await using var appendAfterCommit = new NpgsqlCommand(
                    "SELECT control.commit_query_text_links(@run,@target,@fence,'[]'::jsonb);", scoped);
                appendAfterCommit.Parameters.AddWithValue("run", run.Value);
                appendAfterCommit.Parameters.AddWithValue("target", target.Value);
                appendAfterCommit.Parameters.AddWithValue("fence", lease.FencingToken.Value);
                PostgresException denied = await Assert.ThrowsAsync<PostgresException>(async () =>
                    await appendAfterCommit.ExecuteScalarAsync());
                Assert.Equal("42501", denied.SqlState);
            }
        }
        else
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.CommitRunAsync(commit, CancellationToken.None).AsTask());
        }
        await using NpgsqlConnection verifyConnection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_run WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_query WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_content_link WHERE collection_run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run);
            """, verifyConnection);
        verify.Parameters.AddWithValue("run", run.Value);
        await using NpgsqlDataReader rows = await verify.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        long expected = payloadExists || !attachReference ? 1 : 0;
        for (int column = 0; column < 4; column++)
            Assert.Equal(column == 2 && !attachReference ? 0 : expected, rows.GetInt64(column));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlanLinksBindTwoPlansToOnePayloadAndRollbackMissingContent(bool payloadExists)
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        MigrationBatchResult migrated = await new PostgreSqlMigrationPort(database.DataSource)
            .ApplyPendingAsync(new MigrationApplyRequest(MigrationBatchResult.MaximumResults, Timeout), CancellationToken.None);
        Assert.False(migrated.HasFailures);
        MonitoredInstanceId target = new(Guid.NewGuid());
        await using (var setup = database.DataSource.CreateCommand("""
            INSERT INTO control.observation_target
                (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
                 authentication_mode,transport_security_mode,lifecycle_state,revision,
                 created_at,updated_at,discovery_requested_at)
            VALUES (@target,@key,'M7 plan target','sql01',1433,interval '5 seconds',
                    'windows_integrated_service_identity','mandatory_validated','active',1,
                    statement_timestamp(),statement_timestamp(),statement_timestamp());
            UPDATE control.collector_schedule
            SET last_outcome='succeeded',last_completed_at=clock_timestamp()
            WHERE instance_id=@target AND collector_id IN ('engine.core','database.inventory');
            """))
        {
            setup.Parameters.AddWithValue("target", target.Value);
            setup.Parameters.AddWithValue("key", $"m7-plan.{target.Value:N}");
            await setup.ExecuteNonQueryAsync();
        }
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        var leases = new PostgreSqlWorkerLeasePort(collector);
        CollectorDueWorkItem work = (await runtime.ListDueAsync(
            new ListDueCollectorWorkRequest(ListDueCollectorWorkRequest.MaximumItems, Timeout),
            CancellationToken.None)).Items.Single(item => item.CollectorId.Value == "queries.performance");
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"collector/run/queries.performance/{target.Value:N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
            CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = new(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(
            new BeginCollectorRunRequest(work, run, lease, Timeout), CancellationToken.None)).Status);

        var protectedPlan = new ProtectedSensitivePayload(SensitivePayloadKind.ExecutionPlan,
            new SensitivePayloadFingerprint(RandomNumberGenerator.GetBytes(32)),
            "AES-256-GCM", "m7-plan-test", RandomNumberGenerator.GetBytes(12),
            RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(64));
        SensitivePayloadReference missing = new(new SensitivePayloadId(Guid.NewGuid()),
            SensitivePayloadKind.ExecutionPlan, protectedPlan.Fingerprint);
        DateTimeOffset end = work.RepositoryTimeUtc.AddSeconds(-1);
        QueryOpaqueIdentity query = new(5, new string('a', 64));
        QueryPerformanceObservation[] observations = Enumerable.Range(0, 2).Select(index =>
            new QueryPerformanceObservation(target, work.TargetRevision, query,
                new PlanOpaqueIdentity(query, new string((char)('b' + index), 64)),
                QueryPerformanceSource.QueryStore, QueryStoreState.ReadWrite,
                QueryMetricSemantics.QueryStoreInterval,
                new QueryPerformanceMetricSet(1, 2, 1, 3, 0, 1),
                end.AddMinutes(-1), end, end, QueryCoverage.Complete, true, false,
                planContentReference: payloadExists ? null : missing,
                protectedPlanContent: payloadExists ? protectedPlan : null)).ToArray();
        CollectorPayload payload = new(
            queryPerformance: new QueryPerformanceObservationBatch(observations),
            queryPerformanceStatuses: [new QueryPerformanceDatabaseStatus(5,
                QueryPerformanceReadStatus.QueryStoreRows, "query_store_read", false,
                false, 2, 512, QueryStoreState.ReadWrite)]);
        if (payloadExists)
        {
            payload = await new QueryPerformanceProtectedContentCommitter(
                new PostgreSqlSensitivePayloadPort(collector)).WriteAsync(
                    payload, target, lease, Timeout, CancellationToken.None);
            SensitivePayloadReference[] references = payload.QueryPerformance.Items
                .Select(item => Assert.IsType<SensitivePayloadReference>(item.PlanContentReference))
                .ToArray();
            Assert.Equal(references[0].PayloadId, references[1].PayloadId);
            Assert.All(payload.QueryPerformance.Items, item => Assert.Null(item.ProtectedPlanContent));
        }
        var accounting = new CollectorRunAccounting(payload.ItemCount, payload.ItemCount,
            payload.EstimatedSizeBytes, payload.EstimatedSizeBytes);
        var summary = new CollectorRunSummary(run, target, work.TargetRevision, work.CollectorId,
            work.CollectorManifestVersion, work.OutputSchemaVersion,
            CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(1), 1, accounting, CollectorLossEvidence.None);
        var commit = new CommitCollectorRunRequest(work, summary, payload,
            CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
        if (payloadExists)
        {
            Assert.Equal(CollectorRunCommitStatus.Committed,
                (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            Assert.Equal(CollectorRunCommitStatus.Replayed,
                (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
            await using NpgsqlDataSource server = database.CreateServerDataSource();
            var planReader = new PostgreSqlQueryPlanReadRepositoryPort(server);
            var planRequest = new QueryPlanReadRequest(target, run.Value,
                new PlanOpaqueIdentity(query, new string('b', 64)), Timeout);
            QueryPlanMetadataDto? metadata = await new PostgreSqlQueryPerformanceApiProjectionPort(server)
                .GetPlanAsync(new QueryPlanMetadataRequest(target, planRequest.Plan, Timeout),
                    CancellationToken.None);
            Assert.NotNull(metadata);
            Assert.True(metadata.ContentAvailable);
            ProtectedSensitivePayload? read = await planReader.ReadAsync(
                planRequest, CancellationToken.None);
            Assert.NotNull(read);
            Assert.Equal(protectedPlan.Fingerprint.ToArray(), read.Fingerprint.ToArray());
            Assert.Equal(protectedPlan.GetCiphertext(), read.GetCiphertext());
            Assert.Null(await planReader.ReadAsync(planRequest with
                { CollectionRunId = Guid.NewGuid() }, CancellationToken.None));
            Assert.Null(await planReader.ReadAsync(planRequest with
                { TargetId = new MonitoredInstanceId(Guid.NewGuid()) }, CancellationToken.None));
            Assert.Null(await planReader.ReadAsync(planRequest with
                { Plan = new PlanOpaqueIdentity(query, new string('d', 64)) }, CancellationToken.None));
            await planReader.AuditAsync(planRequest, "S-1-5-21-100", "opened", CancellationToken.None);

            var orphanPlan = new ProtectedSensitivePayload(SensitivePayloadKind.ExecutionPlan,
                new SensitivePayloadFingerprint(RandomNumberGenerator.GetBytes(32)),
                "AES-256-GCM", "m7-orphan-plan-test", RandomNumberGenerator.GetBytes(12),
                RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(64));
            SensitivePayloadReference orphan = await new PostgreSqlSensitivePayloadPort(collector)
                .GetOrAddAsync(new SensitivePayloadGetOrAddRequest(target, orphanPlan, lease, Timeout),
                    CancellationToken.None);
            Guid linkedId = Assert.IsType<SensitivePayloadReference>(
                payload.QueryPerformance.Items[0].PlanContentReference).PayloadId.Value;
            await using (var age = database.DataSource.CreateCommand("""
                UPDATE security.protected_diagnostic_payload
                SET created_at=clock_timestamp()-interval '9 days',
                    last_seen_at=clock_timestamp()-interval '9 days'
                WHERE payload_id IN (@linked,@orphan);
                """))
            {
                age.Parameters.AddWithValue("linked", linkedId);
                age.Parameters.AddWithValue("orphan", orphan.PayloadId.Value);
                Assert.Equal(2, await age.ExecuteNonQueryAsync());
            }
            LeaseAcquisitionResult retentionAcquired = await leases.AcquireAsync(
                new AcquireWorkerLeaseRequest(new WorkerLeaseKey("retention/maintenance"),
                    new WorkerExecutionId(Guid.NewGuid()),
                    new WorkerLeaseDuration(TimeSpan.FromMinutes(1)), Timeout),
                CancellationToken.None);
            WorkerLeaseIdentity retentionLease = Assert.IsType<WorkerLease>(retentionAcquired.Lease).Identity;
            var retention = new PostgreSqlPartitionMaintenancePort(collector);
            Assert.Equal(1, await retention.PruneOrphanQueryPlanPayloadsAsync(
                retentionLease, Timeout, CancellationToken.None));
            Assert.Equal(0, await retention.PruneOrphanQueryPlanPayloadsAsync(
                retentionLease, Timeout, CancellationToken.None));
            await using var remaining = database.DataSource.CreateCommand("""
                SELECT count(*) FROM security.protected_diagnostic_payload
                WHERE payload_id=@linked;
                """);
            remaining.Parameters.AddWithValue("linked", linkedId);
            Assert.Equal(1L, await remaining.ExecuteScalarAsync());
        }
        else
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.CommitRunAsync(commit, CancellationToken.None).AsTask());
        }
        await using NpgsqlConnection verifyConnection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM events.query_performance_run WHERE collection_run_id=@run),
                   (SELECT count(*) FROM events.query_performance_plan_content_link WHERE collection_run_id=@run),
                   (SELECT count(DISTINCT content_reference) FROM events.query_performance_plan_content_link WHERE collection_run_id=@run),
                   (SELECT count(*) FROM telemetry.collection_run_outcome WHERE run_id=@run),
                   (SELECT count(*) FROM events.query_plan_access_audit WHERE collection_run_id=@run AND outcome='opened');
            """, verifyConnection);
        verify.Parameters.AddWithValue("run", run.Value);
        await using NpgsqlDataReader rows = await verify.ExecuteReaderAsync();
        Assert.True(await rows.ReadAsync());
        Assert.Equal(payloadExists ? 1L : 0L, rows.GetInt64(0));
        Assert.Equal(payloadExists ? 2L : 0L, rows.GetInt64(1));
        Assert.Equal(payloadExists ? 1L : 0L, rows.GetInt64(2));
        Assert.Equal(payloadExists ? 1L : 0L, rows.GetInt64(3));
        Assert.Equal(payloadExists ? 1L : 0L, rows.GetInt64(4));
    }

}
