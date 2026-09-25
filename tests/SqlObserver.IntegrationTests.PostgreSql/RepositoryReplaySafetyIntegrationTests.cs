using System.Security.Cryptography;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Diagnostics;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class RepositoryReplaySafetyIntegrationTests
{
    private static readonly RepositoryCallTimeout DefaultTimeout = new(TimeSpan.FromSeconds(30));
    private static readonly WorkerLeaseDuration DefaultLeaseDuration = new(TimeSpan.FromMinutes(2));

    private readonly PostgreSql18Fixture _fixture;

    public RepositoryReplaySafetyIntegrationTests(PostgreSql18Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MetricReplayDeduplicatesExactContentAndDivergenceRollsBackWholeBatch()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "metric-replay");
        DateTimeOffset observedAt = new(2026, 10, 10, 10, 10, 10, TimeSpan.Zero);
        await EnsurePartitionAsync(
            collectorDataSource,
            lease,
            new PartitionSetName("raw_metric_sample"),
            PartitionGranularity.Daily,
            observedAt);

        var limits = new IngestionLimits(10, 100_000, 20_000);
        var ingestion = new PostgreSqlIngestionPort(collectorDataSource);
        var sampleId = new MetricSampleId(Guid.NewGuid());
        var instance = new MonitoredInstanceId(instanceId);
        MetricSample first = CreateMetricSample(sampleId, instance, observedAt, 1, "primary");

        IngestionResult inserted = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(new TelemetryBatch([first], limits), lease.Identity, DefaultTimeout),
            CancellationToken.None);
        MetricSample exactReplay = CreateMetricSample(sampleId, instance, observedAt, 1, "primary");
        IngestionResult replayed = await ingestion.IngestTelemetryAsync(
            new TelemetryIngestionRequest(
                new TelemetryBatch([exactReplay], limits),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(1, inserted.InsertedCount);
        Assert.Equal(1, replayed.DuplicateCount);
        Assert.Equal(0, replayed.PersistedBytes);

        MetricSample divergent = CreateMetricSample(sampleId, instance, observedAt, 99, "conflicting");
        var unrelatedId = new MetricSampleId(Guid.NewGuid());
        MetricSample otherwiseInsertable = CreateMetricSample(
            unrelatedId,
            instance,
            observedAt.AddTicks(TimeSpan.TicksPerMicrosecond),
            2,
            "new");

        InvalidDataException conflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestTelemetryAsync(
                new TelemetryIngestionRequest(
                    new TelemetryBatch([otherwiseInsertable, divergent], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));

        Assert.Contains("divergent content", conflict.Message, StringComparison.Ordinal);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                count(*),
                count(*) FILTER (WHERE sample_id = @unrelated_id),
                max(metric_value) FILTER (WHERE sample_id = @first_id)
            FROM telemetry.raw_metric_sample;
            """,
            connection);
        verify.Parameters.AddWithValue("unrelated_id", unrelatedId.Value);
        verify.Parameters.AddWithValue("first_id", sampleId.Value);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(1D, reader.GetDouble(2));
    }

    [Fact]
    public async Task EventReplayDeduplicatesExactContentAndDivergenceRollsBackWholeBatch()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid instanceId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, instanceId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "event-replay");
        DateTimeOffset occurredAt = new(2026, 11, 11, 11, 11, 11, TimeSpan.Zero);
        await EnsurePartitionAsync(
            collectorDataSource,
            lease,
            new PartitionSetName("diagnostic_event"),
            PartitionGranularity.Monthly,
            occurredAt);

        var limits = new IngestionLimits(10, 100_000, 20_000);
        var ingestion = new PostgreSqlIngestionPort(collectorDataSource);
        var eventId = new DiagnosticEventId(Guid.NewGuid());
        var instance = new MonitoredInstanceId(instanceId);
        DateTimeOffset collectedAt = occurredAt.AddSeconds(1);
        DiagnosticEventEnvelope first = CreateEvent(eventId, instance, occurredAt, collectedAt, "deadlock.test");

        IngestionResult inserted = await ingestion.IngestDiagnosticEventsAsync(
            new DiagnosticEventIngestionRequest(
                new DiagnosticEventBatch([first], limits),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
        DiagnosticEventEnvelope exactReplay = CreateEvent(
            eventId,
            instance,
            occurredAt,
            collectedAt,
            "deadlock.test");
        IngestionResult replayed = await ingestion.IngestDiagnosticEventsAsync(
            new DiagnosticEventIngestionRequest(
                new DiagnosticEventBatch([exactReplay], limits),
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(1, inserted.InsertedCount);
        Assert.Equal(1, replayed.DuplicateCount);

        DiagnosticEventEnvelope divergent = CreateEvent(
            eventId,
            instance,
            occurredAt,
            collectedAt,
            "deadlock.conflicting");
        var unrelatedId = new DiagnosticEventId(Guid.NewGuid());
        DiagnosticEventEnvelope otherwiseInsertable = CreateEvent(
            unrelatedId,
            instance,
            occurredAt.AddTicks(TimeSpan.TicksPerMicrosecond),
            collectedAt,
            "deadlock.new");

        InvalidDataException conflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestDiagnosticEventsAsync(
                new DiagnosticEventIngestionRequest(
                    new DiagnosticEventBatch([otherwiseInsertable, divergent], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));

        Assert.Contains("divergent content", conflict.Message, StringComparison.Ordinal);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand(
            """
            SELECT
                count(*),
                count(*) FILTER (WHERE event_id = @unrelated_id),
                max(event_kind) FILTER (WHERE event_id = @first_id)
            FROM events.diagnostic_event;
            """,
            connection);
        verify.Parameters.AddWithValue("unrelated_id", unrelatedId.Value);
        verify.Parameters.AddWithValue("first_id", eventId.Value);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal("deadlock.test", reader.GetString(2));
        await reader.DisposeAsync();

        await using (var changeSeverity = new NpgsqlCommand(
            """
            UPDATE events.diagnostic_event
            SET severity = 1
            WHERE occurred_at = @occurred_at
              AND event_id = @event_id;
            """,
            connection))
        {
            changeSeverity.Parameters.AddWithValue("occurred_at", occurredAt);
            changeSeverity.Parameters.AddWithValue("event_id", eventId.Value);
            Assert.Equal(1, await changeSeverity.ExecuteNonQueryAsync());
        }

        InvalidDataException severityConflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestDiagnosticEventsAsync(
                new DiagnosticEventIngestionRequest(
                    new DiagnosticEventBatch([exactReplay], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));
        Assert.Contains("divergent content", severityConflict.Message, StringComparison.Ordinal);

        await using (var changeMetadata = new NpgsqlCommand(
            """
            UPDATE events.diagnostic_event
            SET severity = 0,
                safe_metadata = '{"source":"legacy"}'::jsonb
            WHERE occurred_at = @occurred_at
              AND event_id = @event_id;
            """,
            connection))
        {
            changeMetadata.Parameters.AddWithValue("occurred_at", occurredAt);
            changeMetadata.Parameters.AddWithValue("event_id", eventId.Value);
            Assert.Equal(1, await changeMetadata.ExecuteNonQueryAsync());
        }

        InvalidDataException metadataConflict = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await ingestion.IngestDiagnosticEventsAsync(
                new DiagnosticEventIngestionRequest(
                    new DiagnosticEventBatch([exactReplay], limits),
                    lease.Identity,
                    DefaultTimeout),
                CancellationToken.None));
        Assert.Contains("divergent content", metadataConflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SensitiveFingerprintRetryIsImmutableFirstWriteWins()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid targetId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        var target = new MonitoredInstanceId(targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "payload-retry");
        var port = new PostgreSqlSensitivePayloadPort(collectorDataSource);
        byte[] fingerprint = Enumerable.Repeat((byte)41, SensitivePayloadFingerprint.RequiredLength).ToArray();
        ProtectedSensitivePayload first = CreateProtectedPayload(fingerprint, material: 11);
        ProtectedSensitivePayload randomizedRetry = CreateProtectedPayload(fingerprint, material: 22);

        SensitivePayloadReference inserted = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(target, first, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        SensitivePayloadReference exactReplay = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(target, first, lease.Identity, DefaultTimeout),
            CancellationToken.None);
        SensitivePayloadReference randomized = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(target, randomizedRetry, lease.Identity, DefaultTimeout),
            CancellationToken.None);

        Assert.Equal(inserted.PayloadId, exactReplay.PayloadId);
        Assert.Equal(inserted.PayloadId, randomized.PayloadId);
        PersistedPayload persisted = await ReadPersistedPayloadAsync(database, fingerprint);
        Assert.Equal(1L, persisted.Count);
        Assert.Equal(first.ProtectionAlgorithm, persisted.Algorithm);
        Assert.Equal(first.KeyIdentifier, persisted.KeyIdentifier);
        Assert.Equal(first.GetNonce(), persisted.Nonce);
        Assert.Equal(first.GetAuthenticationTag(), persisted.AuthenticationTag);
        Assert.Equal(first.GetCiphertext(), persisted.Ciphertext);
    }

    [Fact]
    public async Task ConcurrentSensitiveFingerprintCollisionCommitsOneCompleteRepresentation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid targetId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        var target = new MonitoredInstanceId(targetId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease firstLease = await AcquireLeaseAsync(collectorDataSource, "payload-concurrency-a");
        WorkerLease secondLease = await AcquireLeaseAsync(collectorDataSource, "payload-concurrency-b");
        var port = new PostgreSqlSensitivePayloadPort(collectorDataSource);
        byte[] fingerprint = Enumerable.Repeat((byte)73, SensitivePayloadFingerprint.RequiredLength).ToArray();
        ProtectedSensitivePayload first = CreateProtectedPayload(fingerprint, material: 31);
        ProtectedSensitivePayload second = CreateProtectedPayload(fingerprint, material: 47);

        Task<SensitivePayloadReference> firstWrite = port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(target, first, firstLease.Identity, DefaultTimeout),
            CancellationToken.None).AsTask();
        Task<SensitivePayloadReference> secondWrite = port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(target, second, secondLease.Identity, DefaultTimeout),
            CancellationToken.None).AsTask();
        SensitivePayloadReference[] references = await Task.WhenAll(firstWrite, secondWrite);

        Assert.Equal(references[0].PayloadId, references[1].PayloadId);
        PersistedPayload persisted = await ReadPersistedPayloadAsync(database, fingerprint);
        bool matchesFirst = PersistedPayloadMatches(first, persisted);
        bool matchesSecond = PersistedPayloadMatches(second, persisted);

        Assert.Equal(1L, persisted.Count);
        Assert.True(matchesFirst ^ matchesSecond);
    }

    [Fact]
    public async Task SensitiveFingerprintCannotBeReusedForAnotherTarget()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, firstId);
        await InsertObservationTargetAsync(database, secondId);
        await using NpgsqlDataSource collectorDataSource = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collectorDataSource, "payload-target-boundary");
        var port = new PostgreSqlSensitivePayloadPort(collectorDataSource);
        byte[] fingerprint = RandomNumberGenerator.GetBytes(SensitivePayloadFingerprint.RequiredLength);
        ProtectedSensitivePayload payload = CreateProtectedPayload(fingerprint, material: 51);
        SensitivePayloadReference original = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(new MonitoredInstanceId(firstId), payload,
                lease.Identity, DefaultTimeout), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(new MonitoredInstanceId(secondId), payload,
                lease.Identity, DefaultTimeout), CancellationToken.None));
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand(
            "SELECT instance_id FROM security.protected_diagnostic_payload WHERE fingerprint=@fingerprint;",
            connection);
        verify.Parameters.AddWithValue("fingerprint", fingerprint);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(firstId, reader.GetGuid(0));
        Assert.False(await reader.ReadAsync());
        Assert.NotEqual(Guid.Empty, original.PayloadId.Value);
    }

    [Fact]
    public async Task FencedOrphanCleanupPreservesReusedAndReferencedCiphertext()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid targetId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        var target = new MonitoredInstanceId(targetId);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLease writeLease = await AcquireLeaseAsync(collector, "payload-cleanup-fixture");
        LeaseAcquisitionResult acquiredRetention = await new PostgreSqlWorkerLeasePort(collector).AcquireAsync(
            new AcquireWorkerLeaseRequest(new WorkerLeaseKey("retention/maintenance"),
                new WorkerExecutionId(Guid.NewGuid()), DefaultLeaseDuration, DefaultTimeout),
            CancellationToken.None);
        WorkerLease retentionLease = Assert.IsType<WorkerLease>(acquiredRetention.Lease);
        var payloads = new PostgreSqlSensitivePayloadPort(collector);
        var protectedRows = Enumerable.Range(0, 4).Select(index => CreateProtectedPayload(
            RandomNumberGenerator.GetBytes(SensitivePayloadFingerprint.RequiredLength),
            material: (byte)(71 + index))).ToArray();
        SensitivePayloadReference[] references = new SensitivePayloadReference[4];
        for (int index = 0; index < references.Length; index++)
            references[index] = await payloads.GetOrAddAsync(new SensitivePayloadGetOrAddRequest(
                target, protectedRows[index], writeLease.Identity, DefaultTimeout), CancellationToken.None);

        Guid runId = Guid.NewGuid();
        byte[] query = RandomNumberGenerator.GetBytes(32);
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand("""
            UPDATE security.protected_diagnostic_payload
            SET created_at=clock_timestamp()-interval '9 days',
                last_seen_at=clock_timestamp()-interval '9 days'
            WHERE payload_id = ANY(@payload_ids);
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,
                 schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            VALUES (@run,@target,'queries.performance',1,1,1,1,'test/payload-cleanup',
                    gen_random_uuid(),1,decode(repeat('00',32),'hex'),now(),now());
            INSERT INTO events.query_performance_run
                (collection_run_id,instance_id,target_revision,window_start,window_end,source,
                 source_state,coverage,freshness,truncated,completion_digest)
            VALUES (@run,@target,1,now()-interval '5 minutes',now(),'query_store','read_write',
                    'complete',true,false,decode(repeat('00',32),'hex'));
            INSERT INTO events.query_performance_query
                (collection_run_id,instance_id,database_id,query_fingerprint)
            VALUES (@run,@target,1,@query);
            INSERT INTO events.query_performance_content_link
                (collection_run_id,instance_id,database_id,query_fingerprint,content_reference,content_available)
            VALUES (@run,@target,1,@query,@linked,true);
            INSERT INTO events.diagnostic_event
                (occurred_at,event_id,instance_id,event_kind,protected_payload_id,collected_at)
            VALUES (now(),gen_random_uuid(),@target,'test.payload',@diagnostic,now());
            """, admin))
        {
            seed.Parameters.AddWithValue("payload_ids", references.Select(reference => reference.PayloadId.Value).ToArray());
            seed.Parameters.AddWithValue("run", runId);
            seed.Parameters.AddWithValue("target", targetId);
            seed.Parameters.AddWithValue("query", query);
            seed.Parameters.AddWithValue("linked", references[2].PayloadId.Value);
            seed.Parameters.AddWithValue("diagnostic", references[3].PayloadId.Value);
            await seed.ExecuteNonQueryAsync();
        }

        SensitivePayloadReference reused = await payloads.GetOrAddAsync(new SensitivePayloadGetOrAddRequest(
            target, protectedRows[1], writeLease.Identity, DefaultTimeout), CancellationToken.None);
        Assert.Equal(references[1].PayloadId, reused.PayloadId);
        var retention = new PostgreSqlPartitionMaintenancePort(collector);
        await using (NpgsqlConnection limits = await collector.OpenConnectionAsync())
        await using (var invalid = new NpgsqlCommand("""
            SELECT system.prune_orphan_query_text_payloads(@owner,@fence,@limit);
            """, limits))
        {
            invalid.Parameters.AddWithValue("owner", retentionLease.Identity.Owner.Value);
            invalid.Parameters.AddWithValue("fence", retentionLease.Identity.FencingToken.Value);
            var limit = invalid.Parameters.Add("limit", NpgsqlTypes.NpgsqlDbType.Integer);
            limit.Value = DBNull.Value;
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await invalid.ExecuteScalarAsync())).SqlState);
            limit.Value = 101;
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await invalid.ExecuteScalarAsync())).SqlState);
        }
        await using (NpgsqlConnection held = await database.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction transaction = await held.BeginTransactionAsync())
        {
            await using var lockRow = new NpgsqlCommand("""
                SELECT payload_id FROM security.protected_diagnostic_payload
                WHERE payload_id=@id FOR UPDATE;
                """, held, transaction);
            lockRow.Parameters.AddWithValue("id", references[0].PayloadId.Value);
            Assert.Equal(references[0].PayloadId.Value, await lockRow.ExecuteScalarAsync());
            Assert.Equal(0, await retention.PruneOrphanQueryTextPayloadsAsync(
                retentionLease.Identity, DefaultTimeout, CancellationToken.None));
            await transaction.CommitAsync();
        }
        int deleted = await retention.PruneOrphanQueryTextPayloadsAsync(
            retentionLease.Identity, DefaultTimeout, CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.Equal(0, await retention.PruneOrphanQueryTextPayloadsAsync(
            retentionLease.Identity, DefaultTimeout, CancellationToken.None));
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var inspect = new NpgsqlCommand("""
            SELECT payload_id FROM security.protected_diagnostic_payload WHERE payload_id = ANY(@payload_ids)
            ORDER BY payload_id;
            """, verify);
        inspect.Parameters.AddWithValue("payload_ids", references.Select(reference => reference.PayloadId.Value).ToArray());
        var remaining = new HashSet<Guid>();
        await using (NpgsqlDataReader reader = await inspect.ExecuteReaderAsync())
            while (await reader.ReadAsync()) remaining.Add(reader.GetGuid(0));
        Assert.DoesNotContain(references[0].PayloadId.Value, remaining);
        Assert.Contains(references[1].PayloadId.Value, remaining);
        Assert.Contains(references[2].PayloadId.Value, remaining);
        Assert.Contains(references[3].PayloadId.Value, remaining);
        await using var audit = new NpgsqlCommand("""
            SELECT safe_details->>'deletedCount' FROM audit.activity
            WHERE action_name='retention.query_text_orphans.prune' AND actor_kind='system';
            """, verify);
        Assert.Equal("1", await audit.ExecuteScalarAsync());

        var stale = new WorkerLeaseIdentity(retentionLease.Identity.Key,
            retentionLease.Identity.Owner, new FencingToken(retentionLease.Identity.FencingToken.Value + 1));
        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(async () =>
            await retention.PruneOrphanQueryTextPayloadsAsync(stale, DefaultTimeout, CancellationToken.None));
        Assert.Equal("55000", rejected.SqlState);
        await using var grants = new NpgsqlCommand("""
            SELECT has_function_privilege('sqlobserver_collector',
                       'system.prune_orphan_query_text_payloads(uuid,bigint,integer)','EXECUTE'),
                   has_function_privilege('sqlobserver_server',
                       'system.prune_orphan_query_text_payloads(uuid,bigint,integer)','EXECUTE');
            """, verify);
        await using NpgsqlDataReader privileges = await grants.ExecuteReaderAsync();
        Assert.True(await privileges.ReadAsync());
        Assert.True(privileges.GetBoolean(0));
        Assert.False(privileges.GetBoolean(1));
        await privileges.CloseAsync();
        await using var owner = new NpgsqlCommand("""
            SELECT pg_get_userbyid(p.proowner), r.rolcanlogin, r.rolbypassrls,
                   (SELECT count(*) FROM pg_auth_members m
                    WHERE m.roleid=r.oid OR m.member=r.oid)
            FROM pg_proc p JOIN pg_roles r ON r.oid=p.proowner
            WHERE p.oid='system.prune_orphan_query_text_payloads(uuid,bigint,integer)'::regprocedure;
            """, verify);
        await using NpgsqlDataReader ownerReader = await owner.ExecuteReaderAsync();
        Assert.True(await ownerReader.ReadAsync());
        Assert.Equal("sqlobserver_payload_expirer", ownerReader.GetString(0));
        Assert.False(ownerReader.GetBoolean(1));
        Assert.True(ownerReader.GetBoolean(2));
        Assert.Equal(0L, ownerReader.GetInt64(3));
    }

    [Fact]
    public async Task OrphanCleanupUpgradePreservesAndRefreshesPreexistingPayload()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prior = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(124, DefaultTimeout), CancellationToken.None);
        Assert.False(prior.HasFailures);
        Guid targetId = Guid.NewGuid(), payloadId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        ProtectedSensitivePayload original = CreateProtectedPayload(
            RandomNumberGenerator.GetBytes(SensitivePayloadFingerprint.RequiredLength), material: 83);
        await using (NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO security.protected_diagnostic_payload
                (payload_id,instance_id,payload_kind,fingerprint,protection_algorithm,
                 key_identifier,nonce,authentication_tag,ciphertext,created_at)
            VALUES (@id,@target,'query_text',@fingerprint,@algorithm,@key,
                    @nonce,@tag,@ciphertext,clock_timestamp()-interval '9 days');
            """, admin))
        {
            seed.Parameters.AddWithValue("id", payloadId);
            seed.Parameters.AddWithValue("target", targetId);
            seed.Parameters.AddWithValue("fingerprint", original.Fingerprint.ToArray());
            seed.Parameters.AddWithValue("algorithm", original.ProtectionAlgorithm);
            seed.Parameters.AddWithValue("key", original.KeyIdentifier);
            seed.Parameters.AddWithValue("nonce", original.GetNonce());
            seed.Parameters.AddWithValue("tag", original.GetAuthenticationTag());
            seed.Parameters.AddWithValue("ciphertext", original.GetCiphertext());
            await seed.ExecuteNonQueryAsync();
        }
        MigrationBatchResult upgraded = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(2, DefaultTimeout), CancellationToken.None);
        Assert.False(upgraded.HasFailures);
        Assert.Equal(2, upgraded.Results.Count);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collector, "payload-upgrade-reuse");
        SensitivePayloadReference reused = await new PostgreSqlSensitivePayloadPort(collector)
            .GetOrAddAsync(new SensitivePayloadGetOrAddRequest(
                new MonitoredInstanceId(targetId), original, lease.Identity, DefaultTimeout),
                CancellationToken.None);
        Assert.Equal(payloadId, reused.PayloadId.Value);
        await using NpgsqlConnection verify = await database.DataSource.OpenConnectionAsync();
        await using var inspect = new NpgsqlCommand("""
            SELECT last_seen_at > clock_timestamp()-interval '1 hour', ciphertext
            FROM security.protected_diagnostic_payload WHERE payload_id=@id;
            """, verify);
        inspect.Parameters.AddWithValue("id", payloadId);
        await using NpgsqlDataReader reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(original.GetCiphertext(), (byte[])reader.GetValue(1));
    }

    [Fact]
    public async Task QueryContentLinkRequiresMatchingPayloadAndQueryTarget()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid ownerId = Guid.NewGuid(), otherId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, ownerId);
        await InsertObservationTargetAsync(database, otherId);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collector, "query-content-link-target");
        var port = new PostgreSqlSensitivePayloadPort(collector);
        ProtectedSensitivePayload payload = CreateProtectedPayload(
            RandomNumberGenerator.GetBytes(SensitivePayloadFingerprint.RequiredLength), material: 57);
        SensitivePayloadReference reference = await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(new MonitoredInstanceId(ownerId), payload,
                lease.Identity, DefaultTimeout), CancellationToken.None);

        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        Guid ownerRun = Guid.NewGuid(), otherRun = Guid.NewGuid();
        byte[] query = RandomNumberGenerator.GetBytes(32);
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO telemetry.collection_run
                (run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,
                 schedule_revision,work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
            SELECT id,target,'queries.performance',1,1,1,1,'test/query-content',gen_random_uuid(),1,
                   decode(repeat('00',32),'hex'),now(),now()
            FROM (VALUES (@owner_run,@owner),(@other_run,@other)) runs(id,target);
            INSERT INTO events.query_performance_run
                (collection_run_id,instance_id,target_revision,window_start,window_end,source,
                 source_state,coverage,freshness,truncated,completion_digest)
            SELECT id,target,1,now()-interval '5 minutes',now(),'query_store','read_write',
                   'complete',true,false,decode(repeat('00',32),'hex')
            FROM (VALUES (@owner_run,@owner),(@other_run,@other)) runs(id,target);
            INSERT INTO events.query_performance_query
                (collection_run_id,instance_id,database_id,query_fingerprint)
            SELECT id,target,1,@query
            FROM (VALUES (@owner_run,@owner),(@other_run,@other)) runs(id,target);
            """, connection))
        {
            seed.Parameters.AddWithValue("owner_run", ownerRun);
            seed.Parameters.AddWithValue("other_run", otherRun);
            seed.Parameters.AddWithValue("owner", ownerId);
            seed.Parameters.AddWithValue("other", otherId);
            seed.Parameters.AddWithValue("query", query);
            await seed.ExecuteNonQueryAsync();
        }
        await using var link = new NpgsqlCommand(
            """
            INSERT INTO events.query_performance_content_link
                (collection_run_id,instance_id,database_id,query_fingerprint,content_reference,content_available)
            VALUES (@run,@target,1,@query,@payload,true);
            """, connection);
        link.Parameters.AddWithValue("run", ownerRun);
        link.Parameters.AddWithValue("target", ownerId);
        link.Parameters.AddWithValue("query", query);
        link.Parameters.AddWithValue("payload", reference.PayloadId.Value);
        Assert.Equal(1, await link.ExecuteNonQueryAsync());

        link.Parameters["run"].Value = otherRun;
        link.Parameters["target"].Value = otherId;
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await link.ExecuteNonQueryAsync());
        Assert.Equal("23503", failure.SqlState);
        Assert.Equal("fk_query_content_link_payload_target", failure.ConstraintName);

        link.Parameters["run"].Value = ownerRun;
        link.Parameters["target"].Value = ownerId;
        link.Parameters["query"].Value = RandomNumberGenerator.GetBytes(32);
        PostgresException wrongQuery = await Assert.ThrowsAsync<PostgresException>(async () =>
            await link.ExecuteNonQueryAsync());
        Assert.Equal("23503", wrongQuery.SqlState);
        Assert.Equal("fk_query_content_link_query_target", wrongQuery.ConstraintName);
    }

    [Fact]
    public async Task ContentLinkUpgradePreservesLegacyLinkAndRejectsNewOrphan()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prior = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(119, DefaultTimeout), CancellationToken.None);
        Assert.False(prior.HasFailures);
        Assert.Equal(119, prior.Results.Count);

        Guid targetId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var link = new NpgsqlCommand(
            """
            INSERT INTO events.query_performance_content_link
                (collection_run_id,instance_id,database_id,query_fingerprint,content_reference)
            VALUES (@run,@target,1,@query,@payload);
            """, connection);
        link.Parameters.AddWithValue("run", Guid.NewGuid());
        link.Parameters.AddWithValue("target", targetId);
        link.Parameters.AddWithValue("query", RandomNumberGenerator.GetBytes(32));
        link.Parameters.AddWithValue("payload", Guid.NewGuid());
        Assert.Equal(1, await link.ExecuteNonQueryAsync());

        MigrationBatchResult upgraded = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(upgraded.HasFailures);
        Assert.Single(upgraded.Results);
        Assert.Equal(1, await ExecuteScalarInt64Async(database,
            "SELECT count(*) FROM events.query_performance_content_link;"));

        link.Parameters["run"].Value = Guid.NewGuid();
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await link.ExecuteNonQueryAsync());
        Assert.Equal("23503", failure.SqlState);
        Assert.Equal("fk_query_content_link_payload_target", failure.ConstraintName);

        MigrationBatchResult queryBound = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(queryBound.HasFailures);
        Assert.Single(queryBound.Results);
        Assert.Equal(1, await ExecuteScalarInt64Async(database,
            "SELECT count(*) FROM events.query_performance_content_link;"));
    }

    [Fact]
    public async Task TargetBindingUpgradePreservesLegacyPayloadButDoesNotReuseIt()
    {
        await using RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        MigrationBatchResult prior = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(118, DefaultTimeout), CancellationToken.None);
        Assert.False(prior.HasFailures);
        Assert.Equal(118, prior.Results.Count);

        Guid targetId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        byte[] fingerprint = RandomNumberGenerator.GetBytes(SensitivePayloadFingerprint.RequiredLength);
        ProtectedSensitivePayload legacy = CreateProtectedPayload(fingerprint, material: 61);
        Guid legacyId = Guid.NewGuid();
        await using (NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO security.protected_diagnostic_payload
                (payload_id,payload_kind,fingerprint,protection_algorithm,key_identifier,nonce,authentication_tag,ciphertext)
            VALUES (@id,'query_text',@fingerprint,@algorithm,@key,@nonce,@tag,@ciphertext);
            """, connection))
        {
            insert.Parameters.AddWithValue("id", legacyId);
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            insert.Parameters.AddWithValue("algorithm", legacy.ProtectionAlgorithm);
            insert.Parameters.AddWithValue("key", legacy.KeyIdentifier);
            insert.Parameters.AddWithValue("nonce", legacy.GetNonce());
            insert.Parameters.AddWithValue("tag", legacy.GetAuthenticationTag());
            insert.Parameters.AddWithValue("ciphertext", legacy.GetCiphertext());
            await insert.ExecuteNonQueryAsync();
        }

        MigrationBatchResult upgraded = await migrations.ApplyPendingAsync(
            new MigrationApplyRequest(1, DefaultTimeout), CancellationToken.None);
        Assert.False(upgraded.HasFailures);
        Assert.Single(upgraded.Results);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        WorkerLease lease = await AcquireLeaseAsync(collector, "legacy-payload-target");
        var port = new PostgreSqlSensitivePayloadPort(collector);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await port.GetOrAddAsync(
            new SensitivePayloadGetOrAddRequest(new MonitoredInstanceId(targetId), legacy,
                lease.Identity, DefaultTimeout), CancellationToken.None));
        await using NpgsqlConnection verifyConnection = await database.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand(
            "SELECT payload_id, instance_id IS NULL FROM security.protected_diagnostic_payload WHERE fingerprint=@fingerprint;",
            verifyConnection);
        verify.Parameters.AddWithValue("fingerprint", fingerprint);
        await using NpgsqlDataReader reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(legacyId, reader.GetGuid(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task SqlConstraintsRejectInvalidNonceTagAndCiphertextSize()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();

        await AssertProtectedPayloadConstraintAsync(
            database,
            "ck_protected_payload_nonce",
            nonce: new byte[ProtectedSensitivePayload.MinimumNonceBytes - 1],
            authenticationTag: new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            ciphertext: [1]);
        await AssertProtectedPayloadConstraintAsync(
            database,
            "ck_protected_payload_authentication_tag",
            nonce: new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            authenticationTag: new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes - 1],
            ciphertext: [1]);
        await AssertProtectedPayloadConstraintAsync(
            database,
            "ck_protected_payload_ciphertext",
            nonce: new byte[ProtectedSensitivePayload.MinimumNonceBytes],
            authenticationTag: new byte[ProtectedSensitivePayload.MinimumAuthenticationTagBytes],
            ciphertext: new byte[ProtectedSensitivePayload.MaximumCiphertextBytes + 1]);

        Assert.Equal(0L, await ExecuteScalarInt64Async(
            database,
            "SELECT count(*) FROM security.protected_diagnostic_payload;"));
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await _fixture.CreateDatabaseAsync();
        try
        {
            var runner = new PostgreSqlMigrationPort(database.DataSource);
            MigrationBatchResult result = await runner.ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, DefaultTimeout),
                CancellationToken.None);
            Assert.False(result.HasFailures);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private static MetricSample CreateMetricSample(
        MetricSampleId sampleId,
        MonitoredInstanceId instance,
        DateTimeOffset observedAtUtc,
        double value,
        string scope) =>
        new(
            sampleId,
            instance,
            new MetricId("engine.replay_safety"),
            observedAtUtc,
            value,
            [new MetricDimension("scope", scope)]);

    private static DiagnosticEventEnvelope CreateEvent(
        DiagnosticEventId eventId,
        MonitoredInstanceId instance,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset collectedAtUtc,
        string kind) =>
        new(
            eventId,
            instance,
            new DiagnosticEventKind(kind),
            occurredAtUtc,
            collectedAtUtc);

    private static ProtectedSensitivePayload CreateProtectedPayload(byte[] fingerprint, byte material) =>
        new(
            SensitivePayloadKind.QueryText,
            new SensitivePayloadFingerprint(fingerprint),
            $"AES-256-GCM-{material}",
            $"integration-key-{material}",
            Enumerable.Repeat(material, 12).ToArray(),
            Enumerable.Repeat(checked((byte)(material + 1)), 16).ToArray(),
            Enumerable.Repeat(checked((byte)(material + 2)), 64).ToArray());

    private static async Task<WorkerLease> AcquireLeaseAsync(NpgsqlDataSource dataSource, string purpose)
    {
        var leases = new PostgreSqlWorkerLeasePort(dataSource);
        LeaseAcquisitionResult result = await leases.AcquireAsync(
            new AcquireWorkerLeaseRequest(
                new WorkerLeaseKey($"integration:{purpose}:{Guid.NewGuid():N}"),
                new WorkerExecutionId(Guid.NewGuid()),
                DefaultLeaseDuration,
                DefaultTimeout),
            CancellationToken.None);
        return Assert.IsType<WorkerLease>(result.Lease);
    }

    private static async Task EnsurePartitionAsync(
        NpgsqlDataSource dataSource,
        WorkerLease lease,
        PartitionSetName setName,
        PartitionGranularity granularity,
        DateTimeOffset anchorUtc)
    {
        var partitions = new PostgreSqlPartitionMaintenancePort(dataSource);
        await partitions.EnsurePartitionsAsync(
            new PartitionCareRequest(
                setName,
                granularity,
                anchorUtc,
                partitionsAhead: 0,
                lease.Identity,
                DefaultTimeout),
            CancellationToken.None);
    }

    private static async Task InsertObservationTargetAsync(
        RepositoryTestDatabase database,
        Guid instanceId)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            WITH repository_clock AS
            (
                SELECT clock_timestamp() AS captured_at
            )
            INSERT INTO control.observation_target
            (
                instance_id, instance_key, display_name,
                created_at, updated_at, discovery_requested_at
            )
            SELECT
                @instance_id, @instance_key, @display_name,
                captured_at, captured_at, captured_at
            FROM repository_clock;
            """,
            connection);
        command.Parameters.AddWithValue("instance_id", instanceId);
        command.Parameters.AddWithValue("instance_key", $"replay-{instanceId:N}");
        command.Parameters.AddWithValue("display_name", "Replay safety integration target");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PersistedPayload> ReadPersistedPayloadAsync(
        RepositoryTestDatabase database,
        byte[] fingerprint)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                count(*) OVER (), protection_algorithm, key_identifier,
                nonce, authentication_tag, ciphertext
            FROM security.protected_diagnostic_payload
            WHERE payload_kind = 'query_text'
              AND fingerprint = @fingerprint;
            """,
            connection);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        return new PersistedPayload(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetFieldValue<byte[]>(3),
            reader.GetFieldValue<byte[]>(4),
            reader.GetFieldValue<byte[]>(5));
    }

    private static bool PersistedPayloadMatches(
        ProtectedSensitivePayload expected,
        PersistedPayload actual) =>
        expected.ProtectionAlgorithm == actual.Algorithm &&
        expected.KeyIdentifier == actual.KeyIdentifier &&
        expected.GetNonce().SequenceEqual(actual.Nonce) &&
        expected.GetAuthenticationTag().SequenceEqual(actual.AuthenticationTag) &&
        expected.GetCiphertext().SequenceEqual(actual.Ciphertext);

    private static async Task AssertProtectedPayloadConstraintAsync(
        RepositoryTestDatabase database,
        string expectedConstraint,
        byte[] nonce,
        byte[] authenticationTag,
        byte[] ciphertext)
    {
        Guid targetId = Guid.NewGuid();
        await InsertObservationTargetAsync(database, targetId);
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO security.protected_diagnostic_payload
            (
                payload_id, instance_id, payload_kind, fingerprint, protection_algorithm,
                key_identifier, nonce, authentication_tag, ciphertext
            )
            VALUES
            (
                @payload_id, @instance_id, 'query_text', @fingerprint, 'AES-256-GCM',
                'integration-key', @nonce, @authentication_tag, @ciphertext
            );
            """,
            connection);
        command.Parameters.AddWithValue("payload_id", Guid.NewGuid());
        command.Parameters.AddWithValue("instance_id", targetId);
        command.Parameters.AddWithValue("fingerprint", RandomNumberGenerator.GetBytes(32));
        command.Parameters.AddWithValue("nonce", nonce);
        command.Parameters.AddWithValue("authentication_tag", authenticationTag);
        command.Parameters.AddWithValue("ciphertext", ciphertext);

        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(async () =>
            await command.ExecuteNonQueryAsync());
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task<long> ExecuteScalarInt64Async(
        RepositoryTestDatabase database,
        string commandText)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(commandText, connection);
        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record PersistedPayload(
        long Count,
        string Algorithm,
        string KeyIdentifier,
        byte[] Nonce,
        byte[] AuthenticationTag,
        byte[] Ciphertext);
}
