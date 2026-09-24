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
        const string identities = """
            DECLARE @regular uniqueidentifier='00000000-0000-0000-0000-000000000001';
            DECLARE @east uniqueidentifier='00000000-0000-0000-0000-000000000002';
            DECLARE @west uniqueidentifier='00000000-0000-0000-0000-000000000003';
            DECLARE @unknown uniqueidentifier='00000000-0000-0000-0000-000000000004';
            DECLARE @null uniqueidentifier='00000000-0000-0000-0000-000000000005';
            DECLARE @invalid_east uniqueidentifier='00000000-0000-0000-0000-000000000006';
            DECLARE @invalid_west uniqueidentifier='00000000-0000-0000-0000-000000000007';
            DECLARE @renamed uniqueidentifier='00000000-0000-0000-0000-000000000008';
            DECLARE @recreated uniqueidentifier='00000000-0000-0000-0000-000000000009';
            DECLARE @stale uniqueidentifier='00000000-0000-0000-0000-000000000010';
            DECLARE @never uniqueidentifier='00000000-0000-0000-0000-000000000011';
            DECLARE @old uniqueidentifier='00000000-0000-0000-0000-000000000012';
            DECLARE @copy uniqueidentifier='00000000-0000-0000-0000-000000000013';
            DECLARE @temp uniqueidentifier='00000000-0000-0000-0000-000000000014';
            """;
        const string databaseFixture = """
            (VALUES (2,N'tempdb'),(5,N'regular'),(6,N'east'),(7,N'west'),(8,N'unknown'),
                    (9,N'null'),(10,N'invalid-east'),(11,N'invalid-west'),(12,N'new-name'),
                    (13,N'recreated'),(14,N'never'),(15,N'offline'),(16,N'old'),(17,N'only-copy')) AS d(database_id,name)
            """;
        const string recoveryFixture = """
            (VALUES (2,@temp),(5,@regular),(6,@east),(7,@west),(8,@unknown),(9,@null),
                    (10,@invalid_east),(11,@invalid_west),(12,@renamed),(13,@recreated),
                    (14,@never),(16,@old),(17,@copy)) AS rs(database_id,database_guid)
            """;
        const string backupFixture = """
            (VALUES
              (N'regular',@regular,CONVERT(char(1),'D'),DATEADD(hour,-3,@now),1,CONVERT(numeric(20,0),100),CONVERT(bit,0),CONVERT(bit,1),CONVERT(bit,0),CONVERT(smallint,-20)),
              (N'regular',@regular,'D',DATEADD(hour,-1,@now),2,200,1,1,0,-20),
              (N'regular',@regular,'I',DATEADD(hour,-2,@now),3,100,0,1,0,22),
              (N'regular',@regular,'L',DATEADD(hour,-2,@now),4,100,1,1,0,0),
              (N'east',@east,'D',DATEADD(hour,-2,@now),5,100,0,1,0,48),
              (N'west',@west,'D',DATEADD(hour,-2,@now),6,100,0,1,0,-48),
              (N'unknown',@unknown,'D',DATEADD(hour,-2,@now),7,100,0,1,0,127),
              (N'null',@null,'D',DATEADD(hour,-2,@now),8,100,0,1,0,NULL),
              (N'invalid-east',@invalid_east,'D',DATEADD(hour,-2,@now),9,100,0,1,0,49),
              (N'invalid-west',@invalid_west,'D',DATEADD(hour,-2,@now),10,100,0,1,0,-49),
              (N'old',@old,'D',DATEADD(day,-36,@now),11,100,0,1,0,0),
              (N'only-copy',@copy,'D',DATEADD(hour,-2,@now),12,100,1,1,0,0),
              (N'regular',@regular,'D',DATEADD(hour,-3,@now),13,100,0,1,0,-20),
              (N'old-name',@renamed,'D',DATEADD(hour,-2,@now),14,100,0,1,0,0),
              (N'recreated',@stale,'D',DATEADD(hour,-2,@now),15,100,0,1,0,0),
              (N'tempdb',@temp,'D',DATEADD(hour,-2,@now),16,100,0,1,0,0)
            ) AS b(database_name,database_guid,type,backup_finish_date,backup_set_id,backup_size,is_copy_only,has_backup_checksums,is_damaged,time_zone)
            """;
        string query = SqlServerOperationalHealthAssetCatalog.LoadEmbedded().Get($"backups.status.sqlserver{major}-windows.v1.sql");
        Assert.Contains("sys.databases AS d", query, StringComparison.Ordinal);
        Assert.Contains("sys.database_recovery_status AS rs", query, StringComparison.Ordinal);
        Assert.Contains("msdb.dbo.backupset AS b", query, StringComparison.Ordinal);
        query = identities + query.Replace("sys.databases AS d", databaseFixture, StringComparison.Ordinal)
            .Replace("sys.database_recovery_status AS rs", recoveryFixture, StringComparison.Ordinal)
            .Replace("msdb.dbo.backupset AS b", backupFixture, StringComparison.Ordinal);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 5 };
        command.Parameters.Add("now", SqlDbType.DateTime).Value = now;
        command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = 100;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(deadline.Token);
        var actual = new Dictionary<long, (int Kind, short? Offset)>();
        int missingFull = 0, unknownIdentity = 0;
        while (await reader.ReadAsync(deadline.Token))
        {
            if (reader.IsDBNull(7))
            {
                Assert.Equal(1, reader.GetInt32(1));
                Assert.True(reader.IsDBNull(2));
                missingFull++;
                if (reader.GetBoolean(9)) unknownIdentity++;
            }
            else actual.Add(reader.GetInt64(7), (reader.GetInt32(1), reader.IsDBNull(8) ? null : reader.GetInt16(8)));
        }
        Assert.Equal(new long[] { 3, 4, 5, 6, 7, 8, 9, 10, 13, 14 }, actual.Keys.Order());
        Assert.Equal(5, missingFull); // Recreated, never backed up, offline, old history, copy-only full.
        Assert.Equal(1, unknownIdentity); // Offline database has no available recovery GUID.
        Assert.Equal((1, (short?)-300), actual[13]);
        Assert.Equal((2, (short?)330), actual[3]);
        Assert.Equal((3, (short?)0), actual[4]);
        Assert.Equal((1, (short?)720), actual[5]);
        Assert.Equal((1, (short?)-720), actual[6]);
        foreach (long id in new long[] { 7, 8, 9, 10 }) Assert.Null(actual[id].Offset);
    }
}
