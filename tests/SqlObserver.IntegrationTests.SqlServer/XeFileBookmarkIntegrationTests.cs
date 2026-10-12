using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;

namespace SqlObserver.IntegrationTests.SqlServer;

[Trait("Category", "RequiresSqlServer")]
public sealed class XeFileBookmarkIntegrationTests
{
    [Fact]
    public async Task NativeResumeSkipsEveryEventInTheBookmarkedBuffer()
    {
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync();
        string pattern;
        string epoch;
        await using (var source = new SqlCommand("""
            ;WITH target_limits AS
            (
                SELECT s.event_session_id,
                    MAX(CASE WHEN f.name=N'max_rollover_files' THEN TRY_CONVERT(bigint,CONVERT(nvarchar(64),f.value)) END) AS files,
                    MAX(CASE WHEN f.name=N'max_file_size' THEN TRY_CONVERT(bigint,CONVERT(nvarchar(64),f.value)) END) AS size_mb
                FROM sys.server_event_sessions s JOIN sys.server_event_session_targets t ON t.event_session_id=s.event_session_id
                LEFT JOIN sys.server_event_session_fields f ON f.event_session_id=s.event_session_id AND f.object_id=t.target_id
                WHERE s.name=N'system_health' AND t.name=N'event_file' GROUP BY s.event_session_id
            ), current_file AS
            (
                SELECT CAST(t.target_data AS xml).value('(/EventFileTarget/File/@name)[1]','nvarchar(260)') AS path,s.create_time
                FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address
                CROSS JOIN target_limits l WHERE s.name=N'system_health' AND t.target_name=N'event_file'
                AND l.files BETWEEN 1 AND 10 AND l.size_mb BETWEEN 1 AND 100
            ), trusted_directory AS
            (
                SELECT LEFT(path,LEN(path)-CHARINDEX(N'\',REVERSE(path))+1) AS directory,create_time
                FROM current_file WHERE CHARINDEX(N'\',REVERSE(path))>0
            )
            SELECT directory+N'system_health*.xel',LOWER(CONVERT(varchar(64),
                HASHBYTES('SHA2_256',directory+N'|'+CONVERT(nvarchar(33),create_time,126)),2))
            FROM trusted_directory;
            """, connection) { CommandTimeout = 20 })
        await using (SqlDataReader row = await source.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync(), "The lab needs an existing bounded system_health event-file target.");
            pattern = row.GetString(0);
            epoch = row.GetString(1);
            Assert.False(await row.ReadAsync());
        }

        string file;
        long offset;
        int events;
        await using (var buffer = new SqlCommand("""
            SELECT TOP(1) file_name,file_offset,COUNT(*) AS events
            FROM sys.fn_xe_file_target_read_file(@pattern,NULL,NULL,NULL)
            GROUP BY file_name,file_offset HAVING COUNT(*) BETWEEN 3 AND 8192
            ORDER BY file_name DESC,file_offset DESC;
            """, connection) { CommandTimeout = 20 })
        {
            buffer.Parameters.AddWithValue("pattern", pattern);
            await using SqlDataReader row = await buffer.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync(), "The lab needs one flushed buffer with at least three events.");
            file = row.GetString(0);
            offset = row.GetInt64(1);
            events = row.GetInt32(2);
        }
        // Native file names carry numeric rollover/generation tokens. Keep the
        // provider path only in this local SQL parameter; domain state has no path.
        string basename = Path.GetFileNameWithoutExtension(file);
        int generationSeparator = basename.LastIndexOf('_');
        int indexSeparator = basename.LastIndexOf('_', generationSeparator - 1);
        long generation = long.Parse(basename.AsSpan(generationSeparator + 1), NumberStyles.None, CultureInfo.InvariantCulture);
        int index = int.Parse(basename.AsSpan(indexSeparator + 1, generationSeparator - indexSeparator - 1), NumberStyles.None, CultureInfo.InvariantCulture);
        var position = new XeFilePosition(generation, index, offset);
        var identity = new XeReadIdentity(new(Guid.NewGuid()), new(1), epoch);
        XeFileReadProgress cutOff = XeFileReadProgressCalculator.Compare(null, identity, [position, position], false);
        Assert.Null(cutOff.SafeBookmark);
        Assert.Equal(XeFileReadProgressKind.CatchingUp, cutOff.Kind);
        XeFileReadProgress complete = XeFileReadProgressCalculator.Compare(null, identity,
            Enumerable.Repeat(position, events).ToArray(), true);
        Assert.Equal(position, complete.SafeBookmark?.Position);
        Assert.Equal(1, complete.CompleteBuffers);

        await using var resume = new SqlCommand("""
            SELECT COALESCE(SUM(CASE WHEN file_name=@file AND file_offset<=@offset THEN CONVERT(bigint,1) ELSE 0 END),0)
            FROM sys.fn_xe_file_target_read_file(@pattern,NULL,@file,@offset);
            """, connection) { CommandTimeout = 20 };
        resume.Parameters.AddWithValue("pattern", pattern);
        resume.Parameters.AddWithValue("file", file);
        resume.Parameters.AddWithValue("offset", offset);
        // Advancing after just two rows would lose every remaining event in the
        // same buffer. The actual TVF returns none of that buffer's events.
        Assert.Equal(0L, await resume.ExecuteScalarAsync());
    }
}
