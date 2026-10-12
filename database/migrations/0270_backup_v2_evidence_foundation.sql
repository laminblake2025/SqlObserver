-- Staged backup evidence. No partitions, collector grant, or schedule activation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

-- Historical qualification is an explicit immutable input, never inferred from
-- the current host zone. Future audited commands must supply serialized .NET
-- rules; PostgreSQL does not reinterpret Windows zone rules as IANA rules.
CREATE TABLE control.backup_historical_zone_qualification
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 qualification_revision bigint NOT NULL CHECK(qualification_revision>0),
 source_zone_id text NOT NULL CHECK(length(source_zone_id) BETWEEN 1 AND 128
   AND source_zone_id !~ '[[:cntrl:]]'),
 serialized_zone_rules text NOT NULL CHECK(octet_length(serialized_zone_rules) BETWEEN 1 AND 65536),
 rules_sha256 bytea NOT NULL CHECK(rules_sha256=sha256(convert_to(serialized_zone_rules,'UTF8'))),
 qualified_from timestamptz NOT NULL CHECK(isfinite(qualified_from)),
 qualified_to timestamptz NOT NULL CHECK(isfinite(qualified_to)),
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK(qualified_from<qualified_to),
 PRIMARY KEY(instance_id,target_revision,qualification_revision)
);

CREATE TABLE telemetry.backup_v2_scan
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 run_id uuid NOT NULL REFERENCES telemetry.collection_run_identity(run_id),
 received_at timestamptz NOT NULL CHECK(isfinite(received_at)),
 source_observed_at timestamptz CHECK(isfinite(source_observed_at)),
 source_zone_id text CHECK(length(source_zone_id) BETWEEN 1 AND 128 AND source_zone_id !~ '[[:cntrl:]]'),
 history_lower_bound_local timestamp CHECK(isfinite(history_lower_bound_local)
   AND history_lower_bound_local>=timestamp '1753-01-01'
   AND history_lower_bound_local<timestamp '10000-01-01'),
 coverage smallint NOT NULL CHECK(coverage BETWEEN 0 AND 6),
 source_rows_read integer NOT NULL CHECK(source_rows_read BETWEEN 0 AND 3073),
 database_count integer NOT NULL CHECK(database_count BETWEEN 0 AND 512),
 item_count integer NOT NULL CHECK(item_count BETWEEN 0 AND 3072 AND item_count>=database_count),
 rejected_items integer NOT NULL CHECK(rejected_items BETWEEN 0 AND 3072),
 rejected_bytes integer NOT NULL CHECK(rejected_bytes BETWEEN 0 AND 4194304),
 CHECK(coverage IN (1,2,3) OR (database_count=0 AND item_count=0
   AND source_observed_at IS NULL AND source_zone_id IS NULL AND history_lower_bound_local IS NULL)),
 CHECK(coverage<>3 OR (item_count=0 AND source_rows_read>0)),
 CHECK(coverage NOT IN (1,2) OR source_rows_read=item_count),
 CHECK(coverage NOT IN (1,2,3) OR
   ((source_rows_read>0 AND source_observed_at IS NOT NULL AND history_lower_bound_local IS NOT NULL)
     OR (source_rows_read=0 AND source_observed_at IS NULL AND history_lower_bound_local IS NULL AND source_zone_id IS NULL))),
 PRIMARY KEY(instance_id,target_revision,received_at,run_id)
) PARTITION BY RANGE(received_at);

CREATE TABLE telemetry.backup_v2_database
(
 instance_id uuid NOT NULL,
 target_revision bigint NOT NULL,
 received_at timestamptz NOT NULL,
 run_id uuid NOT NULL,
 database_ordinal smallint NOT NULL CHECK(database_ordinal BETWEEN 1 AND 512),
 database_fingerprint bytea CHECK(octet_length(database_fingerprint)=32),
 family_fingerprint bytea CHECK(octet_length(family_fingerprint)=32),
 recovery_fork_fingerprint bytea CHECK(octet_length(recovery_fork_fingerprint)=32),
 group_fingerprint bytea CHECK(octet_length(group_fingerprint)=32),
 ag_database_fingerprint bytea CHECK(octet_length(ag_database_fingerprint)=32),
 local_replica_fingerprint bytea CHECK(octet_length(local_replica_fingerprint)=32),
 recovery_model smallint NOT NULL CHECK(recovery_model BETWEEN 0 AND 3),
 catalog_database_state text CHECK(catalog_database_state IN
   ('ONLINE','RESTORING','RECOVERING','RECOVERY_PENDING','SUSPECT','EMERGENCY','OFFLINE')),
 scope smallint NOT NULL CHECK(scope BETWEEN 0 AND 2),
 local_replica_role text CHECK(local_replica_role IN ('PRIMARY','SECONDARY','RESOLVING')),
 local_replica_preferred boolean,
 unclassified_backup_seen boolean NOT NULL,
 coverage smallint NOT NULL CHECK(coverage IN (1,2)),
 CHECK((scope=2 AND (group_fingerprint IS NOT NULL OR ag_database_fingerprint IS NOT NULL OR local_replica_fingerprint IS NOT NULL))
   OR (scope<>2 AND group_fingerprint IS NULL AND ag_database_fingerprint IS NULL
     AND local_replica_fingerprint IS NULL AND local_replica_role IS NULL AND local_replica_preferred IS NULL)),
 CHECK(local_replica_preferred IS NULL OR
   (group_fingerprint IS NOT NULL AND ag_database_fingerprint IS NOT NULL AND local_replica_fingerprint IS NOT NULL)),
 CHECK((coverage=1)=(database_fingerprint IS NOT NULL AND family_fingerprint IS NOT NULL
   AND recovery_fork_fingerprint IS NOT NULL AND recovery_model<>0
   AND coalesce(catalog_database_state='ONLINE',false) AND NOT unclassified_backup_seen
   AND (scope=1 OR scope=2 AND group_fingerprint IS NOT NULL AND ag_database_fingerprint IS NOT NULL
     AND local_replica_fingerprint IS NOT NULL AND coalesce(local_replica_role IN ('PRIMARY','SECONDARY'),false)
     AND local_replica_preferred IS NOT NULL))),
 PRIMARY KEY(instance_id,target_revision,received_at,run_id,database_ordinal),
 UNIQUE(instance_id,target_revision,received_at,run_id,database_fingerprint),
 FOREIGN KEY(instance_id,target_revision,received_at,run_id)
   REFERENCES telemetry.backup_v2_scan(instance_id,target_revision,received_at,run_id)
) PARTITION BY RANGE(received_at);

