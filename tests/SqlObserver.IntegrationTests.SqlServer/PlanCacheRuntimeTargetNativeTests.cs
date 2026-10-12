using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheRuntimeTargetNativeTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task RestrictedTargetSweepQualifiesAccessibleStatementsAndDoesNotOpenClosedDatabases()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(settings.ConnectionString); await admin.OpenAsync(deadline.Token);
        int major = Assert.IsType<int>(await Scalar(admin, "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'));", deadline.Token));
        Assert.InRange(major, 15, 17);
        string suffix = Guid.NewGuid().ToString("N"), login = $"sqlobserver_target_probe_{suffix}";
        string[] names = [$"SqlObserverTargetOwned_{suffix}", $"SqlObserverTargetDenied_{suffix}", $"SqlObserverTargetClosed_{suffix}"];
        var created = new List<string>(); bool loginCreated = false;
        try
        {
            foreach (string name in names)
            {
                await Execute(admin, $"CREATE DATABASE [{name}];", deadline.Token); created.Add(name);
                if (name != names[2]) await Execute(admin, $"ALTER DATABASE [{name}] SET AUTO_CLOSE OFF;", deadline.Token);
            }
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA!1";
            await Execute(admin, $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF;", deadline.Token); loginCreated = true;
            await Execute(admin, $"GRANT {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} TO [{login}]; GRANT VIEW ANY DATABASE TO [{login}];", deadline.Token);
            int[] ids = new int[3];
            for (int index = 0; index < names.Length; index++)
            {
                var databaseSettings = new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = names[index] };
                await using var database = new SqlConnection(databaseSettings.ConnectionString); await database.OpenAsync(deadline.Token);
                if (index == 0) await Execute(admin, $"ALTER DATABASE [{names[0]}] SET QUERY_STORE=ON (OPERATION_MODE=READ_WRITE,QUERY_CAPTURE_MODE=ALL);", deadline.Token);
                ids[index] = Assert.IsType<int>(await Scalar(database, "SELECT CONVERT(int,DB_ID());", deadline.Token));
                await Execute(database, "CREATE TABLE dbo.target_probe_rows(value int NOT NULL); INSERT dbo.target_probe_rows VALUES(1),(2),(3);", deadline.Token);
                await Execute(database, "CREATE PROCEDURE dbo.target_probe AS SELECT SUM(value) FROM dbo.target_probe_rows;", deadline.Token);
                for (int run = 0; run < 3; run++) await Execute(database, "EXEC dbo.target_probe;", deadline.Token);
                if (index == 0)
                {
                    await Execute(database, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT {(major == 15 ? "VIEW DATABASE STATE" : "VIEW DATABASE PERFORMANCE STATE")} TO [{login}];", deadline.Token);
                    await Execute(database, "EXEC sys.sp_query_store_flush_db;", deadline.Token);
                }
            }
            await Execute(admin, $"ALTER DATABASE [{names[2]}] SET AUTO_CLOSE ON;", deadline.Token);
            for (int attempt = 0; attempt < 100 && await Closed() != DBNull.Value; attempt++) await Task.Delay(100, deadline.Token);
            Assert.Equal(DBNull.Value, await Closed());
            var factory = new RestrictedFactory(settings.ConnectionString, login);
            var source = new PlanCacheRuntimeTargetSource(new PlanCacheRuntimeTargetExecutionPort(factory));
            CollectorExecutionRequest request = PlanCacheRuntimeTargetSourceTests.Request();
            PlanCacheRuntimeTargetRead first = await source.ReadAsync(request, 2000, deadline.Token);
            Assert.InRange(factory.Peak, 1, PlanCacheRuntimeTargetSource.MaximumConcurrentDatabases);
            Assert.All(first.Output.Items, item =>
            {
                Assert.Equal(ids[0], item.Watermark.Key.Query.DatabaseId);
                Assert.Equal(item.Before.DatabaseIncarnation, item.After.DatabaseIncarnation);
                Assert.Equal(first.Output.SourceEpoch, item.Before.SourceEpoch);
            });
            Assert.Contains(first.Output.Items, item => item.Watermark.Counters.Executions == 3);
            Assert.DoesNotContain(first.Output.Items, item => item.Watermark.Key.Query.DatabaseId == ids[1]);
            Assert.True(first.Output.UnqualifiedRows > 0);
            Assert.InRange(first.SourceRowsRead, first.Output.CandidateRows, first.Output.CandidateRows + 512);
            Assert.Equal(DBNull.Value, await Closed());
            Assert.True(factory.Opened > 1); Assert.Equal(0, factory.Active);
            PlanCacheRuntimeTargetRead second = await source.ReadAsync(request, 1, deadline.Token);
            Assert.Equal(1, second.Output.CandidateLimit); Assert.InRange(second.Output.CandidateRows, 0, 2);
            Assert.InRange(second.Output.Items.Count, 0, 1); Assert.Equal(0, factory.Active);
            Assert.Equal(DBNull.Value, await Closed());
            CollectorExecutionRequest combinedRequest = QueryPerformanceV2NativeCollectorTests.Request();
            using var protector = new LabProtector();
            var collector = new SqlServerQueryPerformanceV2Collector(factory, protector);
            CollectorExecutionResult combined = await collector.CollectAsync(combinedRequest, deadline.Token);
            Assert.Equal(CollectorRunOutcome.Partial, combined.Outcome);
            Assert.NotEmpty(combined.Payload.QueryPerformance.Items);
            Assert.All(combined.Payload.QueryPerformance.Items, item =>
            {
                Assert.Equal(ids[0], item.Query.DatabaseId);
                Assert.Equal(SqlObserver.Domain.Collection.QueryPerformanceSource.QueryStore, item.Source);
            });
            Assert.NotEmpty(combined.Payload.PlanCacheRuntime!.Items);
            Assert.Contains(combined.Payload.QueryPerformance.Items, item => item.ProtectedContent is not null);
            Assert.Contains(combined.Payload.QueryPerformance.Items, item => item.ProtectedPlanContent is not null);
            Assert.DoesNotContain(combined.Payload.QueryPerformanceStatuses, status => status.FallbackAttempted);
            new SqlObserver.Collectors.CollectorOutputValidator(SqlServerQueryPerformanceV2Collector.OutputContract)
                .Validate(collector.Manifest, combinedRequest, combined);
            Assert.Equal(DBNull.Value, await Closed()); Assert.Equal(0, factory.Active);

            async Task<object?> Closed()
            {
                await using var command = new SqlCommand("SELECT collation_name FROM sys.databases WHERE name=@name;", admin);
                command.Parameters.AddWithValue("name", names[2]); return await command.ExecuteScalarAsync(deadline.Token);
            }
        }
        finally
        {
            foreach (string name in created.AsEnumerable().Reverse())
                await Execute(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", default);
            if (loginCreated) await Execute(admin, $"DROP LOGIN [{login}];", default);
        }
    }

    private sealed class LabProtector : IQuerySensitiveContentProtector, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public bool IsAvailable => true;
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind,
            ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], ciphertext = new byte[content.Length];
            using var cipher = new AesGcm(key, 16);
            cipher.Encrypt(nonce, content.Span, ciphertext, tag, targetId.ToByteArray());
            return ValueTask.FromResult<ProtectedSensitivePayload?>(new(kind, new(SHA256.HashData(content.Span)),
                "AES-256-GCM", "disposable-lab-key", nonce, tag, ciphertext));
        }
        public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); byte[] content = new byte[payload.CiphertextLengthBytes];
            using var cipher = new AesGcm(key, 16);
            cipher.Decrypt(payload.GetNonce(), payload.GetCiphertext(), payload.GetAuthenticationTag(), content, targetId.ToByteArray());
            return ValueTask.FromResult(content);
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }

    private sealed class RestrictedFactory(string connectionString, string login) : ISqlServerConnectionFactory
    {
        internal int Active, Peak, Opened;
        public async ValueTask<SqlConnection> OpenConnectionAsync(SqlObserver.Domain.Targets.SqlServerConnectionPolicy policy,
            CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                await Execute(connection, $"EXECUTE AS LOGIN='{login}';", cancellationToken);
                Assert.Equal(0, await Scalar(connection, "SELECT IS_SRVROLEMEMBER('sysadmin');", cancellationToken));
                int active = Interlocked.Increment(ref Active), old;
                do { old = Peak; } while (active > old && Interlocked.CompareExchange(ref Peak, active, old) != old);
                Interlocked.Increment(ref Opened);
                connection.StateChange += (_, change) => { if (change.CurrentState == System.Data.ConnectionState.Closed) Interlocked.Decrement(ref Active); };
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }
    }

    private static async Task Execute(SqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 }; await command.ExecuteNonQueryAsync(token);
    }
    private static async Task<object?> Scalar(SqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 10 }; return await command.ExecuteScalarAsync(token);
    }
}
