SET NOCOUNT ON;
-- Execute only inside the caller's already-open user database; never change context.
DECLARE @started datetime2(7)=SYSUTCDATETIME(),@completed datetime2(7),
    @database_id int=DB_ID(),@epoch varchar(64),@incarnation varchar(64),@eligible bit=0;
IF @database_id IS NULL OR @database_id NOT BETWEEN 5 AND 32766
    THROW 51001, 'Cache identity requires an open user database context', 1;
SELECT @epoch=CONVERT(varchar(64),HASHBYTES('SHA2_256',
    CONVERT(binary(8),sqlserver_start_time_ms_ticks)+
    CONVERT(varbinary(48),CONVERT(varchar(33),sqlserver_start_time,126))),2)
FROM sys.dm_os_sys_info;
IF EXISTS (SELECT 1 FROM sys.databases
    WHERE database_id=@database_id AND state=0 AND user_access=0)
BEGIN
    SET @eligible=1;
    -- A family or current recovery-fork change also starts a new baseline.
    SELECT @incarnation=CASE WHEN COUNT(*)=1 THEN MAX(CONVERT(varchar(64),
        HASHBYTES('SHA2_256',CONVERT(binary(16),database_guid)+
            CONVERT(binary(16),family_guid)+CONVERT(binary(16),recovery_fork_guid)),2)) END
    FROM sys.database_recovery_status
    WHERE database_id=@database_id
        AND database_guid IS NOT NULL AND family_guid IS NOT NULL AND recovery_fork_guid IS NOT NULL
        AND database_guid<>'00000000-0000-0000-0000-000000000000'
        AND family_guid<>'00000000-0000-0000-0000-000000000000'
        AND recovery_fork_guid<>'00000000-0000-0000-0000-000000000000';
END;
SET @completed=SYSUTCDATETIME();
SELECT CONVERT(int,1) AS source_format,
    TODATETIMEOFFSET(@started,'+00:00') AS read_started_at_utc,
    TODATETIMEOFFSET(@completed,'+00:00') AS read_completed_at_utc,
    @database_id AS database_id,@epoch AS source_epoch,
    @incarnation AS database_incarnation,@eligible AS catalog_eligible;
