using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheRuntimeNativeSourceTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task RestrictedNativeCacheReadsMatchCountersStayQuietAndRecognizeRefilledRecompiles()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(settings.ConnectionString);
        await admin.OpenAsync(deadline.Token);
        int major = Assert.IsType<int>(await Scalar(admin, "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'));"));
        Assert.InRange(major, 15, 17);
        string suffix = Guid.NewGuid().ToString("N"), name = $"SqlObserverCacheProbe_{suffix}", login = $"sqlobserver_cache_probe_{suffix}";
        bool createdDatabase = false, createdLogin = false;
        try
        {
            await Execute(admin, $"CREATE DATABASE [{name}];"); createdDatabase = true;
            var databaseSettings = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = name };
            await using var database = new SqlConnection(databaseSettings.ConnectionString);
            await database.OpenAsync(deadline.Token);
            await Execute(database, "CREATE TABLE dbo.cache_probe_rows(value int NOT NULL); INSERT dbo.cache_probe_rows VALUES(1),(2),(3),(4),(5);");
            await Execute(database, "CREATE PROCEDURE dbo.cache_probe @minimum int AS SELECT SUM(value) FROM dbo.cache_probe_rows WHERE value>=@minimum; SELECT COUNT_BIG(*) FROM dbo.cache_probe_rows WHERE value>=@minimum;");
            for (int run = 0; run < 3; run++) await Execute(database, "EXEC dbo.cache_probe @minimum=1;");
            int databaseId = Assert.IsType<int>(await Scalar(database, "SELECT CONVERT(int,DB_ID());"));
            int objectId = Assert.IsType<int>(await Scalar(database, "SELECT CONVERT(int,OBJECT_ID('dbo.cache_probe'));"));
            // Qualification is explicit inside this already-open disposable
            // database. The target-wide cache source never opens GUID catalogs.
            Guid guid = Assert.IsType<Guid>(await Scalar(database, "SELECT database_guid FROM sys.database_recovery_status WHERE database_id=DB_ID();"));
            string incarnation = Convert.ToHexString(SHA256.HashData(guid.ToByteArray())).ToLowerInvariant();
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA!1";
            await Execute(admin, $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF;"); createdLogin = true;
            await Execute(admin, $"GRANT {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} TO [{login}]; GRANT VIEW ANY DATABASE TO [{login}];");
            var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
            PlanCacheRuntimeSourceBundle bundle = PlanCacheRuntimeSourceBundle.LoadEmbedded();
            PlanCacheRuntimeSourcePage first = await ReadRestricted(2000);
            Dictionary<string, PlanCacheRuntimeCounters> reference = await Reference(admin, databaseId, objectId);
            PlanCacheRuntimeSourceRow[] owned = Owned(first, reference);
            Assert.Equal(2, owned.Length); Assert.All(owned, row => Assert.Equal(3L, row.Counters.Executions));
            Assert.All(owned, row => Assert.Equal(reference[row.CacheEntryFingerprint], row.Counters));
            Assert.NotEqual(owned[0].Query, owned[1].Query);
            HashSet<string> legacyQueries = await LegacyQueries(admin, databaseId);
            Assert.All(owned, row => Assert.Contains(row.Query.QueryFingerprint, legacyQueries));
            PlanCacheRuntimeSourcePage quiet = await ReadRestricted(2000);
            Assert.Equal(first.SourceEpoch, quiet.SourceEpoch);
            foreach (PlanCacheRuntimeSourceRow row in Owned(quiet, reference))
            {
                var old = owned.Single(item => item.CacheEntryFingerprint == row.CacheEntryFingerprint);
                var result = PlanCacheRuntimeWatermarkCalculator.Compare(Watermark(first, old), Watermark(quiet, row), true);
                Assert.Equal(PlanCacheRuntimeTransitionKind.Comparable, result.Kind);
                Assert.Equal(new(0, 0, 0, 0, 0, 0), result.Delta);
            }
            for (int run = 0; run < 2; run++) await Execute(database, "EXEC dbo.cache_probe @minimum=1;");
            PlanCacheRuntimeSourcePage work = await ReadRestricted(2000);
            reference = await Reference(admin, databaseId, objectId);
            PlanCacheRuntimeSourceRow[] worked = Owned(work, reference);
            foreach (PlanCacheRuntimeSourceRow row in worked)
            {
                Assert.Equal(reference[row.CacheEntryFingerprint], row.Counters);
                var old = owned.Single(item => item.CacheEntryFingerprint == row.CacheEntryFingerprint);
                var result = PlanCacheRuntimeWatermarkCalculator.Compare(Watermark(first, old), Watermark(work, row), true);
                Assert.Equal(2L, result.Delta!.Executions);
            }
            await Execute(database, "EXEC sys.sp_recompile N'dbo.cache_probe';");
            for (int run = 0; run < 10; run++) await Execute(database, "EXEC dbo.cache_probe @minimum=1;");
            PlanCacheRuntimeSourcePage recompiled = await ReadRestricted(2000);
            reference = await Reference(admin, databaseId, objectId);
            foreach (PlanCacheRuntimeSourceRow row in Owned(recompiled, reference))
            {
                Assert.Equal(reference[row.CacheEntryFingerprint], row.Counters);
                var old = worked.Single(item => item.Query == row.Query);
                Assert.NotEqual(old.CacheEntryFingerprint, row.CacheEntryFingerprint);
                Assert.True(row.Counters.Executions > old.Counters.Executions);
                var result = PlanCacheRuntimeWatermarkCalculator.Compare(Watermark(work, old), Watermark(recompiled, row), true);
                Assert.Equal(PlanCacheRuntimeTransitionKind.BaselineUnavailable, result.Kind);
                Assert.Null(result.Delta);
            }
            PlanCacheRuntimeSourceRow[] beforeEviction = Owned(recompiled, reference);
            await EvictOwnedPlan(admin, databaseId, objectId);
            for (int run = 0; run < 20; run++) await Execute(database, "EXEC dbo.cache_probe @minimum=1;");
            PlanCacheRuntimeSourcePage recached = await ReadRestricted(2000);
            reference = await Reference(admin, databaseId, objectId);
            foreach (PlanCacheRuntimeSourceRow row in Owned(recached, reference))
            {
                Assert.Equal(reference[row.CacheEntryFingerprint], row.Counters);
                var old = beforeEviction.Single(item => item.Query == row.Query);
                Assert.NotEqual(old.CacheEntryFingerprint, row.CacheEntryFingerprint);
                Assert.True(row.Counters.Executions > old.Counters.Executions);
                var result = PlanCacheRuntimeWatermarkCalculator.Compare(Watermark(recompiled, old), Watermark(recached, row), true);
                Assert.Equal(PlanCacheRuntimeTransitionKind.BaselineUnavailable, result.Kind);
                Assert.Null(result.Delta);
            }
            PlanCacheRuntimeSourcePage capped = await ReadRestricted(1);
            Assert.Equal(2, capped.CandidateRows); Assert.False(capped.Exhaustive); Assert.InRange(capped.Rows.Count, 0, 1);
            await Execute(admin, $"REVOKE {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} FROM [{login}];");
            SqlException denied = await Assert.ThrowsAsync<SqlException>(() => ReadRestricted(2000));
            Assert.True(SqlServerCollectorErrorClassifier.IsPermissionDenied(denied.Number));

            PlanCacheRuntimeWatermark Watermark(PlanCacheRuntimeSourcePage page, PlanCacheRuntimeSourceRow row) =>
                new(new(target, revision, row.Query, row.Plan, incarnation, page.SourceEpoch, row.CacheEntryFingerprint), row.Counters, page.ReadCompletedAtUtc);

            async Task<PlanCacheRuntimeSourcePage> ReadRestricted(int limit)
            {
                await Execute(admin, $"EXECUTE AS LOGIN='{login}';");
                try
                {
                    Assert.Equal(0, await Scalar(admin, "SELECT IS_SRVROLEMEMBER('sysadmin');"));
                    Assert.Equal(0, await Scalar(admin, "SELECT HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER');"));
                    return await bundle.ReadAsync(admin, target, revision, limit, deadline.Token);
                }
                finally { await Execute(admin, "REVERT;"); }
            }
        }
        finally
        {
            // Connections to the disposable database have left scope before
            // cleanup; no server-wide cache clear or service changes occur.
            if (createdDatabase) await Execute(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            if (createdLogin) await Execute(admin, $"DROP LOGIN [{login}];");
        }
    }

    private static PlanCacheRuntimeSourceRow[] Owned(PlanCacheRuntimeSourcePage page, Dictionary<string, PlanCacheRuntimeCounters> reference)
    {
        var rows = page.Rows.Where(row => reference.ContainsKey(row.CacheEntryFingerprint)).ToArray();
        Assert.Equal(reference.Count, rows.Length); return rows;
    }

    private static async Task<Dictionary<string, PlanCacheRuntimeCounters>> Reference(SqlConnection connection, int databaseId, int objectId)
    {
        await using var command = new SqlCommand("""
            SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(binary(4),ps.database_id)+
              CONVERT(binary(4),DATALENGTH(s.plan_handle))+s.plan_handle+
              CONVERT(binary(4),DATALENGTH(s.sql_handle))+s.sql_handle+
              CONVERT(binary(4),s.statement_start_offset)+CONVERT(binary(4),s.statement_end_offset)+CONVERT(binary(8),s.plan_generation_num)+
              CONVERT(varbinary(48),CONVERT(varchar(33),s.creation_time,126))),2),
              s.total_worker_time,s.total_elapsed_time,s.execution_count,s.total_logical_reads,s.total_logical_writes,s.total_rows
            FROM sys.dm_exec_query_stats s JOIN sys.dm_exec_procedure_stats ps ON ps.plan_handle=s.plan_handle
            WHERE ps.database_id=@database AND ps.object_id=@object;
            """, connection) { CommandTimeout = 20 };
        command.Parameters.AddWithValue("database", databaseId); command.Parameters.AddWithValue("object", objectId);
        var result = new Dictionary<string, PlanCacheRuntimeCounters>(StringComparer.Ordinal);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0).ToLowerInvariant(), new(reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        Assert.Equal(2, result.Count); return result;
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EvictOwnedPlan(SqlConnection connection, int databaseId, int objectId)
    {
        await using var lookup = new SqlCommand("SELECT plan_handle FROM sys.dm_exec_procedure_stats WHERE database_id=@database AND object_id=@object;", connection)
        { CommandTimeout = 20 };
        lookup.Parameters.AddWithValue("database", databaseId); lookup.Parameters.AddWithValue("object", objectId);
        byte[] handle;
        await using (SqlDataReader reader = await lookup.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); handle = reader.GetFieldValue<byte[]>(0);
            Assert.InRange(handle.Length, 1, 64); Assert.False(await reader.ReadAsync());
        }
        // The mandatory non-null parameter comes solely from this disposable
        // database's exact procedure. Never issue an unqualified cache clear.
        await using var evict = new SqlCommand("DBCC FREEPROCCACHE(@owned_handle) WITH NO_INFOMSGS;", connection)
        { CommandTimeout = 20 };
        evict.Parameters.Add("@owned_handle", System.Data.SqlDbType.VarBinary, 64).Value = handle;
        await evict.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> LegacyQueries(SqlConnection connection, int databaseId)
    {
        await using var command = new SqlCommand(SqlServerQueryPerformanceCollector.PlanCacheSql, connection)
        { CommandTimeout = 20 };
        command.Parameters.Add("@probe_rows", System.Data.SqlDbType.Int).Value = 2001;
        command.Parameters.Add("@sample_start", System.Data.SqlDbType.DateTime2).Value = DateTime.UtcNow.AddMinutes(-1);
        command.Parameters.Add("@sample_end", System.Data.SqlDbType.DateTime2).Value = DateTime.UtcNow;
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (reader.GetInt32(0) == databaseId) result.Add(reader.GetString(1).ToLowerInvariant());
        return result;
    }

    private static async Task<object?> Scalar(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        return await command.ExecuteScalarAsync();
    }
}
