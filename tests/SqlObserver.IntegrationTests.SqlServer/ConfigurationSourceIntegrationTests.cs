using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class ConfigurationSourceIntegrationTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    [Trait("Category", "RequiresSqlServerDisposable")]
    public async Task LocalSqlServer2022SourceIsVisibleWithoutPerformanceStateGrant()
    {
        Assert.NotEqual("Release", Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"));
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync();
        await using (var version = new SqlCommand(
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')), IS_SRVROLEMEMBER(N'sysadmin');",
            connection))
        await using (SqlDataReader reader = await version.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(16, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
        }
        SqlServerConfigurationRead privileged = await ReadSourceAsync(connection, 16);
        string login = "sqlobserver_config_probe_" + Guid.NewGuid().ToString("N");
        string password = "Aa9!" + Guid.NewGuid().ToString("N");
        async Task ExecuteAsync(string statement)
        {
            await using var command = new SqlCommand(statement, connection) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync();
        }
        async Task<SqlServerConfigurationRead> ReadAsLoginAsync(bool hasGrant)
        {
            await ExecuteAsync($"EXECUTE AS LOGIN = N'{login}';");
            try
            {
                await using var permission = new SqlCommand(
                    "SELECT IS_SRVROLEMEMBER(N'sysadmin'), " +
                    "HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW SERVER PERFORMANCE STATE');",
                    connection);
                await using SqlDataReader permissionReader = await permission.ExecuteReaderAsync();
                Assert.True(await permissionReader.ReadAsync());
                Assert.Equal(0, permissionReader.GetInt32(0));
                Assert.Equal(hasGrant ? 1 : 0, permissionReader.GetInt32(1));
                await permissionReader.CloseAsync();
                return await ReadSourceAsync(connection, 16);
            }
            finally { await ExecuteAsync("REVERT;"); }
        }

        try
        {
            await ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = ON;");
            SqlServerConfigurationRead withoutGrant = await ReadAsLoginAsync(false);
            await ExecuteAsync($"GRANT VIEW SERVER PERFORMANCE STATE TO [{login}];");
            SqlServerConfigurationRead withGrant = await ReadAsLoginAsync(true);
            Assert.False(withoutGrant.Incomplete);
            Assert.Equal(privileged.Values.ToArray(), withoutGrant.Values.ToArray());
            Assert.Equal(privileged.Values.ToArray(), withGrant.Values.ToArray());
        }
        finally
        {
            await ExecuteAsync($"IF EXISTS (SELECT 1 FROM sys.server_principals " +
                $"WHERE name=N'{login}') DROP LOGIN [{login}];");
        }
    }

    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task LabServerReturnsCompleteExactConfigurationValues()
    {
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync();
        await using var version = new SqlCommand(
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));", connection);
        int major = (int)(await version.ExecuteScalarAsync()
            ?? throw new InvalidDataException("The SQL Server version is unavailable."));
        Assert.InRange(major, 15, 17);
        await using var command = new SqlCommand(
            SqlServerConfigurationSource.LoadPinnedQuery(major), connection)
        {
            CommandTimeout = 5,
        };
        command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value =
            SqlServerConfigurationSource.MaximumConfigurations + 1;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult);

        SqlServerConfigurationRead result = await SqlServerConfigurationSource.ReadAsync(
            reader, CancellationToken.None);

        Assert.False(result.Incomplete);
        Assert.NotEmpty(result.Values);
        Assert.Equal(result.Values.Count, result.SourceRowsRead);
        Assert.Equal(TimeSpan.Zero, result.ObservedAtUtc.Offset);
        Assert.Contains(result.Values, value =>
            value.Name == "max server memory (MB)" &&
            !string.IsNullOrEmpty(value.ConfiguredValue) &&
            !string.IsNullOrEmpty(value.InUseValue));
    }

    private static async Task<SqlServerConfigurationRead> ReadSourceAsync(
        SqlConnection connection, int major)
    {
        await using var command = new SqlCommand(
            SqlServerConfigurationSource.LoadPinnedQuery(major), connection)
        {
            CommandTimeout = 5,
        };
        command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value =
            SqlServerConfigurationSource.MaximumConfigurations + 1;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult);
        return await SqlServerConfigurationSource.ReadAsync(reader, CancellationToken.None);
    }
}
