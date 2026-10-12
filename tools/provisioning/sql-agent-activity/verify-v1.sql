-- DBA-only, read-only post-install inspection. This does not install or qualify the module.
USE [master];
SET NOCOUNT ON;
DECLARE @module int = OBJECT_ID(N'sqlobserver_agent.capture_v1', N'P');
DECLARE @certificate int = CERT_ID(N'SqlObserverAgentActivityCertificateV1');
IF @module IS NULL OR @certificate IS NULL
    THROW 51000, 'Expected qualification objects are absent.', 1;
IF CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')) <> 17
    THROW 51000, 'Qualification assets require SQL Server major 17.', 1;
SELECT HASHBYTES('SHA2_256', CONVERT(varbinary(max), OBJECT_DEFINITION(@module))) AS definition_sha256,
       m.execute_as_principal_id, m.uses_ansi_nulls, m.uses_quoted_identifier,
       s.name AS schema_name, USER_NAME(s.principal_id) AS schema_owner
FROM sys.sql_modules AS m JOIN sys.objects AS o ON o.object_id=m.object_id
JOIN sys.schemas AS s ON s.schema_id=o.schema_id WHERE m.object_id=@module;
-- Compare definition_sha256 to manifest.expectedSqlDefinitionUtf16LeSha256;
-- execute_as_principal_id must be NULL (CALLER), both ANSI flags must be 1.
SELECT c.thumbprint, c.pvt_key_encryption_type_desc,
       HASHBYTES('SHA2_256', CERTENCODED(c.certificate_id)) AS public_certificate_sha256
FROM sys.certificates AS c WHERE c.certificate_id=@certificate;
SELECT s.entity_id, s.is_signed, s.is_signature_valid
FROM sys.fn_check_object_signatures(N'certificate',
    (SELECT thumbprint FROM sys.certificates WHERE certificate_id=@certificate)) AS s
WHERE s.entity_id=@module;
-- Require exactly one expected certificate signature and is_signed=is_signature_valid=1.
SELECT cp.class, cp.major_id, cp.crypt_type, cp.thumbprint
FROM sys.crypt_properties AS cp WHERE cp.class=1 AND cp.major_id=@module;
SELECT p.type_desc, permission.class_desc, permission.permission_name, permission.state_desc
FROM sys.server_principals AS p LEFT JOIN sys.server_permissions AS permission ON permission.grantee_principal_id=p.principal_id
WHERE p.name=N'SqlObserverAgentActivityCertificateLoginV1';
USE [msdb];
SELECT c.thumbprint, c.pvt_key_encryption_type_desc,
       HASHBYTES('SHA2_256', CERTENCODED(c.certificate_id)) AS public_certificate_sha256
FROM sys.certificates AS c WHERE c.name=N'SqlObserverAgentActivityCertificateV1';
SELECT p.type_desc, permission.class_desc, permission.permission_name, permission.state_desc,
       OBJECT_SCHEMA_NAME(permission.major_id) AS object_schema, OBJECT_NAME(permission.major_id) AS object_name,
       permission.minor_id
FROM sys.database_principals AS p LEFT JOIN sys.database_permissions AS permission ON permission.grantee_principal_id=p.principal_id
WHERE p.name=N'SqlObserverAgentActivityCertificateUserV1';
-- Require matching public bytes/thumbprints, both NO_PRIVATE_KEY, exact listed grants,
-- and separately verify original monitor mapping/grants against the deployment receipt.
-- These rows alone do not establish caller permissions, full job visibility or source consistency.
