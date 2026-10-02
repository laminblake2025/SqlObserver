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
    public Task NativeCombinedProducerCommitsBaselinesDeltasProtectedLinksReplayAndRollback() => NativeRoundtrip(false);

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public Task NativeCacheEvictionAndRecompileCommitNewBaselinesBeforeResumingMeasuredHistory() => NativeRoundtrip(true);

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public Task NativeDatabaseRecreationKeepsHistoricalIncarnationsSeparateThroughCommitAndReads() => NativeRoundtrip(true, true);

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public Task V3NativeCombinedProducerPersistsProtectedCacheContentWithCountersReplayAndRollback() =>
        NativeRoundtrip(false, protocolVersion: 3);

    private async Task NativeRoundtrip(bool qualifyCacheLifecycle, bool recreateDatabase = false, int protocolVersion = 2)
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
            await using Scene scene = await Prepare(protocolVersion: protocolVersion);
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
            ISqlServerCollector collector = protocolVersion == 3 ? new SqlServerQueryPerformanceV3Collector(factory, protector) : new SqlServerQueryPerformanceV2Collector(factory, protector);
            var registration = new CollectorRegistration(9, collector, new CollectorOutputValidator(protocolVersion == 3
                ? SqlServerQueryPerformanceV3Collector.OutputContract : SqlServerQueryPerformanceV2Collector.OutputContract),
                new(protocolVersion == 3 ? QueryPerformanceV3ManifestTests.ManifestDigest : QueryPerformanceV2ManifestTests.ManifestDigest),
                new(protocolVersion == 3 ? QueryPerformanceV3ManifestTests.BundleDigest : QueryPerformanceV2ManifestTests.BundleDigest));
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
            if (protocolVersion == 3)
            {
                var cacheContent = firstLinked.PlanCacheContent!;
                Assert.Equal(PlanCacheContentCaptureState.Enabled, cacheContent.CaptureState);
                Assert.Contains(cacheContent.Items, item => item.Text.Reference is not null && item.Plan.Reference is not null);
                await using var linkedEvidence = scene.Database.DataSource.CreateCommand("""
                    SELECT count(*)=@count AND bool_and(link.content_state<>'available' OR payload.payload_id IS NOT NULL)
                    FROM events.plan_cache_content_link link
                    LEFT JOIN security.protected_diagnostic_payload payload ON payload.payload_id=link.content_reference AND payload.instance_id=link.instance_id
                    WHERE link.run_id=@run;
                    """);
                linkedEvidence.Parameters.AddWithValue("run", first.Run.Value); linkedEvidence.Parameters.AddWithValue("count", cacheContent.Items.Count * 2);
                Assert.True(Assert.IsType<bool>(await linkedEvidence.ExecuteScalarAsync(deadline.Token)));
                await using var contentServer = scene.Database.CreateServerDataSource();
                var contentService = new SqlObserver.Application.Services.PlanCacheContentReadService(
                    new PostgreSqlPlanCacheContentReadRepositoryPort(contentServer), protector);
                var contentAuthorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), AuthorizationPrincipalState.Active,
                    [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target])),
                     new RoleAuthorizationGrant(ApplicationRole.QueryTextReader, TargetAuthorizationScope.ForTargets([target]))]);
                foreach (var item in firstResult.Payload.PlanCacheContent!.Items)
                    foreach (var value in new[] { item.Text, item.Plan })
                    {
                        if (value.ProtectedContent is not { } encrypted) continue;
                        byte[] clear = await protector.UnprotectAsync(target.Value, encrypted, deadline.Token);
                        try
                        {
                            Assert.NotEmpty(clear); Assert.InRange(clear.Length, 1, value.CiphertextBytes);
                            var contentKey = item.Statement.Watermark.Key;
                            var opened = await contentService.ReadAsync(contentAuthorization, new(target, first.Run.Value,
                                contentKey.Query.DatabaseId, contentKey.DatabaseIncarnation, contentKey.Query.QueryFingerprint,
                                contentKey.Plan.PlanFingerprint, contentKey.CacheEntryFingerprint, value.Kind, Timeout), deadline.Token);
                            Assert.Equal("available", opened.Status);
                            Assert.Equal(System.Text.Encoding.UTF8.GetString(clear), opened.Content);
                            Assert.Equal(firstLinked.PlanCacheRuntime!.ReadCompletedUtc, opened.ObservedAtUtc);
                        }
                        finally { CryptographicOperations.ZeroMemory(clear); }
                    }
            }
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
                planCacheRuntime: secondLinked.PlanCacheRuntime, planCacheContent: secondLinked.PlanCacheContent);
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
                    new PlanCacheIntervalHistoryRequest(target, ids[1], secondProbe.Watermark.Key.DatabaseIncarnation,
                        secondProbe.Watermark.Key.Query.QueryFingerprint, secondProbe.Watermark.Key.Plan.PlanFingerprint,
                        ranking.FromUtc, ranking.ToUtc, 100, Timeout), deadline.Token);
            var measuredHistory = Assert.Single(history.Items, item => item.CacheEntryFingerprint == secondProbe.Watermark.Key.CacheEntryFingerprint && item.Transition == "comparable");
            Assert.Equal("3", measuredHistory.Executions);
            Assert.Equal(firstProbe.Watermark.ObservedAtUtc, measuredHistory.IntervalFromUtc);
            Assert.Equal(secondProbe.Watermark.ObservedAtUtc, measuredHistory.IntervalToUtc);
            Assert.Equal(decimal.Parse(ranked.CpuMilliseconds, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(measuredHistory.CpuMilliseconds!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains(history.Items, item => item.CacheEntryFingerprint == firstProbe.Watermark.Key.CacheEntryFingerprint
                && item.Transition == "baseline_unavailable" && item.Executions is null);

            if (qualifyCacheLifecycle)
            {
                var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21-1-2-3-1234"), AuthorizationPrincipalState.Active,
                    [new RoleAuthorizationGrant(ApplicationRole.Viewer, TargetAuthorizationScope.ForTargets([target]))]);
                var historyService = new SqlObserver.Application.Services.PlanCacheIntervalHistoryQueryService(new PostgreSqlPlanCacheIntervalHistoryPort(server));
                var rankingService = new SqlObserver.Application.Services.PlanCacheIntervalRankingQueryService(new PostgreSqlPlanCacheIntervalRankingPort(server));
                await using var cacheDatabase = new SqlConnection(new SqlConnectionStringBuilder(settings.ConnectionString)
                { InitialCatalog = names[1] }.ConnectionString);
                await cacheDatabase.OpenAsync(deadline.Token);
                // Remove only this owned fixture procedure. Capture its absence before
                // executing it again; the collector itself remains entirely passive.
                await NativeExecute(cacheDatabase, "EXEC sys.sp_recompile N'dbo.runtime_probe';", deadline.Token);
                var absent = await Capture();
                Assert.DoesNotContain(absent.Result.Payload.PlanCacheRuntime!.Items, item => item.Watermark.Key.Query == secondProbe.Watermark.Key.Query);
                var absentHistory = await ReadHistory(secondProbe, absent.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                Assert.Equal(2, absentHistory.Items.Count(item => item.CacheEntryFingerprint == firstProbe.Watermark.Key.CacheEntryFingerprint));
                Assert.DoesNotContain(absentHistory.Items, item => item.CollectionRunId == absent.Frame.Run.Value);
                Assert.Equal("3", Assert.Single(absentHistory.Items, item => item.Transition == "comparable").Executions);

                // New allocation has a HIGHER total than the old entry. Identity,
                // rather than a counter decrease, must prevent an invented delta.
                for (int execution = 0; execution < 10; execution++) await NativeExecute(cacheDatabase, "EXEC dbo.runtime_probe;", deadline.Token);
                var baseline = await Capture();
                var freshProbe = Assert.Single(baseline.Result.Payload.PlanCacheRuntime!.Items,
                    item => item.Watermark.Key.Query == secondProbe.Watermark.Key.Query && item.Watermark.Counters.Executions == 10);
                Assert.NotEqual(secondProbe.Watermark.Key.CacheEntryFingerprint, freshProbe.Watermark.Key.CacheEntryFingerprint);
                Assert.Equal(secondProbe.Watermark.Key.DatabaseIncarnation, freshProbe.Watermark.Key.DatabaseIncarnation);
                Assert.Equal(secondProbe.Watermark.Key.SourceEpoch, freshProbe.Watermark.Key.SourceEpoch);
                await AssertDelta(baseline.Frame, freshProbe, null);
                var baselineHistory = await ReadHistory(freshProbe, baseline.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                var newBaseline = Assert.Single(baselineHistory.Items, item => item.CollectionRunId == baseline.Frame.Run.Value
                    && item.CacheEntryFingerprint == freshProbe.Watermark.Key.CacheEntryFingerprint);
                Assert.Equal("baseline_unavailable", newBaseline.Transition);
                Assert.Null(newBaseline.IntervalFromUtc); Assert.Null(newBaseline.IntervalToUtc);
                Assert.Null(newBaseline.CpuMilliseconds); Assert.Null(newBaseline.DurationMilliseconds); Assert.Null(newBaseline.Executions);
                Assert.Null(newBaseline.LogicalReads); Assert.Null(newBaseline.Writes); Assert.Null(newBaseline.Rows);
                var baselineRanking = await ReadRanking(baseline.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                Assert.Equal(3m, QueryExecutions(baselineRanking));

                for (int execution = 0; execution < 3; execution++) await NativeExecute(cacheDatabase, "EXEC dbo.runtime_probe;", deadline.Token);
                var resumed = await Capture();
                var resumedProbe = Assert.Single(resumed.Result.Payload.PlanCacheRuntime!.Items,
                    item => item.Watermark.Key == freshProbe.Watermark.Key);
                Assert.Equal(13, resumedProbe.Watermark.Counters.Executions);
                await AssertDelta(resumed.Frame, resumedProbe, freshProbe);
                var resumedHistory = await ReadHistory(resumedProbe, resumed.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                var measured = Assert.Single(resumedHistory.Items, item => item.CollectionRunId == resumed.Frame.Run.Value
                    && item.CacheEntryFingerprint == freshProbe.Watermark.Key.CacheEntryFingerprint);
                Assert.Equal("comparable", measured.Transition); Assert.Equal("3", measured.Executions);
                Assert.Equal(freshProbe.Watermark.ObservedAtUtc, measured.IntervalFromUtc);
                Assert.Equal(resumedProbe.Watermark.ObservedAtUtc, measured.IntervalToUtc);
                var finalRanking = await ReadRanking(resumed.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                Assert.Equal(6m, QueryExecutions(finalRanking));
                Assert.Equal(0, factory.Active);

                if (recreateDatabase)
                {
                    await cacheDatabase.CloseAsync();
                    await NativeExecute(admin, $"ALTER DATABASE [{names[1]}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{names[1]}]; CREATE DATABASE [{names[1]}]; ALTER DATABASE [{names[1]}] SET AUTO_CLOSE OFF; ALTER DATABASE [{names[1]}] SET QUERY_STORE=OFF;", deadline.Token);
                    await cacheDatabase.OpenAsync(deadline.Token);
                    int recreatedId = Assert.IsType<int>(await NativeScalar(cacheDatabase, "SELECT CONVERT(int,DB_ID());", deadline.Token));
                    Assert.Equal(ids[1], recreatedId);
                    await NativeExecute(cacheDatabase, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT {(major == 15 ? "VIEW DATABASE STATE" : "VIEW DATABASE PERFORMANCE STATE")} TO [{login}]; CREATE TABLE dbo.runtime_probe_rows(value int NOT NULL); INSERT dbo.runtime_probe_rows VALUES(1),(2),(3);", deadline.Token);
                    await NativeExecute(cacheDatabase, "CREATE PROCEDURE dbo.runtime_probe AS SELECT SUM(value) FROM dbo.runtime_probe_rows;", deadline.Token);
                    for (int execution = 0; execution < 20; execution++) await NativeExecute(cacheDatabase, "EXEC dbo.runtime_probe;", deadline.Token);
                    var recreatedBaseline = await Capture();
                    var recreatedProbe = Assert.Single(recreatedBaseline.Result.Payload.PlanCacheRuntime!.Items,
                        item => item.Watermark.Key.Query == secondProbe.Watermark.Key.Query && item.Watermark.Counters.Executions == 20);
                    Assert.NotEqual(secondProbe.Watermark.Key.DatabaseIncarnation, recreatedProbe.Watermark.Key.DatabaseIncarnation);
                    Assert.Equal(secondProbe.Watermark.Key.SourceEpoch, recreatedProbe.Watermark.Key.SourceEpoch);
                    await AssertDelta(recreatedBaseline.Frame, recreatedProbe, null);
                    var recreatedHistory = await ReadHistory(recreatedProbe, recreatedBaseline.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                    var initial = Assert.Single(recreatedHistory.Items);
                    Assert.Equal("baseline_unavailable", initial.Transition); Assert.Null(initial.Executions);
                    var beforeNewDelta = await ReadRanking(recreatedBaseline.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                    Assert.Equal(6m, QueryExecutions(beforeNewDelta));
                    Assert.DoesNotContain(beforeNewDelta.Items, item => item.DatabaseIncarnation == recreatedProbe.Watermark.Key.DatabaseIncarnation
                        && item.QueryFingerprint == recreatedProbe.Watermark.Key.Query.QueryFingerprint);

                    for (int execution = 0; execution < 3; execution++) await NativeExecute(cacheDatabase, "EXEC dbo.runtime_probe;", deadline.Token);
                    var recreatedComparison = await Capture();
                    var comparedProbe = Assert.Single(recreatedComparison.Result.Payload.PlanCacheRuntime!.Items,
                        item => item.Watermark.Key == recreatedProbe.Watermark.Key);
                    Assert.Equal(23, comparedProbe.Watermark.Counters.Executions);
                    await AssertDelta(recreatedComparison.Frame, comparedProbe, recreatedProbe);
                    var newHistory = await ReadHistory(comparedProbe, recreatedComparison.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                    Assert.Equal(2, newHistory.Items.Count);
                    Assert.Equal("3", Assert.Single(newHistory.Items, item => item.Transition == "comparable").Executions);
                    Assert.DoesNotContain(newHistory.Items, item => item.CacheEntryFingerprint == secondProbe.Watermark.Key.CacheEntryFingerprint
                        || item.CacheEntryFingerprint == resumedProbe.Watermark.Key.CacheEntryFingerprint);
                    var oldHistory = await ReadHistory(resumedProbe, recreatedComparison.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                    Assert.Equal(2, oldHistory.Items.Count(item => item.CacheEntryFingerprint == resumedProbe.Watermark.Key.CacheEntryFingerprint));
                    Assert.DoesNotContain(oldHistory.Items, item => item.CacheEntryFingerprint == comparedProbe.Watermark.Key.CacheEntryFingerprint);
                    var incarnations = await ReadRanking(recreatedComparison.Result.Payload.PlanCacheRuntime.ReadCompletedUtc);
                    Assert.Equal(6m, QueryExecutions(incarnations));
                    var newRank = Assert.Single(incarnations.Items, item => item.DatabaseId == ids[1]
                        && item.QueryFingerprint == comparedProbe.Watermark.Key.Query.QueryFingerprint
                        && item.DatabaseIncarnation == comparedProbe.Watermark.Key.DatabaseIncarnation);
                    Assert.Equal("3", newRank.Executions);
                    Assert.Equal(0, factory.Active);
                }

                async Task<PlanCacheIntervalHistoryResult> ReadHistory(PlanCacheRuntimeStatementObservation probe, DateTimeOffset end) =>
                    await historyService.ReadAsync(authorization, new(target, ids[1], probe.Watermark.Key.DatabaseIncarnation,
                        probe.Watermark.Key.Query.QueryFingerprint, probe.Watermark.Key.Plan.PlanFingerprint,
                        ranking.FromUtc, end.AddMilliseconds(1), 100, Timeout), deadline.Token);
                async Task<PlanCacheIntervalRankingResult> ReadRanking(DateTimeOffset end) =>
                    await rankingService.ReadAsync(authorization, new(target, ranking.FromUtc, end.AddMilliseconds(1),
                        QueryPerformanceMetric.Executions, 100, Timeout), deadline.Token);
                decimal QueryExecutions(PlanCacheIntervalRankingResult rows) => rows.Items.Where(item => item.DatabaseId == ids[1]
                    && item.DatabaseIncarnation == secondProbe.Watermark.Key.DatabaseIncarnation
                    && item.QueryFingerprint == secondProbe.Watermark.Key.Query.QueryFingerprint)
                    .Sum(item => decimal.Parse(item.Executions, System.Globalization.CultureInfo.InvariantCulture));
            }

            async Task<(Frame Frame, CollectorEngineResult Result)> Capture()
            {
                Frame frame = await Start(scene, cache: false);
                var result = await new CollectorExecutionEngine().ExecuteAsync(registration, frame.Work, frame.Run, deadline.Token);
                Assert.True(result.Summary.Outcome is CollectorRunOutcome.Succeeded or CollectorRunOutcome.Partial, result.Summary.Reason.ToString());
                Assert.NotNull(result.Payload.PlanCacheRuntime);
                CollectorPayload linked = await Link(frame, result.Payload);
                Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
                    new(frame.Work, result.Summary, linked, result.NextCircuit, frame.Lease, Timeout), deadline.Token)).Status);
                await AssertCanonical(frame, result, linked);
                string beforeReplay = await Snapshot(scene);
                Assert.Equal(CollectorRunCommitStatus.Replayed, (await runtime.CommitRunAsync(
                    new(frame.Work, result.Summary, linked, result.NextCircuit, frame.Lease, Timeout), deadline.Token)).Status);
                Assert.Equal(beforeReplay, await Snapshot(scene));
                return (frame, result);
            }

            async Task<CollectorPayload> Link(Frame frame, CollectorPayload source)
            {
                CollectorPayload linked = await new QueryPerformanceProtectedContentCommitter(new PostgreSqlSensitivePayloadPort(scene.Collector))
                    .WriteAsync(source, target, frame.Lease, Timeout, deadline.Token);
                Assert.Same(source.PlanCacheRuntime, linked.PlanCacheRuntime);
                Assert.Equal(QueryPerformanceV3PersistenceBudget.Measure(source.QueryPerformance.Items, source.QueryPerformanceStatuses, source.PlanCacheRuntime, source.PlanCacheContent),
                    QueryPerformanceV3PersistenceBudget.Measure(linked.QueryPerformance.Items, linked.QueryPerformanceStatuses, linked.PlanCacheRuntime, linked.PlanCacheContent));
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
