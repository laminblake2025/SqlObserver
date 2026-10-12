IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24' THROW 51000,'Synthetic workload host mismatch.',1;
GO
USE SqlObserverLabSales;
SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 125000;
WAITFOR DELAY '00:00:03';
BEGIN TRANSACTION;
UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=1;
ROLLBACK;
SELECT 'blocked_request_completed' AS outcome,SYSUTCDATETIME() AS completed_utc;
