-- Operator-run, single-instance transactional replication fixture. SQL Agent must be running.
-- The SQL Agent service identity owns replication jobs; the observer remains read-only.
SET NOCOUNT ON;
IF CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')) <> N'WIN-QNGOV5GDM24'
 THROW 51000,'This fixture is restricted to WIN-QNGOV5GDM24.',1;
IF DB_ID(N'SqlObserverLabPublisher') IS NULL CREATE DATABASE SqlObserverLabPublisher;
IF DB_ID(N'SqlObserverLabSubscriber') IS NULL CREATE DATABASE SqlObserverLabSubscriber;
IF NOT EXISTS(SELECT 1 FROM sys.servers WHERE is_distributor=1)
 EXEC sys.sp_adddistributor @distributor=@@SERVERNAME;
IF DB_ID(N'SqlObserverLabDistribution') IS NULL
 EXEC sys.sp_adddistributiondb @database=N'SqlObserverLabDistribution',@min_distretention=0,@max_distretention=24,@history_retention=24;
GO
USE SqlObserverLabDistribution;
IF NOT EXISTS(SELECT 1 FROM msdb.dbo.MSdistpublishers WHERE name=@@SERVERNAME)
 EXEC sys.sp_adddistpublisher @publisher=@@SERVERNAME,@distribution_db=N'SqlObserverLabDistribution',@security_mode=1;
IF USER_ID(N'WIN-QNGOV5GDM24\SqlObserverCollector') IS NULL
 CREATE USER [WIN-QNGOV5GDM24\SqlObserverCollector] FOR LOGIN [WIN-QNGOV5GDM24\SqlObserverCollector];
ALTER ROLE replmonitor ADD MEMBER [WIN-QNGOV5GDM24\SqlObserverCollector];
-- replmonitor permits monitor procedures, but our passive query reads these three objects.
GRANT SELECT ON dbo.MSdistribution_status TO [WIN-QNGOV5GDM24\SqlObserverCollector];
GRANT SELECT ON dbo.MSdistribution_history TO [WIN-QNGOV5GDM24\SqlObserverCollector];
GRANT SELECT ON dbo.MSdistribution_agents TO [WIN-QNGOV5GDM24\SqlObserverCollector];
GO
USE master;
USE msdb;
GRANT SELECT ON dbo.sysjobactivity TO [WIN-QNGOV5GDM24\SqlObserverCollector];
USE master;
EXEC sys.sp_replicationdboption @dbname=N'SqlObserverLabPublisher',@optname=N'publish',@value=N'true';
GO
USE SqlObserverLabPublisher;
IF OBJECT_ID(N'dbo.OrderEvents') IS NULL
BEGIN
 CREATE TABLE dbo.OrderEvents(EventId bigint NOT NULL PRIMARY KEY,OrderId int NOT NULL,Status varchar(16) NOT NULL,Amount decimal(12,2) NOT NULL,ChangedUtc datetime2(3) NOT NULL);
 INSERT dbo.OrderEvents SELECT TOP (1000) ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),ROW_NUMBER() OVER(ORDER BY (SELECT NULL)),'Created',19.95,SYSUTCDATETIME() FROM sys.all_objects;
END;
IF NOT EXISTS(SELECT 1 FROM dbo.syspublications WHERE name=N'SqlObserverLabOrders')
 EXEC sys.sp_addpublication @publication=N'SqlObserverLabOrders',@status=N'active',@repl_freq=N'continuous',@allow_push=N'true',@independent_agent=N'true',@immediate_sync=N'false';
IF NOT EXISTS(SELECT 1 FROM SqlObserverLabDistribution.dbo.MSlogreader_agents WHERE publisher_db=N'SqlObserverLabPublisher')
 EXEC sys.sp_addlogreader_agent @job_login=NULL,@job_password=NULL,@publisher_security_mode=1;
IF NOT EXISTS(SELECT 1 FROM dbo.sysarticles WHERE name=N'OrderEvents')
 EXEC sys.sp_addarticle @publication=N'SqlObserverLabOrders',@article=N'OrderEvents',@source_owner=N'dbo',@source_object=N'OrderEvents',@type=N'logbased',@pre_creation_cmd=N'drop';
IF NOT EXISTS(SELECT 1 FROM dbo.syspublications WHERE name=N'SqlObserverLabOrders' AND snapshot_jobid IS NOT NULL)
 EXEC sys.sp_addpublication_snapshot @publication=N'SqlObserverLabOrders',@frequency_type=1,@publisher_security_mode=1,@job_login=NULL,@job_password=NULL;
IF NOT EXISTS(SELECT 1 FROM SqlObserverLabDistribution.dbo.MSdistribution_agents WHERE publisher_db=N'SqlObserverLabPublisher' AND subscriber_db=N'SqlObserverLabSubscriber')
BEGIN
 EXEC sys.sp_addsubscription @publication=N'SqlObserverLabOrders',@subscriber=@@SERVERNAME,@destination_db=N'SqlObserverLabSubscriber',@subscription_type=N'push',@sync_type=N'automatic',@article=N'all',@update_mode=N'read only';
 EXEC sys.sp_addpushsubscription_agent @publication=N'SqlObserverLabOrders',@subscriber=@@SERVERNAME,@subscriber_db=N'SqlObserverLabSubscriber',@subscriber_security_mode=1,@frequency_type=64,@job_login=NULL,@job_password=NULL;
END;
EXEC sys.sp_startpublication_snapshot @publication=N'SqlObserverLabOrders';
SELECT N'Snapshot requested; verify subscriber convergence before validation.' AS result;
