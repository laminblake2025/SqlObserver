using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed class SqlAgentModuleSource : ISqlAgentModuleSource
{
    internal const string IdentitySha256 = "79758491916909181f6731268396e2acbf25e98dd68de4d62104a97339dcbe13";
    private const string Invalid = "Agent module connection identity is invalid; private values withheld.";
    private static readonly (string Name, Type Type)[] IdentityShape =
    [
        ("observed_utc",typeof(DateTime)),("current_sid_matches",typeof(int)),("original_sid_matches",typeof(int)),
        ("non_sysadmin",typeof(int)),("integrated_auth",typeof(int)),("physical_tcp",typeof(int)),("encrypted",typeof(int)),
        ("no_control_server",typeof(int)),("no_alter_login",typeof(int)),("connection_id",typeof(Guid)),("session_id",typeof(int)),
        ("database_id",typeof(int)),("database_name",typeof(string)),("server_name",typeof(string)),("machine_name",typeof(string)),
        ("instance_name",typeof(string)),("product_major_version",typeof(int)),("engine_edition",typeof(int))
    ];

    public async ValueTask<ISqlAgentModuleSession> OpenAsync(CollectorExecutionRequest request, TimeSpan budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SqlConnection connection = SqlServerIntegratedConnectionFactory.CreateConnection(request.ConnectionPolicy,
            SqlServerIntegratedConnectionFactory.CollectionApplicationName);
        try
        {
            // This path owns a single physical session; never transparently reconnect or return it to a pool.
            var settings = new SqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false, ConnectRetryCount = 0 };
            settings.ConnectTimeout = Math.Min(settings.ConnectTimeout, Seconds(budget));
            connection.ConnectionString = settings.ConnectionString;
            await connection.OpenAsync(token).ConfigureAwait(false);
            return new Session(connection);
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static string LoadIdentitySql()
    {
        const string resource = "SqlObserver.Infrastructure.SqlServer.AgentModuleAssets.sql-agent.activity.module-identity.sqlserver17-windows.v1.sql";
        using Stream stream = typeof(SqlAgentModuleSource).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException(Invalid);
        if (stream.Length is <= 0 or > 8192) throw new InvalidDataException(Invalid);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        string sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql))) != IdentitySha256) throw new InvalidDataException(Invalid);
        return sql;
    }

    internal static async ValueTask<SqlAgentModuleIdentity> ParseIdentityAsync(DbDataReader reader, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (reader.FieldCount != IdentityShape.Length) throw new InvalidDataException(Invalid);
        for (int index = 0; index < IdentityShape.Length; index++)
            if (reader.GetName(index) != IdentityShape[index].Name || reader.GetFieldType(index) != IdentityShape[index].Type)
                throw new InvalidDataException(Invalid);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.IsDBNull(0)) throw new InvalidDataException(Invalid);
        DateTime observed = reader.GetDateTime(0);
        if (observed == DateTime.MinValue || observed.Kind == DateTimeKind.Local) throw new InvalidDataException(Invalid);
        for (int index = 1; index <= 8; index++)
            if (reader.IsDBNull(index) || reader.GetInt32(index) != 1) throw new UnauthorizedAccessException(Invalid);
        if (reader.IsDBNull(9) || reader.IsDBNull(10) || reader.IsDBNull(11)) throw new InvalidDataException(Invalid);
        Guid id = reader.GetGuid(9); int session = reader.GetInt32(10), database = reader.GetInt32(11);
        string databaseName = Name(reader, 12)!;
        string server = Name(reader, 13)!, machine = Name(reader, 14)!;
        string? instance = Name(reader, 15, nullable: true);
        if (reader.IsDBNull(16) || reader.IsDBNull(17) || reader.GetInt32(16) != 17 || reader.GetInt32(17) is not (2 or 3) ||
            id == Guid.Empty || session is <= 0 or > 32767 || database != 1 || databaseName != "master") throw new UnauthorizedAccessException(Invalid);
        if (await reader.ReadAsync(token).ConfigureAwait(false) || await reader.NextResultAsync(token).ConfigureAwait(false)) throw new InvalidDataException(Invalid);
        token.ThrowIfCancellationRequested();
        return new(id, session, server, machine, instance, DateTime.SpecifyKind(observed, DateTimeKind.Utc));
    }

    private static string? Name(DbDataReader reader, int index, bool nullable = false)
    {
        if (reader.IsDBNull(index)) return nullable ? null : throw new InvalidDataException(Invalid);
        long length = reader.GetChars(index, 0, null, 0, 0);
        if (length is <= 0 or > 128) throw new InvalidDataException(Invalid);
        string value = reader.GetString(index);
        if (value.Length != length || value.Trim() != value || value.Any(char.IsControl)) throw new InvalidDataException(Invalid);
        return value;
    }
    private static int Seconds(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero || budget > SqlServerAgentActivityModuleCaptureReader.MaximumBudget)
            throw new TimeoutException("The original Agent module deadline expired.");
        return Math.Max(1, (int)Math.Ceiling(budget.TotalSeconds));
    }

    private sealed class Session(SqlConnection connection) : ISqlAgentModuleSession
    {
        private readonly Guid connectionId = connection.ClientConnectionId;
        private readonly int serverProcessId = connection.ServerProcessId;
        public Guid ClientConnectionId => connectionId;
        public int ServerProcessId => serverProcessId;
        public void RequireConnection()
        {
            if (connectionId == Guid.Empty || serverProcessId is <= 0 or > 32767 || connection.State != ConnectionState.Open ||
                connection.ClientConnectionId != connectionId || connection.ServerProcessId != serverProcessId || connection.Database != "master")
                throw new InvalidDataException(Invalid);
        }
        public async ValueTask<SqlAgentModuleIdentity> AttestAsync(ReadOnlyMemory<byte> expectedSid, TimeSpan budget, CancellationToken token)
        {
            RequireConnection();
            byte[] sid = expectedSid.ToArray();
            try
            {
                await using var command = new SqlCommand(LoadIdentitySql(), connection) { CommandTimeout = Seconds(budget) };
                command.Parameters.Add("@expected_sid", SqlDbType.VarBinary, 68).Value = sid;
                SqlAgentModuleIdentity result;
                await using (SqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    result = await ParseIdentityAsync(reader, token).ConfigureAwait(false);
                RequireConnection();
                // The server's connection GUID and the driver's client GUID are different namespaces.
                // Bind the DMV row to this driver's SPID; the reader also requires both GUIDs to remain stable.
                if (result.SessionId != serverProcessId) throw new UnauthorizedAccessException(Invalid);
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(sid); }
        }
        public async ValueTask<SqlAgentModuleMetadataReceipt> MetadataAsync(SqlAgentModuleMetadataPins pins, TimeSpan budget, CancellationToken token)
        {
            RequireConnection();
            var result = await SqlAgentModuleMetadataProbe.ReadAsync(connection, pins, SqlAgentModuleMetadataProbe.LoadSql(), budget, token).ConfigureAwait(false);
            RequireConnection(); return result;
        }
        public async ValueTask<SqlAgentActivityCapture> CaptureAsync(CollectorExecutionRequest request, TimeSpan budget, CancellationToken token)
        {
            RequireConnection();
            await using var command = new SqlCommand("EXEC master.sqlobserver_agent.capture_v1 @maximum_rows=@maximum_rows;", connection) { CommandTimeout = Seconds(budget) };
            command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value = 513;
            SqlAgentActivityCapture result;
            await using (SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false))
                result = await SqlServerAgentActivityCaptureReader.ParseAsync(request, new CaptureReader(reader), token).ConfigureAwait(false);
            RequireConnection(); return result;
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class CaptureReader(SqlDataReader reader) : ISqlAgentActivityResultReader
    {
        public int FieldCount => reader.FieldCount;
        public string GetName(int ordinal) => reader.GetName(ordinal);
        public Type GetFieldType(int ordinal) => reader.GetFieldType(ordinal);
        public bool IsDBNull(int ordinal) => reader.IsDBNull(ordinal);
        public int GetInt32(int ordinal) => reader.GetInt32(ordinal);
        public Guid GetGuid(int ordinal) => reader.GetGuid(ordinal);
        public DateTime GetDateTime(int ordinal) => reader.GetDateTime(ordinal);
        public DateTimeOffset GetDateTimeOffset(int ordinal) => reader.GetDateTimeOffset(ordinal);
        public ValueTask<bool> ReadAsync(CancellationToken cancellationToken) => new(reader.ReadAsync(cancellationToken));
        public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken) => new(reader.NextResultAsync(cancellationToken));
    }
}
