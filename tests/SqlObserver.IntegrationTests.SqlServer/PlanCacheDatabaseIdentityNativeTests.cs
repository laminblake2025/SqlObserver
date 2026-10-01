using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheDatabaseIdentityNativeTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task RestrictedLiveBracketsRejectReusedDatabaseIdAndLeaveClosedDatabaseClosed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(settings.ConnectionString);
        await admin.OpenAsync(deadline.Token);
        int major = Assert.IsType<int>(await Scalar(admin, "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'));"));
        Assert.InRange(major, 15, 17);
        string suffix = Guid.NewGuid().ToString("N"), name = $"SqlObserverCacheIdentityProbe_{suffix}",
            closedName = $"SqlObserverCacheIdentityClosedProbe_{suffix}", login = $"sqlobserver_cache_identity_probe_{suffix}";
        bool databaseCreated = false, closedCreated = false, loginCreated = false;
        var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(3);
        var identity = PlanCacheDatabaseIdentitySource.LoadEmbedded(); var source = PlanCacheRuntimeSourceBundle.LoadEmbedded();
        try
        {
            await Execute(admin, $"CREATE DATABASE [{name}];"); databaseCreated = true;
            await Execute(admin, $"CREATE DATABASE [{closedName}];"); closedCreated = true;
            await Execute(admin, $"ALTER DATABASE [{closedName}] SET AUTO_CLOSE ON;");
            var closedSettings = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = closedName };
            await using (var closed = new SqlConnection(closedSettings.ConnectionString))
            {
                await closed.OpenAsync(deadline.Token);
                await Execute(closed, "CREATE TABLE dbo.closed_probe(value int NOT NULL);");
            }
            // The catalog's NULL collation exposes AUTO_CLOSE without opening it.
            for (int attempt = 0; attempt < 100 && await ClosedCollation() != DBNull.Value; attempt++)
                await Task.Delay(100, deadline.Token);
            Assert.Equal(DBNull.Value, await ClosedCollation());
            string password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "aA!1";
            await Execute(admin, $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF;"); loginCreated = true;
            await Execute(admin, $"GRANT {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} TO [{login}]; GRANT VIEW ANY DATABASE TO [{login}];");
            var first = await ReadOwned(3);
            Assert.Equal(DBNull.Value, await ClosedCollation());
            Assert.NotEmpty(first.Read.Statements);
            Assert.Equal(first.Read.Source.Rows.Count - first.Read.Statements.Count, first.Read.UnqualifiedStatementRows);
            Assert.All(first.Read.Statements, statement =>
            {
                Assert.Equal(first.Before.DatabaseIncarnation, statement.Watermark.Key.DatabaseIncarnation);
                Assert.True(statement.Before.ReadCompletedAtUtc <= first.Read.Source.ReadStartedAtUtc);
                Assert.True(statement.After.ReadStartedAtUtc >= first.Read.Source.ReadCompletedAtUtc);
            });
            await Execute(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];"); databaseCreated = false;
            await Execute(admin, $"CREATE DATABASE [{name}];"); databaseCreated = true;
            var replaced = await ReadOwned(20);
            Assert.Equal(first.Before.DatabaseId, replaced.Before.DatabaseId);
            Assert.NotEqual(first.Before.DatabaseIncarnation, replaced.Before.DatabaseIncarnation);
            Assert.Equal(first.Before.SourceEpoch, replaced.Before.SourceEpoch);
            var interrupted = PlanCacheRuntimeSourceAssembler.Bind(first.Read.Source, [first.Before], [replaced.After]);
            Assert.Empty(interrupted.Statements);
            Assert.Equal(first.Read.Source.Rows.Count, interrupted.UnqualifiedStatementRows);
            var previous = first.Read.Statements.First(statement => statement.Watermark.Counters.Executions == 3).Watermark;
            var current = replaced.Read.Statements.Single(statement => statement.Watermark.Key.Query == previous.Key.Query).Watermark;
            Assert.Equal(20, current.Counters.Executions);
            var transition = PlanCacheRuntimeWatermarkCalculator.Compare(previous, current, true);
            Assert.Equal(PlanCacheRuntimeTransitionKind.BaselineUnavailable, transition.Kind); Assert.Null(transition.Delta);
            Assert.Equal(DBNull.Value, await ClosedCollation());
            // A system connection cannot silently become a user-database identity.
            SqlException invalidContext = await Assert.ThrowsAsync<SqlException>(() => identity.ReadAsync(admin,
                target, revision, deadline.Token));
            Assert.Equal(51001, invalidContext.Number);

            async Task<(PlanCacheDatabaseIdentitySnapshot Before, PlanCacheDatabaseIdentitySnapshot After,
                PlanCacheRuntimeQualifiedRead Read)> ReadOwned(int executions)
            {
                var databaseSettings = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = name };
                await using var database = new SqlConnection(databaseSettings.ConnectionString);
                await database.OpenAsync(deadline.Token);
                await Execute(database, $"CREATE USER [{login}] FOR LOGIN [{login}];");
                await Execute(database, "CREATE TABLE dbo.identity_probe_rows(value int NOT NULL); INSERT dbo.identity_probe_rows VALUES(1),(2),(3);");
                await Execute(database, "CREATE PROCEDURE dbo.identity_probe AS SELECT SUM(value) FROM dbo.identity_probe_rows; SELECT COUNT_BIG(*) FROM dbo.identity_probe_rows;");
                for (int run = 0; run < executions; run++) await Execute(database, "EXEC dbo.identity_probe;");
                await Execute(database, $"EXECUTE AS LOGIN='{login}';");
                try
                {
                    Assert.Equal(0, await Scalar(database, "SELECT IS_SRVROLEMEMBER('sysadmin');"));
                    var before = await identity.ReadAsync(database, target, revision, deadline.Token);
                    var page = await source.ReadAsync(database, target, revision, 2000, deadline.Token);
                    var after = await identity.ReadAsync(database, target, revision, deadline.Token);
                    Assert.NotNull(before.DatabaseIncarnation); Assert.Equal(before.DatabaseIncarnation, after.DatabaseIncarnation);
                    Assert.True(page.Rows.Count(row => row.Query.DatabaseId == before.DatabaseId) >= 2);
                    // Other visible databases retain explicit unqualified rows.
                    return (before, after, PlanCacheRuntimeSourceAssembler.Bind(page, [before], [after]));
                }
                finally { await Execute(database, "REVERT;"); }
            }

            async Task<object?> ClosedCollation()
            {
                await using var command = new SqlCommand("SELECT collation_name FROM sys.databases WHERE name=@name;", admin) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("name", closedName);
                return await command.ExecuteScalarAsync(deadline.Token);
            }
        }
        finally
        {
            if (databaseCreated) await Execute(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            if (closedCreated) await Execute(admin, $"ALTER DATABASE [{closedName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{closedName}];");
            if (loginCreated) await Execute(admin, $"DROP LOGIN [{login}];");
        }
    }

    private static async Task Execute(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        return await command.ExecuteScalarAsync();
    }
}
