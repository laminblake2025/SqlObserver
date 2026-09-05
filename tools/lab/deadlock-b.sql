IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24' THROW 51000,'Synthetic workload host mismatch.',1;
GO
USE SqlObserverLabSales;
SET NOCOUNT ON; SET XACT_ABORT ON; SET LOCK_TIMEOUT 30000;
WAITFOR DELAY '00:00:02';
BEGIN TRY
 BEGIN TRANSACTION;
 UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=4;
 WAITFOR DELAY '00:00:05';
 UPDATE dbo.LockScenario SET Value=Value+1 WHERE Id=3;
 ROLLBACK;
 SELECT 'deadlock_peer_completed' AS outcome;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 IF ERROR_NUMBER()<>1205 THROW;
 SELECT 'expected_deadlock_victim' AS outcome,ERROR_NUMBER() AS error_number;
END CATCH;
