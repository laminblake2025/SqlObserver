IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24' THROW 51000,'Synthetic workload host mismatch.',1;
GO
SET NOCOUNT ON;
DECLARE @base nvarchar(4000)=CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultBackupPath'));
IF @base IS NULL THROW 51000,'Default SQL Server backup path is unavailable.',1;
IF RIGHT(@base,1)<>N'\' SET @base+=N'\';
DECLARE @suffix nvarchar(40)=REPLACE(CONVERT(nvarchar(36),NEWID()),'-','');
DECLARE @sales nvarchar(4000)=@base+N'SqlObserverLabSales_'+@suffix+N'.bak';
DECLARE @warehouse nvarchar(4000)=@base+N'SqlObserverLabWarehouse_'+@suffix+N'.bak';
BACKUP DATABASE SqlObserverLabSales TO DISK=@sales WITH COPY_ONLY,COMPRESSION,CHECKSUM;
RESTORE VERIFYONLY FROM DISK=@sales WITH CHECKSUM;
BACKUP DATABASE SqlObserverLabWarehouse TO DISK=@warehouse WITH COPY_ONLY,COMPRESSION,CHECKSUM;
RESTORE VERIFYONLY FROM DISK=@warehouse WITH CHECKSUM;
SELECT 'synthetic_backups_verified' AS outcome,SYSUTCDATETIME() AS completed_utc;
