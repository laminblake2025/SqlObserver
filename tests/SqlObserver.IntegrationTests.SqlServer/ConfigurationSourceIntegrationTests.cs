using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class ConfigurationSourceIntegrationTests
{
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
}
