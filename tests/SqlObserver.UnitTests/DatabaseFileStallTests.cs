using System.Data;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class DatabaseFileStallTests
{
    [Theory]
    [InlineData(7L, 19L)]
    [InlineData(0L, 0L)]
    public void CollectorMapsSeparateReadAndWriteStallsFromActualRowOrdinals(long readStall, long writeStall)
    {
        using DataTable table = FileRow(readStall, writeStall);
        using DataTableReader reader = table.CreateDataReader();
        Assert.True(reader.Read());

        DatabaseFileObservation observation = SqlServerDatabaseFilesCollector.MapRow(
            reader, M4TestData.TargetId, M4TestData.TargetRevision, out int rowBytes);

        Assert.Equal(readStall, observation.ReadStallMilliseconds);
        Assert.Equal(writeStall, observation.WriteStallMilliseconds);
        Assert.Equal(readStall + writeStall, observation.IoStallMilliseconds);
        Assert.Equal(13, observation.ReadCount);
        Assert.Equal(29, observation.WriteCount);
        Assert.Equal(DatabaseFileObservation.FixedEstimatedBytes + observation.LogicalName.Utf8Bytes + 16, observation.EstimatedSizeBytes);
        Assert.Equal(160 + observation.LogicalName.Utf8Bytes + "ROWS"u8.Length + "ONLINE"u8.Length, rowBytes);
    }

    [Fact]
    public void LegacyObservationKeepsBothStallComponentsUnknownAndPreservesAccounting()
    {
        DatabaseFileObservation observation = Observation(26);

        Assert.Equal(26, observation.IoStallMilliseconds);
        Assert.Null(observation.ReadStallMilliseconds);
        Assert.Null(observation.WriteStallMilliseconds);
        Assert.Equal(DatabaseFileObservation.FixedEstimatedBytes + observation.LogicalName.Utf8Bytes, observation.EstimatedSizeBytes);
    }

    [Theory]
    [InlineData(-1L, 6L)]
    [InlineData(6L, -1L)]
    public void NegativeStallComponentIsRejectedEvenWhenSumMatches(long readStall, long writeStall)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Observation(5, readStall, writeStall));
    }

    [Theory]
    [InlineData(null, 5L)]
    [InlineData(5L, null)]
    public void IncompleteStallSplitIsRejected(long? readStall, long? writeStall)
    {
        Assert.Throws<ArgumentException>(() => Observation(5, readStall, writeStall));
    }

    [Fact]
    public void OverflowingStallSplitIsRejected()
    {
        Assert.Throws<OverflowException>(() => Observation(long.MaxValue, long.MaxValue, 1));
    }

    [Fact]
    public void StallSplitMustEqualExistingTotal()
    {
        Assert.Throws<ArgumentException>(() => Observation(5, 2, 4));
    }

    private static DatabaseFileObservation Observation(long total, long? readStall = null, long? writeStall = null) => new(
        M4TestData.TargetId, M4TestData.TargetRevision, 5, 1, new SqlServerObjectName("warehouse_data"),
        DatabaseFileType.Rows, DatabaseFileState.Online, 1_048_576, null, 65_536, 0,
        13, 29, 8192, 16384, total, M4TestData.RepositoryTime, readStall, writeStall);

    private static DataTable FileRow(long readStall, long writeStall)
    {
        var table = new DataTable();
        table.Columns.Add("observed_at_utc", typeof(DateTime));
        table.Columns.Add("database_id", typeof(int));
        table.Columns.Add("file_id", typeof(int));
        table.Columns.Add("logical_name", typeof(string));
        table.Columns.Add("type", typeof(string));
        table.Columns.Add("state", typeof(string));
        table.Columns.Add("size_bytes", typeof(decimal));
        table.Columns.Add("maximum_size_bytes", typeof(decimal));
        table.Columns.Add("growth", typeof(int));
        table.Columns.Add("percent_growth", typeof(bool));
        table.Columns.Add("read_count", typeof(long));
        table.Columns.Add("write_count", typeof(long));
        table.Columns.Add("bytes_read", typeof(long));
        table.Columns.Add("bytes_written", typeof(long));
        table.Columns.Add("read_stall_ms", typeof(long));
        table.Columns.Add("write_stall_ms", typeof(long));
        table.Rows.Add(M4TestData.RepositoryTime.UtcDateTime, 5, 1, "warehouse_data", "ROWS", "ONLINE",
            1_048_576m, DBNull.Value, 8, false, 13L, 29L, 8192L, 16384L, readStall, writeStall);
        return table;
    }
}
