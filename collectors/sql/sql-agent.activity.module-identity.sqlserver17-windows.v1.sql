-- Fixed external actual-monitor identity/property observation. No module execution or grants.
-- Expected SID is derived from the caller's Windows token, never supplied by the target request.
SET NOCOUNT ON;
SELECT TOP (2)
  CONVERT(datetime2(7),SYSUTCDATETIME()) AS observed_utc,
  CONVERT(int,CASE WHEN SUSER_SID()=@expected_sid THEN 1 ELSE 0 END) AS current_sid_matches,
  CONVERT(int,CASE WHEN SUSER_SID(ORIGINAL_LOGIN())=@expected_sid THEN 1 ELSE 0 END) AS original_sid_matches,
  CONVERT(int,CASE WHEN IS_SRVROLEMEMBER(N'sysadmin')=0 THEN 1 ELSE 0 END) AS non_sysadmin,
  CONVERT(int,CASE WHEN CONNECTIONPROPERTY(N'auth_scheme') IN (N'KERBEROS',N'NTLM') THEN 1 ELSE 0 END) AS integrated_auth,
  CONVERT(int,CASE WHEN c.net_transport=N'TCP' AND c.protocol_type=N'TSQL'
    AND c.parent_connection_id IS NULL AND CONNECTIONPROPERTY(N'physical_net_transport')=N'TCP' THEN 1 ELSE 0 END) AS physical_tcp,
  CONVERT(int,CASE WHEN c.encrypt_option=N'TRUE' THEN 1 ELSE 0 END) AS encrypted,
  CONVERT(int,CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,N'CONTROL SERVER')=0 THEN 1 ELSE 0 END) AS no_control_server,
  CONVERT(int,CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,N'ALTER ANY LOGIN')=0 THEN 1 ELSE 0 END) AS no_alter_login,
  c.connection_id AS connection_id, CONVERT(int,c.session_id) AS session_id,
  CONVERT(int,DB_ID()) AS database_id, CONVERT(nvarchar(128),DB_NAME()) AS database_name,
  CONVERT(nvarchar(128),SERVERPROPERTY(N'ServerName')) AS server_name,
  CONVERT(nvarchar(128),SERVERPROPERTY(N'MachineName')) AS machine_name,
  CONVERT(nvarchar(128),SERVERPROPERTY(N'InstanceName')) AS instance_name,
  CONVERT(int,SERVERPROPERTY(N'ProductMajorVersion')) AS product_major_version,
  CONVERT(int,SERVERPROPERTY(N'EngineEdition')) AS engine_edition
FROM sys.dm_exec_connections AS c WHERE c.session_id=@@SPID;
