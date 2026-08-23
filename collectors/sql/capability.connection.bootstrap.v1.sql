SET NOCOUNT ON;

SELECT TOP (1)
    CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')) AS product_major_version,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')) AS product_version,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductLevel')) AS product_level,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'Edition')) AS edition,
    CONVERT(int, SERVERPROPERTY(N'EngineEdition')) AS engine_edition,
    CONVERT(bit, COALESCE(SERVERPROPERTY(N'IsHadrEnabled'), 0)) AS is_hadr_enabled,
    CONVERT(bit, COALESCE(SERVERPROPERTY(N'IsIntegratedSecurityOnly'), 0)) AS is_integrated_security_only,
    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE'), 0)) AS has_view_server_state,
    CONVERT(bit, COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER PERFORMANCE STATE'), 0)) AS has_view_server_performance_state,
    CONVERT(bit, COALESCE(IS_SRVROLEMEMBER(N'##MS_ServerPerformanceStateReader##'), 0)) AS is_performance_reader_member,
    CONVERT(bit, COALESCE(IS_SRVROLEMEMBER(N'sysadmin'), 0)) AS is_sysadmin,
    CONVERT(nvarchar(40), CONNECTIONPROPERTY(N'auth_scheme')) AS auth_scheme,
    CONVERT(nvarchar(40), CONNECTIONPROPERTY(N'net_transport')) AS net_transport,
    CONVERT(nvarchar(1), SERVERPROPERTY(N'PathSeparator')) AS path_separator;
