DECLARE @family uniqueidentifier='aaaaaaaa-0000-0000-0000-000000000001';
DECLARE @fork uniqueidentifier='bbbbbbbb-0000-0000-0000-000000000001';
DECLARE @other_fork uniqueidentifier='bbbbbbbb-0000-0000-0000-000000000002';
DECLARE @duplicate uniqueidentifier='cccccccc-0000-0000-0000-000000000001';
DECLARE @group uniqueidentifier='dddddddd-0000-0000-0000-000000000001';
DECLARE @group_database uniqueidentifier='dddddddd-0000-0000-0000-000000000002';
DECLARE @replica uniqueidentifier='eeeeeeee-0000-0000-0000-000000000001';
DECLARE @fixture_now datetime=GETDATE();
DECLARE @fixture_databases TABLE
 (database_id int,name nvarchar(128),recovery_model_desc nvarchar(60),state_desc nvarchar(60),
  group_database_id uniqueidentifier,replica_id uniqueidentifier);
INSERT INTO @fixture_databases VALUES
 (2,N'tempdb',N'SIMPLE',N'ONLINE',NULL,NULL),
 (5,N'renamed-now',N'FULL',N'ONLINE',NULL,NULL),
 (6,N'only-copy-full',N'SIMPLE',N'ONLINE',NULL,NULL),
 (7,N'never-seen',N'FULL',N'ONLINE',NULL,NULL),
 (8,N'recreated-name',N'FULL',N'ONLINE',NULL,NULL),
 (9,N'unknown-guid',N'FULL',N'OFFLINE',NULL,NULL),
 (10,N'duplicate-a',N'FULL',N'ONLINE',NULL,NULL),
 (11,N'duplicate-b',N'FULL',N'ONLINE',NULL,NULL),
 (12,N'ag-secondary',N'FULL',N'ONLINE',@group_database,@replica),
 (13,N'wrong-fork',N'FULL',N'ONLINE',NULL,NULL),
 (14,N'unknown-offset',N'FULL',N'ONLINE',NULL,NULL),
 (15,N'log-only',N'BULK_LOGGED',N'ONLINE',NULL,NULL),
 (16,N'unknown-recovery-row',N'SIMPLE',N'ONLINE',NULL,NULL),
 (17,N'unknown-copy-marker',N'FULL',N'ONLINE',NULL,NULL),
 (18,N'unresolved-finish',N'FULL',N'ONLINE',NULL,NULL),
 (19,N'partial-ag-identity',N'FULL',N'ONLINE',NULL,@replica);
DECLARE @fixture_recovery TABLE
 (database_id int,database_guid uniqueidentifier,family_guid uniqueidentifier,recovery_fork_guid uniqueidentifier);
INSERT INTO @fixture_recovery
 SELECT database_id,NEWID(),@family,@fork FROM @fixture_databases WHERE database_id NOT IN (9,16);
UPDATE @fixture_recovery SET database_guid=@duplicate WHERE database_id IN (10,11);
DECLARE @fixture_local_replicas TABLE
 (group_id uniqueidentifier,replica_id uniqueidentifier,role_desc nvarchar(60),is_local bit);
INSERT INTO @fixture_local_replicas VALUES(@group,@replica,N'SECONDARY',1);
DECLARE @fixture_backups TABLE
 (database_name nvarchar(128),database_guid uniqueidentifier,family_guid uniqueidentifier,
  first_recovery_fork_guid uniqueidentifier,last_recovery_fork_guid uniqueidentifier,
  type char(1),is_copy_only bit,backup_start_date datetime,backup_finish_date datetime,
  backup_set_id int,time_zone smallint,backup_size numeric(20,0),
  has_backup_checksums bit,is_damaged bit,has_incomplete_metadata bit);
INSERT INTO @fixture_backups
 SELECT N'old-name-before-rename',rs.database_guid,@family,@fork,@fork,k.type,k.copy_only,
  DATEADD(minute,-121,@fixture_now),DATEADD(minute,-120,@fixture_now),k.id,0,k.size,1,0,0
 FROM @fixture_recovery AS rs
 CROSS JOIN (VALUES ('D',CONVERT(bit,0),100,CONVERT(numeric(20,0),100)),
  ('D',0,101,200),('D',1,102,300),('I',0,103,100),('L',0,104,100),('L',1,105,100)) AS k(type,copy_only,id,size)
 WHERE rs.database_id=5;
INSERT INTO @fixture_backups
 SELECT N'irrelevant-name',rs.database_guid,@family,@fork,
  CASE WHEN rs.database_id=13 THEN @other_fork ELSE @fork END,
  CASE WHEN rs.database_id=15 THEN 'L' ELSE 'D' END,
  CASE WHEN rs.database_id IN (6,12) THEN 1 WHEN rs.database_id=17 THEN NULL ELSE 0 END,
  DATEADD(minute,-31,@fixture_now),DATEADD(minute,-30,@fixture_now),rs.database_id,
  CASE WHEN rs.database_id=14 THEN 127 ELSE -20 END,100,1,0,0
 FROM @fixture_recovery AS rs WHERE rs.database_id IN (6,10,11,12,13,14,15,17);
INSERT INTO @fixture_backups VALUES
 (N'recreated-name',NEWID(),@family,@fork,@fork,'D',0,@fixture_now,@fixture_now,900,0,100,1,0,0);
-- A known older timestamp must not hide an unresolved candidate in the same slot.
INSERT INTO @fixture_backups
 SELECT N'unresolved-finish',rs.database_guid,@family,@fork,@fork,'D',0,
  DATEADD(minute,-2,@fixture_now),v.finish,v.id,0,100,1,0,0
 FROM @fixture_recovery AS rs
 CROSS JOIN (VALUES (CONVERT(datetime,NULL),901),(DATEADD(minute,-1,@fixture_now),902)) AS v(finish,id)
 WHERE rs.database_id=18;
-- History outside the bounded window cannot erase a no-record inventory row.
INSERT INTO @fixture_backups
 SELECT N'never-seen',rs.database_guid,@family,@fork,@fork,'D',0,
  DATEADD(day,-36,@fixture_now),DATEADD(day,-36,@fixture_now),903,0,100,1,0,0
 FROM @fixture_recovery AS rs WHERE rs.database_id=7;
