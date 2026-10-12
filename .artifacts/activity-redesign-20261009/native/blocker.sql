IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24' THROW 51000,'Synthetic workload host mismatch.',1;
GO
USE SqlObserverLabSales;
SET NOCOUNT ON; SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=1;
SELECT 'blocker_started' AS outcome,@@SPID AS session_id,SYSUTCDATETIME() AS started_utc;
WAITFOR DELAY '00:01:50';
ROLLBACK;
SELECT 'blocker_rolled_back' AS outcome,SYSUTCDATETIME() AS completed_utc;
