using System.Data;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerBufferNodePleSourceTests
{
    [Fact]
    public void PinnedQueryReadsOnlyOrderedBufferNodePle()
    {
        string sql = SqlServerBufferNodePleSource.LoadPinnedQuery();
        Assert.StartsWith("SET NOCOUNT ON;\n", sql, StringComparison.Ordinal);
        Assert.Contains("TOP (@maximum_rows)", sql, StringComparison.Ordinal);
        Assert.Contains("sys.dm_os_performance_counters", sql, StringComparison.Ordinal);
        Assert.Contains("N'%:Buffer Node'", sql, StringComparison.Ordinal);
        Assert.Contains("N'Page life expectancy'", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY node_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinctNodesKeepOwnValuesAndOneObservationTime()
    {
        using DataTable table = Rows();
        Add(table, 0, 800);
        Add(table, 1, 175);
        using DataTableReader reader = table.CreateDataReader();

        BufferNodePleRead read = await SqlServerBufferNodePleSource.ReadAsync(
            reader, 128, 65_536, CancellationToken.None);

        Assert.False(read.Incomplete);
        Assert.Equal(2, read.SourceRowsRead);
        Assert.Equal(128, read.ResponseBytes);
        Assert.Equal(0, read.ObservedAtUtc.Offset.TotalMinutes);
        Assert.Equal([new BufferNodePle(0, 800), new BufferNodePle(1, 175)], read.Nodes);
    }

    [Fact]
    public async Task LookaheadAndByteCapWithholdPartialNodeSets()
    {
        using DataTable table = Rows();
        Add(table, 0, 800);
        Add(table, 1, 175);
        using DataTableReader rowReader = table.CreateDataReader();
        using DataTableReader byteReader = table.CreateDataReader();

        BufferNodePleRead rowCap = await SqlServerBufferNodePleSource.ReadAsync(
            rowReader, 1, 65_536, CancellationToken.None);
        BufferNodePleRead byteCap = await SqlServerBufferNodePleSource.ReadAsync(
            byteReader, 128, 64, CancellationToken.None);

        Assert.True(rowCap.Incomplete);
        Assert.True(byteCap.Incomplete);
        Assert.Empty(rowCap.Nodes);
        Assert.Empty(byteCap.Nodes);
        Assert.Equal(2, rowCap.SourceRowsRead);
        Assert.Equal(2, byteCap.SourceRowsRead);
    }

    [Fact]
    public async Task DuplicateNodeOrNegativeCounterFailsClosed()
    {
        using DataTable duplicate = Rows();
        Add(duplicate, 0, 800);
        Add(duplicate, 0, 175);
        using DataTableReader duplicateReader = duplicate.CreateDataReader();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SqlServerBufferNodePleSource.ReadAsync(duplicateReader, 128, 65_536,
                CancellationToken.None));

        using DataTable negative = Rows();
        Add(negative, 0, -1);
        using DataTableReader negativeReader = negative.CreateDataReader();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SqlServerBufferNodePleSource.ReadAsync(negativeReader, 128, 65_536,
                CancellationToken.None));

        using DataTable unknown = Rows();
        unknown.Rows.Add(new DateTime(2026, 9, 28, 12, 0, 0), DBNull.Value, 10L);
        using DataTableReader unknownReader = unknown.CreateDataReader();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SqlServerBufferNodePleSource.ReadAsync(unknownReader, 128, 65_536,
                CancellationToken.None));
    }

    private static DataTable Rows()
    {
        var table = new DataTable();
        table.Columns.Add("observed_at_utc", typeof(DateTime));
        table.Columns.Add("node_id", typeof(int));
        table.Columns.Add("page_life_expectancy_seconds", typeof(long));
        return table;
    }

    private static void Add(DataTable table, int node, long seconds) =>
        table.Rows.Add(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Unspecified), node, seconds);
}
