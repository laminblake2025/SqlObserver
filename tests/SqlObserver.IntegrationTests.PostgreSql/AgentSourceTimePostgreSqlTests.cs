using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class M9OperationalHealthPostgreSqlIntegrationFixtureTests
{
    private const string AgentSourceReadSignature = "reporting.list_sql_agent_failures_v2(uuid,uuid,bigint,timestamptz,timestamptz,timestamptz,bytea,integer)";
    private static readonly RepositoryCallTimeout AgentSourceTimeout = new(TimeSpan.FromSeconds(10));
    private static readonly JsonSerializerOptions AgentSourceJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTime AgentSourceKnownStart = new(2026, 8, 25, 12, 3, 4, DateTimeKind.Unspecified);
    private static readonly string[] AgentSourceInvalidTimes =
    [
        "2026-08-25T12:03:04+02:00", "2026-08-25T12:03:04Z",
        "2026-02-30T12:03:04", "2026-08-25T12:03:04.125",
    ];

    [Fact]
    public async Task AgentSourceTimeUpgradePreservesLegacyReplayAndFirstObservation()
    {
        await using RepositoryTestDatabase database = await fixture.CreateDatabaseAsync();
        var migrations = new PostgreSqlMigrationPort(database.DataSource);
        var migrationTimeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        MigrationBatchResult initial = await migrations.ApplyPendingAsync(new MigrationApplyRequest(86, migrationTimeout), CancellationToken.None);
        Assert.False(initial.HasFailures);
        Assert.Equal(86, initial.Results.Count);
        Guid target = Guid.NewGuid(), job = Guid.NewGuid();
        await PrepareAgentSourceTargetAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        AgentSourceRun firstRun = await BeginAgentSourceRunAsync(collector, target);
        SqlAgentFailureObservation legacy = AgentSourceObservation(firstRun, job, 42, 'a', null);
        CommitCollectorRunRequest firstCommit = AgentSourceCommit(firstRun, [legacy]);
        string legacyJson = LegacyAgentSourcePayload(firstCommit);
        Assert.DoesNotContain("sourceLocalStart", legacyJson, StringComparison.Ordinal);
        Assert.Equal("committed", await ExecuteAgentSourceCommitAsync(collector, firstCommit, payloadOverride: legacyJson));
        string history = await ReadAgentSourceStateAsync(database, target, omitSource: true);
        string registry = await ReadAgentSourceRegistryAsync(database);

        MigrationBatchResult upgrade = await migrations.ApplyPendingAsync(new MigrationApplyRequest(1, migrationTimeout), CancellationToken.None);
        Assert.False(upgrade.HasFailures, upgrade.Results.FirstOrDefault(item => item.Outcome == MigrationOutcome.Failed)?.FailureCode);
        Assert.Equal(87, Assert.Single(upgrade.Results).Migration.Number.Value);
        Assert.Equal(registry, await ReadAgentSourceRegistryAsync(database));
        Assert.Equal(history, await ReadAgentSourceStateAsync(database, target, omitSource: true));
        await using (var pins = database.DataSource.CreateCommand("SELECT array_agg(encode(asset_bundle_sha256,'hex') ORDER BY execution_order) FROM control.collector_contract WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health') AND collector_version=1;"))
            Assert.Equal(Enumerable.Repeat(SqlServerOperationalHealthAssetCatalog.LoadEmbedded().BundleChecksum, 4), Assert.IsType<string[]>(await pins.ExecuteScalarAsync()));

        Assert.Equal("replayed", await ExecuteAgentSourceCommitAsync(collector, firstCommit, payloadOverride: legacyJson));
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(firstCommit, CancellationToken.None)).Status);
        Assert.Equal(history, await ReadAgentSourceStateAsync(database, target, omitSource: true));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Null(Assert.Single((await ReadAgentSourcePageAsync(server, target, legacy.FirstObservedAtUtc)).Items).SourceLocalStart);

        await ExecuteAsync(database, "UPDATE control.collector_schedule SET next_due_at=clock_timestamp() WHERE instance_id=@target AND collector_id='sql-agent.failures';", ("target", target));
        AgentSourceRun secondRun = await BeginAgentSourceRunAsync(collector, target, firstRun.Lease);
        SqlAgentFailureObservation reobserved = legacy with { DetectedAtUtc = secondRun.Work.RepositoryTimeUtc, SourceLocalStart = AgentSourceKnownStart };
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(AgentSourceCommit(secondRun, [reobserved]), CancellationToken.None)).Status);
        SqlAgentFailureObservation projected = Assert.Single((await ReadAgentSourcePageAsync(server, target, legacy.FirstObservedAtUtc)).Items);
        Assert.Equal(legacy.FailureFingerprint, projected.FailureFingerprint);
        Assert.Equal(legacy.FirstObservedAtUtc, projected.FirstObservedAtUtc);
        Assert.Equal(AgentSourceKnownStart, projected.SourceLocalStart);
        Assert.Equal(DateTimeKind.Unspecified, projected.SourceLocalStart!.Value.Kind);
        await using var counts = database.DataSource.CreateCommand("""
            SELECT (SELECT count(*) FROM telemetry.sql_agent_failure WHERE instance_id=@target),
              (SELECT count(*) FROM telemetry.sql_agent_failure_occurrence WHERE instance_id=@target),
              (SELECT source_local_start IS NULL FROM telemetry.sql_agent_failure_occurrence WHERE run_id=@first),
              (SELECT source_local_start FROM telemetry.sql_agent_failure_occurrence WHERE run_id=@second);
            """);
        counts.Parameters.AddWithValue("target", target);
        counts.Parameters.AddWithValue("first", firstRun.RunId.Value);
        counts.Parameters.AddWithValue("second", secondRun.RunId.Value);
        await using NpgsqlDataReader reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(AgentSourceKnownStart, reader.GetDateTime(3));
    }

    [Fact]
    public async Task AgentSourceTimeRoundTripsKnownAndNullAndRejectsDivergentReplay()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await PrepareAgentSourceTargetAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        AgentSourceRun run = await BeginAgentSourceRunAsync(collector, target);
        SqlAgentFailureObservation known = AgentSourceObservation(run, Guid.NewGuid(), 42, 'a', AgentSourceKnownStart);
        SqlAgentFailureObservation unknown = AgentSourceObservation(run, Guid.NewGuid(), 43, 'b', null);
        CommitCollectorRunRequest commit = AgentSourceCommit(run, [known, unknown]);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        string committed = await ReadAgentSourceStateAsync(database, target);
        Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(commit, CancellationToken.None)).Status);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        SqlAgentFailureSnapshot page = await ReadAgentSourcePageAsync(server, target, run.Work.RepositoryTimeUtc);
        Assert.Equal(2, page.Items.Count);
        SqlAgentFailureObservation actualKnown = Assert.Single(page.Items, item => item.FailureFingerprint == known.FailureFingerprint);
        Assert.Equal(AgentSourceKnownStart, actualKnown.SourceLocalStart);
        Assert.Equal(DateTimeKind.Unspecified, actualKnown.SourceLocalStart!.Value.Kind);
        Assert.Equal(known.DurationSeconds, actualKnown.DurationSeconds);
        Assert.Null(Assert.Single(page.Items, item => item.FailureFingerprint == unknown.FailureFingerprint).SourceLocalStart);
        await using (var bytes = database.DataSource.CreateCommand("SELECT persisted_bytes FROM telemetry.collection_run_outcome WHERE run_id=@run;"))
        {
            bytes.Parameters.AddWithValue("run", run.RunId.Value);
            Assert.Equal(328L, Assert.IsType<long>(await bytes.ExecuteScalarAsync()));
        }

        CommitCollectorRunRequest changed = AgentSourceCommit(run, [known with { SourceLocalStart = AgentSourceKnownStart.AddSeconds(1) }, unknown]);
        PostgresException divergent = await Assert.ThrowsAsync<PostgresException>(() => runtime.CommitRunAsync(changed, CancellationToken.None).AsTask());
        Assert.Equal("40001", divergent.SqlState);
        Assert.Equal(committed, await ReadAgentSourceStateAsync(database, target));
    }

    [Fact]
    public async Task AgentSourceTimeInvalidSqlInputsRollBackAndExplicitNullRemainsAccepted()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await PrepareAgentSourceTargetAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        AgentSourceRun run = await BeginAgentSourceRunAsync(collector, target);
        CommitCollectorRunRequest commit = AgentSourceCommit(run, [AgentSourceObservation(run, Guid.NewGuid(), 42, 'a', null)]);
        string before = await ReadAgentSourceStateAsync(database, target);
        foreach (string value in AgentSourceInvalidTimes)
        {
            PostgresException invalid = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAgentSourceCommitAsync(collector, commit,
                mutatePayload: payload => payload["items"]![0]!["sourceLocalStart"] = value));
            Assert.Equal("22023", invalid.SqlState);
            Assert.Equal(before, await ReadAgentSourceStateAsync(database, target));
        }
        Assert.Equal("committed", await ExecuteAgentSourceCommitAsync(collector, commit,
            mutatePayload: payload => payload["items"]![0]!["sourceLocalStart"] = null));
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        Assert.Null(Assert.Single((await ReadAgentSourcePageAsync(server, target, run.Work.RepositoryTimeUtc)).Items).SourceLocalStart);
    }

    [Fact]
    public async Task AgentSourceTimeProjectionKeepsServerOnlyAclScopeAndBoundedInputs()
    {
        await using RepositoryTestDatabase database = await CreateMigratedDatabaseAsync();
        Guid target = Guid.NewGuid();
        await PrepareAgentSourceTargetAsync(database, target);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        AgentSourceRun run = await BeginAgentSourceRunAsync(collector, target);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
            AgentSourceCommit(run, [AgentSourceObservation(run, Guid.NewGuid(), 42, 'a', AgentSourceKnownStart)]), CancellationToken.None)).Status);
        await using (var acl = database.DataSource.CreateCommand("""
            SELECT pg_get_userbyid(p.proowner)='sqlobserver_migrator',p.prosecdef,
              has_function_privilege('sqlobserver_server',p.oid,'EXECUTE'),
              NOT has_function_privilege('sqlobserver_collector',p.oid,'EXECUTE'),
              NOT has_function_privilege('sqlobserver_auditor',p.oid,'EXECUTE'),
              NOT EXISTS(SELECT FROM aclexplode(p.proacl) a WHERE a.grantee=0 AND a.privilege_type='EXECUTE')
            FROM pg_proc p WHERE p.oid=to_regprocedure(@signature);
            """))
        {
            acl.Parameters.AddWithValue("signature", AgentSourceReadSignature);
            await using NpgsqlDataReader reader = await acl.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (int index = 0; index < 6; index++) Assert.True(reader.GetBoolean(index), $"Agent v2 projection ACL check {index}");
        }
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await SetAgentSourceScopeAsync(connection, Guid.NewGuid());
        PostgresException scope = await Assert.ThrowsAsync<PostgresException>(() => CountAgentSourceRowsAsync(connection, run));
        Assert.Equal("42501", scope.SqlState);
        await SetAgentSourceScopeAsync(connection, target);
        Assert.Equal(1L, await CountAgentSourceRowsAsync(connection, run));
        PostgresException oversized = await Assert.ThrowsAsync<PostgresException>(() => CountAgentSourceRowsAsync(connection, run, limit: 202));
        Assert.Equal("22023", oversized.SqlState);
        PostgresException partial = await Assert.ThrowsAsync<PostgresException>(() => CountAgentSourceRowsAsync(connection, run, after: run.Work.RepositoryTimeUtc));
        Assert.Equal("22023", partial.SqlState);
    }

    private sealed record AgentSourceRun(CollectorDueWorkItem Work, WorkerLeaseIdentity Lease, CollectorRunId RunId);

    private static async Task PrepareAgentSourceTargetAsync(RepositoryTestDatabase database, Guid target)
    {
        await ExecuteAsync(database, """
            INSERT INTO control.observation_target(instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,
              authentication_mode,transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
            VALUES(@target,@key,'Agent source time','sql01',1433,interval '5 seconds','windows_integrated_service_identity',
              'mandatory_validated','active',1,statement_timestamp(),statement_timestamp(),statement_timestamp());
            """, ("target", target), ("key", "agent.source." + target.ToString("N")));
        await PrimeOperationalPrerequisitesAsync(database, target);
    }

    private static async Task<AgentSourceRun> BeginAgentSourceRunAsync(NpgsqlDataSource collector, Guid target, WorkerLeaseIdentity? existingLease = null)
    {
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(collector);
        CollectorDueWorkItem work = Assert.Single((await runtime.ListDueAsync(new ListDueCollectorWorkRequest(16, AgentSourceTimeout), CancellationToken.None)).Items,
            item => item.TargetId.Value == target && item.CollectorId.Value == "sql-agent.failures");
        WorkerLeaseIdentity lease = existingLease ?? await AcquireAsync(collector, work);
        var run = new CollectorRunId(Guid.NewGuid());
        Assert.Equal(CollectorRunStartStatus.Started, (await runtime.BeginRunAsync(new BeginCollectorRunRequest(work, run, lease, AgentSourceTimeout), CancellationToken.None)).Status);
        return new AgentSourceRun(work, lease, run);
    }

    private static SqlAgentFailureObservation AgentSourceObservation(AgentSourceRun run, Guid job, long history, char fingerprint, DateTime? local) =>
        new(run.Work.TargetId, run.Work.TargetRevision, job, history, 0, 0, AgentFailureKind.Failed, 500, 16, 0, 90_123,
            run.Work.RepositoryTimeUtc, new string(fingerprint, 64)) { SourceLocalStart = local };

    private static CommitCollectorRunRequest AgentSourceCommit(AgentSourceRun run, IReadOnlyList<SqlAgentFailureObservation> items)
    {
        int bytes = items.Sum(item => 192 + (item.SourceLocalStart is null ? 0 : 8));
        var snapshot = new SqlAgentFailureSnapshot(run.Work.TargetId, run.Work.TargetRevision, run.RunId,
            run.Work.RepositoryTimeUtc, OperationalObservationState.Complete, items, items.Count, false, null, null);
        var payload = new CollectorPayload(operationalHealth: new OperationalHealthPayload(snapshot, items.Count, bytes));
        var summary = new CollectorRunSummary(run.RunId, run.Work.TargetId, run.Work.TargetRevision, run.Work.CollectorId,
            run.Work.CollectorManifestVersion, run.Work.OutputSchemaVersion, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(items.Count, items.Count, bytes, bytes), CollectorLossEvidence.None);
        return new CommitCollectorRunRequest(run.Work, summary, payload, CollectorCircuitSnapshot.Closed(run.Work.RepositoryTimeUtc), run.Lease, AgentSourceTimeout);
    }

    private static string LegacyAgentSourcePayload(CommitCollectorRunRequest request)
    {
        // Freeze the exact pre-0087 anonymous-object member order and JSON shape.
        var agent = Assert.IsType<SqlAgentFailureSnapshot>(request.Payload.OperationalHealth!.Snapshot);
        return JsonSerializer.Serialize(new
        {
            kind = "sql_agent_failures", observedAtUtc = agent.ObservedAtUtc, state = (int)agent.State,
            sourceRowsRead = agent.SourceRowsRead, truncated = agent.Truncated,
            coverageFromUtc = (DateTimeOffset?)null, coverageToUtc = (DateTimeOffset?)null,
            items = agent.Items.Select(x => new { x.JobId, x.HistoryInstanceId, x.StepId, x.RunStatus,
                failureKind = (int)x.FailureKind, x.MessageId, x.Severity, x.RetryAttempt, x.DurationSeconds,
                x.FirstObservedAtUtc, x.FailureFingerprint }),
        }, AgentSourceJsonOptions);
    }

    private static async Task<string> ExecuteAgentSourceCommitAsync(NpgsqlDataSource collector, CommitCollectorRunRequest request,
        string? payloadOverride = null, Action<JsonObject>? mutatePayload = null)
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        Type port = typeof(PostgreSqlCollectorRuntimeRepositoryPort);
        string sql = Assert.IsType<string>(port.GetField("CommitAgentM9Sql", hidden)!.GetValue(null));
        byte[] requestDigest = (byte[])port.GetMethod("CreateRequestDigest", hidden)!.Invoke(null, [request.Work, request.Summary.RunId, request.Lease])!;
        await using NpgsqlConnection connection = await collector.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 10 };
        port.GetMethod("AddRunIdentity", hidden)!.Invoke(null, [command, request.Work, request.Summary.RunId, request.Lease, requestDigest]);
        port.GetMethod("AddM9CommitParameters", hidden)!.Invoke(null, [command, request]);
        if (payloadOverride is not null || mutatePayload is not null)
        {
            string json = payloadOverride ?? Assert.IsType<string>(command.Parameters["m9_payload"].Value);
            if (mutatePayload is not null)
            {
                JsonObject parsed = JsonNode.Parse(json)!.AsObject();
                mutatePayload(parsed);
                json = parsed.ToJsonString(AgentSourceJsonOptions);
            }
            command.Parameters["m9_payload"].Value = json;
            command.Parameters["completion_digest"].Value = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        }
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<SqlAgentFailureSnapshot> ReadAgentSourcePageAsync(NpgsqlDataSource server, Guid target, DateTimeOffset firstObserved) =>
        Assert.IsType<SqlAgentFailureSnapshot>(await new PostgreSqlOperationalHealthProjectionPort(server).GetAgentFailuresAsync(
            new OperationalHealthRequest(new MonitoredInstanceId(target), firstObserved.AddMinutes(-1), firstObserved.AddMinutes(5),
                50, null, AgentSourceTimeout), CancellationToken.None));

    private static async Task<string> ReadAgentSourceStateAsync(RepositoryTestDatabase database, Guid target, bool omitSource = false)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_build_object(
              'events',(SELECT jsonb_agg(to_jsonb(f) ORDER BY failure_fingerprint) FROM telemetry.sql_agent_failure f WHERE instance_id=@target),
              'occurrences',(SELECT jsonb_agg(CASE WHEN @omit_source THEN to_jsonb(o)-'source_local_start' ELSE to_jsonb(o) END ORDER BY run_id,failure_fingerprint) FROM telemetry.sql_agent_failure_occurrence o WHERE instance_id=@target),
              'scans',(SELECT jsonb_agg(to_jsonb(s) ORDER BY run_id) FROM telemetry.sql_agent_failure_scan_snapshot s WHERE instance_id=@target),
              'replays',(SELECT jsonb_agg(to_jsonb(r) ORDER BY run_id) FROM telemetry.m9_commit_replay r WHERE instance_id=@target),
              'outcomes',(SELECT jsonb_agg(to_jsonb(o) ORDER BY o.run_id) FROM telemetry.collection_run_outcome o JOIN telemetry.collection_run r USING(run_id) WHERE r.instance_id=@target),
              'schedule',(SELECT to_jsonb(s) FROM control.collector_schedule s WHERE instance_id=@target AND collector_id='sql-agent.failures'))::text;
            """);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("omit_source", omitSource);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ReadAgentSourceRegistryAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT jsonb_agg(CASE WHEN collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
              AND collector_version=1 THEN to_jsonb(c)-'asset_bundle_sha256' ELSE to_jsonb(c) END ORDER BY collector_id,collector_version)::text
            FROM control.collector_contract c;
            """);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task SetAgentSourceScopeAsync(NpgsqlConnection connection, Guid target)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("scope", target.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAgentSourceRowsAsync(NpgsqlConnection connection, AgentSourceRun run, int limit = 201, DateTimeOffset? after = null)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM reporting.list_sql_agent_failures_v2(@target,@run,1,@from,@to,@after,@fingerprint,@limit);", connection) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("target", run.Work.TargetId.Value);
        command.Parameters.AddWithValue("run", run.RunId.Value);
        command.Parameters.AddWithValue("from", run.Work.RepositoryTimeUtc.AddMinutes(-1));
        command.Parameters.AddWithValue("to", run.Work.RepositoryTimeUtc.AddMinutes(1));
        command.Parameters.AddWithValue("after", NpgsqlDbType.TimestampTz, (object?)after ?? DBNull.Value);
        command.Parameters.AddWithValue("fingerprint", NpgsqlDbType.Bytea, DBNull.Value);
        command.Parameters.AddWithValue("limit", limit);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
