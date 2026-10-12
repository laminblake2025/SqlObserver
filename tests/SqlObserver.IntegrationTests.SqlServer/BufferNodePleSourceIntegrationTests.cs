using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class BufferNodePleSourceIntegrationTests
{
    [Fact]
    [Trait("Category", "RequiresSqlServer")]
    public async Task LocalSqlServerReturnsBoundedIndividualBufferNodes()
    {
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            SqlServerBufferNodePleSource.LoadPinnedQuery(), connection)
        {
            CommandTimeout = 5,
        };
        command.Parameters.Add("@maximum_rows", SqlDbType.Int).Value = 129;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SequentialAccess | CommandBehavior.SingleResult);

        BufferNodePleRead result = await SqlServerBufferNodePleSource.ReadAsync(
            reader, 128, 65_536, CancellationToken.None);

        Assert.False(result.Incomplete);
        Assert.NotEmpty(result.Nodes);
        Assert.Equal(result.Nodes.Count, result.SourceRowsRead);
        Assert.Equal(TimeSpan.Zero, result.ObservedAtUtc.Offset);
        Assert.All(result.Nodes, node =>
        {
            Assert.InRange(node.NodeId, 0, 1023);
            Assert.True(node.PageLifeExpectancySeconds >= 0);
        });
    }
}
