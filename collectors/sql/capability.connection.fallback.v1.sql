SET NOCOUNT ON;

SELECT TOP (1)
    CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')) AS product_major_version,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductVersion')) AS product_version,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'ProductLevel')) AS product_level,
    CONVERT(nvarchar(128), SERVERPROPERTY(N'Edition')) AS edition,
    CONVERT(int, SERVERPROPERTY(N'EngineEdition')) AS engine_edition,
    CONVERT
    (
        nvarchar(256),
        CASE CONVERT(nvarchar(1), SERVERPROPERTY(N'PathSeparator'))
            WHEN N'\' THEN N'Windows'
            WHEN N'/' THEN N'Linux'
            ELSE N'Other'
        END
    ) AS host_platform,
    CAST(NULL AS nvarchar(256)) AS host_distribution,
    CAST(NULL AS nvarchar(256)) AS host_release,
    CONVERT(bit, COALESCE(SERVERPROPERTY(N'IsHadrEnabled'), 0)) AS is_hadr_enabled,
    CONVERT(bit, COALESCE(SERVERPROPERTY(N'IsIntegratedSecurityOnly'), 0)) AS is_integrated_security_only,
    CONVERT
    (
        bit,
        CASE CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion'))
            WHEN 15 THEN COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER STATE'), 0)
            WHEN 16 THEN COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER PERFORMANCE STATE'), 0)
            WHEN 17 THEN COALESCE(HAS_PERMS_BY_NAME(NULL, NULL, N'VIEW SERVER PERFORMANCE STATE'), 0)
            ELSE 0
        END
    ) AS has_required_permission,
    CONVERT(bit, COALESCE(IS_SRVROLEMEMBER(N'##MS_ServerPerformanceStateReader##'), 0)) AS is_performance_reader_member,
    CONVERT(bit, COALESCE(IS_SRVROLEMEMBER(N'sysadmin'), 0)) AS is_sysadmin,
    CONVERT(nvarchar(40), CONNECTIONPROPERTY(N'auth_scheme')) AS auth_scheme,
    CONVERT(nvarchar(40), CONNECTIONPROPERTY(N'net_transport')) AS net_transport,
    CAST(NULL AS nvarchar(40)) AS encrypt_option;
