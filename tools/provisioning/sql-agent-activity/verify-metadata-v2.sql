-- Qualification-only external metadata probe. Run as the actual monitor, outside the signed module.
-- The caller must supply independent trusted DBA enrollment and an already owned master session.
-- Never concatenate this probe and capture EXEC into one submitted batch: validate before EXEC.
-- Required candidate grants: VIEW DEFINITION on the fixed object and fixed certificate only.
-- Signature functions require VIEW DEFINITION on the certificate:
-- https://learn.microsoft.com/sql/t-sql/functions/is-objectsigned-transact-sql
-- https://learn.microsoft.com/sql/relational-databases/system-functions/sys-fn-check-object-signatures-transact-sql
-- IS_OBJECTSIGNED NULL alone is not unsigned proof; inventory plus fn_check 0:0 are required.
SET NOCOUNT ON;
IF DB_ID() <> 1 OR DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'master'
    THROW 50002, 'Metadata probe requires master.', 1;

DECLARE @module int = OBJECT_ID(N'sqlobserver_agent.capture_v1');
DECLARE @certificate int = CERT_ID(N'SqlObserverAgentActivityCertificateV1');
DECLARE @thumbprint varbinary(32) = (SELECT thumbprint FROM sys.certificates WHERE certificate_id=@certificate);

SELECT TOP (2)
    CONVERT(int,2) AS schema_version,
    CONVERT(int,DB_ID()) AS database_id,
    CONVERT(nvarchar(128),DB_NAME()) AS database_name,
    CONVERT(int,@@SPID) AS session_id,
    CONVERT(datetime2(7),SYSUTCDATETIME()) AS observed_utc,
    CONVERT(int,o.object_id) AS object_id,
    CONVERT(nvarchar(128),s.name) AS schema_name,
    CONVERT(nvarchar(128),o.name) AS module_name,
    CONVERT(varchar(2),RTRIM(o.type)) AS module_type,
    CONVERT(int,m.execute_as_principal_id) AS execute_as_principal_id,
    CONVERT(int,m.uses_ansi_nulls) AS uses_ansi_nulls,
    CONVERT(int,m.uses_quoted_identifier) AS uses_quoted_identifier,
    LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),m.definition)),2)) AS definition_sha256,
    CONVERT(int,c.certificate_id) AS certificate_id,
    CONVERT(nvarchar(128),c.name) AS certificate_name,
    LOWER(CONVERT(varchar(64),HASHBYTES('SHA2_256',CERTENCODED(c.certificate_id)),2)) AS public_certificate_sha256,
    LOWER(CONVERT(varchar(64),c.thumbprint,2)) AS thumbprint_hex,
    CONVERT(int,DATALENGTH(c.thumbprint)) AS thumbprint_bytes,
    CONVERT(nvarchar(60),c.pvt_key_encryption_type_desc) AS private_key_state,
    CONVERT(int,HAS_PERMS_BY_NAME(N'sqlobserver_agent.capture_v1',N'OBJECT',N'VIEW DEFINITION')) AS object_view_definition,
    CONVERT(int,HAS_PERMS_BY_NAME(N'SqlObserverAgentActivityCertificateV1',N'CERTIFICATE',N'VIEW DEFINITION')) AS certificate_view_definition,
    CONVERT(int,IS_OBJECTSIGNED(N'OBJECT',o.object_id,N'certificate',c.thumbprint)) AS is_object_signed
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id=o.schema_id
JOIN sys.sql_modules AS m ON m.object_id=o.object_id
CROSS JOIN sys.certificates AS c
WHERE o.object_id=@module AND c.certificate_id=@certificate
  AND s.name COLLATE Latin1_General_100_BIN2=N'sqlobserver_agent'
  AND o.name COLLATE Latin1_General_100_BIN2=N'capture_v1'
  AND c.name COLLATE Latin1_General_100_BIN2=N'SqlObserverAgentActivityCertificateV1';

-- Return all signature types for this object, not only the expected certificate signature.
-- Two rows suffice to reject every multiple-signature inventory without unbounded transfer.
SELECT TOP (2) CONVERT(int,class) AS signature_class,
    CONVERT(int,major_id) AS object_id,
    CONVERT(varchar(4),crypt_type) AS crypt_type,
    LOWER(CONVERT(varchar(64),thumbprint,2)) AS thumbprint_hex
FROM sys.crypt_properties
WHERE class=1 AND major_id=@module
ORDER BY crypt_type,thumbprint;

SELECT TOP (2) CONVERT(int,entity_id) AS object_id,
    CONVERT(int,is_signed) AS is_signed,
    CONVERT(int,is_signature_valid) AS is_signature_valid
FROM sys.fn_check_object_signatures(N'certificate',@thumbprint)
WHERE entity_id=@module;
