using System.Data;
using Microsoft.Data.SqlClient;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class BackupSqlProjectionRegressionTests
{
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [Trait("Category", "RequiresSqlServer")]
    public async Task NativeBackupProjectionSelectsRegularFullAndConvertsSourceOffset(int major)
    {
        // Exercise the embedded query against a fixed VALUES source. No target
        // backup history or objects are read or changed by this fixture.
        var settings = new SqlConnectionStringBuilder(SqlServerLabContract.ConnectionString);
        Assert.NotEqual(SqlConnectionEncryptOption.Optional, settings.Encrypt);
        Assert.False(settings.TrustServerCertificate);
        settings.ConnectTimeout = 5;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        await using var clock = new SqlCommand("SELECT GETDATE();", connection) { CommandTimeout = 5 };
        DateTime now = Assert.IsType<DateTime>(await clock.ExecuteScalarAsync(deadline.Token));
        const string fixture = """
            (VALUES
              (N'regular',CONVERT(char(1),'D'),DATEADD(hour,-3,@now),1,CONVERT(numeric(20,0),100),CONVERT(bit,0),CONVERT(bit,1),CONVERT(bit,0),CONVERT(smallint,-20)),
              (N'regular','D',DATEADD(hour,-1,@now),2,200,1,1,0,-20),
              (N'regular','I',DATEADD(hour,-2,@now),3,100,0,1,0,22),
              (N'regular','L',DATEADD(hour,-2,@now),4,100,1,1,0,0),
              (N'east','D',DATEADD(hour,-2,@now),5,100,0,1,0,48),
              (N'west','D',DATEADD(hour,-2,@now),6,100,0,1,0,-48),
              (N'unknown','D',DATEADD(hour,-2,@now),7,100,0,1,0,127),
              (N'null','D',DATEADD(hour,-2,@now),8,100,0,1,0,NULL),
              (N'invalid-east','D',DATEADD(hour,-2,@now),9,100,0,1,0,49),
              (N'invalid-west','D',DATEADD(hour,-2,@now),10,100,0,1,0,-49),
              (N'old','D',DATEADD(day,-36,@now),11,100,0,1,0,0),
              (N'only-copy','D',DATEADD(hour,-2,@now),12,100,1,1,0,0),
              (N'regular','D',DATEADD(hour,-3,@now),13,100,0,1,0,-20)
            ) AS b(database_name,type,backup_finish_date,backup_set_id,backup_size,is_copy_only,has_backup_checksums,is_damaged,time_zone)
            """;
        string query = SqlServerOperationalHealthAssetCatalog.LoadEmbedded().Get($"backups.status.sqlserver{major}-windows.v1.sql");
        const string source = "msdb.dbo.backupset AS b";
        Assert.Contains(source, query, StringComparison.Ordinal);
        query = query.Replace(source, fixture, StringComparison.Ordinal);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 5 };
        command.Parameters.Add("now", SqlDbType.DateTime).Value = now;
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = 100;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token);
        var actual = new Dictionary<long, (int Kind, short? Offset)>();
        while (await reader.ReadAsync(deadline.Token))
            actual.Add(reader.GetInt64(7), (reader.GetInt32(1), reader.IsDBNull(8) ? null : reader.GetInt16(8)));
        Assert.Equal(new long[] { 3, 4, 5, 6, 7, 8, 9, 10, 13 }, actual.Keys.Order());
        Assert.Equal((1, (short?)-300), actual[13]);
        Assert.Equal((2, (short?)330), actual[3]);
        Assert.Equal((3, (short?)0), actual[4]);
        Assert.Equal((1, (short?)720), actual[5]);
        Assert.Equal((1, (short?)-720), actual[6]);
        foreach (long id in new long[] { 7, 8, 9, 10 }) Assert.Null(actual[id].Offset);
    }
}
