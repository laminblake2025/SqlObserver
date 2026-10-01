SET NOCOUNT ON;
-- Resume inputs are numeric tokens. Only the trusted target supplies paths.
IF @maximum_metadata_rows NOT BETWEEN 1 AND 8192 OR @maximum_deadlocks NOT BETWEEN 1 AND 256
    OR @maximum_response_bytes NOT BETWEEN 4096 AND 1048576
    OR @maximum_metadata_rows>(@maximum_response_bytes-128)/64-1
    OR (@initial_generation IS NULL AND (@initial_index IS NOT NULL OR @initial_offset IS NOT NULL))
    OR (@initial_generation IS NOT NULL AND (@initial_index IS NULL OR @initial_offset IS NULL
        OR @initial_generation<=0 OR @initial_index<0 OR @initial_offset<0 OR @expected_epoch IS NULL))
    OR (@expected_epoch IS NOT NULL AND (LEN(@expected_epoch)<>64
        OR @expected_epoch COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'))
    THROW 51000,'The XE read request is outside its bounded contract.',1;

DECLARE @state nvarchar(32)=N'missing',@path nvarchar(260),@directory nvarchar(260),
    @basename nvarchar(260),@stem nvarchar(260),@prefix_index nvarchar(260),@prefix nvarchar(260),
    @pattern nvarchar(260),@initial_file nvarchar(260),@epoch varchar(64),@session_created datetime2(7),
    @server_started_ticks bigint,@session_address varbinary(8),@epoch_seed nvarchar(1024),
    @files bigint,@size_mb bigint,@generation bigint,@index int,@separator int;
SELECT @files=MAX(CASE WHEN f.name=N'max_rollover_files' THEN TRY_CONVERT(bigint,CONVERT(nvarchar(64),f.value)) END),
    @size_mb=MAX(CASE WHEN f.name=N'max_file_size' THEN TRY_CONVERT(bigint,CONVERT(nvarchar(64),f.value)) END)
FROM sys.server_event_sessions s JOIN sys.server_event_session_targets t ON t.event_session_id=s.event_session_id
LEFT JOIN sys.server_event_session_fields f ON f.event_session_id=s.event_session_id AND f.object_id=t.target_id
WHERE s.name=N'system_health' AND t.name=N'event_file';
SELECT @path=CAST(t.target_data AS xml).value('(/EventFileTarget/File/@name)[1]','nvarchar(260)'),
    @session_created=s.create_time,@session_address=s.address
FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address
WHERE s.name=N'system_health' AND t.target_name=N'event_file';
IF @path IS NOT NULL
BEGIN
    SET @state=N'config_invalid';
    SET @separator=CHARINDEX(N'\',REVERSE(@path));
    IF @files BETWEEN 1 AND 10 AND @size_mb BETWEEN 1 AND 100 AND @separator>0
    BEGIN
        SET @directory=LEFT(@path,LEN(@path)-@separator+1);
        SET @basename=RIGHT(@path,@separator-1);
        SET @stem=LEFT(@basename,CASE WHEN LEN(@basename)>4 THEN LEN(@basename)-4 ELSE 0 END);
        SET @separator=CHARINDEX(N'_',REVERSE(@stem));
        IF RIGHT(@basename,4)=N'.xel' AND @separator>1
        BEGIN
            SET @generation=TRY_CONVERT(bigint,RIGHT(@stem,@separator-1));
            SET @prefix_index=LEFT(@stem,LEN(@stem)-@separator);
            SET @separator=CHARINDEX(N'_',REVERSE(@prefix_index));
            IF @separator>1
            BEGIN
                SET @index=TRY_CONVERT(int,RIGHT(@prefix_index,@separator-1));
                SET @prefix=LEFT(@prefix_index,LEN(@prefix_index)-@separator);
                IF @generation>0 AND @index>=0 AND LEFT(@prefix,13)=N'system_health'
                    AND REPLACE(@prefix,N'-',N'') COLLATE Latin1_General_100_BIN2 NOT LIKE N'%[^a-zA-Z0-9_]%'
                    AND @basename=@prefix+N'_'+CONVERT(nvarchar(12),@index)+N'_'+CONVERT(nvarchar(20),@generation)+N'.xel'
                BEGIN
                    SELECT @server_started_ticks=sqlserver_start_time_ms_ticks FROM sys.dm_os_sys_info;
                    SET @epoch_seed=@directory+N'|'+@prefix+N'|'+CONVERT(nvarchar(16),@session_address,2)
                        +N'|'+CONVERT(nvarchar(20),@server_started_ticks)+N'|'+CONVERT(nvarchar(12),@files)
                        +N'|'+CONVERT(nvarchar(12),@size_mb)+N'|';
                    SET @epoch=LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@epoch_seed
                        +CONVERT(nvarchar(23),CONVERT(datetime2(3),@session_created),126)),2));
                    -- create_time is a reconstructed wall-clock value and can
                    -- jitter by a datetime tick on the same native session.
                    -- Retain the original digest only within 10 ms, with the
                    -- exact session address, boot tick, path and caps unchanged.
                    IF @expected_epoch IS NOT NULL AND @expected_epoch<>@epoch AND EXISTS
                    (
                        SELECT 1 FROM (VALUES(-10),(-9),(-8),(-7),(-6),(-5),(-4),(-3),(-2),(-1),
                            (0),(1),(2),(3),(4),(5),(6),(7),(8),(9),(10)) tolerance(delta)
                        WHERE @expected_epoch=LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',@epoch_seed
                            +CONVERT(nvarchar(23),DATEADD(millisecond,tolerance.delta,CONVERT(datetime2(3),@session_created)),126)),2))
                    ) SET @epoch=@expected_epoch;
                    SET @pattern=@directory+@prefix+N'_*.xel';
                    SET @state=CASE WHEN @expected_epoch IS NOT NULL AND @expected_epoch<>@epoch
                        OR @initial_generation>@generation
                        OR @initial_generation=@generation AND @initial_index<>@index THEN N'reset_required' ELSE N'ready' END;
                    IF @initial_generation IS NOT NULL
                        SET @initial_file=@directory+@prefix+N'_'+CONVERT(nvarchar(12),@initial_index)
                            +N'_'+CONVERT(nvarchar(20),@initial_generation)+N'.xel';
                    IF @initial_offset>@size_mb*1048576+1048576
                        THROW 51000,'The XE buffer offset exceeds the configured file bound.',1;
                END
            END
        END
    END