CREATE TABLE telemetry.backup_v2_sample
(
 instance_id uuid NOT NULL,
 target_revision bigint NOT NULL,
 received_at timestamptz NOT NULL,
 run_id uuid NOT NULL,
 database_ordinal smallint NOT NULL,
 kind smallint NOT NULL CHECK(kind BETWEEN 1 AND 3),
 copy_only boolean NOT NULL,
 finish_utc timestamptz CHECK(isfinite(finish_utc)),
 source_local_start timestamp CHECK(isfinite(source_local_start)
   AND source_local_start>=timestamp '1753-01-01' AND source_local_start<timestamp '10000-01-01'),
 source_local_finish timestamp CHECK(isfinite(source_local_finish)
   AND source_local_finish>=timestamp '1753-01-01' AND source_local_finish<timestamp '10000-01-01'),
 recorded_start_offset_minutes smallint CHECK(recorded_start_offset_minutes BETWEEN -720 AND 720
   AND recorded_start_offset_minutes%15=0),
 size_bytes bigint CHECK(size_bytes>=0),
 has_checksum boolean,
 is_damaged boolean,
 metadata_complete boolean,
 first_recovery_fork_fingerprint bytea CHECK(octet_length(first_recovery_fork_fingerprint)=32),
 last_recovery_fork_fingerprint bytea NOT NULL CHECK(octet_length(last_recovery_fork_fingerprint)=32),
 time_state smallint NOT NULL CHECK(time_state BETWEEN 1 AND 11),
 qualification_revision bigint CHECK(qualification_revision>0),
 CHECK(kind<>2 OR NOT copy_only),
 CHECK(metadata_complete IS DISTINCT FROM true OR first_recovery_fork_fingerprint IS NOT NULL),
 CHECK((time_state=1)=(finish_utc IS NOT NULL)),
 CHECK(time_state<>1 OR (source_local_start IS NOT NULL AND source_local_finish IS NOT NULL
   AND recorded_start_offset_minutes IS NOT NULL AND qualification_revision IS NOT NULL)),
 CHECK(time_state<>4 OR qualification_revision IS NULL),
 CHECK(time_state<>2 OR source_local_finish IS NULL),
 CHECK(time_state<>3 OR (source_local_start IS NULL AND source_local_finish IS NOT NULL)),
 CHECK(time_state NOT IN (4,5,6,7,8,9,10,11) OR (source_local_start IS NOT NULL AND source_local_finish IS NOT NULL)),
 CHECK(time_state<>5 OR (recorded_start_offset_minutes IS NULL AND qualification_revision IS NOT NULL)),
 CHECK(time_state NOT IN (6,7,8,9,10,11) OR
   (qualification_revision IS NOT NULL AND recorded_start_offset_minutes IS NOT NULL)),
 PRIMARY KEY(instance_id,target_revision,received_at,run_id,database_ordinal,kind,copy_only),
 FOREIGN KEY(instance_id,target_revision,received_at,run_id,database_ordinal)
   REFERENCES telemetry.backup_v2_database(instance_id,target_revision,received_at,run_id,database_ordinal),
 FOREIGN KEY(instance_id,target_revision,qualification_revision)
   REFERENCES control.backup_historical_zone_qualification(instance_id,target_revision,qualification_revision)
) PARTITION BY RANGE(received_at);

CREATE INDEX ix_backup_v2_scan_target_time ON telemetry.backup_v2_scan(instance_id,received_at DESC,run_id);
CREATE INDEX ix_backup_v2_database_identity ON telemetry.backup_v2_database(instance_id,database_fingerprint,received_at DESC);

DO $protection$
DECLARE relation text; schema_name text;
BEGIN
 FOREACH relation IN ARRAY ARRAY['backup_v2_scan','backup_v2_database','backup_v2_sample','backup_historical_zone_qualification'] LOOP
  schema_name:=CASE WHEN relation='backup_historical_zone_qualification' THEN 'control' ELSE 'telemetry' END;
  EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',schema_name,relation);
  EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',schema_name,relation);
  IF schema_name='telemetry' THEN
   EXECUTE format('CREATE POLICY %I ON %I.%I USING(instance_id::text=current_setting(''sqlobserver.target_scope'',true)) WITH CHECK(instance_id::text=current_setting(''sqlobserver.target_scope'',true))',relation||'_scope',schema_name,relation);
  END IF;
  EXECUTE format('CREATE POLICY %I ON %I.%I FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true)',relation||'_owner',schema_name,relation);
  EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON %I.%I FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation()',relation||'_append_only',schema_name,relation);
  EXECUTE format('REVOKE ALL ON %I.%I FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor',schema_name,relation);
 END LOOP;
END;
$protection$;
