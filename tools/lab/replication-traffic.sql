SET NOCOUNT ON;
IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24'
 THROW 51000,'This fixture is restricted to WIN-QNGOV5GDM24.',1;
USE SqlObserverLabPublisher;
DECLARE @iteration int=0;
WHILE @iteration<4
BEGIN
 UPDATE dbo.OrderEvents SET ChangedUtc=SYSUTCDATETIME() WHERE EventId BETWEEN 1 AND 100;
 WAITFOR DELAY '00:00:05';
 SET @iteration+=1;
END;
SELECT N'400 bounded row updates submitted to the real replication log reader.' AS result;
