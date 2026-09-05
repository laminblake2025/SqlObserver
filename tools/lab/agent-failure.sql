IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24' THROW 51000,'Synthetic workload host mismatch.',1;
GO
USE msdb;
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @ready int=0, @attempt int=0;
WHILE @ready=0 AND @attempt<30
BEGIN
 BEGIN TRY
  EXEC dbo.sp_is_sqlagent_starting;
  SET @ready=1;
 END TRY
 BEGIN CATCH
  IF ERROR_NUMBER()<>14258 THROW;
  SET @attempt+=1;
  WAITFOR DELAY '00:00:02';
 END CATCH;
END;
IF @ready=0 THROW 51000,'SQL Agent did not become ready within 60 seconds.',1;
BEGIN TRY
BEGIN TRANSACTION;
DECLARE @name sysname=N'SqlObserver Synthetic Failure';
IF EXISTS(SELECT 1 FROM dbo.sysjobs WHERE name=@name AND description<>N'Synthetic operator-run SqlObserver validation; no schedule.')
 THROW 51000,'A different job already owns the synthetic job name.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.sysjobs WHERE name=@name)
BEGIN
 EXEC dbo.sp_add_job @job_name=@name,@enabled=1,@description=N'Synthetic operator-run SqlObserver validation; no schedule.';
END;
IF NOT EXISTS(SELECT 1 FROM dbo.sysjobsteps s JOIN dbo.sysjobs j ON j.job_id=s.job_id WHERE j.name=@name)
BEGIN
 EXEC dbo.sp_add_jobstep @job_name=@name,@step_name=N'Expected synthetic failure',@subsystem=N'TSQL',@database_name=N'SqlObserverLabSales',@command=N'THROW 51042, ''Expected synthetic SqlObserver job failure'', 1;',@retry_attempts=0;
END;
IF NOT EXISTS(SELECT 1 FROM dbo.sysjobservers s JOIN dbo.sysjobs j ON j.job_id=s.job_id WHERE j.name=@name)
BEGIN
 EXEC dbo.sp_add_jobserver @job_name=@name;
END;
COMMIT TRANSACTION;
EXEC dbo.sp_start_job @job_name=@name;
SELECT 'synthetic_failure_job_started' AS outcome;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
