using System.Data;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerConfigurationSourceTests
{
    [Fact]
    public void EachMajorUsesAPinnedBoundedSource()
    {
        for (int major = 15; major <= 17; major++)
        {
            string query = SqlServerConfigurationSource.LoadPinnedQuery(major);
            Assert.Contains("TOP (@maximum_rows)", query, StringComparison.Ordinal);
            Assert.Contains("sys.configurations", query, StringComparison.Ordinal);
            Assert.Contains("value_in_use", query, StringComparison.Ordinal);
            Assert.Contains("ORDER BY configuration.configuration_id", query,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PreservesLargeIntegerValuesAndConfiguredVersusRunningState()
    {
        using DataTable rows = Rows();
        Add(rows, 1, "max server memory (MB)", "bigint", "9223372036854775807", "8192");
        Add(rows, 2, "show advanced options", "bit", "1", "0");
        using DataTableReader reader = rows.CreateDataReader();

        SqlServerConfigurationRead result = await SqlServerConfigurationSource.ReadAsync(
            reader, CancellationToken.None);

        Assert.False(result.Incomplete);
        Assert.Equal(2, result.SourceRowsRead);
        Assert.Equal("9223372036854775807", result.Values[0].ConfiguredValue);
        Assert.Equal("8192", result.Values[0].InUseValue);
        Assert.Equal("0", result.Values[1].InUseValue);
    }

    [Fact]
    public async Task LookaheadAndByteBudgetWithholdTheEntireScan()
    {
        using DataTable tooMany = Rows();
        for (int id = 1; id <= SqlServerConfigurationSource.MaximumConfigurations + 1; id++)
            Add(tooMany, id, "option", "int", "0", "0");
        using DataTableReader rowReader = tooMany.CreateDataReader();
        SqlServerConfigurationRead rowCap = await SqlServerConfigurationSource.ReadAsync(
            rowReader, CancellationToken.None);
        Assert.True(rowCap.SourceRowLimitReached);
        Assert.Empty(rowCap.Values);

        using DataTable tooLarge = Rows();
        for (int id = 1; id <= 300; id++)
            Add(tooLarge, id, new string('x', 200), "int", "0", "0");
        using DataTableReader byteReader = tooLarge.CreateDataReader();
        SqlServerConfigurationRead byteCap = await SqlServerConfigurationSource.ReadAsync(
            byteReader, CancellationToken.None);
        Assert.True(byteCap.ResponseByteLimitReached);
        Assert.Empty(byteCap.Values);
    }

    [Fact]
    public async Task MalformedOrUnsupportedValuesFailClosed()
    {
        using DataTable mismatch = Rows();
        Add(mismatch, 1, "option", "int", "1", "2", "bigint");
        await AssertInvalidAsync(mismatch);

        using DataTable invalid = Rows();
        Add(invalid, 1, "option", "int", "1", "2");
        Add(invalid, 1, "duplicate", "int", "1", "2");
        await AssertInvalidAsync(invalid);

        using DataTable unsupported = Rows();
        Add(unsupported, 1, "option", "decimal", "1.1", "2.2");
        await AssertInvalidAsync(unsupported);

        using DataTable missing = Rows();
        missing.Rows.Add(DateTime.UtcNow, 1, "option", "int", "int",
            DBNull.Value, "2", true, false);
        await AssertInvalidAsync(missing);
    }

    private static async Task AssertInvalidAsync(DataTable table)
    {
        using DataTableReader reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SqlServerConfigurationSource.ReadAsync(reader, CancellationToken.None));
    }

    private static DataTable Rows()
    {
        var table = new DataTable();
        table.Columns.Add("observed_at_utc", typeof(DateTime));
        table.Columns.Add("configuration_id", typeof(int));
        table.Columns.Add("configuration_name", typeof(string));
        table.Columns.Add("configured_source_type", typeof(string));
        table.Columns.Add("in_use_source_type", typeof(string));
        table.Columns.Add("configured_value", typeof(string));
        table.Columns.Add("in_use_value", typeof(string));
        table.Columns.Add("is_dynamic", typeof(bool));
        table.Columns.Add("is_advanced", typeof(bool));
        return table;
    }

    private static void Add(DataTable table, int id, string name, string type,
        string configured, string inUse, string? inUseType = null) =>
        table.Rows.Add(new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
            id, name, type, inUseType ?? type, configured, inUse, true, false);
}
