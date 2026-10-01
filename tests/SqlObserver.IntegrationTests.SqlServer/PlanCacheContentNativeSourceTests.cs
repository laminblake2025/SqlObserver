using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheContentNativeSourceTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task RestrictedNativeCaptureProtectsExactStatementPlanAndSkipsContentWithoutAKey()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString)
        { InitialCatalog = "master", Pooling = false };
        await using var admin = new SqlConnection(settings.ConnectionString); await admin.OpenAsync(deadline.Token);
        int major = Assert.IsType<int>(await Scalar(admin, "SELECT CONVERT(int,SERVERPROPERTY('ProductMajorVersion'));"));
        Assert.InRange(major, 15, 17);
        string suffix = Guid.NewGuid().ToString("N"), name = $"SqlObserverCacheContent_{suffix}", login = $"sqlobserver_cache_content_{suffix}";
        bool createdDatabase = false, createdLogin = false;
        try
        {
            await Execute(admin, $"CREATE DATABASE [{name}];"); createdDatabase = true;
            await Execute(admin, $"ALTER DATABASE [{name}] SET AUTO_CLOSE OFF; ALTER DATABASE [{name}] SET QUERY_STORE=OFF;");
            await using var database = new SqlConnection(new SqlConnectionStringBuilder(settings.ConnectionString) { InitialCatalog = name }.ConnectionString);
            await database.OpenAsync(deadline.Token);
            await Execute(database, "CREATE TABLE dbo.content_probe_rows(value int NOT NULL); INSERT dbo.content_probe_rows SELECT TOP (1000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)) FROM sys.all_objects;");
            const string statement = "SELECT SUM(CONVERT(bigint,a.value)+b.value) /* inert '<script>marker</script>' */ FROM dbo.content_probe_rows a CROSS JOIN dbo.content_probe_rows b OPTION(MAXDOP 1)";
            // A surrogate pair before the statement exercises native UTF-16
            // offsets; the returned text must omit procedure and other statements.
            await Execute(database, $"CREATE PROCEDURE dbo.content_probe AS /* 🛰 offset marker */ {statement}; SELECT COUNT_BIG(*) FROM dbo.content_probe_rows;");
            for (int count = 0; count < 30; count++) await Execute(database, "EXEC dbo.content_probe;");
            int databaseId = Assert.IsType<int>(await Scalar(database, "SELECT CONVERT(int,DB_ID());"));
            string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA!1";
            await Execute(admin, $"CREATE LOGIN [{login}] WITH PASSWORD='{password}',CHECK_POLICY=OFF;"); createdLogin = true;
            await Execute(admin, $"GRANT {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} TO [{login}]; GRANT VIEW ANY DATABASE TO [{login}];");
            var target = new MonitoredInstanceId(Guid.NewGuid()); var revision = new ObservationTargetRevision(1);
            var bundle = PlanCacheContentSourceBundle.LoadEmbedded();
            using var protector = new Protector();
            PlanCacheContentSourceRead captured = await Restricted(protector);
            PlanCacheProtectedContent item = Assert.Single(captured.Content, row => row.Statement.Query.DatabaseId == databaseId
                && row.Text is not null && Decode(row.Text).StartsWith("SELECT SUM", StringComparison.Ordinal));
            Assert.Equal(statement, Decode(item.Text!).TrimEnd(';'));
            Assert.DoesNotContain("CREATE PROCEDURE", Decode(item.Text!), StringComparison.Ordinal);
            Assert.DoesNotContain("COUNT_BIG", Decode(item.Text!), StringComparison.Ordinal);
            Assert.Equal("protected", item.PlanState); Assert.NotNull(item.Plan);
            string xml = Decode(item.Plan!);
            using (var xmlReader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
            {
                xmlReader.MoveToContent(); Assert.Equal("ShowPlanXML", xmlReader.LocalName);
                var statements = new List<string>();
                while (xmlReader.Read())
                    if (xmlReader.NodeType == XmlNodeType.Element && xmlReader.LocalName == "StmtSimple")
                        statements.Add(xmlReader.GetAttribute("StatementText")!);
                Assert.Equal(statement, Assert.Single(statements).TrimEnd(';'));
            }
            Assert.Contains(captured.Counters.Rows, row => row.CacheEntryFingerprint == item.Statement.CacheEntryFingerprint);
            Assert.Equal(30L, item.Statement.Counters.Executions);
            Assert.InRange(captured.SourceContentBytes, 1, PlanCacheContentSourceReader.MaximumSourceContentBytes);

            PlanCacheContentSourceRead noKey = await Restricted(new UnavailableQuerySensitiveContentProtector());
            Assert.Empty(noKey.Content); Assert.Equal(0, noKey.SourceContentBytes); Assert.Equal(0, noKey.ProtectedContentBytes);
            Assert.Contains(noKey.Counters.Rows, row => row.CacheEntryFingerprint == item.Statement.CacheEntryFingerprint);

            await Execute(database, "EXEC sys.sp_recompile N'dbo.content_probe';");
            PlanCacheContentSourceRead absent = await Restricted(protector);
            Assert.DoesNotContain(absent.Counters.Rows, row => row.CacheEntryFingerprint == item.Statement.CacheEntryFingerprint);
            Assert.DoesNotContain(absent.Content, row => row.Statement.CacheEntryFingerprint == item.Statement.CacheEntryFingerprint);
            await Execute(admin, $"REVOKE {(major == 15 ? "VIEW SERVER STATE" : "VIEW SERVER PERFORMANCE STATE")} FROM [{login}];");
            SqlException denied = await Assert.ThrowsAsync<SqlException>(() => Restricted(protector));
            Assert.True(SqlServerCollectorErrorClassifier.IsPermissionDenied(denied.Number));

            string Decode(ProtectedSensitivePayload payload)
            {
                byte[] bytes = protector.Decrypt(target.Value, payload);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            async Task<PlanCacheContentSourceRead> Restricted(IQuerySensitiveContentProtector provider)
            {
                await Execute(admin, $"EXECUTE AS LOGIN='{login}';");
                try
                {
                    Assert.Equal(0, await Scalar(admin, "SELECT IS_SRVROLEMEMBER('sysadmin');"));
                    Assert.Equal(0, await Scalar(admin, "SELECT HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER');"));
                    return await bundle.ReadAsync(admin, target, revision, 2000, 8, provider, deadline.Token);
                }
                finally { await Execute(admin, "REVERT;"); }
            }
        }
        finally
        {
            if (createdDatabase) await Execute(admin, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            if (createdLogin) await Execute(admin, $"DROP LOGIN [{login}];");
        }

        async Task Execute(SqlConnection connection, string sql)
        { await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 }; await command.ExecuteNonQueryAsync(deadline.Token); }
        async Task<object?> Scalar(SqlConnection connection, string sql)
        { await using var command = new SqlCommand(sql, connection) { CommandTimeout = 20 }; return await command.ExecuteScalarAsync(deadline.Token); }
    }

    private sealed class Protector : IQuerySensitiveContentProtector, IDisposable
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public bool IsAvailable => true;
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[content.Length];
            using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, content.Span, cipher, tag, Aad(targetId, kind));
            return ValueTask.FromResult<ProtectedSensitivePayload?>(new(kind, new(HMACSHA256.HashData(key, content.Span)), "AES-256-GCM", "disposable-cache-content", nonce, tag, cipher));
        }
        public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Decrypt(targetId, payload));
        }
        internal byte[] Decrypt(Guid targetId, ProtectedSensitivePayload payload)
        {
            var bytes = new byte[payload.CiphertextLengthBytes];
            using var aes = new AesGcm(key, 16); aes.Decrypt(payload.GetNonce(), payload.GetCiphertext(), payload.GetAuthenticationTag(), bytes, Aad(targetId, payload.Kind));
            return bytes;
        }
        private static byte[] Aad(Guid targetId, SensitivePayloadKind kind) => Encoding.UTF8.GetBytes($"{targetId:D}:{kind}");
        public void Dispose() => CryptographicOperations.ZeroMemory(key);
    }
}
