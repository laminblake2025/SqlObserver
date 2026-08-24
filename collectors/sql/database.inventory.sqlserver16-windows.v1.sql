SET NOCOUNT ON;

SELECT TOP (@maximum_rows)
    SYSUTCDATETIME() AS observed_at_utc,
    databases.database_id,
    databases.name AS database_name,
    databases.state_desc,
    databases.user_access_desc,
    databases.recovery_model_desc,
    databases.is_read_only,
    databases.compatibility_level
FROM sys.databases AS databases
WHERE databases.source_database_id IS NULL
ORDER BY databases.database_id;
