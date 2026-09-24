using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[Collection(PostgreSql18CollectionDefinition.Name)]
[Trait("Category", "RequiresPostgreSql")]
public sealed class McpIncidentListPostgreSqlTests(PostgreSql18Fixture fixture)
{
    private const string FenceSignature = "control.read_incident_publication_revision(uuid,bigint,bigint)";
    private const string ListSignature = "reporting.list_incidents_metadata(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,bigint,timestamptz,uuid)";
    private static readonly RepositoryCallTimeout Timeout = new(TimeSpan.FromSeconds(5));

    [Fact]
    public async Task IncidentListFunctionsAreServerOnlyAndRejectCrossTargetScope()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot();
        await InsertTargetAsync(database, target);
        await InsertTargetAsync(database, other);
        await InsertThreadAsync(database, target, Guid.NewGuid(), snapshot.AddMinutes(-10));
        await InsertThreadAsync(database, other, Guid.NewGuid(), snapshot.AddMinutes(-10));
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        foreach (string signature in new[] { FenceSignature, ListSignature })
        {
            await using var permissions = new NpgsqlCommand("""
                SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator',p.prosecdef,
                  has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
                  NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
                  NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
                  NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
                FROM pg_proc p WHERE p.oid=to_regprocedure(@signature);
                """, admin) { CommandTimeout = 5 };
            permissions.Parameters.AddWithValue("signature", signature);
            await using NpgsqlDataReader reader = await permissions.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), signature);
            for (int i = 0; i < 6; i++) Assert.True(reader.GetBoolean(i), $"ACL check {i}: {signature}");
        }
        Assert.Equal(0L, await ScalarAsync(admin, """
            SELECT count(*) FROM (VALUES ('sqlobserver_server'),('sqlobserver_collector'),('sqlobserver_auditor')) roles(name)
            WHERE has_table_privilege(name,'control.incident_publication_revision','SELECT,INSERT,UPDATE,DELETE');
            """));

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        Assert.True(await ReadFenceAsync(connection, target) > 0);
        PostgresException deniedFence = await Assert.ThrowsAsync<PostgresException>(() => ReadFenceAsync(connection, other));
        Assert.Equal("42501", deniedFence.SqlState);
        PostgresException deniedList = await Assert.ThrowsAsync<PostgresException>(() => CountListAsync(connection, other, snapshot, 101, 1));
        Assert.Equal("42501", deniedList.SqlState);
    }

    [Fact]
    public async Task IncidentListPagesAll101EqualTimeThreadsWithoutDuplicatesOrGaps()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), other = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot(), opened = snapshot.AddMinutes(-10);
        await InsertTargetAsync(database, target);
        await InsertTargetAsync(database, other);
        await SeedThreadsAsync(database, target, opened, 101);
        await InsertThreadAsync(database, other, Guid.NewGuid(), opened);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = Repository(server);
        IncidentListQuery query = Query(target, snapshot, 100);

        IncidentListPage first = await repository.ListIncidentsAsync(query, CancellationToken.None);
        Assert.Equal(100, first.Items.Count);
        Assert.True(first.HasMore);
        IncidentListCursor cursor = Assert.IsType<IncidentListCursor>(first.NextCursor);
        Assert.Equal(first.Items[^1].ThreadId, cursor.ThreadId);
        Assert.Equal(opened, cursor.OpenedAtUtc);
        Assert.Equal(first.PublicationRevision, cursor.PublicationRevision);
        Assert.Equal(snapshot, cursor.SnapshotUtc);
        IncidentListPage second = await repository.ListIncidentsAsync(query with { Cursor = cursor }, CancellationToken.None);
        Assert.Single(second.Items);
        Assert.False(second.HasMore);
        Assert.Null(second.NextCursor);
        Assert.Equal(first.PublicationRevision, second.PublicationRevision);
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal(opened, item.OpenedAtUtc));

        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        Guid[] expected = await ReadThreadIdsAsync(admin, target);
        Guid[] actual = first.Items.Concat(second.Items).Select(item => item.ThreadId).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(101, actual.Distinct().Count());
        IncidentListItem last = second.Items[0];
        var finalCursor = new IncidentListCursor(query.TargetId, second.TargetRevision, query.FromUtc, query.ToUtc,
            snapshot, second.PublicationRevision, last.OpenedAtUtc, last.ThreadId);
        IncidentListPage exhausted = await repository.ListIncidentsAsync(query with { Cursor = finalCursor }, CancellationToken.None);
        Assert.Empty(exhausted.Items);
        Assert.False(exhausted.HasMore);
        Assert.Null(exhausted.NextCursor);
    }

    [Fact]
    public async Task IncidentListMetadataAndEvidenceUseOnlyGenerationsVisibleAtSnapshot()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), thread = Guid.NewGuid(), futureOnly = Guid.NewGuid(), futureThread = Guid.NewGuid();
        Guid pastPacket = Guid.NewGuid(), futurePacket = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot(), opened = snapshot.AddMinutes(-20), past = snapshot.AddMinutes(-10);
        await InsertTargetAsync(database, target);
        await InsertThreadAsync(database, target, thread, opened);
        await InsertThreadAsync(database, target, futureOnly, opened.AddMinutes(1));
        await InsertThreadAsync(database, target, futureThread, snapshot.AddSeconds(1));
        await ExecuteAsync(database, """
            INSERT INTO analytics.evidence_packet_v2(occurred_at,packet_id,instance_id,target_revision,evidence_kind,
              source_digest,identity_digest,source_cutoff_digest,evidence,confidence,visibility_state)
            VALUES(@past,@past_packet,@target,1,'metric',decode(repeat('a',64),'hex'),decode(repeat('b',64),'hex'),decode(repeat('c',64),'hex'),'{}'::jsonb,.9,'complete'),
                  (@past,@future_packet,@target,1,'metric',decode(repeat('d',64),'hex'),decode(repeat('e',64),'hex'),decode(repeat('f',64),'hex'),'{}'::jsonb,.9,'complete');
            """, ("past", past), ("past_packet", pastPacket), ("future_packet", futurePacket), ("target", target));
        await InsertGenerationAsync(database, target, thread, 1, past, pastPacket);
        await InsertGenerationAsync(database, target, thread, 2, snapshot.AddSeconds(1), futurePacket);
        await InsertGenerationAsync(database, target, futureOnly, 1, snapshot.AddSeconds(1));
        await ExecuteAsync(database, "UPDATE analytics.incident_thread SET current_generation=2 WHERE thread_id=@thread;", ("thread", thread));

        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = Repository(server);
        IncidentListQuery query = Query(target, snapshot, 100) with { ToUtc = snapshot.AddHours(1) };
        IncidentListPage page = await repository.ListIncidentsAsync(query, CancellationToken.None);
        Assert.Equal(2, page.Items.Count);
        Assert.DoesNotContain(page.Items, item => item.ThreadId == futureThread);
        IncidentListItem observed = Assert.Single(page.Items, item => item.ThreadId == thread);
        Assert.Equal(1L, observed.GenerationCount);
        Assert.Equal(past, observed.LatestGenerationObservedAtUtc);
        IncidentListItem pending = Assert.Single(page.Items, item => item.ThreadId == futureOnly);
        Assert.Equal(0L, pending.GenerationCount);
        Assert.Null(pending.LatestGenerationObservedAtUtc);

        IncidentEvidencePage evidence = Assert.IsType<IncidentEvidencePage>(await repository.GetIncidentEvidenceAsync(
            new IncidentEvidenceQuery(query.Authorization, query.TargetId, observed.ThreadId, 100, Timeout,
                page.TargetRevision, page.SnapshotUtc), CancellationToken.None));
        Assert.Equal(page.SnapshotUtc, evidence.SnapshotUtc);
        Assert.Equal(pastPacket, Assert.Single(evidence.Items).PacketId);
        Assert.Equal(observed.GenerationCount, evidence.Generations.Count);
        Assert.Equal(past, Assert.Single(evidence.Generations).ObservedAtUtc);
        Assert.Null(await repository.GetIncidentEvidenceAsync(new IncidentEvidenceQuery(query.Authorization, query.TargetId,
            futureOnly, 100, Timeout, page.TargetRevision, page.SnapshotUtc), CancellationToken.None));
    }

    [Fact]
    public async Task BackdatedIncidentInsertedAfterPageInvalidatesContinuation()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot();
        await InsertTargetAsync(database, target);
        await SeedThreadsAsync(database, target, snapshot.AddMinutes(-10), 2);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = Repository(server);
        IncidentListQuery query = Query(target, snapshot, 1);
        IncidentListPage first = await repository.ListIncidentsAsync(query, CancellationToken.None);
        IncidentListCursor cursor = Assert.IsType<IncidentListCursor>(first.NextCursor);
        await InsertThreadAsync(database, target, Guid.NewGuid(), first.Items[0].OpenedAtUtc.AddSeconds(-1));

        await AssertContinuationChangedAsync(repository, query, cursor);
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        PostgresException stale = await Assert.ThrowsAsync<PostgresException>(() => ReadFenceAsync(connection, target, first.PublicationRevision));
        Assert.Equal("40001", stale.SqlState);
        Assert.True(await ReadFenceAsync(connection, target) > first.PublicationRevision);
    }

    [Fact]
    public async Task WriterTransactionStartedBeforePageStillInvalidatesAfterBackdatedCommit()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        DateTimeOffset opened = Snapshot().AddMinutes(-10);
        await InsertTargetAsync(database, target);
        await SeedThreadsAsync(database, target, opened, 2);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = Repository(server);
        await using NpgsqlConnection writer = await database.DataSource.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await writer.BeginTransactionAsync();
        await using var startedCommand = new NpgsqlCommand("SELECT transaction_timestamp();", writer, transaction) { CommandTimeout = 5 };
        DateTime writerStarted = Assert.IsType<DateTime>(await startedCommand.ExecuteScalarAsync());
        IncidentListQuery query = Query(target, Snapshot(), 1) with { SnapshotUtc = null };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        IncidentListPage first = await repository.ListIncidentsAsync(query, deadline.Token);
        Assert.True(new DateTimeOffset(DateTime.SpecifyKind(writerStarted, DateTimeKind.Utc)) <= first.SnapshotUtc);
        IncidentListCursor cursor = Assert.IsType<IncidentListCursor>(first.NextCursor);

        await using NpgsqlConnection readerConnection = await server.OpenConnectionAsync(deadline.Token);
        await SetScopeAsync(readerConnection, target);
        await using NpgsqlTransaction readerTransaction = await readerConnection.BeginTransactionAsync(deadline.Token);
        Assert.Equal(first.PublicationRevision, await ReadFenceAsync(readerConnection, target, first.PublicationRevision, readerTransaction));
        await using var readerPidCommand = new NpgsqlCommand("SELECT pg_backend_pid();", readerConnection, readerTransaction) { CommandTimeout = 5 };
        int readerPid = Assert.IsType<int>(await readerPidCommand.ExecuteScalarAsync(deadline.Token));

        // The transaction's timestamp predates the page, but the publication
        // occurs after it. A timestamp-only fence would miss this insertion.
        await using var insert = new NpgsqlCommand(ThreadInsertSql, writer, transaction) { CommandTimeout = 5 };
        insert.Parameters.AddWithValue("target", target);
        insert.Parameters.AddWithValue("thread", Guid.NewGuid());
        insert.Parameters.AddWithValue("opened", opened.AddSeconds(-1));
        Task<int> insertion = insert.ExecuteNonQueryAsync(deadline.Token);
        bool readerReleased = false;
        try
        {
            await using (NpgsqlConnection observer = await database.DataSource.OpenConnectionAsync(deadline.Token))
            await using (var blocked = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_stat_activity activity
                    WHERE activity.datname=current_database()
                      AND activity.state='active' AND activity.wait_event_type='Lock'
                      AND activity.query LIKE '%INSERT INTO analytics.incident_thread%'
                      AND @reader_pid=ANY(pg_catalog.pg_blocking_pids(activity.pid))
                );
                """, observer) { CommandTimeout = 5 })
            {
                blocked.Parameters.AddWithValue("reader_pid", readerPid);
                while (await blocked.ExecuteScalarAsync(deadline.Token) is not true)
                {
                    // Wait for the observed dependency, not an assumed elapsed delay.
                    await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
                }
            }
            Assert.False(insertion.IsCompleted);
            Assert.Equal(2L, await CountListAsync(readerConnection, target, first.SnapshotUtc, 101,
                first.PublicationRevision, transaction: readerTransaction));
            await readerTransaction.CommitAsync(deadline.Token);
            readerReleased = true;
            Assert.Equal(1, await insertion);
            await transaction.CommitAsync(deadline.Token);
        }
        finally
        {
            if (!readerReleased)
            {
                try
                {
                    if (readerTransaction.Connection is not null) await readerTransaction.RollbackAsync(CancellationToken.None);
                }
                finally
                {
                    // Observe the bounded writer even when the lock assertion
                    // fails, before its connection is disposed.
                    try { await insertion; } catch { }
                }
            }
        }
        await AssertContinuationChangedAsync(repository, query, cursor);
    }

    [Fact]
    public async Task MissingPublicationFenceReturnsEmptyWithoutCallingMetadataFunction()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await InsertTargetAsync(database, target);
        await ExecuteAsync(database, $"REVOKE EXECUTE ON FUNCTION {ListSignature} FROM sqlobserver_server;");
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        IncidentListPage page = await Repository(server).ListIncidentsAsync(Query(target, Snapshot(), 100), CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Equal(0L, page.PublicationRevision);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task EveryThreadAndGenerationMutationAdvancesPublicationFence()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), extra = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot();
        await InsertTargetAsync(database, target);
        await SeedThreadsAsync(database, target, snapshot.AddMinutes(-10), 3);
        await using NpgsqlConnection admin = await database.DataSource.OpenConnectionAsync();
        Guid generationThread = (await ReadThreadIdsAsync(admin, target))[0];
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = Repository(server);
        IncidentListQuery query = Query(target, snapshot, 1);
        (string Sql, (string Name, object Value)[] Parameters)[] changes =
        [
            (ThreadInsertSql, [("target", target), ("thread", extra), ("opened", snapshot.AddMinutes(-20))]),
            ("UPDATE analytics.incident_thread SET summary='{\"updated\":true}'::jsonb WHERE thread_id=@thread;", [("thread", extra)]),
            ("DELETE FROM analytics.incident_thread WHERE thread_id=@thread;", [("thread", extra)]),
            (GenerationInsertSql, [("target", target), ("thread", generationThread), ("generation", 1L), ("observed", snapshot.AddMinutes(-5)), ("packet", DBNull.Value)]),
            ("UPDATE analytics.incident_generation SET details='{\"updated\":true}'::jsonb WHERE thread_id=@thread;", [("thread", generationThread)]),
            ("DELETE FROM analytics.incident_generation WHERE thread_id=@thread;", [("thread", generationThread)]),
        ];
        for (int index = 0; index < changes.Length; index++)
        {
            IncidentListPage before = await repository.ListIncidentsAsync(query, CancellationToken.None);
            IncidentListCursor cursor = Assert.IsType<IncidentListCursor>(before.NextCursor);
            (string sql, (string Name, object Value)[] parameters) = changes[index];
            if (index >= 4)
            {
                // Exercise maintenance UPDATE/DELETE fencing while retaining
                // the new publication trigger; ordinary generations are append-only.
                sql = "BEGIN; ALTER TABLE analytics.incident_generation DISABLE TRIGGER m10_incident_generation_append_only; " + sql +
                    " ALTER TABLE analytics.incident_generation ENABLE TRIGGER m10_incident_generation_append_only; COMMIT;";
            }
            await ExecuteAsync(database, sql, parameters);
            await AssertContinuationChangedAsync(repository, query, cursor);
            IncidentListPage after = await repository.ListIncidentsAsync(query, CancellationToken.None);
            Assert.True(after.PublicationRevision > before.PublicationRevision, $"Mutation {index} did not publish a new revision.");
        }
    }

    [Fact]
    public async Task IncidentMetadataSqlRejectsInvalidLimitsWindowsAndPartialCursors()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid(), thread = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot(), opened = snapshot.AddMinutes(-10);
        await InsertTargetAsync(database, target);
        await InsertThreadAsync(database, target, thread, opened);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetScopeAsync(connection, target);
        long publication = await ReadFenceAsync(connection, target);
        Assert.Equal(1L, await CountListAsync(connection, target, snapshot, 1, publication));
        Assert.Equal(1L, await CountListAsync(connection, target, snapshot, 101, publication));
        foreach (int limit in new[] { 0, 102 })
        {
            PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => CountListAsync(connection, target, snapshot, limit, publication));
            Assert.Equal("22023", error.SqlState);
        }
        PostgresException missingId = await Assert.ThrowsAsync<PostgresException>(() => CountListAsync(connection, target, snapshot, 101, publication, opened));
        Assert.Equal("22023", missingId.SqlState);
        PostgresException missingTime = await Assert.ThrowsAsync<PostgresException>(() => CountListAsync(connection, target, snapshot, 101, publication, null, thread));
        Assert.Equal("22023", missingTime.SqlState);
        PostgresException oversizedWindow = await Assert.ThrowsAsync<PostgresException>(() => CountListAsync(connection, target, snapshot, 101, publication, from: snapshot.AddDays(-32)));
        Assert.Equal("22023", oversizedWindow.SqlState);
        PostgresException negativePublication = await Assert.ThrowsAsync<PostgresException>(() => CountListAsync(connection, target, snapshot, 101, -1));
        Assert.Equal("22023", negativePublication.SqlState);
    }

    [Fact]
    public async Task IncidentListUpgradeSeedsExistingHistoryAndInstallsPublicationTriggers()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var migrationTimeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        MigrationBatchResult initial = await migrations.ApplyPendingAsync(new MigrationApplyRequest(85, migrationTimeout), CancellationToken.None);
        Assert.False(initial.HasFailures);
        Assert.Equal(85, initial.Results.Count);
        Guid target = Guid.NewGuid(), thread = Guid.NewGuid();
        DateTimeOffset snapshot = Snapshot(), opened = snapshot.AddMinutes(-20), observed = snapshot.AddMinutes(-10);
        await InsertTargetAsync(database, target);
        await InsertThreadAsync(database, target, thread, opened);
        await InsertGenerationAsync(database, target, thread, 1, observed);
        const string historySql = """
            SELECT jsonb_build_object(
                'threads',(SELECT jsonb_agg(to_jsonb(t) ORDER BY thread_id) FROM analytics.incident_thread t),
                'generations',(SELECT jsonb_agg(to_jsonb(g) ORDER BY thread_id,generation) FROM analytics.incident_generation g))::text;
            """;
        string before = await ScalarTextAsync(database, historySql);

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(new MigrationApplyRequest(1, migrationTimeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures, upgrade.Results.FirstOrDefault(item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
        Assert.Equal(86, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(before, await ScalarTextAsync(database, historySql));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        var repository = Repository(server);
        IncidentListQuery query = Query(target, snapshot, 100);
        IncidentListPage seeded = await repository.ListIncidentsAsync(query, CancellationToken.None);
        Assert.Equal(1L, seeded.PublicationRevision);
        IncidentListItem item = Assert.Single(seeded.Items);
        Assert.Equal(thread, item.ThreadId);
        Assert.Equal(opened, item.OpenedAtUtc);
        Assert.Equal(1L, item.GenerationCount);
        Assert.Equal(observed, item.LatestGenerationObservedAtUtc);

        await InsertGenerationAsync(database, target, thread, 2, observed.AddMinutes(1));
        IncidentListPage published = await repository.ListIncidentsAsync(query, CancellationToken.None);
        Assert.Equal(2L, published.PublicationRevision);
        Assert.Equal(2L, Assert.Single(published.Items).GenerationCount);
    }

    private static async Task AssertContinuationChangedAsync(PostgreSqlAnalyticsRepositoryPort repository, IncidentListQuery query, IncidentListCursor cursor) =>
        await Assert.ThrowsAsync<IncidentListChangedException>(() => repository.ListIncidentsAsync(query with { Cursor = cursor }, CancellationToken.None).AsTask());

    private static PostgreSqlAnalyticsRepositoryPort Repository(NpgsqlDataSource server) =>
        new(server, new IdentityFingerprintKey(new byte[IdentityFingerprintKey.RequiredLength]));

    private static IncidentListQuery Query(Guid target, DateTimeOffset snapshot, int limit)
    {
        var targetId = new MonitoredInstanceId(target);
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-711"), AuthorizationPrincipalState.Active,
            [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([targetId]));
        return new IncidentListQuery(authorization, targetId, snapshot.AddHours(-1), snapshot, limit, Timeout,
            new ObservationTargetRevision(1), snapshot);
    }

    private static DateTimeOffset Snapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow.AddMinutes(-1);
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private const string ThreadInsertSql = """
        INSERT INTO analytics.incident_thread(thread_id,instance_id,target_revision,opened_at,state,current_generation,summary)
        VALUES(@thread,@target,1,@opened,'open',1,'{}'::jsonb);
        """;
    private const string GenerationInsertSql = """
        INSERT INTO analytics.incident_generation(instance_id,target_revision,thread_id,generation,observed_at,state,
          evidence_packet_id,correlation_digest,supersedes_previous,details)
        VALUES(@target,1,@thread,@generation,@observed,'open',@packet,decode(repeat('d',64),'hex'),false,'{}'::jsonb);
        """;

    private static Task InsertThreadAsync(RepositoryTestDatabase database, Guid target, Guid thread, DateTimeOffset opened) =>
        ExecuteAsync(database, ThreadInsertSql, ("target", target), ("thread", thread), ("opened", opened));

    private static Task InsertGenerationAsync(RepositoryTestDatabase database, Guid target, Guid thread, long generation,
        DateTimeOffset observed, Guid? packet = null) => ExecuteAsync(database, GenerationInsertSql,
            ("target", target), ("thread", thread), ("generation", generation), ("observed", observed), ("packet", (object?)packet ?? DBNull.Value));

    private static Task SeedThreadsAsync(RepositoryTestDatabase database, Guid target, DateTimeOffset opened, int count) =>
        ExecuteAsync(database, """
            INSERT INTO analytics.incident_thread(thread_id,instance_id,target_revision,opened_at,state,current_generation,summary)
            SELECT md5(format('%s-%s',@seed,g))::uuid,@target,1,@opened,'open',1,'{}'::jsonb
            FROM generate_series(1,@count) AS values(g);
            """, ("seed", target.ToString()), ("target", target), ("opened", opened), ("count", count));

    private static Task InsertTargetAsync(RepositoryTestDatabase database, Guid target) => ExecuteAsync(database, """
        INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
          authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
        VALUES(@target,@key,'Incident list target','sql01',1433,interval '5 seconds','windows_integrated_service_identity',
          'mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
        """, ("target", target), ("key", "incident-list-" + target.ToString("N")));

    private static async Task SetScopeAsync(NpgsqlConnection connection, Guid target)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("scope", target.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadFenceAsync(NpgsqlConnection connection, Guid target, long? expected = null,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("SELECT control.read_incident_publication_revision(@target,1,@expected);", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("expected", NpgsqlDbType.Bigint, (object?)expected ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountListAsync(NpgsqlConnection connection, Guid target, DateTimeOffset snapshot,
        int limit, long publication, DateTimeOffset? cursorAt = null, Guid? cursorId = null, DateTimeOffset? from = null,
        NpgsqlTransaction? transaction = null)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM reporting.list_incidents_metadata(@target,1,@from,@to,@limit,@snapshot,@publication,@cursor_at,@cursor_id);", connection, transaction) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("from", from ?? snapshot.AddHours(-1));
        command.Parameters.AddWithValue("to", snapshot);
        command.Parameters.AddWithValue("limit", limit);
        command.Parameters.AddWithValue("snapshot", snapshot);
        command.Parameters.AddWithValue("publication", publication);
        command.Parameters.AddWithValue("cursor_at", NpgsqlDbType.TimestampTz, (object?)cursorAt ?? DBNull.Value);
        command.Parameters.AddWithValue("cursor_id", NpgsqlDbType.Uuid, (object?)cursorId ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<Guid[]> ReadThreadIdsAsync(NpgsqlConnection connection, Guid target)
    {
        await using var command = new NpgsqlCommand("SELECT thread_id FROM analytics.incident_thread WHERE instance_id=@target ORDER BY opened_at,thread_id;", connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("target", target);
        var ids = new List<Guid>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids.ToArray();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(RepositoryTestDatabase database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        command.CommandTimeout = 5;
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(RepositoryTestDatabase database, string sql, params (string Name, object Value)[] values)
    {
        await using NpgsqlConnection connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 };
        foreach ((string name, object value) in values)
        {
            if (name == "packet") command.Parameters.AddWithValue(name, NpgsqlDbType.Uuid, value);
            else command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<RepositoryTestDatabase> CreateMigratedDatabaseAsync()
    {
        RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        try
        {
            MigrationBatchResult result = await new PostgreSqlMigrationPort(database.DataSource).ApplyPendingAsync(
                new MigrationApplyRequest(MigrationBatchResult.MaximumResults, new RepositoryCallTimeout(TimeSpan.FromSeconds(30))), CancellationToken.None);
            Assert.False(result.HasFailures, result.Results.FirstOrDefault(item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
            return database;
        }
        catch { await database.DisposeAsync(); throw; }
    }
}