END;

CREATE TABLE #xe_read_rows
(
    row_id int IDENTITY(1,1) NOT NULL,file_name nvarchar(260) NOT NULL,
    generation bigint NULL,rollover_index int NULL,buffer_offset bigint NOT NULL,occurred_at datetime2(7) NOT NULL,
    is_deadlock bit NOT NULL,oversized bit NOT NULL DEFAULT(0),quota_exceeded bit NOT NULL DEFAULT(0),event_xml nvarchar(max) NULL
);
IF @state=N'ready'
BEGIN TRY
    CREATE TABLE #xe_metadata_page
    (
        file_name nvarchar(260) NOT NULL,generation bigint NULL,file_offset bigint NOT NULL,
        timestamp_utc datetime2(7) NOT NULL,is_deadlock int NOT NULL
    );
    -- Materialize the small page before expanding filename validation. Nested
    -- filename expressions in the full-source sort can dominate a cold read.
    INSERT #xe_metadata_page
    SELECT TOP(@maximum_metadata_rows+1) raw.file_name,
        TRY_CONVERT(bigint,SUBSTRING(raw.file_name,LEN(raw.file_name)-CHARINDEX(N'_',REVERSE(raw.file_name))+2,
            CASE WHEN CHARINDEX(N'_',REVERSE(raw.file_name))>=5 THEN CHARINDEX(N'_',REVERSE(raw.file_name))-5 ELSE 0 END)) AS generation,
        raw.file_offset,raw.timestamp_utc,
        -- Do not request event_data here. Native XE otherwise serializes every
        -- unrelated event before the page sort, even behind a residual CASE.
        CASE WHEN raw.object_name=N'xml_deadlock_report'
            AND raw.timestamp_utc>=DATEADD(day,-33,SYSUTCDATETIME()) THEN 1 ELSE 0 END
    FROM sys.fn_xe_file_target_read_file(@pattern,NULL,@initial_file,@initial_offset) raw
    ORDER BY generation,raw.file_offset,raw.timestamp_utc;
    ;WITH names AS
    (
        SELECT r.*,RIGHT(file_name,CASE WHEN CHARINDEX(N'\',REVERSE(file_name))>0
            THEN CHARINDEX(N'\',REVERSE(file_name))-1 ELSE 0 END) AS basename FROM #xe_metadata_page r
    ), stems AS
    (
        SELECT n.*,LEFT(basename,CASE WHEN LEN(basename)>4 THEN LEN(basename)-4 ELSE 0 END) AS stem FROM names n
    ), tokens AS
    (
        SELECT s.*,
            LEFT(stem,LEN(stem)-CHARINDEX(N'_',REVERSE(stem))) AS prefix_index FROM stems s
    ), positions AS
    (
        SELECT t.*,TRY_CONVERT(int,RIGHT(prefix_index,CASE WHEN CHARINDEX(N'_',REVERSE(prefix_index))>0
            THEN CHARINDEX(N'_',REVERSE(prefix_index))-1 ELSE 0 END)) AS rollover_index FROM tokens t
    )
    INSERT #xe_read_rows(file_name,generation,rollover_index,buffer_offset,occurred_at,is_deadlock)
    SELECT TOP(@maximum_metadata_rows+1) file_name,
        CASE WHEN file_name=@directory+@prefix+N'_'+CONVERT(nvarchar(12),rollover_index)
            +N'_'+CONVERT(nvarchar(20),generation)+N'.xel' THEN generation ELSE NULL END,
        rollover_index,file_offset,timestamp_utc,CONVERT(bit,is_deadlock)
    FROM positions ORDER BY generation,rollover_index,file_offset,timestamp_utc;
    IF EXISTS(SELECT 1 FROM #xe_read_rows WHERE generation IS NULL OR generation<=0 OR rollover_index IS NULL
        OR rollover_index<0 OR buffer_offset<0 OR buffer_offset>@size_mb*1048576+1048576)
        SET @state=N'config_invalid';
    IF @state=N'ready' AND EXISTS(SELECT 1 FROM #xe_read_rows WHERE is_deadlock=1)
    BEGIN
        -- The second native read requests XML only for deadlocks, with an
        -- object-name predicate the TVF supports. Keep timestamp filtering
        -- residual; native timestamp pushdown can falsely return no rows.
        SELECT m.*,ROW_NUMBER() OVER(ORDER BY generation,rollover_index,buffer_offset,occurred_at,row_id) AS ordinal,
            ROW_NUMBER() OVER(PARTITION BY file_name,buffer_offset,occurred_at ORDER BY row_id) AS key_ordinal
        INTO #xe_deadlock_keys FROM #xe_read_rows m WHERE is_deadlock=1;
        CREATE TABLE #xe_deadlock_xml
        (
            file_name nvarchar(260) NOT NULL,buffer_offset bigint NOT NULL,occurred_at datetime2(7) NOT NULL,
            key_ordinal bigint NOT NULL,oversized bit NOT NULL,quota_exceeded bit NOT NULL,event_xml nvarchar(max) NULL
        );
        ;WITH raw_deadlocks AS
        (
            SELECT TOP(@maximum_deadlocks+1) raw.file_name,raw.file_offset,raw.timestamp_utc,matched.first_ordinal,
                CASE WHEN DATALENGTH(raw.event_data)<=1048576 THEN raw.event_data END AS event_data,
                CASE WHEN DATALENGTH(raw.event_data)<=1048576 THEN
                    DATALENGTH(CONVERT(varchar(max),raw.event_data COLLATE Latin1_General_100_BIN2_UTF8)) END AS xml_bytes
            FROM sys.fn_xe_file_target_read_file(@pattern,NULL,@initial_file,@initial_offset) raw
            CROSS APPLY(SELECT MIN(k.ordinal) AS first_ordinal FROM #xe_deadlock_keys k WHERE k.ordinal<=@maximum_deadlocks
                AND k.file_name=raw.file_name AND k.buffer_offset=raw.file_offset AND k.occurred_at=raw.timestamp_utc) matched
            WHERE raw.object_name=N'xml_deadlock_report'
                AND CASE WHEN raw.timestamp_utc>=DATEADD(day,-33,SYSUTCDATETIME()) THEN 1 ELSE 0 END=1
                AND matched.first_ordinal IS NOT NULL
            ORDER BY matched.first_ordinal,
                HASHBYTES('SHA2_256',CASE WHEN DATALENGTH(raw.event_data)<=1048576 THEN raw.event_data END)
        ), budgeted AS
        (
            SELECT r.*,ROW_NUMBER() OVER(PARTITION BY file_name,file_offset,timestamp_utc
                    ORDER BY HASHBYTES('SHA2_256',event_data)) AS key_ordinal,
                SUM(CASE WHEN xml_bytes<=524288 THEN CONVERT(bigint,xml_bytes) ELSE 0 END)
                    OVER(ORDER BY first_ordinal,
                        HASHBYTES('SHA2_256',event_data) ROWS UNBOUNDED PRECEDING) AS xml_total
            FROM raw_deadlocks r
        )
        INSERT #xe_deadlock_xml
        SELECT file_name,file_offset,timestamp_utc,key_ordinal,
            CONVERT(bit,CASE WHEN xml_bytes IS NULL OR xml_bytes>524288 THEN 1 ELSE 0 END),
            CONVERT(bit,CASE WHEN xml_total>@maximum_response_bytes-128-(SELECT COUNT_BIG(*)*64 FROM #xe_read_rows) THEN 1 ELSE 0 END),
            CASE WHEN xml_bytes<=524288 AND xml_total<=@maximum_response_bytes-128-(SELECT COUNT_BIG(*)*64 FROM #xe_read_rows)
                THEN event_data END FROM budgeted;
        IF EXISTS(SELECT 1 FROM #xe_deadlock_keys k LEFT JOIN #xe_deadlock_xml x ON x.file_name=k.file_name
            AND x.buffer_offset=k.buffer_offset AND x.occurred_at=k.occurred_at AND x.key_ordinal=k.key_ordinal
            WHERE k.ordinal<=@maximum_deadlocks AND x.key_ordinal IS NULL)
            SET @state=N'reset_required';
        UPDATE m SET oversized=COALESCE(x.oversized,0),
            quota_exceeded=CONVERT(bit,CASE WHEN k.ordinal>@maximum_deadlocks THEN 1 ELSE COALESCE(x.quota_exceeded,0) END),
            event_xml=CASE WHEN k.ordinal<=@maximum_deadlocks THEN x.event_xml END
        FROM #xe_read_rows m JOIN #xe_deadlock_keys k ON k.row_id=m.row_id
        LEFT JOIN #xe_deadlock_xml x ON x.file_name=k.file_name AND x.buffer_offset=k.buffer_offset
            AND x.occurred_at=k.occurred_at AND x.key_ordinal=k.key_ordinal;
    END;
    DECLARE @after_created datetime2(7),@after_path nvarchar(260),@after_address varbinary(8);
    SELECT @after_created=s.create_time,@after_address=s.address,
        @after_path=CAST(t.target_data AS xml).value('(/EventFileTarget/File/@name)[1]','nvarchar(260)')
    FROM sys.dm_xe_sessions s JOIN sys.dm_xe_session_targets t ON t.event_session_address=s.address
    WHERE s.name=N'system_health' AND t.target_name=N'event_file';
    -- Compare materialized values rather than pushing computed predicates into
    -- dynamic management views, whose native filtering can omit matching rows.
    IF @after_created IS NULL OR @after_address IS NULL OR @after_address<>@session_address
        OR ABS(DATEDIFF_BIG(millisecond,@after_created,@session_created))>10 OR @after_path IS NULL
        OR LEFT(@after_path,LEN(@directory+@prefix+N'_'))<>@directory+@prefix+N'_'
        SET @state=N'reset_required';
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.server_event_sessions s
        JOIN sys.server_event_session_targets t ON t.event_session_id=s.event_session_id
        LEFT JOIN sys.server_event_session_fields f ON f.event_session_id=s.event_session_id AND f.object_id=t.target_id
        WHERE s.name=N'system_health' AND t.name=N'event_file'
        GROUP BY s.event_session_id
        HAVING MAX(CASE WHEN f.name=N'max_rollover_files' THEN TRY_CONVERT(bigint,CONVERT(nvarchar(64),f.value)) END)=@files
            AND MAX(CASE WHEN f.name=N'max_file_size' THEN TRY_CONVERT(bigint,CONVERT(nvarchar(64),f.value)) END)=@size_mb
    ) SET @state=N'reset_required';
END TRY
BEGIN CATCH
    -- A rolled-away initial file is a visible reset, never an empty read.
    IF ERROR_NUMBER()=25717 AND @initial_file IS NOT NULL SET @state=N'reset_required'; ELSE THROW;
END CATCH;

DECLARE @read_at datetime2(7)=SYSUTCDATETIME();
SELECT @state AS source_state,@epoch AS source_epoch,@read_at AS read_at_utc,
    CASE WHEN @state=N'ready' THEN COALESCE((SELECT MIN(occurred_at) FROM #xe_read_rows),@read_at) END AS retained_from_utc,
    CONVERT(bit,CASE WHEN @state=N'ready' AND (SELECT COUNT(*) FROM #xe_read_rows)>@maximum_metadata_rows THEN 1 ELSE 0 END) AS has_more;
IF @state=N'ready'
    SELECT generation,rollover_index,buffer_offset,occurred_at,is_deadlock,oversized,quota_exceeded,event_xml
    FROM #xe_read_rows ORDER BY generation,rollover_index,buffer_offset,occurred_at;
