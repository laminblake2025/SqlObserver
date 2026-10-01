using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Security;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.PostgreSql;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.IntegrationTests.SqlServer;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class QueryPerformanceV2CommitPostgreSqlTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task NativeCombinedProducerCommitsBaselinesDeltasProtectedLinksReplayAndRollback()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(settings.ConnectionString); await admin.OpenAsync(deadline.Token);
        string suffix = Guid.NewGuid().ToString("N"), login = $"sqlobserver_v2_roundtrip_{suffix}";
        string[] names = [$"SqlObserverV2QueryStore_{suffix}", $"SqlObserverV2Cache_{suffix}"];
        var created = new List<string>(); bool loginCreated = false;
        try
        {
            int major = Assert.IsType<int>(await NativeScalar(admin, "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'));", deadline.Token));
            Assert.InRange(major, 15, 17);
            foreach (string name in names)
            {
                await NativeExecute(admin, $"CREATE DATABASE [{name}];", deadline.Token); created.Add(name);
                await NativeExecute(admin, $"ALTER DATABASE [{name}] SET AUTO_CLOSE OFF;", deadline.Token);
            }
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA!1";
            await NativeExecute(admin, $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF;", deadline.Token); loginCreated = true;
            await NativeExecute(admin, $"GRANT {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} TO [{login}]; GRANT VIEW ANY DATABASE TO [{login}];", deadline.Token);
            await NativeExecute(admin, $"ALTER DATABASE [{names[0]}] SET QUERY_STORE=ON (OPERATION_MODE=READ_WRITE,QUERY_CAPTURE_MODE=ALL); ALTER DATABASE [{names[1]}] SET QUERY_STORE=OFF;", deadline.Token);
            int[] ids = new int[2];
            for (int index = 0; index < names.Length; index++)
            {
                await using var connection = new SqlConnection(new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = names[index] }.ConnectionString);
                await connection.OpenAsync(deadline.Token);
                ids[index] = Assert.IsType<int>(await NativeScalar(connection, "SELECT CONVERT(int,DB_ID());", deadline.Token));
                await NativeExecute(connection, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT {(major == 15 ? "VIEW DATABASE STATE" : "VIEW DATABASE PERFORMANCE STATE")} TO [{login}]; CREATE TABLE dbo.runtime_probe_rows(value int NOT NULL); INSERT dbo.runtime_probe_rows VALUES(1),(2),(3);", deadline.Token);
                await NativeExecute(connection, "CREATE PROCEDURE dbo.runtime_probe AS SELECT SUM(value) FROM dbo.runtime_probe_rows;", deadline.Token);
                for (int run = 0; run < (index == 0 ? 3 : 5); run++) await NativeExecute(connection, "EXEC dbo.runtime_probe;", deadline.Token);
                if (index == 0) await NativeExecute(connection, "EXEC sys.sp_query_store_flush_db;", deadline.Token);
            }
            await using Scene scene = await Prepare();
            SqlServerEndpoint endpoint = SqlServerLabContract.Endpoint;
            await using (var configure = scene.Database.DataSource.CreateCommand("UPDATE control.observation_target SET host_name=@host,instance_name=@instance,tcp_port=@port WHERE instance_id=@target;"))
            {
                configure.Parameters.AddWithValue("host", endpoint.HostName.Value);
                configure.Parameters.AddWithValue("instance", (object?)endpoint.InstanceName?.Value ?? DBNull.Value);
                configure.Parameters.AddWithValue("port", (object?)endpoint.TcpPort ?? DBNull.Value);
                configure.Parameters.AddWithValue("target", scene.Target); await configure.ExecuteNonQueryAsync(deadline.Token);
            }
            var factory = new RoundtripSqlFactory(settings.ConnectionString, login);
            var discovery = new SqlServerCapabilityDiscoveryPort(factory, SqlServerCapabilityAssetCatalog.LoadEmbedded(),
                SqlServerCapabilityV2AssetCatalog.LoadEmbedded(), SqlServerCapabilityV3AssetCatalog.LoadEmbedded(),
                assetsV4: SqlServerCapabilityV4AssetCatalog.LoadEmbedded());
            var target = new MonitoredInstanceId(scene.Target);
            CapabilityProfile profile = await discovery.DiscoverAsync(new(target, new(1), SqlServerLabContract.ConnectionPolicy,
                new(TimeSpan.FromSeconds(10)), new(TimeSpan.FromMinutes(5))), deadline.Token);
            Assert.NotNull(profile.ServerIdentity); Assert.Equal(major, profile.ServerIdentity.Version.Major); Assert.False(profile.IsSysAdmin);
            var leases = new PostgreSqlWorkerLeasePort(scene.Collector);
            WorkerLeaseIdentity capabilityLease = Assert.IsType<WorkerLease>((await leases.AcquireAsync(new(new("collector/capability.connection"),
                new(Guid.NewGuid()), new(TimeSpan.FromMinutes(1)), Timeout), deadline.Token)).Lease).Identity;
            try
            {
                var audit = new AdministrativeAuditEnvelope(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"),
                    new(Guid.NewGuid()), AdministrativeAuditAction.RecordCapabilityProfile, target);
                Assert.Equal(CapabilityProfileRecordStatus.Recorded, (await new PostgreSqlCapabilityProfilePort(scene.Collector)
                    .RecordAsync(new(profile, capabilityLease, audit, Timeout), deadline.Token)).Status);
            }
            finally { await leases.ReleaseAsync(new(capabilityLease, Timeout), deadline.Token); }
            await using NpgsqlDataSource migrator = MigratorDataSource(scene);
            var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(migrator);
            using var protector = new RoundtripProtector();
            var collector = new SqlServerQueryPerformanceV2Collector(factory, protector);
            var registration = new CollectorRegistration(9, collector, new CollectorOutputValidator(SqlServerQueryPerformanceV2Collector.OutputContract),
                new(QueryPerformanceV2ManifestTests.ManifestDigest), new(QueryPerformanceV2ManifestTests.BundleDigest));
            Frame first = await Start(scene, cache: false);
            Assert.NotNull(first.Work.CapabilityProfile);
            var firstResult = await new CollectorExecutionEngine().ExecuteAsync(registration, first.Work, first.Run, deadline.Token);
            Assert.True(firstResult.Summary.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial, firstResult.Summary.Reason.ToString());
            Assert.Contains(firstResult.Payload.QueryPerformance.Items, item => item.Query.DatabaseId == ids[0]);
            Assert.DoesNotContain(firstResult.Payload.QueryPerformance.Items, item => item.Query.DatabaseId == ids[1]);
            Assert.Contains(firstResult.Payload.QueryPerformanceStatuses, status => status.DatabaseId == ids[1] && status.Status == QueryPerformanceReadStatus.QueryStoreDisabled && status.SourceState == QueryStoreState.Disabled);
            Assert.DoesNotContain(firstResult.Payload.QueryPerformanceStatuses, status => status.FallbackAttempted);
            var firstProbe = Assert.Single(firstResult.Payload.PlanCacheRuntime!.Items, item =>
                item.Watermark.Key.Query.DatabaseId == ids[1] && item.Watermark.Counters.Executions == 5);
            Assert.Contains(firstResult.Payload.QueryPerformance.Items, item => item.ProtectedContent is not null);
            Assert.Contains(firstResult.Payload.QueryPerformance.Items, item => item.ProtectedPlanContent is not null);
            CollectorPayload firstLinked = await Link(first, firstResult.Payload);
            var firstRequest = new CommitCollectorRunRequest(first.Work, firstResult.Summary, firstLinked, firstResult.NextCircuit, first.Lease, Timeout);
            var committed = await runtime.CommitRunAsync(firstRequest, deadline.Token);
            Assert.Equal(CollectorRunCommitStatus.Committed, committed.Status);
            Assert.Equal(firstLinked.ItemCount, committed.InsertedCount); Assert.Equal(firstLinked.EstimatedSizeBytes, committed.PersistedBytes);
            await AssertDelta(first, firstProbe, null);
            await AssertCanonical(first, firstResult, firstLinked);
            string afterFirst = await Snapshot(scene);
            Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(firstRequest, deadline.Token)).Status);
            Assert.Equal(afterFirst, await Snapshot(scene));
            CollectorRunSummary original = firstResult.Summary;
            var changedSummary = new CollectorRunSummary(original.RunId, original.TargetId, original.TargetRevision,
                original.CollectorId, original.CollectorManifestVersion, original.OutputSchemaVersion, original.Outcome,
                original.Reason, original.Duration + TimeSpan.FromMilliseconds(1), original.AttemptCount,
                original.Accounting, original.Loss);
            Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () => await runtime.CommitRunAsync(
                new(first.Work, changedSummary, firstLinked, firstResult.NextCircuit, first.Lease, Timeout), deadline.Token))).SqlState);
            Assert.Equal(afterFirst, await Snapshot(scene));
            await using (var connection = new SqlConnection(new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = names[1] }.ConnectionString))
            {
                await connection.OpenAsync(deadline.Token);
                for (int run = 0; run < 3; run++) await NativeExecute(connection, "EXEC dbo.runtime_probe;", deadline.Token);
            }
            Frame second = await Start(scene, cache: false);
            var secondResult = await new CollectorExecutionEngine().ExecuteAsync(registration, second.Work, second.Run, deadline.Token);
            Assert.True(secondResult.Summary.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial, secondResult.Summary.Reason.ToString());
            var secondProbe = Assert.Single(secondResult.Payload.PlanCacheRuntime!.Items,
                item => item.Watermark.Key == firstProbe.Watermark.Key);
            Assert.Equal(8, secondProbe.Watermark.Counters.Executions);
            CollectorPayload secondLinked = await Link(second, secondResult.Payload);
            var secondRequest = new CommitCollectorRunRequest(second.Work, secondResult.Summary, secondLinked, secondResult.NextCircuit, second.Lease, Timeout);
            QueryPerformanceObservation replace = secondLinked.QueryPerformance.Items.First(static item => item.PlanContentReference is not null);
            var badItems = secondLinked.QueryPerformance.Items.Select(item => ReferenceEquals(item, replace)
                ? item.WithPlanContentReference(new(new(Guid.NewGuid()), SensitivePayloadKind.ExecutionPlan, replace.PlanContentReference!.Fingerprint)) : item).ToArray();
            var badPayload = new CollectorPayload(queryPerformance: new(badItems), queryPerformanceStatuses: secondLinked.QueryPerformanceStatuses,
                planCacheRuntime: secondLinked.PlanCacheRuntime);
            string beforeFailure = await Snapshot(scene);
            Assert.Equal("23503", (await Assert.ThrowsAsync<PostgresException>(async () => await runtime.CommitRunAsync(
                new(second.Work, secondResult.Summary, badPayload, secondResult.NextCircuit, second.Lease, Timeout), deadline.Token))).SqlState);
            Assert.Equal(beforeFailure, await Snapshot(scene));
            await Sql(scene, $"""
                CREATE FUNCTION control.expire_native_roundtrip_fixture() RETURNS trigger LANGUAGE plpgsql AS $trigger$
                BEGIN UPDATE control.worker_lease SET released_at=clock_timestamp() WHERE work_key='{second.Lease.Key.Value}';
                RETURN NEW; END $trigger$;
                CREATE TRIGGER expire_native_roundtrip_fixture BEFORE INSERT ON telemetry.collection_run_outcome
                    FOR EACH ROW EXECUTE FUNCTION control.expire_native_roundtrip_fixture();
                """);
            CollectorRunCommitResult lost = await runtime.CommitRunAsync(secondRequest, deadline.Token);
            Assert.Equal(CollectorRunCommitStatus.LeaseLost, lost.Status); Assert.Equal(0, lost.InsertedCount); Assert.Equal(0, lost.PersistedBytes);
            Assert.Equal(beforeFailure, await Snapshot(scene));
            await Sql(scene, "DROP TRIGGER expire_native_roundtrip_fixture ON telemetry.collection_run_outcome; DROP FUNCTION control.expire_native_roundtrip_fixture();");
            Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(secondRequest, deadline.Token)).Status);
            await AssertDelta(second, secondProbe, firstProbe);
            await AssertCanonical(second, secondResult, secondLinked);
            string afterSecond = await Snapshot(scene);
            Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(secondRequest, deadline.Token)).Status);
            Assert.Equal(afterSecond, await Snapshot(scene)); Assert.Equal(0, factory.Active);

            // Exercise the public read port as the server role against the actual native counter differences.
            await using var server = scene.Database.CreateServerDataSource();
            var ranking = await new SqlObserver.Application.Services.PlanCacheIntervalRankingQueryService(
                new PostgreSqlPlanCacheIntervalRankingPort(server)).ReadAsync(
                    new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), AuthorizationPrincipalState.Active,
                        [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target]))]),
                    new PlanCacheIntervalRankingRequest(target, firstResult.Payload.PlanCacheRuntime.ReadStartedUtc.AddMilliseconds(-1),
                        secondResult.Payload.PlanCacheRuntime.ReadCompletedUtc.AddMilliseconds(1), QueryPerformanceMetric.CpuMilliseconds, 100, Timeout),
                    deadline.Token);
            var ranked = Assert.Single(ranking.Items, item => item.DatabaseId == ids[1]
                && item.QueryFingerprint == secondProbe.Watermark.Key.Query.QueryFingerprint
                && item.PlanFingerprint == secondProbe.Watermark.Key.Plan.PlanFingerprint
                && item.DatabaseIncarnation == secondProbe.Watermark.Key.DatabaseIncarnation);
            Assert.Equal("3", ranked.Executions);
            var beforeCounters = firstProbe.Watermark.Counters; var afterCounters = secondProbe.Watermark.Counters;
            Assert.Equal((afterCounters.CpuMicroseconds - beforeCounters.CpuMicroseconds) / 1000m,
                decimal.Parse(ranked.CpuMilliseconds, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal((afterCounters.DurationMicroseconds - beforeCounters.DurationMicroseconds) / 1000m,
                decimal.Parse(ranked.DurationMilliseconds, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal((afterCounters.LogicalReads - beforeCounters.LogicalReads).ToString(System.Globalization.CultureInfo.InvariantCulture), ranked.LogicalReads);
            Assert.Equal((afterCounters.Writes - beforeCounters.Writes).ToString(System.Globalization.CultureInfo.InvariantCulture), ranked.Writes);
            Assert.Equal((afterCounters.Rows - beforeCounters.Rows).ToString(System.Globalization.CultureInfo.InvariantCulture), ranked.Rows);

            var history = await new SqlObserver.Application.Services.PlanCacheIntervalHistoryQueryService(
                new PostgreSqlPlanCacheIntervalHistoryPort(server)).ReadAsync(
                    new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), AuthorizationPrincipalState.Active,
                        [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target]))]),
                    new PlanCacheIntervalHistoryRequest(target,ids[1],secondProbe.Watermark.Key.DatabaseIncarnation,
                        secondProbe.Watermark.Key.Query.QueryFingerprint,secondProbe.Watermark.Key.Plan.PlanFingerprint,
                        ranking.FromUtc,ranking.ToUtc,100,Timeout),deadline.Token);
            var measuredHistory = Assert.Single(history.Items,item => item.CacheEntryFingerprint == secondProbe.Watermark.Key.CacheEntryFingerprint && item.Transition == "comparable");
            Assert.Equal("3",measuredHistory.Executions);
            Assert.Equal(firstProbe.Watermark.ObservedAtUtc,measuredHistory.IntervalFromUtc);
            Assert.Equal(secondProbe.Watermark.ObservedAtUtc,measuredHistory.IntervalToUtc);
            Assert.Equal(decimal.Parse(ranked.CpuMilliseconds,System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(measuredHistory.CpuMilliseconds!,System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains(history.Items,item => item.CacheEntryFingerprint == firstProbe.Watermark.Key.CacheEntryFingerprint
                && item.Transition == "baseline_unavailable" && item.Executions is null);

            async Task<CollectorPayload> Link(Frame frame, CollectorPayload source)
            {
                CollectorPayload linked = await new QueryPerformanceProtectedContentCommitter(new PostgreSqlSensitivePayloadPort(scene.Collector))
                    .WriteAsync(source, target, frame.Lease, Timeout, deadline.Token);
                Assert.Same(source.PlanCacheRuntime, linked.PlanCacheRuntime);
                Assert.Equal(QueryPerformanceV2PersistenceBudget.Measure(source.QueryPerformance.Items, source.QueryPerformanceStatuses, source.PlanCacheRuntime),
                    QueryPerformanceV2PersistenceBudget.Measure(linked.QueryPerformance.Items, linked.QueryPerformanceStatuses, linked.PlanCacheRuntime));
                Assert.DoesNotContain(linked.QueryPerformance.Items, item => item.ProtectedContent is not null || item.ProtectedPlanContent is not null);
                return linked;
            }
            async Task AssertDelta(Frame frame, PlanCacheRuntimeStatementObservation current, PlanCacheRuntimeStatementObservation? previous)
            {
                await using var command = scene.Database.DataSource.CreateCommand("SELECT transition,cpu_us,duration_us,execution_count,logical_reads,writes,rows_processed FROM events.plan_cache_runtime_delta WHERE run_id=@run AND database_id=@database AND cache_entry_fingerprint=@entry;");
                command.Parameters.AddWithValue("run", frame.Run.Value); command.Parameters.AddWithValue("database", ids[1]);
                command.Parameters.AddWithValue("entry", Convert.FromHexString(current.Watermark.Key.CacheEntryFingerprint));
                await using var reader = await command.ExecuteReaderAsync(deadline.Token); Assert.True(await reader.ReadAsync(deadline.Token));
                Assert.Equal(previous is null ? "baseline_unavailable" : "comparable", reader.GetString(0));
                if (previous is null) for (int index = 1; index < 7; index++) Assert.True(reader.IsDBNull(index));
                else
                {
                    var now = current.Watermark.Counters; var old = previous.Watermark.Counters;
                    long[] deltas = [now.CpuMicroseconds-old.CpuMicroseconds, now.DurationMicroseconds-old.DurationMicroseconds,
                        now.Executions-old.Executions, now.LogicalReads-old.LogicalReads, now.Writes-old.Writes, now.Rows-old.Rows];
                    for (int index = 0; index < deltas.Length; index++) Assert.Equal(deltas[index], reader.GetInt64(index + 1));
                    Assert.Equal(3, deltas[2]);
                }
                Assert.False(await reader.ReadAsync(deadline.Token));
            }
            async Task AssertCanonical(Frame frame, CollectorEngineResult result, CollectorPayload linked)
            {
                await using var command = scene.Database.DataSource.CreateCommand("""
                    SELECT
                        (SELECT output_item_count=@items AND output_bytes=@bytes AND source_row_count=@source
                            AND response_bytes=@response FROM telemetry.collection_run_outcome WHERE run_id=@run),
                        (SELECT count(*)=@qs FROM events.query_performance_observation WHERE collection_run_id=@run),
                        (SELECT count(*)=@cache FROM events.plan_cache_runtime_delta WHERE run_id=@run),
                        (SELECT count(*)>0 FROM events.query_performance_content_link WHERE collection_run_id=@run),
                        (SELECT count(*)>0 FROM events.query_performance_plan_content_link WHERE collection_run_id=@run),
                        (SELECT active_run_id IS NULL FROM control.collector_schedule
                            WHERE instance_id=@target AND collector_id='queries.performance'),
                        (SELECT count(*)=@waits FROM events.query_performance_wait_snapshot WHERE collection_run_id=@run);
                    """);
                command.Parameters.AddWithValue("run", frame.Run.Value); command.Parameters.AddWithValue("target", scene.Target);
                command.Parameters.AddWithValue("items", linked.ItemCount); command.Parameters.AddWithValue("bytes", linked.EstimatedSizeBytes);
                command.Parameters.AddWithValue("source", result.Summary.Accounting.SourceRowsRead); command.Parameters.AddWithValue("response", result.Summary.Accounting.ResponseBytes);
                command.Parameters.AddWithValue("qs", linked.QueryPerformance.Items.Count); command.Parameters.AddWithValue("cache", linked.PlanCacheRuntime!.Items.Count);
                command.Parameters.AddWithValue("waits", linked.QueryPerformance.Items.Count(static item => item.WaitSnapshot is not null));
                await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
                {
                    Assert.True(await reader.ReadAsync(deadline.Token));
                    for (int index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
                }
                foreach (QueryPerformanceObservation item in linked.QueryPerformance.Items)
                {
                    await using var metrics = scene.Database.DataSource.CreateCommand("""
                        SELECT cpu_ms,duration_ms,execution_count,logical_reads,writes,rows_processed
                        FROM events.query_performance_observation WHERE collection_run_id=@run AND database_id=@database
                            AND query_fingerprint=@query AND plan_fingerprint=@plan AND interval_start=@start AND interval_end=@end;
                        """);
                    metrics.Parameters.AddWithValue("run", frame.Run.Value); metrics.Parameters.AddWithValue("database", item.Query.DatabaseId);
                    metrics.Parameters.AddWithValue("query", Convert.FromHexString(item.Query.QueryFingerprint));
                    metrics.Parameters.AddWithValue("plan", Convert.FromHexString(item.Plan!.PlanFingerprint));
                    metrics.Parameters.AddWithValue("start", item.IntervalStartUtc); metrics.Parameters.AddWithValue("end", item.IntervalEndUtc);
                    await using var row = await metrics.ExecuteReaderAsync(deadline.Token); Assert.True(await row.ReadAsync(deadline.Token));
                    long?[] expected = [item.Metrics.CpuMilliseconds, item.Metrics.DurationMilliseconds, item.Metrics.Executions,
                        item.Metrics.LogicalReads, item.Metrics.Writes, item.Metrics.Rows];
                    for (int index = 0; index < expected.Length; index++) Assert.Equal((object?)expected[index] ?? DBNull.Value, row.GetValue(index));
                    Assert.False(await row.ReadAsync(deadline.Token));
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (string name in created)
                await NativeExecute(admin, $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", cleanup.Token);
            if (loginCreated) await NativeExecute(admin, $"DROP LOGIN [{login}];", cleanup.Token);
        }
    }

    private sealed class RoundtripSqlFactory(string connectionString, string login) : ISqlServerConnectionFactory
    {
        internal int Active;
        public async ValueTask<SqlConnection> OpenConnectionAsync(SqlServerConnectionPolicy policy, CancellationToken token)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(token);
                await NativeExecute(connection, $"EXECUTE AS LOGIN=N'{login}';", token);
                Assert.Equal(0, Assert.IsType<int>(await NativeScalar(connection, "SELECT IS_SRVROLEMEMBER('sysadmin');", token)));
                Interlocked.Increment(ref Active);
                connection.StateChange += (_, change) => { if (change.OriginalState == System.Data.ConnectionState.Open && change.CurrentState == System.Data.ConnectionState.Closed) Interlocked.Decrement(ref Active); };
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
    private sealed class RoundtripProtector : IQuerySensitiveContentProtector, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public bool IsAvailable => true;
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ciphertext = new byte[content.Length];
            using var cipher = new AesGcm(key, 16); cipher.Encrypt(nonce, content.Span, ciphertext, tag, targetId.ToByteArray());
            return ValueTask.FromResult<ProtectedSensitivePayload?>(new(kind, new(SHA256.HashData(content.Span)), "AES-256-GCM", "disposable-roundtrip-key", nonce, tag, ciphertext));
        }
        public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); byte[] content = new byte[payload.CiphertextLengthBytes];
            using var cipher = new AesGcm(key, 16); cipher.Decrypt(payload.GetNonce(), payload.GetCiphertext(), payload.GetAuthenticationTag(), content, targetId.ToByteArray());
            return ValueTask.FromResult(content);
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }
    private static async Task NativeExecute(SqlConnection connection, string sql, CancellationToken token)
    { await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync(token); }
    private static async Task<object?> NativeScalar(SqlConnection connection, string sql, CancellationToken token)
    { await using var command = new SqlCommand(sql, connection) { CommandTimeout = 10 }; return await command.ExecuteScalarAsync(token); }
}
