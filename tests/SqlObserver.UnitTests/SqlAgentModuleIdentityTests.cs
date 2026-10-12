using System.Data;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentModuleIdentityTests
{
    [Fact]
    public async Task DefaultInstanceRetainsNullAndSourceUtcAndSession()
    {
        using var reader = Reader(Table());
        var value = await SqlAgentModuleSource.ParseIdentityAsync(reader, CancellationToken.None);
        Assert.Equal("SQLTEST", value.ServerName); Assert.Equal("SQLTEST", value.MachineName);
        Assert.Null(value.InstanceName); Assert.Equal(51, value.SessionId);
        Assert.Equal(DateTimeKind.Utc, value.ObservedAtUtc.Kind);
        Assert.NotEqual(Guid.Empty, value.ServerConnectionId);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task EveryActualIdentityTransportAndPrivilegeFlagIsRequired(int ordinal)
    {
        var table = Table(); table.Rows[0][ordinal] = 0;
        using var reader = Reader(table);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await SqlAgentModuleSource.ParseIdentityAsync(reader, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public async Task InvisibleIdentityIsDeniedRatherThanInterpretedAsSafe(int ordinal)
    {
        var table = Table(); table.Rows[0][ordinal] = DBNull.Value;
        using var reader = Reader(table);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await SqlAgentModuleSource.ParseIdentityAsync(reader, CancellationToken.None));
    }

    [Theory]
    [InlineData("empty-connection")] [InlineData("zero-session")] [InlineData("wrong-database-id")]
    [InlineData("wrong-database-name")] [InlineData("wrong-major")] [InlineData("unsupported-edition")]
    public async Task ServerAndOwnedSessionContextAreNotInferred(string change)
    {
        var table = Table();
        switch (change)
        {
            case "empty-connection": table.Rows[0][9] = Guid.Empty; break;
            case "zero-session": table.Rows[0][10] = 0; break;
            case "wrong-database-id": table.Rows[0][11] = 2; break;
            case "wrong-database-name": table.Rows[0][12] = "msdb"; break;
            case "wrong-major": table.Rows[0][16] = 16; break;
            case "unsupported-edition": table.Rows[0][17] = 4; break;
        }
        using var reader = Reader(table);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await SqlAgentModuleSource.ParseIdentityAsync(reader, CancellationToken.None));
    }

    [Theory]
    [InlineData("missing-row")] [InlineData("extra-row")] [InlineData("extra-set")]
    [InlineData("wrong-alias")] [InlineData("extra-column")] [InlineData("missing-property")]
    [InlineData("oversized-property")] [InlineData("control-property")] [InlineData("trimmed-property")]
    [InlineData("zero-time")]
    public async Task BoundedPropertySchemaAndCardinalityFailClosed(string change)
    {
        var table = Table(); var tables = new[] { table };
        switch (change)
        {
            case "missing-row": table.Rows.Clear(); break;
            case "extra-row": table.Rows.Add(table.Rows[0].ItemArray); break;
            case "extra-set": tables = [table, table.Copy()]; break;
            case "wrong-alias": table.Columns[13].ColumnName = "ServerName"; break;
            case "extra-column": table.Columns.Add("extra", typeof(int)); break;
            case "missing-property": table.Rows[0][13] = DBNull.Value; break;
            case "oversized-property": table.Rows[0][13] = new string('a', 129); break;
            case "control-property": table.Rows[0][14] = "private\nvalue"; break;
            case "trimmed-property": table.Rows[0][14] = " SQLTEST"; break;
            case "zero-time": table.Rows[0][0] = DateTime.MinValue; break;
        }
        using var reader = new SqlAgentModuleMetadataProbeTests.StringFixtureReader(tables);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await SqlAgentModuleSource.ParseIdentityAsync(reader, CancellationToken.None));
        Assert.DoesNotContain("private", error.Message.Replace("private values withheld", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void FixedIdentityAssetAttestsSidAndPropertiesWithoutHardwareClaims()
    {
        string sql = SqlAgentModuleSource.LoadIdentitySql();
        Assert.Contains("SUSER_SID()=@expected_sid", sql, StringComparison.Ordinal);
        Assert.Contains("SUSER_SID(ORIGINAL_LOGIN())=@expected_sid", sql, StringComparison.Ordinal);
        Assert.Contains("SERVERPROPERTY(N'ServerName')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("database_guid", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static SqlAgentModuleMetadataProbeTests.StringFixtureReader Reader(DataTable table) => new([table]);
    private static DataTable Table()
    {
        var table = new DataTable("identity") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        (string Name, Type Type)[] shape =
        [
            ("observed_utc",typeof(DateTime)),("current_sid_matches",typeof(int)),("original_sid_matches",typeof(int)),
            ("non_sysadmin",typeof(int)),("integrated_auth",typeof(int)),("physical_tcp",typeof(int)),("encrypted",typeof(int)),
            ("no_control_server",typeof(int)),("no_alter_login",typeof(int)),("connection_id",typeof(Guid)),("session_id",typeof(int)),
            ("database_id",typeof(int)),("database_name",typeof(string)),("server_name",typeof(string)),("machine_name",typeof(string)),
            ("instance_name",typeof(string)),("product_major_version",typeof(int)),("engine_edition",typeof(int))
        ];
        foreach (var column in shape) table.Columns.Add(column.Name, column.Type);
        table.Rows.Add(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Unspecified), 1, 1, 1, 1, 1, 1, 1, 1,
            Guid.NewGuid(), 51, 1, "master", "SQLTEST", "SQLTEST", DBNull.Value, 17, 3);
        return table;
    }
}
