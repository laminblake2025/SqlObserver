SET NOCOUNT ON;

-- SQL Server 2016 boxed engine editions are Windows-only. No newer host DMV is probed.
SELECT TOP (1)
    CONVERT(int, PARSENAME(CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')), 4)) AS product_major_version,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')) AS product_version,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'Edition')) AS edition,
    CONVERT(int, SERVERPROPERTY(N'EngineEdition')) AS engine_edition,
    CONVERT(nvarchar(256), N'Windows') AS host_platform,
    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE'), 0)) AS has_required_permission,
    CONVERT(bit, COALESCE(IS_SRVROLEMEMBER(N'sysadmin'), 0)) AS is_sysadmin,
    CONVERT(nvarchar(40), CONNECTIONPROPERTY(N'auth_scheme')) AS auth_scheme,
    CONVERT(nvarchar(40), connection_info.encrypt_option) AS encrypt_option,
    CONVERT(bit, 0) AS feature_replication,
    CONVERT(bit, 0) AS feature_host_binding
FROM (SELECT TOP (1) encrypt_option FROM sys.dm_exec_connections WHERE session_id = @@SPID) AS connection_info
WHERE CONVERT(int, PARSENAME(CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')), 4)) = 13 AND CONVERT(int, SERVERPROPERTY(N'EngineEdition')) IN (2, 3, 4);
