-- M10: host/replication evidence, analytics, incident correlation, and
-- production partition/retention hardening.
--
-- This migration is forward-only.  M10 writes to new v2 parents and exposes
-- compatibility projections; historical parents are never copied in one
-- unbounded transaction.  Collector asset digests are pinned integration
-- constants in the M10 contract below.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;

-- One fixed resolver is used by every analytics read/write contract.  It
-- locks the current target row, enforces transaction-local scope, and rejects
-- stale caller revisions before any protected table is touched.
CREATE OR REPLACE FUNCTION control.resolve_m10_target_revision(p_instance_id uuid,p_requested_revision bigint DEFAULT NULL)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,control AS $m10_target_revision$
DECLARE current_revision bigint;
BEGIN
 IF p_instance_id IS NULL OR p_instance_id='00000000-0000-0000-0000-000000000000'::uuid
    OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
 THEN RAISE EXCEPTION 'analytics target scope is required' USING ERRCODE='42501'; END IF;
 SELECT revision INTO current_revision FROM control.observation_target WHERE instance_id=p_instance_id FOR SHARE;
 IF NOT FOUND OR current_revision IS NULL OR current_revision<1 THEN RAISE EXCEPTION 'analytics target does not exist' USING ERRCODE='22023'; END IF;
 IF p_requested_revision IS NOT NULL AND p_requested_revision<>current_revision THEN RAISE EXCEPTION 'analytics target revision conflict' USING ERRCODE='40001'; END IF;
 RETURN current_revision;
END $m10_target_revision$;
REVOKE ALL ON FUNCTION control.resolve_m10_target_revision(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.resolve_m10_target_revision(uuid,bigint) TO sqlobserver_server,sqlobserver_collector;

-- PostgreSQL 18 provides sha256(bytea) in pg_catalog.  M9 originally used
-- a pgcrypto-only two-argument hash helper, which is deliberately not a dependency.
CREATE OR REPLACE FUNCTION telemetry.commit_m9_health(uuid,text,integer,integer,bigint,uuid,text,uuid,text,jsonb,bytea)
RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, telemetry, control
AS $m9_sha256$
DECLARE n integer;
BEGIN
 IF $2 NOT IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
    OR $3 <> 1 OR $4 <> 1 OR $5 <= 0
    OR octet_length(convert_to($10::text,'UTF8')) > 1048576
 THEN RAISE EXCEPTION 'collector contract rejected' USING ERRCODE='22023'; END IF;
 IF $6 IS NULL OR $9 !~ '^[0-9a-f]{64}$' OR octet_length($11) <> 32
 THEN RAISE EXCEPTION 'request digest rejected' USING ERRCODE='22023'; END IF;
 SELECT jsonb_array_length($10) INTO n;
 IF n IS NULL OR n > 2048 THEN RAISE EXCEPTION 'row bound rejected' USING ERRCODE='22023'; END IF;
 INSERT INTO telemetry.m9_commit_replay
   (run_id,instance_id,target_revision,collector_id,collector_version,
    output_schema_version,request_digest,payload_digest)
 VALUES
   ($6,$1,(SELECT revision FROM control.observation_target WHERE instance_id=$1),
    $2,$3,$4,decode($9,'hex'),sha256(convert_to($10::text,'UTF8')))
 ON CONFLICT(run_id) DO UPDATE
 SET request_digest=EXCLUDED.request_digest,payload_digest=EXCLUDED.payload_digest
 WHERE telemetry.m9_commit_replay.request_digest=EXCLUDED.request_digest
   AND telemetry.m9_commit_replay.payload_digest=EXCLUDED.payload_digest;
 IF NOT FOUND THEN RAISE EXCEPTION 'divergent replay digest' USING ERRCODE='40001'; END IF;
 RETURN true;
END
$m9_sha256$;
REVOKE ALL ON FUNCTION telemetry.commit_m9_health(uuid,text,integer,integer,bigint,uuid,text,uuid,text,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- M10 collector contracts.  Manifest and bundle digests are byte-exact LF
-- hashes of the checked-in assets and are asserted below on every upgrade.
-- capability.connection v3 is discovery-only (it has no collection schedule),
-- but its five-resource LF bundle is pinned here so migration/runtime parity
-- cannot silently drift from the capability probe contract.
DO $m10_capability_v3_asset_gate$
BEGIN
 IF '54eeb9f20404e8e9610994c140702347f45a43a64eb7405c47bbe618d5f95bba' IS DISTINCT FROM
    '54eeb9f20404e8e9610994c140702347f45a43a64eb7405c47bbe618d5f95bba'
 THEN RAISE EXCEPTION 'capability.connection v3 asset bundle parity failed' USING ERRCODE='55000'; END IF;
END $m10_capability_v3_asset_gate$;
INSERT INTO control.collector_contract
 (collector_id,collector_version,execution_order,manifest_schema_version,
  output_schema_version,manifest_sha256,asset_bundle_sha256,default_interval,
  minimum_interval,execution_timeout,maximum_rows,maximum_response_bytes,
  estimated_cost,maximum_attempts,circuit_failure_threshold,circuit_open_interval)
VALUES
 ('host.metrics',1,14,5,1,
  decode('ea1cdd808a9d9245db30012beafe40ec09b148a430016281993f31a1046e8b35','hex'),
  decode('cf629310626827ea9b91baab7ef21427d20c230adfaeff472ddfd26d1ebfee26','hex'),
  interval '1 minute',interval '30 seconds',interval '5 seconds',256,262144,
  'moderate',2,3,interval '5 minutes'),
 ('replication.health',1,15,5,1,
  decode('1cc5d831d59222c75555791fbf1a3045b486158195dbed42384ab43d0e4a9509','hex'),
  decode('7e06e0e3d1c71dd3c9e5a2e2acd14412984e761009921a63bf1141a5b34d18aa','hex'),
  interval '1 minute',interval '30 seconds',interval '10 seconds',2048,2097152,
  'moderate',2,3,interval '5 minutes')
-- collector_contract is protected by a statement-level append-only trigger.
-- Even a conflict-free INSERT ... DO UPDATE fires that trigger, so upgrades
-- must leave an existing immutable contract untouched and let the exact digest
-- gate below reject any drift.
ON CONFLICT (collector_id,collector_version) DO NOTHING;
DO $m10_asset_digest_gate$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM control.collector_contract WHERE collector_id='host.metrics' AND collector_version=1 AND manifest_sha256=decode('ea1cdd808a9d9245db30012beafe40ec09b148a430016281993f31a1046e8b35','hex') AND asset_bundle_sha256=decode('cf629310626827ea9b91baab7ef21427d20c230adfaeff472ddfd26d1ebfee26','hex'))
    OR NOT EXISTS (SELECT 1 FROM control.collector_contract WHERE collector_id='replication.health' AND collector_version=1 AND manifest_sha256=decode('1cc5d831d59222c75555791fbf1a3045b486158195dbed42384ab43d0e4a9509','hex') AND asset_bundle_sha256=decode('7e06e0e3d1c71dd3c9e5a2e2acd14412984e761009921a63bf1141a5b34d18aa','hex'))
 THEN RAISE EXCEPTION 'M10 collector asset digest parity failed' USING ERRCODE='55000'; END IF;
END $m10_asset_digest_gate$;
INSERT INTO control.collector_dependency
 (collector_id,collector_version,prerequisite_collector_id,prerequisite_collector_version)
VALUES
 ('host.metrics',1,'engine.core',1),
 ('replication.health',1,'engine.core',1)
ON CONFLICT DO NOTHING;

-- M10 keeps the historical M9 reconciliation contract immutable while adding
-- host/replication schedules through a separately versioned exact-order gate.
CREATE OR REPLACE FUNCTION control.reconcile_collector_catalog_m10
(
 p_collector_ids text[], p_collector_versions integer[], p_manifest_sha256 bytea[], p_asset_bundle_sha256 bytea[],
 p_execution_orders integer[], p_work_key text, p_owner_execution_id uuid, p_fencing_token bigint
) RETURNS TABLE(inserted_count integer,updated_count integer,unchanged_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=control,pg_catalog AS $m10_catalog$
DECLARE now_utc timestamptz := clock_timestamp(); m9_result record;
BEGIN
 IF p_work_key IS DISTINCT FROM 'collector/catalog/reconcile' OR p_owner_execution_id IS NULL OR p_fencing_token<=0
    OR cardinality(p_collector_ids)<>15 OR cardinality(p_collector_versions)<>15 OR cardinality(p_manifest_sha256)<>15
    OR cardinality(p_asset_bundle_sha256)<>15 OR cardinality(p_execution_orders)<>15
    OR p_collector_ids IS DISTINCT FROM ARRAY['engine.core','database.inventory','database.files','activity.sessions','activity.requests','waits.server','blocking.current','deadlocks.system-health','queries.performance','backups.status','sql-agent.failures','tempdb.health','availability-groups.health','host.metrics','replication.health']::text[]
    OR p_collector_versions IS DISTINCT FROM ARRAY[1,1,1,1,1,1,1,1,1,1,1,1,1,1,1]::integer[]
    OR p_execution_orders IS DISTINCT FROM ARRAY[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15]::integer[]
    OR EXISTS(SELECT 1 FROM unnest(p_manifest_sha256,p_asset_bundle_sha256) AS d(a,b) WHERE octet_length(a)<>32 OR octet_length(b)<>32)
 THEN RAISE EXCEPTION 'M10 catalog is not the exact ordered 15-entry contract' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 SELECT * INTO m9_result FROM control.reconcile_collector_catalog_m9(p_collector_ids[1:13],p_collector_versions[1:13],p_manifest_sha256[1:13],p_asset_bundle_sha256[1:13],p_execution_orders[1:13],p_work_key,p_owner_execution_id,p_fencing_token);
 INSERT INTO control.collector_schedule(instance_id,collector_id,collector_version,target_revision,schedule_revision,enabled,collection_interval,next_due_at,circuit_state,consecutive_failure_count,created_at,updated_at)
 SELECT target.instance_id,contract.collector_id,contract.collector_version,target.revision,1,true,contract.default_interval,now_utc,'closed',0,now_utc,now_utc
 FROM control.observation_target target CROSS JOIN control.collector_contract contract
 WHERE target.host_name IS NOT NULL AND target.lifecycle_state IN ('pending_discovery','active') AND contract.execution_order BETWEEN 14 AND 15
 ON CONFLICT (instance_id,collector_id) DO UPDATE SET target_revision=EXCLUDED.target_revision,schedule_revision=collector_schedule.schedule_revision+1,enabled=EXCLUDED.enabled,collection_interval=EXCLUDED.collection_interval,next_due_at=EXCLUDED.next_due_at,active_run_id=NULL,circuit_state='closed',consecutive_failure_count=0,circuit_open_until=NULL,updated_at=EXCLUDED.updated_at WHERE collector_schedule.target_revision IS DISTINCT FROM EXCLUDED.target_revision AND collector_schedule.active_run_id IS NULL;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY SELECT 0,0,15,now_utc;
END $m10_catalog$;
REVOKE ALL ON FUNCTION control.reconcile_collector_catalog_m10(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.reconcile_collector_catalog_m10(text[],integer[],bytea[],bytea[],integer[],text,uuid,bigint) TO sqlobserver_collector;


-- Stable host identity is separate from a target endpoint.  Rebinding a
-- target increments binding_revision and fences stale collector evidence.
-- PostgreSQL requires a unique key to exist before a composite foreign key
-- can be declared (revision is the authority, not an application default).
CREATE UNIQUE INDEX IF NOT EXISTS ux_m10_target_revision
 ON control.observation_target(instance_id,revision);
CREATE TABLE IF NOT EXISTS control.host_binding
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 host_id uuid NOT NULL,
 target_revision bigint NOT NULL,
 binding_revision bigint NOT NULL DEFAULT 1,
 host_name text NOT NULL,
 identity_fingerprint bytea NOT NULL CHECK (octet_length(identity_fingerprint)=32),
 binding_state text NOT NULL DEFAULT 'active'
   CHECK (binding_state IN ('pending','active','stale','retired')),
 first_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 last_seen_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY (instance_id,target_revision),
 UNIQUE (instance_id,target_revision,host_id,binding_revision),
 CHECK (target_revision > 0 AND binding_revision > 0),
 CONSTRAINT fk_m10_host_binding_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK (octet_length(host_name) BETWEEN 1 AND 512),
 CHECK (last_seen_at >= first_seen_at)
);
-- A target revision is a first-class fence.  The composite key lets every M10
-- row prove that the revision was issued for this target (rather than merely
-- checking that the target UUID exists).
CREATE INDEX IF NOT EXISTS ix_host_binding_host_time
 ON control.host_binding(host_id,last_seen_at DESC);
CREATE TABLE IF NOT EXISTS control.host_profile
(
 instance_id uuid NOT NULL,
 target_revision bigint NOT NULL,
 host_id uuid NOT NULL,
 binding_revision bigint NOT NULL,
 profile_revision bigint NOT NULL DEFAULT 1,
 os_family text,
 os_version text,
 cpu_count integer CHECK (cpu_count IS NULL OR cpu_count BETWEEN 1 AND 4096),
 memory_bytes bigint CHECK (memory_bytes IS NULL OR memory_bytes >= 0),
 capability_state text NOT NULL DEFAULT 'unknown'
   CHECK (capability_state IN ('unknown','available','partial','unsupported','permission_denied')),
 profile jsonb NOT NULL DEFAULT '{}'::jsonb,
 observed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,target_revision,host_id,binding_revision,profile_revision),
 CONSTRAINT fk_m10_host_profile_binding FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.host_binding(instance_id,target_revision),
 CONSTRAINT uq_m10_host_profile_identity UNIQUE(instance_id,target_revision,host_id,binding_revision,profile_revision),
 CHECK (jsonb_typeof(profile)='object' AND octet_length(profile::text)<=65536)
);
CREATE INDEX IF NOT EXISTS ix_host_profile_observed ON control.host_profile(host_id,observed_at DESC);
CREATE TABLE IF NOT EXISTS control.replication_profile
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL,
 topology_revision bigint NOT NULL DEFAULT 1,
 topology_kind text NOT NULL CHECK (topology_kind IN ('none','availability_group','log_shipping','replication','mixed','unknown')),
 capability_state text NOT NULL CHECK (capability_state IN ('unknown','available','partial','unsupported','permission_denied')),
 topology jsonb NOT NULL DEFAULT '{}'::jsonb,
 observed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,target_revision,topology_revision),
 CONSTRAINT fk_m10_replication_profile_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK (target_revision > 0 AND jsonb_typeof(topology)='object' AND octet_length(topology::text)<=131072)
);
CREATE TABLE IF NOT EXISTS control.replication_distribution_binding
(
 instance_id uuid NOT NULL,
 target_revision bigint NOT NULL,
 database_name text NOT NULL,
 tcp_port integer,
 binding_revision bigint NOT NULL DEFAULT 1,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(instance_id,target_revision),
 CONSTRAINT fk_m10_replication_distribution_target FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK (target_revision>0 AND binding_revision>0 AND database_name ~ '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$'),
 CHECK (tcp_port IS NULL OR tcp_port BETWEEN 1 AND 65535)
);
CREATE INDEX IF NOT EXISTS ix_m10_replication_distribution_current
 ON control.replication_distribution_binding(instance_id,target_revision,binding_revision DESC);
CREATE OR REPLACE FUNCTION control.resolve_m10_replication_distribution_binding(p_instance_id uuid,p_target_revision bigint)
RETURNS TABLE(database_name text,tcp_port integer)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,control AS $$
 SELECT b.database_name,b.tcp_port
 FROM control.replication_distribution_binding b
 WHERE p_instance_id IS NOT NULL AND p_target_revision>0
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND b.instance_id=p_instance_id AND b.target_revision=p_target_revision
 ORDER BY b.binding_revision DESC LIMIT 1;
$$;
REVOKE ALL ON FUNCTION control.resolve_m10_replication_distribution_binding(uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.resolve_m10_replication_distribution_binding(uuid,bigint) TO sqlobserver_server,sqlobserver_collector;

-- M10 high-volume parents.  All timestamps are UTC timestamptz partition keys.
CREATE TABLE IF NOT EXISTS telemetry.host_metric_snapshot_v2
(
 observed_at timestamptz NOT NULL, run_id uuid NOT NULL,
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL, host_id uuid NOT NULL, binding_revision bigint NOT NULL,
 profile_revision bigint NOT NULL, metric_key text NOT NULL,
 metric_value double precision NOT NULL, dimensions jsonb NOT NULL DEFAULT '{}'::jsonb,
 collected_at timestamptz NOT NULL,
 PRIMARY KEY(observed_at,run_id,metric_key,dimensions),
 CONSTRAINT fk_m10_host_metric_binding FOREIGN KEY (instance_id,target_revision,host_id,binding_revision,profile_revision)
   REFERENCES control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision),
 CHECK (target_revision>0 AND metric_key ~ '^[a-z][a-z0-9._-]*$' AND octet_length(metric_key)<=128),
 CHECK (metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)),
 CHECK (jsonb_typeof(dimensions)='object' AND octet_length(dimensions::text)<=16384)
) PARTITION BY RANGE(observed_at);
CREATE TABLE IF NOT EXISTS telemetry.replication_snapshot_v2
(
 observed_at timestamptz NOT NULL, run_id uuid NOT NULL,
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL, topology_fingerprint bytea NOT NULL,
 database_fingerprint bytea, role text NOT NULL, synchronization_state text NOT NULL,
 send_queue_bytes bigint, redo_queue_bytes bigint, pending_commands bigint, latency_seconds double precision,
 visibility_scope smallint NOT NULL,
 state_available boolean NOT NULL, collected_at timestamptz NOT NULL,
 PRIMARY KEY(observed_at,run_id,topology_fingerprint),
 CONSTRAINT fk_m10_replication_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK (octet_length(topology_fingerprint)=32 AND (database_fingerprint IS NULL OR octet_length(database_fingerprint)=32)),
 CHECK (visibility_scope BETWEEN 1 AND 3 AND target_revision>0),
 CHECK (send_queue_bytes IS NULL OR send_queue_bytes>=0), CHECK (redo_queue_bytes IS NULL OR redo_queue_bytes>=0),
 CHECK (pending_commands IS NULL OR pending_commands>=0),
 CHECK (latency_seconds IS NULL OR latency_seconds>=0 AND latency_seconds NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
) PARTITION BY RANGE(observed_at);
ALTER TABLE telemetry.replication_snapshot_v2 ADD COLUMN IF NOT EXISTS pending_commands bigint;
ALTER TABLE telemetry.replication_snapshot_v2 ADD COLUMN IF NOT EXISTS latency_seconds double precision;
DO $m10_replication_metric_columns$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_m10_replication_metric_values') THEN
  ALTER TABLE telemetry.replication_snapshot_v2 ADD CONSTRAINT ck_m10_replication_metric_values CHECK
   ((pending_commands IS NULL OR pending_commands>=0) AND
    (latency_seconds IS NULL OR latency_seconds>=0 AND latency_seconds NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)));
 END IF;
END $m10_replication_metric_columns$;
CREATE TABLE IF NOT EXISTS analytics.metric_rollup_v2
(
 bucket_start timestamptz NOT NULL, instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL, rollup_interval text NOT NULL CHECK (rollup_interval IN ('5m','hour','day')),
 metric_key text NOT NULL, aggregation text NOT NULL CHECK (aggregation IN ('avg','min','max','sum','count','p50','p95')),
 dimension_hash bytea NOT NULL CHECK (octet_length(dimension_hash)=32),
 generation bigint NOT NULL DEFAULT 1 CHECK (generation>0),
 sample_count integer NOT NULL, value double precision, visibility_state text NOT NULL DEFAULT 'complete'
   CHECK (visibility_state IN ('complete','partial','unavailable','unsupported')),
 computed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,dimension_hash,generation),
 CONSTRAINT fk_m10_rollup_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision), CHECK(sample_count>=0)
) PARTITION BY RANGE(bucket_start);
CREATE TABLE IF NOT EXISTS analytics.evidence_packet_v2
(
 occurred_at timestamptz NOT NULL, packet_id uuid NOT NULL, instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
  target_revision bigint NOT NULL, evidence_kind text NOT NULL, source_run_id uuid, source_digest bytea NOT NULL,
  identity_digest bytea NOT NULL DEFAULT sha256(convert_to('','UTF8')), source_cutoff_digest bytea NOT NULL DEFAULT sha256(convert_to('','UTF8')), source_cutoff_utc timestamptz,
 evidence jsonb NOT NULL, confidence numeric(5,4) CHECK(confidence BETWEEN 0 AND 1), visibility_state text NOT NULL,
 PRIMARY KEY(occurred_at,packet_id), CHECK(octet_length(source_digest)=32 AND octet_length(identity_digest)=32 AND octet_length(source_cutoff_digest)=32 AND jsonb_typeof(evidence)='object' AND octet_length(evidence::text)<=262144),
 CONSTRAINT fk_m10_evidence_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK(visibility_state IN ('complete','partial','unavailable','unsupported'))
 ) PARTITION BY RANGE(occurred_at);
 ALTER TABLE analytics.evidence_packet_v2 ADD COLUMN IF NOT EXISTS source_cutoff_utc timestamptz;
CREATE INDEX IF NOT EXISTS ix_host_metric_target_time ON telemetry.host_metric_snapshot_v2(instance_id,observed_at DESC);
CREATE INDEX IF NOT EXISTS ix_host_metric_brin ON telemetry.host_metric_snapshot_v2 USING brin(observed_at);
CREATE INDEX IF NOT EXISTS ix_replication_target_time ON telemetry.replication_snapshot_v2(instance_id,observed_at DESC);
CREATE INDEX IF NOT EXISTS ix_replication_brin ON telemetry.replication_snapshot_v2 USING brin(observed_at);
CREATE INDEX IF NOT EXISTS ix_rollup_target_time ON analytics.metric_rollup_v2(instance_id,bucket_start DESC);
CREATE INDEX IF NOT EXISTS ix_evidence_target_time ON analytics.evidence_packet_v2(instance_id,occurred_at DESC);
CREATE OR REPLACE VIEW telemetry.host_metric_snapshot AS SELECT * FROM telemetry.host_metric_snapshot_v2;
CREATE OR REPLACE VIEW telemetry.replication_snapshot AS SELECT * FROM telemetry.replication_snapshot_v2;
CREATE OR REPLACE VIEW analytics.metric_rollup AS SELECT * FROM analytics.metric_rollup_v2;
CREATE OR REPLACE VIEW analytics.evidence_packet AS SELECT * FROM analytics.evidence_packet_v2;
REVOKE ALL ON telemetry.host_metric_snapshot,telemetry.replication_snapshot,analytics.metric_rollup,analytics.evidence_packet FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE TABLE IF NOT EXISTS analytics.metric_catalog
(
 metric_key text PRIMARY KEY, display_name text NOT NULL, unit text NOT NULL,
 source_kind text NOT NULL CHECK(source_kind IN ('raw','host','replication','derived')),
 aggregation text NOT NULL CHECK(aggregation IN ('avg','min','max','sum','count','gauge')),
 enabled boolean NOT NULL DEFAULT true, definition jsonb NOT NULL DEFAULT '{}'::jsonb,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK(metric_key ~ '^[a-z][a-z0-9._-]*$' AND jsonb_typeof(definition)='object' AND octet_length(definition::text)<=32768)
);
-- Keep the executable catalog in lock-step with MetricCatalogV1 in the
-- application.  The order, labels, units, kind, enabled bit, and dimensions
-- are all part of the immutable catalog contract.
INSERT INTO analytics.metric_catalog(metric_key,display_name,unit,source_kind,aggregation,enabled,definition)
VALUES
 ('host.cpu.percent','CPU utilization','percent','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
 ('host.memory.available_bytes','Available memory','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
 ('host.memory.committed_bytes','Committed memory','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
 ('host.volume.free_bytes','Volume free space','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
 ('host.volume.total_bytes','Volume total space','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
 ('host.volume.queue_length','Volume disk queue length','count','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
 ('host.volume.read_latency_ms','Volume read latency','milliseconds','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
 ('host.volume.write_latency_ms','Volume write latency','milliseconds','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
 ('replication.pending_commands','Pending replication commands','count','replication','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
 ('replication.latency_seconds','Replication latency','seconds','replication','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array()))
ON CONFLICT(metric_key) DO NOTHING;
DO $m10_metric_catalog_gate$
BEGIN
 IF (SELECT count(*) FROM analytics.metric_catalog)<>10
    OR (SELECT string_agg(metric_key||'|'||display_name||'|'||unit||'|'||source_kind||'|'||aggregation||'|'||enabled::text||'|'||definition::text,E'\n' ORDER BY metric_key) FROM analytics.metric_catalog)
       <> (SELECT string_agg(metric_key||'|'||display_name||'|'||unit||'|'||source_kind||'|'||aggregation||'|'||enabled::text||'|'||definition::text,E'\n' ORDER BY metric_key) FROM (VALUES
          ('host.cpu.percent','CPU utilization','percent','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
          ('host.memory.available_bytes','Available memory','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
          ('host.memory.committed_bytes','Committed memory','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
          ('host.volume.free_bytes','Volume free space','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
          ('host.volume.total_bytes','Volume total space','bytes','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
          ('host.volume.queue_length','Volume disk queue length','count','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
          ('host.volume.read_latency_ms','Volume read latency','milliseconds','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
          ('host.volume.write_latency_ms','Volume write latency','milliseconds','host','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array('volume'))),
          ('replication.pending_commands','Pending replication commands','count','replication','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array())),
          ('replication.latency_seconds','Replication latency','seconds','replication','gauge',true,jsonb_build_object('catalogVersion',1,'kind','Gauge','dimensions',jsonb_build_array()))
       ) AS expected(metric_key,display_name,unit,source_kind,aggregation,enabled,definition))
    OR encode(sha256(convert_to((SELECT string_agg(metric_key||'|'||display_name||'|'||unit||'|'||source_kind||'|'||aggregation||'|'||enabled::text||'|'||coalesce((SELECT string_agg(value,',' ORDER BY value) FROM jsonb_array_elements_text(definition->'dimensions')),''),E'\n' ORDER BY metric_key) FROM analytics.metric_catalog),'UTF8')),'hex') <> 'b67a7f7d8ee3af1228bfb8fbc485e0e61595586c88af4b9af3abefd524980dd4'
 THEN RAISE EXCEPTION 'metric_catalog is not executable catalog-v1 ordered/all-field parity' USING ERRCODE='22023'; END IF;
END $m10_metric_catalog_gate$;
-- Catalog-v1 is append-only.  A duplicate key is accepted only when the
-- all-field digest gate above proves it is byte-for-byte equivalent.
DROP TRIGGER IF EXISTS m10_metric_catalog_append_only ON analytics.metric_catalog;
CREATE TRIGGER m10_metric_catalog_append_only BEFORE UPDATE OR DELETE ON analytics.metric_catalog FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TABLE IF NOT EXISTS analytics.metric_baseline
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id), target_revision bigint NOT NULL,
 metric_key text NOT NULL REFERENCES analytics.metric_catalog(metric_key), hour_of_week integer, complete_days integer,
 window_start timestamptz NOT NULL, window_end timestamptz NOT NULL, sample_count integer NOT NULL,
 mean double precision, stddev double precision, median double precision, mad double precision, p10 double precision, p90 double precision,
  coverage double precision, confidence double precision, lower_bound double precision, upper_bound double precision,
  dimensions jsonb NOT NULL DEFAULT '{}'::jsonb, dimension_hash bytea NOT NULL,
 visibility_state text NOT NULL, computed_at timestamptz NOT NULL DEFAULT clock_timestamp(), generation bigint NOT NULL DEFAULT 1,
 PRIMARY KEY(instance_id,metric_key,window_start,generation), CHECK(window_end>window_start AND sample_count>=0),
 CONSTRAINT fk_m10_baseline_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK(target_revision>0 AND (hour_of_week IS NULL OR hour_of_week BETWEEN 0 AND 167) AND (complete_days IS NULL OR complete_days>=0) AND (coverage IS NULL OR coverage BETWEEN 0 AND 1)),
 CHECK(visibility_state IN ('complete','partial','unavailable','unsupported'))
);
CREATE TABLE IF NOT EXISTS analytics.metric_forecast
(
 forecast_id uuid PRIMARY KEY, instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id), target_revision bigint NOT NULL, metric_key text NOT NULL,
 horizon_start timestamptz NOT NULL, horizon_end timestamptz NOT NULL, model text NOT NULL,
 predicted_value double precision, lower_bound double precision, upper_bound double precision,
 confidence numeric(5,4) CHECK(confidence BETWEEN 0 AND 1), residual double precision, slope_per_day double precision, source_generation bigint NOT NULL,
 visibility_state text NOT NULL, computed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CONSTRAINT fk_m10_forecast_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK(target_revision>0 AND horizon_end>horizon_start AND visibility_state IN ('complete','partial','unavailable','unsupported'))
);
CREATE INDEX IF NOT EXISTS ix_forecast_target_horizon ON analytics.metric_forecast(instance_id,horizon_start);

CREATE TABLE IF NOT EXISTS analytics.incident_thread
(
 thread_id uuid PRIMARY KEY, instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL, opened_at timestamptz NOT NULL, closed_at timestamptz,
 state text NOT NULL CHECK(state IN ('open','acknowledged','resolved','unknown')),
 summary jsonb NOT NULL DEFAULT '{}'::jsonb, current_generation bigint NOT NULL DEFAULT 1,
 CONSTRAINT fk_m10_incident_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK(closed_at IS NULL OR closed_at>=opened_at AND jsonb_typeof(summary)='object' AND octet_length(summary::text)<=65536)
);
CREATE TABLE IF NOT EXISTS analytics.incident_generation
(
 instance_id uuid NOT NULL, target_revision bigint NOT NULL,
 thread_id uuid NOT NULL REFERENCES analytics.incident_thread(thread_id), generation bigint NOT NULL,
 observed_at timestamptz NOT NULL, state text NOT NULL, evidence_packet_id uuid,
 correlation_digest bytea NOT NULL, supersedes_previous boolean NOT NULL DEFAULT false, details jsonb NOT NULL DEFAULT '{}'::jsonb,
 PRIMARY KEY(thread_id,generation), CHECK(target_revision>0 AND generation>0 AND octet_length(correlation_digest)=32 AND jsonb_typeof(details)='object')
);
CREATE INDEX IF NOT EXISTS ix_incident_target_time ON analytics.incident_thread(instance_id,opened_at DESC);

CREATE TABLE IF NOT EXISTS control.analytics_job
(
 job_id uuid PRIMARY KEY, job_kind text NOT NULL CHECK(job_kind IN ('rollup','baseline','forecast','correlation','evidence','incident','backfill','retention')),
 instance_id uuid REFERENCES control.observation_target(instance_id), target_revision bigint,
  from_utc timestamptz, to_utc timestamptz, metric_key text, cursor text, current_day_utc timestamptz,
  cursor_source_kind text, cursor_observed_at timestamptz, cursor_source_id uuid, cursor_metric_key text,
  cursor_dimension_hash bytea, cursor_ordinal integer, cursor_target_id uuid, cursor_target_revision bigint,
  cursor_day_utc timestamptz, cursor_catalog_version integer,
  source_cutoff_utc timestamptz, generation bigint NOT NULL DEFAULT 1, horizon_seconds double precision,
 status text NOT NULL DEFAULT 'queued'
   CHECK(status IN ('queued','running','succeeded','partial','failed','cancelled')),
 work_key text NOT NULL, owner_execution_id uuid, fencing_token bigint,
 requested_at timestamptz NOT NULL DEFAULT clock_timestamp(), started_at timestamptz, completed_at timestamptz,
 attempt integer NOT NULL DEFAULT 0 CHECK(attempt BETWEEN 0 AND 5), last_error text,
 CONSTRAINT fk_m10_analytics_job_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CONSTRAINT ck_m10_rollup_job_target CHECK
   (job_kind<>'rollup' OR (instance_id IS NOT NULL AND target_revision IS NOT NULL AND target_revision>0))
);
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS target_revision bigint;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS from_utc timestamptz;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS to_utc timestamptz;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS metric_key text;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor text;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS current_day_utc timestamptz;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_source_kind text;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_observed_at timestamptz;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_source_id uuid;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_metric_key text;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_dimension_hash bytea;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_ordinal integer;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_target_id uuid;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_target_revision bigint;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_day_utc timestamptz;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS cursor_catalog_version integer;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS source_cutoff_utc timestamptz;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS generation bigint NOT NULL DEFAULT 1;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS horizon_seconds double precision;
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS analytics_job_job_kind_check;
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS ck_m10_analytics_job_kind;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_m10_analytics_job_kind CHECK
 (job_kind IN ('rollup','baseline','forecast','correlation','evidence','incident','backfill','retention'));
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS ck_m10_derivation_metadata;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_m10_derivation_metadata CHECK
 (generation>0 AND (source_cutoff_utc IS NULL OR to_utc IS NULL OR source_cutoff_utc>=to_utc)
  AND (horizon_seconds IS NULL OR horizon_seconds>0 AND horizon_seconds<=7776000));
DO $m10_job_target_fk$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='fk_m10_analytics_job_target_revision') THEN
  ALTER TABLE control.analytics_job ADD CONSTRAINT fk_m10_analytics_job_target_revision
    FOREIGN KEY (instance_id,target_revision) REFERENCES control.observation_target(instance_id,revision);
 END IF;
END $m10_job_target_fk$;
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS ck_m10_backfill_job_metadata;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_m10_backfill_job_metadata CHECK
 (job_kind<>'backfill' OR (instance_id IS NOT NULL AND target_revision IS NOT NULL AND from_utc IS NOT NULL AND to_utc IS NOT NULL AND to_utc>from_utc AND to_utc-from_utc<=interval '90 days' AND (metric_key IS NULL OR metric_key ~ '^[a-z][a-z0-9._-]{0,127}$') AND (cursor IS NULL OR octet_length(cursor)<=4096)));
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS ck_m10_rollup_job_target;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_m10_rollup_job_target CHECK
 (job_kind<>'rollup' OR (instance_id IS NOT NULL AND target_revision IS NOT NULL AND target_revision>0));
CREATE INDEX IF NOT EXISTS ix_analytics_job_due ON control.analytics_job(status,requested_at);
CREATE TABLE IF NOT EXISTS control.analytics_watermark
(
 stream_key text PRIMARY KEY, instance_id uuid, watermark_at timestamptz NOT NULL,
 generation bigint NOT NULL DEFAULT 1, updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK(generation>0)
);
CREATE TABLE IF NOT EXISTS control.analytics_replay
(
 operation_id uuid PRIMARY KEY, job_id uuid NOT NULL REFERENCES control.analytics_job(job_id),
 instance_id uuid, operation_kind text, target_revision bigint, work_key text, owner_execution_id uuid, fencing_token bigint,
 request_digest bytea NOT NULL, result_digest bytea NOT NULL, result jsonb NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(), CHECK(octet_length(request_digest)=32 AND octet_length(result_digest)=32)
);
ALTER TABLE control.analytics_replay ADD COLUMN IF NOT EXISTS operation_kind text;
ALTER TABLE control.analytics_replay ADD COLUMN IF NOT EXISTS target_revision bigint;
ALTER TABLE control.analytics_replay ADD COLUMN IF NOT EXISTS work_key text;
ALTER TABLE control.analytics_replay ADD COLUMN IF NOT EXISTS owner_execution_id uuid;
ALTER TABLE control.analytics_replay ADD COLUMN IF NOT EXISTS fencing_token bigint;
ALTER TABLE control.analytics_replay DROP CONSTRAINT IF EXISTS fk_m10_replay_target_revision;
ALTER TABLE control.analytics_replay ADD CONSTRAINT fk_m10_replay_target_revision FOREIGN KEY(instance_id,target_revision) REFERENCES control.observation_target(instance_id,revision);
CREATE OR REPLACE FUNCTION control.record_m10_analytics_replay(
 p_operation_id uuid,p_operation_kind text,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,
 p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_result_digest bytea,p_result jsonb)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_replay_common$
DECLARE existing control.analytics_replay%ROWTYPE; computed_result_digest bytea;
BEGIN
 IF p_operation_id IS NULL OR p_operation_kind IS NULL OR p_operation_kind !~ '^[a-z][a-z0-9._-]{0,63}$' OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<1 OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing_token<1 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_result)<>'object' THEN
   RAISE EXCEPTION 'analytics replay identity bounds rejected' USING ERRCODE='22023';
 END IF;
 INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,status,work_key,owner_execution_id,fencing_token)
  VALUES(p_job_id,CASE WHEN p_operation_kind IN ('rollup','baseline','forecast','correlation','backfill','retention') THEN p_operation_kind ELSE 'correlation' END,p_instance_id,p_target_revision,'running',p_work_key,p_owner_execution_id,p_fencing_token)
  ON CONFLICT(job_id) DO NOTHING;
 IF EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND (j.instance_id IS DISTINCT FROM p_instance_id OR j.target_revision IS DISTINCT FROM p_target_revision OR j.work_key IS DISTINCT FROM p_work_key OR j.owner_execution_id IS DISTINCT FROM p_owner_execution_id OR j.fencing_token IS DISTINCT FROM p_fencing_token)) THEN
   RAISE EXCEPTION 'analytics replay job identity divergence' USING ERRCODE='40001';
 END IF;
  computed_result_digest:=sha256(convert_to(p_result::text,'UTF8'));
  INSERT INTO control.analytics_replay(operation_id,job_id,instance_id,operation_kind,target_revision,work_key,owner_execution_id,fencing_token,request_digest,result_digest,result)
  VALUES(p_operation_id,p_job_id,p_instance_id,p_operation_kind,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,computed_result_digest,p_result)
 ON CONFLICT(operation_id) DO NOTHING;
 IF FOUND THEN RETURN true; END IF;
 SELECT * INTO existing FROM control.analytics_replay WHERE operation_id=p_operation_id FOR SHARE;
  IF existing.operation_kind IS NOT DISTINCT FROM p_operation_kind AND existing.job_id=p_job_id AND existing.instance_id=p_instance_id AND existing.target_revision=p_target_revision AND existing.work_key=p_work_key AND existing.owner_execution_id=p_owner_execution_id AND existing.fencing_token=p_fencing_token AND existing.request_digest=p_request_digest AND existing.result_digest=computed_result_digest THEN RETURN false; END IF;
 RAISE EXCEPTION 'divergent analytics replay identity' USING ERRCODE='40001';
END $m10_replay_common$;
REVOKE ALL ON FUNCTION control.record_m10_analytics_replay(uuid,text,uuid,uuid,bigint,text,uuid,bigint,bytea,bytea,jsonb) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- Every M10 evidence table and control ledger is append-only.  Existing
-- repository trigger is SECURITY DEFINER and permits only the retention
-- context for cascaded data removal.
DO $m10_drop_triggers$
DECLARE t record;
BEGIN
 FOR t IN SELECT * FROM (VALUES
  ('control','host_binding','m10_host_binding_append_only'),('control','host_profile','m10_host_profile_append_only'),('control','replication_profile','m10_replication_profile_append_only'),('control','replication_distribution_binding','m10_replication_distribution_binding_append_only'),
  ('telemetry','host_metric_snapshot_v2','m10_host_metric_append_only'),('telemetry','replication_snapshot_v2','m10_replication_append_only'),('analytics','metric_rollup_v2','m10_rollup_append_only'),('analytics','evidence_packet_v2','m10_evidence_append_only'),('analytics','metric_baseline','m10_baseline_append_only'),('analytics','metric_forecast','m10_forecast_append_only'),('analytics','incident_generation','m10_incident_generation_append_only'),('control','analytics_replay','m10_replay_append_only')) AS x(s,t,n)
 LOOP EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I.%I',t.n,t.s,t.t); END LOOP;
END $m10_drop_triggers$;
CREATE TRIGGER m10_host_binding_append_only BEFORE UPDATE OR DELETE ON control.host_binding FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_host_profile_append_only BEFORE UPDATE OR DELETE ON control.host_profile FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_replication_profile_append_only BEFORE UPDATE OR DELETE ON control.replication_profile FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_replication_distribution_binding_append_only BEFORE UPDATE OR DELETE ON control.replication_distribution_binding FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_host_metric_append_only BEFORE UPDATE OR DELETE ON telemetry.host_metric_snapshot_v2 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_replication_append_only BEFORE UPDATE OR DELETE ON telemetry.replication_snapshot_v2 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_rollup_append_only BEFORE UPDATE OR DELETE ON analytics.metric_rollup_v2 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_evidence_append_only BEFORE UPDATE OR DELETE ON analytics.evidence_packet_v2 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_baseline_append_only BEFORE UPDATE OR DELETE ON analytics.metric_baseline FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_forecast_append_only BEFORE UPDATE OR DELETE ON analytics.metric_forecast FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_incident_generation_append_only BEFORE UPDATE OR DELETE ON analytics.incident_generation FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_replay_append_only BEFORE UPDATE OR DELETE ON control.analytics_replay FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

-- Force target scope on all externally reachable evidence.  Migrator gets the
-- explicit owner policy; collector/server access is through fixed functions.
DO $m10_drop_policies$
DECLARE r record;
BEGIN
 FOR r IN SELECT * FROM (VALUES
  ('control','host_binding','m10_host_binding_scope'),('control','host_binding','m10_host_binding_owner'),('control','replication_profile','m10_replication_profile_scope'),('control','replication_profile','m10_replication_profile_owner'),('control','replication_distribution_binding','m10_replication_distribution_binding_scope'),('control','replication_distribution_binding','m10_replication_distribution_binding_owner'),
  ('telemetry','host_metric_snapshot_v2','m10_host_metric_snapshot_v2_scope'),('telemetry','host_metric_snapshot_v2','m10_host_metric_snapshot_v2_owner'),('telemetry','replication_snapshot_v2','m10_replication_snapshot_v2_scope'),('telemetry','replication_snapshot_v2','m10_replication_snapshot_v2_owner'),
  ('analytics','metric_rollup_v2','m10_metric_rollup_v2_scope'),('analytics','metric_rollup_v2','m10_metric_rollup_v2_owner'),('analytics','evidence_packet_v2','m10_evidence_packet_v2_scope'),('analytics','evidence_packet_v2','m10_evidence_packet_v2_owner'),('analytics','metric_baseline','m10_metric_baseline_scope'),('analytics','metric_baseline','m10_metric_baseline_owner'),('analytics','metric_forecast','m10_metric_forecast_scope'),('analytics','metric_forecast','m10_metric_forecast_owner'),('analytics','incident_thread','m10_incident_thread_scope'),('analytics','incident_thread','m10_incident_thread_owner'),('control','analytics_replay','m10_analytics_replay_scope'),('control','analytics_replay','m10_analytics_replay_owner')) AS x(s,t,n)
 LOOP EXECUTE format('DROP POLICY IF EXISTS %I ON %I.%I',r.n,r.s,r.t); END LOOP;
END $m10_drop_policies$;
DO $m10_rls$
DECLARE r record;
BEGIN
 FOR r IN SELECT * FROM (VALUES
  ('control','host_binding'),('control','replication_profile'),('control','replication_distribution_binding'),
  ('telemetry','host_metric_snapshot_v2'),('telemetry','replication_snapshot_v2'),
  ('analytics','metric_rollup_v2'),('analytics','evidence_packet_v2'),('analytics','metric_baseline'),
  ('analytics','metric_forecast'),('analytics','incident_thread'),
  ('control','analytics_replay')) AS x(schema_name,table_name)
 LOOP
  EXECUTE format('ALTER TABLE %I.%I ENABLE ROW LEVEL SECURITY',r.schema_name,r.table_name);
  EXECUTE format('ALTER TABLE %I.%I FORCE ROW LEVEL SECURITY',r.schema_name,r.table_name);
  EXECUTE format('CREATE POLICY %I ON %I.%I USING (instance_id IS NOT NULL AND instance_id::text=current_setting(''sqlobserver.target_scope'',true)) WITH CHECK (instance_id IS NOT NULL AND instance_id::text=current_setting(''sqlobserver.target_scope'',true))', 'm10_'||r.table_name||'_scope',r.schema_name,r.table_name);
  EXECUTE format('CREATE POLICY %I ON %I.%I FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true)', 'm10_'||r.table_name||'_owner',r.schema_name,r.table_name);
 END LOOP;
END $m10_rls$;
ALTER TABLE control.host_profile ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.host_profile FORCE ROW LEVEL SECURITY;
CREATE POLICY m10_host_profile_scope ON control.host_profile USING (instance_id::text=current_setting('sqlobserver.target_scope',true)) WITH CHECK (instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY m10_host_profile_owner ON control.host_profile FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
ALTER TABLE analytics.incident_generation ENABLE ROW LEVEL SECURITY;
ALTER TABLE analytics.incident_generation FORCE ROW LEVEL SECURITY;
CREATE POLICY m10_incident_generation_scope ON analytics.incident_generation USING (instance_id::text=current_setting('sqlobserver.target_scope',true) AND EXISTS (SELECT 1 FROM analytics.incident_thread t WHERE t.thread_id=incident_generation.thread_id AND t.instance_id=incident_generation.instance_id AND t.target_revision=incident_generation.target_revision)) WITH CHECK (instance_id::text=current_setting('sqlobserver.target_scope',true) AND EXISTS (SELECT 1 FROM analytics.incident_thread t WHERE t.thread_id=incident_generation.thread_id AND t.instance_id=incident_generation.instance_id AND t.target_revision=incident_generation.target_revision));
CREATE POLICY m10_incident_generation_owner ON analytics.incident_generation FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
ALTER TABLE control.analytics_watermark ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.analytics_watermark FORCE ROW LEVEL SECURITY;
CREATE POLICY m10_analytics_watermark_scope ON control.analytics_watermark USING (instance_id IS NOT NULL AND instance_id::text=current_setting('sqlobserver.target_scope',true)) WITH CHECK (instance_id IS NOT NULL AND instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY m10_analytics_watermark_owner ON control.analytics_watermark FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);

-- Fixed, bounded collector commits.  JSON is used as a transport envelope;
-- only allowlisted fields are persisted by the host/replication adapters.
CREATE TABLE IF NOT EXISTS telemetry.m10_commit_replay
(
 run_id uuid NOT NULL, instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL, collector_id text NOT NULL, collector_version integer NOT NULL,
 request_digest bytea NOT NULL, payload_digest bytea NOT NULL, committed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY(run_id,instance_id,target_revision),
 CONSTRAINT fk_m10_commit_replay_target_revision FOREIGN KEY (instance_id,target_revision)
   REFERENCES control.observation_target(instance_id,revision),
 CHECK(collector_id IN ('host.metrics','replication.health') AND collector_version=1 AND octet_length(request_digest)=32 AND octet_length(payload_digest)=32 AND target_revision>0)
);
ALTER TABLE telemetry.m10_commit_replay ENABLE ROW LEVEL SECURITY;
ALTER TABLE telemetry.m10_commit_replay FORCE ROW LEVEL SECURITY;
CREATE POLICY m10_commit_replay_scope ON telemetry.m10_commit_replay USING(instance_id::text=current_setting('sqlobserver.target_scope',true)) WITH CHECK(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY m10_commit_replay_owner ON telemetry.m10_commit_replay FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE TRIGGER m10_commit_replay_append_only BEFORE UPDATE OR DELETE ON telemetry.m10_commit_replay FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE INDEX IF NOT EXISTS ix_m10_replay_target ON telemetry.m10_commit_replay(instance_id,committed_at DESC);

CREATE OR REPLACE FUNCTION telemetry.commit_m10_host_metrics
 (p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_payload jsonb,p_completion_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,telemetry,control SET TimeZone='UTC'
AS $m10_host_commit$
DECLARE existing telemetry.m10_commit_replay%ROWTYPE; computed_payload_digest bytea; n integer; now_utc timestamptz:=clock_timestamp();
BEGIN
 IF p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0
    OR octet_length(p_request_digest)<>32 OR octet_length(p_completion_digest)<>32
    OR jsonb_typeof(p_payload)<>'object' OR jsonb_typeof(p_payload->'items')<>'array'
    OR jsonb_array_length(p_payload->'items')>256 OR octet_length(p_payload::text)>262144
    OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision
    OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1 OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text
    OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision
    OR p_payload->>'hostId' IS NULL OR p_payload->>'hostFingerprint' !~ '^[0-9a-fA-F]{64}$'
    OR (p_payload->>'bindingRevision')::bigint IS NULL OR (p_payload->>'profileRevision')::bigint IS NULL
 THEN RAISE EXCEPTION 'M10 host commit bounds or target scope rejected' USING ERRCODE='22023'; END IF;
 IF NOT EXISTS (
   SELECT 1 FROM control.host_binding b
   WHERE b.instance_id=p_instance_id AND b.target_revision=p_target_revision
     AND b.host_id=(p_payload->>'hostId')::uuid
     AND b.binding_revision=(p_payload->>'bindingRevision')::bigint
     AND b.identity_fingerprint=decode(p_payload->>'hostFingerprint','hex')
     AND b.binding_state='active'
 ) OR NOT EXISTS (
   SELECT 1 FROM control.host_profile h
   WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision
     AND h.host_id=(p_payload->>'hostId')::uuid
     AND h.binding_revision=(p_payload->>'bindingRevision')::bigint
     AND h.profile_revision=(p_payload->>'profileRevision')::bigint
 ) THEN RAISE EXCEPTION 'M10 host binding/profile revision is not current' USING ERRCODE='40001'; END IF;
 IF EXISTS (
   SELECT 1 FROM jsonb_array_elements(p_payload->'items') x
   WHERE NOT EXISTS (
     SELECT 1 FROM analytics.metric_catalog c
     WHERE c.metric_key=x->>'metricKey' AND c.source_kind='host' AND c.enabled
       AND NOT EXISTS (SELECT 1 FROM jsonb_object_keys(coalesce(x->'dimensions','{}'::jsonb)) k WHERE NOT (c.definition->'dimensions' ? k))
   )
 ) THEN RAISE EXCEPTION 'M10 host metric is not enabled or dimensions are not catalog-v1 allowlisted' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 computed_payload_digest:=sha256(convert_to(p_payload::text,'UTF8'));
 INSERT INTO telemetry.m10_commit_replay(run_id,instance_id,target_revision,collector_id,collector_version,request_digest,payload_digest)
 VALUES(p_run_id,p_instance_id,p_target_revision,'host.metrics',1,p_request_digest,computed_payload_digest)
 ON CONFLICT(run_id,instance_id,target_revision) DO NOTHING;
 IF NOT FOUND THEN
   SELECT * INTO existing FROM telemetry.m10_commit_replay WHERE run_id=p_run_id AND instance_id=p_instance_id AND target_revision=p_target_revision;
   IF existing.request_digest<>p_request_digest OR existing.payload_digest<>computed_payload_digest THEN RAISE EXCEPTION 'M10 divergent replay digest' USING ERRCODE='40001'; END IF;
   RETURN QUERY SELECT 'replayed',0,0,existing.committed_at; RETURN;
 END IF;
 n:=0;
 INSERT INTO telemetry.host_metric_snapshot_v2(observed_at,run_id,instance_id,target_revision,host_id,binding_revision,profile_revision,metric_key,metric_value,dimensions,collected_at)
 SELECT coalesce((x->>'observedAtUtc')::timestamptz,now_utc),p_run_id,p_instance_id,p_target_revision,
        (p_payload->>'hostId')::uuid,(p_payload->>'bindingRevision')::bigint,(p_payload->>'profileRevision')::bigint,
        x->>'metricKey',(x->>'value')::double precision,coalesce(x->'dimensions','{}'::jsonb),now_utc
 FROM jsonb_array_elements(p_payload->'items') x;
 GET DIAGNOSTICS n=ROW_COUNT;
 RETURN QUERY SELECT 'committed',n,0,now_utc;
END $m10_host_commit$;

CREATE OR REPLACE FUNCTION telemetry.commit_m10_replication
 (p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_payload jsonb,p_completion_digest bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,telemetry,control SET TimeZone='UTC'
AS $m10_replication_commit$
DECLARE existing telemetry.m10_commit_replay%ROWTYPE; computed_payload_digest bytea; n integer; now_utc timestamptz:=clock_timestamp();
BEGIN
 IF p_run_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_completion_digest)<>32 OR jsonb_typeof(p_payload)<>'object' OR jsonb_typeof(p_payload->'items')<>'array' OR jsonb_array_length(p_payload->'items')>2048 OR octet_length(p_payload::text)>2097152 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision OR (p_payload->>'schemaVersion')::integer IS DISTINCT FROM 1 OR p_payload->>'targetId' IS DISTINCT FROM p_instance_id::text OR (p_payload->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 replication commit bounds or target scope rejected' USING ERRCODE='22023'; END IF;
 IF EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'items') x WHERE
     (x->>'pendingCommands') IS NOT NULL AND ((x->>'pendingCommands')::numeric < 0 OR (x->>'pendingCommands')::numeric > 2000000000)
     OR (x->>'latencySeconds') IS NOT NULL AND ((x->>'latencySeconds')::double precision < 0 OR (x->>'latencySeconds')::double precision > 86400)
     OR (x->>'latencyMillis') IS NOT NULL AND ((x->>'latencyMillis')::double precision < 0 OR (x->>'latencyMillis')::double precision > 86400000)
   ) THEN RAISE EXCEPTION 'M10 replication metric mapping bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 computed_payload_digest:=sha256(convert_to(p_payload::text,'UTF8'));
 INSERT INTO telemetry.m10_commit_replay(run_id,instance_id,target_revision,collector_id,collector_version,request_digest,payload_digest) VALUES(p_run_id,p_instance_id,p_target_revision,'replication.health',1,p_request_digest,computed_payload_digest) ON CONFLICT(run_id,instance_id,target_revision) DO NOTHING;
 IF NOT FOUND THEN SELECT * INTO existing FROM telemetry.m10_commit_replay WHERE run_id=p_run_id AND instance_id=p_instance_id AND target_revision=p_target_revision; IF existing.request_digest<>p_request_digest OR existing.payload_digest<>computed_payload_digest THEN RAISE EXCEPTION 'M10 divergent replay digest' USING ERRCODE='40001'; END IF; RETURN QUERY SELECT 'replayed',0,0,existing.committed_at; RETURN; END IF;
 INSERT INTO telemetry.replication_snapshot_v2(observed_at,run_id,instance_id,target_revision,topology_fingerprint,database_fingerprint,role,synchronization_state,send_queue_bytes,redo_queue_bytes,pending_commands,latency_seconds,visibility_scope,state_available,collected_at)
 SELECT coalesce((x->>'observedAtUtc')::timestamptz,now_utc),p_run_id,p_instance_id,p_target_revision,decode(x->>'topologyFingerprint','hex'),decode(nullif(x->>'databaseFingerprint',''),'hex'),x->>'role',x->>'synchronizationState',(x->>'sendQueueBytes')::bigint,(x->>'redoQueueBytes')::bigint,
        coalesce((x->>'pendingCommands')::bigint,(x->>'sendQueueBytes')::bigint),
        coalesce((x->>'latencySeconds')::double precision,(x->>'latencyMillis')::double precision/1000.0),
        coalesce((x->>'visibilityScope')::smallint,3),coalesce((x->>'stateAvailable')::boolean,false),now_utc FROM jsonb_array_elements(p_payload->'items') x
 WHERE x->>'topologyFingerprint' ~ '^[0-9a-fA-F]{64}$';
 GET DIAGNOSTICS n=ROW_COUNT; RETURN QUERY SELECT 'committed',n,0,now_utc;
 IF EXISTS (SELECT 1 FROM jsonb_array_elements(p_payload->'items') x WHERE coalesce((x->>'visibilityScope')::smallint,3)=3 OR x ? 'visibilityGap') THEN
   INSERT INTO telemetry.visibility_gap(gap_id,run_id,instance_id,collector_id,reason_code,gap_started_at,gap_ended_at,lost_row_count,lost_byte_count,count_is_exact,recorded_at,topology_fingerprint,gap_evidence)
   VALUES (left(encode(sha256(convert_to('m10-replication-gap|'||p_run_id::text,'UTF8')),'hex'),32)::uuid,p_run_id,p_instance_id,'replication.health','capability_missing',now_utc,now_utc,greatest(n,1),0,false,now_utc,
     coalesce((SELECT decode(x->>'topologyFingerprint','hex') FROM jsonb_array_elements(p_payload->'items') x WHERE x->>'topologyFingerprint' ~ '^[0-9a-fA-F]{64}' LIMIT 1),sha256(convert_to('m10-replication-gap|'||p_instance_id::text||'|'||p_target_revision::text||'|'||p_run_id::text,'UTF8'))),
     jsonb_build_object('kind','visibility_gap','reason','distribution_database_unbound','targetRevision',p_target_revision))
   ON CONFLICT(run_id) DO NOTHING;
 END IF;
END $m10_replication_commit$;
REVOKE ALL ON FUNCTION telemetry.commit_m10_host_metrics(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),telemetry.commit_m10_replication(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION telemetry.commit_m10_host_metrics(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),telemetry.commit_m10_replication(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;
CREATE OR REPLACE FUNCTION telemetry.commit_host_metrics(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,telemetry,control AS $$ SELECT * FROM telemetry.commit_m10_host_metrics($1,$2,$3,$4,$5,$6,$7,$8,$9) $$;
CREATE OR REPLACE FUNCTION telemetry.commit_replication_health(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,telemetry,control AS $$ SELECT * FROM telemetry.commit_m10_replication($1,$2,$3,$4,$5,$6,$7,$8,$9) $$;
REVOKE ALL ON FUNCTION telemetry.commit_host_metrics(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),telemetry.commit_replication_health(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION telemetry.commit_host_metrics(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),telemetry.commit_replication_health(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;

CREATE OR REPLACE FUNCTION reporting.list_m10_host_metrics(p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF telemetry.host_metric_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 SELECT h.* FROM telemetry.host_metric_snapshot_v2 h WHERE h.instance_id=p_instance_id AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY h.observed_at,h.run_id,h.metric_key LIMIT least(greatest(p_limit,1),201)
$$;
CREATE OR REPLACE FUNCTION reporting.list_m10_replication(p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF telemetry.replication_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 SELECT r.* FROM telemetry.replication_snapshot_v2 r WHERE r.instance_id=p_instance_id AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY r.observed_at,r.run_id LIMIT least(greatest(p_limit,1),201)
$$;
REVOKE ALL ON FUNCTION reporting.list_m10_host_metrics(uuid,timestamptz,timestamptz,integer,timestamptz),reporting.list_m10_replication(uuid,timestamptz,timestamptz,integer,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE OR REPLACE FUNCTION reporting.list_host_metrics(uuid,timestamptz,timestamptz,integer,timestamptz) RETURNS SETOF telemetry.host_metric_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control AS $$ SELECT * FROM reporting.list_m10_host_metrics($1,$2,$3,$4,$5) $$;
CREATE OR REPLACE FUNCTION reporting.list_replication_health(uuid,timestamptz,timestamptz,integer,timestamptz) RETURNS SETOF telemetry.replication_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control AS $$ SELECT * FROM reporting.list_m10_replication($1,$2,$3,$4,$5) $$;
REVOKE ALL ON FUNCTION reporting.list_host_metrics(uuid,timestamptz,timestamptz,integer,timestamptz),reporting.list_replication_health(uuid,timestamptz,timestamptz,integer,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION analytics.compare_metric_windows(p_instance_id uuid,p_metric_key text,p_left_start timestamptz,p_left_end timestamptz,p_right_start timestamptz,p_right_end timestamptz,p_snapshot_utc timestamptz)
RETURNS TABLE(left_value double precision,right_value double precision,delta double precision,left_samples bigint,right_samples bigint,visibility_state text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $$
 SELECT avg(r.value) FILTER (WHERE r.bucket_start>=p_left_start AND r.bucket_start<p_left_end),avg(r.value) FILTER (WHERE r.bucket_start>=p_right_start AND r.bucket_start<p_right_end),avg(r.value) FILTER (WHERE r.bucket_start>=p_right_start AND r.bucket_start<p_right_end)-avg(r.value) FILTER (WHERE r.bucket_start>=p_left_start AND r.bucket_start<p_left_end),sum(r.sample_count) FILTER (WHERE r.bucket_start>=p_left_start AND r.bucket_start<p_left_end),sum(r.sample_count) FILTER (WHERE r.bucket_start>=p_right_start AND r.bucket_start<p_right_end),CASE WHEN bool_and(r.visibility_state='complete') THEN 'complete' ELSE 'partial' END FROM analytics.metric_rollup_v2 r WHERE r.instance_id=p_instance_id AND r.metric_key=p_metric_key AND r.aggregation='avg' AND r.bucket_start<=p_snapshot_utc AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND p_left_end>p_left_start AND p_right_end>p_right_start;
$$;
CREATE OR REPLACE FUNCTION reporting.get_storage_forecast(p_instance_id uuid,p_metric_key text,p_horizon interval,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.metric_forecast LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT f.* FROM analytics.metric_forecast f WHERE f.instance_id=p_instance_id AND f.metric_key=p_metric_key AND f.horizon_start<=p_snapshot_utc+p_horizon AND f.horizon_end>p_snapshot_utc AND p_horizon BETWEEN interval '1 hour' AND interval '366 days' AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY f.horizon_start LIMIT 201;
$$;
CREATE OR REPLACE FUNCTION reporting.get_storage_forecast(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_horizon interval,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.metric_forecast LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT f.* FROM analytics.metric_forecast f WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND f.instance_id=p_instance_id AND f.target_revision=p_target_revision AND f.metric_key=p_metric_key AND f.horizon_start<=p_snapshot_utc+p_horizon AND f.horizon_end>p_snapshot_utc AND p_horizon BETWEEN interval '1 hour' AND interval '366 days' AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY f.horizon_start LIMIT 201;
$$;
CREATE OR REPLACE FUNCTION reporting.get_incident_evidence(p_instance_id uuid,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.evidence_packet_v2 LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT e.* FROM analytics.evidence_packet_v2 e JOIN analytics.incident_generation g ON g.evidence_packet_id=e.packet_id JOIN analytics.incident_thread t ON t.thread_id=g.thread_id WHERE e.instance_id=p_instance_id AND t.thread_id=p_thread_id AND e.occurred_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 200 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY e.occurred_at,e.packet_id LIMIT p_limit+1;
$$;
REVOKE ALL ON FUNCTION analytics.compare_metric_windows(uuid,text,timestamptz,timestamptz,timestamptz,timestamptz,timestamptz),reporting.get_storage_forecast(uuid,text,interval,timestamptz),reporting.get_incident_evidence(uuid,uuid,integer,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION reporting.get_storage_forecast(uuid,bigint,text,interval,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_storage_forecast(uuid,bigint,text,interval,timestamptz) TO sqlobserver_server;

-- Immutable history and explicit retention safety state.
CREATE TABLE IF NOT EXISTS system.retention_policy_history
(
 history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, data_class text NOT NULL,
 enabled boolean NOT NULL, retain_for interval, changed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 changed_by text NOT NULL DEFAULT session_user, change_reason text NOT NULL,
 CHECK(retain_for IS NULL OR retain_for>=interval '1 day')
);
CREATE TABLE IF NOT EXISTS system.recovery_attestation
(
 attestation_id uuid PRIMARY KEY, attested_at timestamptz NOT NULL, attested_by text NOT NULL,
 backup_set_reference text NOT NULL, restore_tested_at timestamptz, expires_at timestamptz NOT NULL,
 attestation_digest bytea NOT NULL, valid boolean NOT NULL DEFAULT true,
 CHECK(octet_length(attestation_digest)=32 AND expires_at>attested_at)
 );
 ALTER TABLE system.recovery_attestation ADD COLUMN IF NOT EXISTS actor_sid text;
 ALTER TABLE system.recovery_attestation ADD COLUMN IF NOT EXISTS correlation_id uuid;
 ALTER TABLE system.recovery_attestation ADD COLUMN IF NOT EXISTS change_reason text;
 ALTER TABLE system.recovery_attestation ADD COLUMN IF NOT EXISTS request_digest bytea;
CREATE TABLE IF NOT EXISTS system.retention_execution
(
 execution_id uuid PRIMARY KEY, data_class text NOT NULL, parent_schema name NOT NULL, parent_table name NOT NULL, partition_name name NOT NULL,
 range_start timestamptz NOT NULL, range_end timestamptz NOT NULL, state text NOT NULL,
 policy_revision bigint NOT NULL DEFAULT 1, registry_revision bytea NOT NULL DEFAULT sha256(convert_to('','UTF8')),
 previewed_at timestamptz NOT NULL DEFAULT clock_timestamp(), detached_at timestamptz, drop_after timestamptz,
 dropped_at timestamptz, reader_lease_count integer NOT NULL DEFAULT 0, attempt integer NOT NULL DEFAULT 0,
 last_error text, CHECK(state IN ('previewed','blocked','detached','grace','dropped','failed','retry')),
 CHECK(range_end>range_start AND reader_lease_count>=0 AND attempt BETWEEN 0 AND 20 AND policy_revision>0 AND octet_length(registry_revision)=32)
);
CREATE INDEX IF NOT EXISTS ix_retention_execution_retry ON system.retention_execution(state,drop_after);
CREATE TABLE IF NOT EXISTS system.retention_backfill_state
(
 parent_schema name NOT NULL, parent_table name NOT NULL, partition_name name NOT NULL,
 range_start timestamptz NOT NULL, range_end timestamptz NOT NULL, cursor_key text,
 cursor_observed_at timestamptz, cursor_tie_breaker text,
 rows_copied bigint NOT NULL DEFAULT 0, bytes_copied bigint NOT NULL DEFAULT 0,
 state text NOT NULL DEFAULT 'pending' CHECK(state IN ('pending','running','complete','blocked','failed')),
 updated_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY(parent_schema,parent_table,partition_name),
 CHECK(rows_copied>=0 AND bytes_copied>=0)
);
ALTER TABLE system.retention_backfill_state ADD COLUMN IF NOT EXISTS cursor_observed_at timestamptz;
ALTER TABLE system.retention_backfill_state ADD COLUMN IF NOT EXISTS cursor_tie_breaker text;
CREATE TABLE IF NOT EXISTS system.partition_reader_lease
(
 lease_id uuid PRIMARY KEY, parent_schema name NOT NULL, parent_table name NOT NULL, partition_name name NOT NULL,
 owner_execution_id uuid NOT NULL, acquired_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 expires_at timestamptz NOT NULL, released_at timestamptz, CHECK(expires_at>acquired_at)
);
CREATE INDEX IF NOT EXISTS ix_partition_reader_lease_active ON system.partition_reader_lease(parent_schema,parent_table,partition_name,expires_at) WHERE released_at IS NULL;
CREATE TABLE IF NOT EXISTS system.retention_drop_retry
(
 retry_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, execution_id uuid NOT NULL REFERENCES system.retention_execution(execution_id),
 attempted_at timestamptz NOT NULL DEFAULT clock_timestamp(), error_code text NOT NULL, error_detail text NOT NULL, next_attempt_at timestamptz NOT NULL
);
DROP TRIGGER IF EXISTS m10_policy_history_append_only ON system.retention_policy_history;
DROP TRIGGER IF EXISTS m10_recovery_append_only ON system.recovery_attestation;
DROP TRIGGER IF EXISTS m10_drop_retry_append_only ON system.retention_drop_retry;
CREATE TRIGGER m10_policy_history_append_only BEFORE UPDATE OR DELETE ON system.retention_policy_history FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_recovery_append_only BEFORE UPDATE OR DELETE ON system.recovery_attestation FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE TRIGGER m10_drop_retry_append_only BEFORE UPDATE OR DELETE ON system.retention_drop_retry FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
CREATE OR REPLACE FUNCTION system.capture_m10_retention_policy_change() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,system AS $policy_history$
BEGIN
 INSERT INTO system.retention_policy_history(data_class,enabled,retain_for,changed_by,change_reason)
 VALUES(NEW.data_class,NEW.enabled,NEW.retain_for,coalesce(nullif(current_setting('sqlobserver.retention_changed_by',true),''),session_user),coalesce(nullif(current_setting('sqlobserver.retention_change_reason',true),''),CASE WHEN TG_OP='INSERT' THEN 'policy_created' ELSE 'policy_updated' END));
 RETURN NEW;
END $policy_history$;
DROP TRIGGER IF EXISTS m10_retention_policy_history ON system.retention_policy;
CREATE TRIGGER m10_retention_policy_history AFTER INSERT OR UPDATE ON system.retention_policy FOR EACH ROW EXECUTE FUNCTION system.capture_m10_retention_policy_change();
REVOKE ALL ON FUNCTION system.capture_m10_retention_policy_change() FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- Extend the authoritative registry allowlist.  Existing run/outcome/gap
-- envelopes remain unpartitioned; only evidence streams are registered here.
ALTER TABLE system.partition_registry DROP CONSTRAINT IF EXISTS ck_partition_registry_parent;
ALTER TABLE system.partition_registry ADD CONSTRAINT ck_partition_registry_parent CHECK
 (parent_schema IN ('telemetry'::name,'events'::name,'analytics'::name,'alerting'::name) AND parent_table IN
 ('raw_metric_sample','diagnostic_event','activity_session_snapshot','activity_request_snapshot','server_wait_snapshot','blocking_edge',
  'database_inventory_snapshot','database_file_snapshot','host_metric_snapshot_v2','replication_snapshot_v2',
  'backup_status_snapshot','sql_agent_failure_scan_snapshot','sql_agent_failure_occurrence','tempdb_snapshot','tempdb_file_snapshot',
  'availability_group_replica_snapshot','availability_group_database_snapshot','query_performance_observation',
  'deadlock_summary','deadlock_participant','deadlock_relation','state_history','delivery_attempt','delivery_outbox',
  'metric_rollup_v2','evidence_packet_v2',
  'activity_session_snapshot_v2','activity_request_snapshot_v2','server_wait_snapshot_v2','blocking_edge_v2',
  'database_inventory_snapshot_v2','database_file_snapshot_v2','query_performance_observation_v2',
  'deadlock_summary_v2','deadlock_participant_v2','deadlock_relation_v2','state_history_v2','delivery_attempt_v2','delivery_outbox_v2',
  'backup_status_snapshot_v2','sql_agent_failure_scan_snapshot_v2','sql_agent_failure_occurrence_v2','tempdb_snapshot_v2','tempdb_file_snapshot_v2',
  'availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2'));
ALTER TABLE system.retention_policy DROP CONSTRAINT IF EXISTS ck_retention_policy_data_class;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_data_class CHECK (data_class IN
 ('raw_metric_samples','diagnostic_events','m5_activity','m5_blocking','m6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery',
  'm9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences','m9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases',
  'm10_host_metrics','m10_replication','m10_rollups','m10_evidence'));
ALTER TABLE system.retention_policy DROP CONSTRAINT IF EXISTS ck_retention_policy_parent;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_parent CHECK
 (partition_granularity IN ('day','month','none') AND ((data_class='diagnostic_events' AND parent_schema='events' AND parent_table='diagnostic_event') OR (data_class IN ('raw_metric_samples','m5_activity','m5_blocking','m6_deadlocks','m7_query_performance','m8_alert_history','m8_alert_delivery','m9_backup_status','m9_agent_history','m9_agent_failures','m9_agent_occurrences','m9_tempdb','m9_tempdb_files','m9_ag_replicas','m9_ag_databases','m10_host_metrics','m10_replication','m10_rollups','m10_evidence'))));
INSERT INTO system.retention_policy(data_class,parent_schema,parent_table,partition_granularity,enabled,retain_for,minimum_partitions_to_keep)
VALUES
 ('m5_activity','telemetry','activity_session_snapshot_v2','day',false,NULL,3),('m5_blocking','events','blocking_edge_v2','month',false,NULL,3),
 ('m6_deadlocks','events','deadlock_summary_v2','month',false,NULL,3),('m7_query_performance','events','query_performance_observation_v2','day',false,NULL,3),
 ('m8_alert_history','alerting','state_history_v2','day',false,NULL,3),('m8_alert_delivery','alerting','delivery_outbox_v2','day',false,NULL,3),
 ('m10_host_metrics','telemetry','host_metric_snapshot_v2','day',false,NULL,3),('m10_replication','telemetry','replication_snapshot_v2','day',false,NULL,3),
 ('m10_rollups','analytics','metric_rollup_v2','day',false,NULL,3),('m10_evidence','analytics','evidence_packet_v2','month',false,NULL,3)
ON CONFLICT(data_class) DO UPDATE SET enabled=false,retain_for=NULL,updated_at=clock_timestamp()
 WHERE system.retention_policy.enabled OR system.retention_policy.retain_for IS NOT NULL;

-- Fixed allowlisted partition set.  Daily D-1..D+7 and monthly current+2
-- are created and registered in one short catalog operation.  Legacy
-- unpartitioned classes get a LIKE-based v2 parent first; LIKE intentionally
-- excludes unique constraints whose keys do not contain the time boundary.
DO $m10_v2_parents$
DECLARE s text; source_schema text; source_table text; dest_schema text; dest_table text; key_column text;
BEGIN
 FOREACH s IN ARRAY ARRAY[
  'telemetry:activity_session_snapshot:activity_session_snapshot_v2:observed_at',
  'telemetry:activity_request_snapshot:activity_request_snapshot_v2:observed_at',
  'telemetry:server_wait_snapshot:server_wait_snapshot_v2:observed_at',
  'events:blocking_edge:blocking_edge_v2:observed_at',
  'telemetry:database_inventory_snapshot:database_inventory_snapshot_v2:observed_at',
  'telemetry:database_file_snapshot:database_file_snapshot_v2:observed_at',
  'events:query_performance_observation:query_performance_observation_v2:observed_at',
  'events:deadlock_summary:deadlock_summary_v2:occurred_at',
  'events:deadlock_participant:deadlock_participant_v2:occurred_at',
  'events:deadlock_relation:deadlock_relation_v2:occurred_at',
  'alerting:state_history:state_history_v2:observed_at',
  'alerting:delivery_attempt:delivery_attempt_v2:attempted_at',
  'alerting:delivery_outbox:delivery_outbox_v2:due_at',
  'telemetry:backup_status_snapshot:backup_status_snapshot_v2:observed_at',
  'telemetry:sql_agent_failure_scan_snapshot:sql_agent_failure_scan_snapshot_v2:observed_at',
  'telemetry:sql_agent_failure_occurrence:sql_agent_failure_occurrence_v2:collected_at',
  'telemetry:tempdb_snapshot:tempdb_snapshot_v2:observed_at',
  'telemetry:tempdb_file_snapshot:tempdb_file_snapshot_v2:observed_at',
  'telemetry:availability_group_replica_snapshot:availability_group_replica_snapshot_v2:observed_at',
  'telemetry:availability_group_database_snapshot:availability_group_database_snapshot_v2:observed_at'
 ] LOOP
  source_schema:=split_part(s,':',1); source_table:=split_part(s,':',2); dest_schema:=source_schema; dest_table:=split_part(s,':',3); key_column:=split_part(s,':',4);
  EXECUTE format('CREATE TABLE IF NOT EXISTS %I.%I (LIKE %I.%I INCLUDING DEFAULTS INCLUDING STORAGE) PARTITION BY RANGE (%I)',dest_schema,dest_table,source_schema,source_table,key_column);
 END LOOP;
END $m10_v2_parents$;

-- Fixed allowlisted partition set.  The daily list is deliberately v2-only:
-- new writes and bounded backfills target these parents, while old names stay
-- registered for compatibility with the M2--M9 maintenance functions.
CREATE OR REPLACE FUNCTION control.ensure_m10_partition_set(p_anchor date DEFAULT (current_date)) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,events,analytics,system SET TimeZone='UTC'
AS $ensure_m10$
DECLARE d date; m date; spec text; parent_name text; col_name text; n integer:=0;
 daily text[]:=ARRAY['activity_session_snapshot_v2:telemetry:observed_at','activity_request_snapshot_v2:telemetry:observed_at','server_wait_snapshot_v2:telemetry:observed_at','blocking_edge_v2:events:observed_at','database_inventory_snapshot_v2:telemetry:observed_at','database_file_snapshot_v2:telemetry:observed_at','host_metric_snapshot_v2:telemetry:observed_at','replication_snapshot_v2:telemetry:observed_at','backup_status_snapshot_v2:telemetry:observed_at','sql_agent_failure_scan_snapshot_v2:telemetry:observed_at','sql_agent_failure_occurrence_v2:telemetry:collected_at','tempdb_snapshot_v2:telemetry:observed_at','tempdb_file_snapshot_v2:telemetry:observed_at','availability_group_replica_snapshot_v2:telemetry:observed_at','availability_group_database_snapshot_v2:telemetry:observed_at','query_performance_observation_v2:events:observed_at','metric_rollup_v2:analytics:bucket_start']; monthly text[]:=ARRAY['diagnostic_event:events:occurred_at','deadlock_summary_v2:events:occurred_at','deadlock_participant_v2:events:occurred_at','deadlock_relation_v2:events:occurred_at','evidence_packet_v2:analytics:occurred_at','state_history_v2:alerting:observed_at','delivery_attempt_v2:alerting:attempted_at','delivery_outbox_v2:alerting:due_at'];
BEGIN
 IF p_anchor IS NULL OR p_anchor<current_date-1 OR p_anchor>current_date+1 THEN RAISE EXCEPTION 'M10 anchor outside UTC maintenance window' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('sqlobserver:m10:partition-set',0));
 FOREACH spec IN ARRAY daily LOOP parent_name:=split_part(spec,':',1); col_name:=split_part(spec,':',3); FOR d IN SELECT p_anchor+i FROM generate_series(-1,7) i LOOP EXECUTE format('CREATE TABLE IF NOT EXISTS %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',split_part(spec,':',2),format('%s_p%s',parent_name,to_char(d,'YYYYMMDD')),split_part(spec,':',2),parent_name,d::timestamptz,(d+1)::timestamptz); INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end) VALUES(split_part(spec,':',2),parent_name,split_part(spec,':',2),format('%s_p%s',parent_name,to_char(d,'YYYYMMDD')),'day',d::timestamptz,(d+1)::timestamptz) ON CONFLICT DO NOTHING; n:=n+1; END LOOP; END LOOP;
 m:=date_trunc('month',p_anchor)::date; FOREACH spec IN ARRAY monthly LOOP parent_name:=split_part(spec,':',1); col_name:=split_part(spec,':',3); FOR d IN SELECT (m + (i||' month')::interval)::date FROM generate_series(0,2) i LOOP EXECUTE format('CREATE TABLE IF NOT EXISTS %I.%I PARTITION OF %I.%I FOR VALUES FROM (%L) TO (%L)',split_part(spec,':',2),format('%s_p%s',parent_name,to_char(d,'YYYYMM')),split_part(spec,':',2),parent_name,d::timestamptz,(d+interval '1 month')::timestamptz); INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end) VALUES(split_part(spec,':',2),parent_name,split_part(spec,':',2),format('%s_p%s',parent_name,to_char(d,'YYYYMM')),'month',d::timestamptz,(d+interval '1 month')::timestamptz) ON CONFLICT DO NOTHING; n:=n+1; END LOOP; END LOOP; RETURN n;
END $ensure_m10$;
REVOKE ALL ON FUNCTION control.ensure_m10_partition_set(date) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.ensure_m10_partition_set(date) TO sqlobserver_collector;
SELECT control.ensure_m10_partition_set(current_date);

-- Compatibility projections make the v2 storage transition observable to
-- readers while the resumable backfill advances one UTC day at a time.
CREATE OR REPLACE VIEW reporting.m10_host_metric_compat AS SELECT * FROM telemetry.host_metric_snapshot_v2;
CREATE OR REPLACE VIEW reporting.m10_replication_compat AS SELECT * FROM telemetry.replication_snapshot_v2;
CREATE OR REPLACE VIEW reporting.m10_rollup_compat AS SELECT * FROM analytics.metric_rollup_v2;
CREATE OR REPLACE VIEW reporting.m10_evidence_compat AS SELECT * FROM analytics.evidence_packet_v2;
REVOKE ALL ON reporting.m10_host_metric_compat,reporting.m10_replication_compat,reporting.m10_rollup_compat,reporting.m10_evidence_compat FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION control.run_m10_backfill(p_parent_schema name,p_parent_table name,p_day date,p_max_rows integer DEFAULT 100000,p_max_bytes integer DEFAULT 8388608)
RETURNS TABLE(rows_copied integer,bytes_copied integer,completed boolean,next_cursor text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,control,telemetry,events,analytics,system SET TimeZone='UTC'
AS $backfill$
DECLARE v_rows integer:=0; v_bytes integer:=0; dest text; source text; ts_col text; source_rel regclass; destination_rel regclass; next_cursor text; state_cursor_ts timestamptz; state_cursor_key text; candidate_ts timestamptz; candidate_key text; has_more boolean;
BEGIN
 IF p_parent_schema||'.'||p_parent_table NOT IN ('telemetry.activity_session_snapshot_v2','telemetry.activity_request_snapshot_v2','telemetry.server_wait_snapshot_v2','events.blocking_edge_v2','telemetry.database_inventory_snapshot_v2','telemetry.database_file_snapshot_v2','events.query_performance_observation_v2','events.deadlock_summary_v2','events.deadlock_participant_v2','events.deadlock_relation_v2','alerting.state_history_v2','alerting.delivery_attempt_v2','alerting.delivery_outbox_v2','telemetry.backup_status_snapshot_v2','telemetry.sql_agent_failure_scan_snapshot_v2','telemetry.sql_agent_failure_occurrence_v2','telemetry.tempdb_snapshot_v2','telemetry.tempdb_file_snapshot_v2','telemetry.availability_group_replica_snapshot_v2','telemetry.availability_group_database_snapshot_v2','telemetry.host_metric_snapshot_v2','telemetry.replication_snapshot_v2','analytics.metric_rollup_v2','analytics.evidence_packet_v2') THEN RAISE EXCEPTION 'M10 backfill parent is not allowlisted' USING ERRCODE='42501'; END IF;
 IF p_day IS NULL OR p_max_rows NOT BETWEEN 1 AND 100000 OR p_max_bytes NOT BETWEEN 1 AND 8388608 THEN RAISE EXCEPTION 'M10 backfill fence bounds rejected' USING ERRCODE='22023'; END IF;
 -- Source relations are an explicit reviewed map.  Never derive a source by
 -- stripping `_v2`: several of the compatibility views intentionally point
 -- at the destination parent and would otherwise self-copy forever.
 source:=CASE p_parent_schema||'.'||p_parent_table
   WHEN 'telemetry.activity_session_snapshot_v2' THEN 'telemetry.activity_session_snapshot'
   WHEN 'telemetry.activity_request_snapshot_v2' THEN 'telemetry.activity_request_snapshot'
   WHEN 'telemetry.server_wait_snapshot_v2' THEN 'telemetry.server_wait_snapshot'
   WHEN 'events.blocking_edge_v2' THEN 'events.blocking_edge'
   WHEN 'telemetry.database_inventory_snapshot_v2' THEN 'telemetry.database_inventory_snapshot'
   WHEN 'telemetry.database_file_snapshot_v2' THEN 'telemetry.database_file_snapshot'
   WHEN 'events.query_performance_observation_v2' THEN 'events.query_performance_observation'
   WHEN 'events.deadlock_summary_v2' THEN 'events.deadlock_summary'
   WHEN 'events.deadlock_participant_v2' THEN 'events.deadlock_participant'
   WHEN 'events.deadlock_relation_v2' THEN 'events.deadlock_relation'
   WHEN 'alerting.state_history_v2' THEN 'alerting.state_history'
   WHEN 'alerting.delivery_attempt_v2' THEN 'alerting.delivery_attempt'
   WHEN 'alerting.delivery_outbox_v2' THEN 'alerting.delivery_outbox'
   WHEN 'telemetry.backup_status_snapshot_v2' THEN 'telemetry.backup_status_snapshot'
   WHEN 'telemetry.sql_agent_failure_scan_snapshot_v2' THEN 'telemetry.sql_agent_failure_scan_snapshot'
   WHEN 'telemetry.sql_agent_failure_occurrence_v2' THEN 'telemetry.sql_agent_failure_occurrence'
   WHEN 'telemetry.tempdb_snapshot_v2' THEN 'telemetry.tempdb_snapshot'
   WHEN 'telemetry.tempdb_file_snapshot_v2' THEN 'telemetry.tempdb_file_snapshot'
   WHEN 'telemetry.availability_group_replica_snapshot_v2' THEN 'telemetry.availability_group_replica_snapshot'
   WHEN 'telemetry.availability_group_database_snapshot_v2' THEN 'telemetry.availability_group_database_snapshot'
   ELSE NULL END;
 destination_rel:=to_regclass(format('%I.%I',p_parent_schema,p_parent_table));
 source_rel:=CASE WHEN source IS NULL THEN NULL::regclass ELSE to_regclass(source) END;
 SELECT cursor_observed_at,cursor_tie_breaker INTO state_cursor_ts,state_cursor_key FROM system.retention_backfill_state WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table AND partition_name=format('%s_p%s',p_parent_table,to_char(p_day,'YYYYMMDD')) FOR UPDATE;
 -- A missing explicit source is a valid no-op for newly introduced M10
 -- parents.  Legacy copies are one UTC day, cursor-fenced, and bounded by
 -- both rows and encoded bytes.
 IF source_rel IS NOT NULL AND destination_rel IS NOT NULL THEN
  ts_col:=CASE WHEN p_parent_table IN ('activity_session_snapshot_v2','activity_request_snapshot_v2','server_wait_snapshot_v2','blocking_edge_v2','database_inventory_snapshot_v2','database_file_snapshot_v2','host_metric_snapshot_v2','replication_snapshot_v2','backup_status_snapshot_v2','sql_agent_failure_scan_snapshot_v2','tempdb_snapshot_v2','tempdb_file_snapshot_v2','availability_group_replica_snapshot_v2','availability_group_database_snapshot_v2') THEN 'observed_at' WHEN p_parent_table='sql_agent_failure_occurrence_v2' THEN 'collected_at' WHEN p_parent_table IN ('query_performance_observation_v2','metric_rollup_v2') THEN CASE WHEN p_parent_table='metric_rollup_v2' THEN 'bucket_start' ELSE 'observed_at' END ELSE 'occurred_at' END;
  EXECUTE format('WITH ordered AS (SELECT s,s.%1$I AS cursor_ts,row_to_json(s)::text AS cursor_key,octet_length(row_to_json(s)::text)::integer AS row_bytes FROM %2$s s WHERE s.%1$I >= $1::timestamptz AND s.%1$I < ($1::timestamptz+interval ''1 day'') AND ($4::timestamptz IS NULL OR s.%1$I>$4::timestamptz OR (s.%1$I=$4::timestamptz AND row_to_json(s)::text>$5)) ORDER BY s.%1$I,row_to_json(s)::text), bounded AS (SELECT o.*,sum(o.row_bytes) OVER (ORDER BY o.cursor_ts,o.cursor_key) AS total_bytes,row_number() OVER (ORDER BY o.cursor_ts,o.cursor_key) AS rn FROM ordered o), selected AS (SELECT * FROM bounded WHERE total_bytes<=$3 AND rn<=$2) SELECT count(*)::integer,coalesce(sum(row_bytes),0)::integer,max(cursor_ts),(SELECT cursor_key FROM selected ORDER BY cursor_ts DESC,cursor_key DESC LIMIT 1) FROM selected',ts_col,source_rel) INTO v_rows,v_bytes,candidate_ts,candidate_key USING p_day::timestamptz,p_max_rows,p_max_bytes,state_cursor_ts,state_cursor_key;
  IF v_rows=0 THEN EXECUTE format('SELECT EXISTS(SELECT 1 FROM %2$s s WHERE s.%1$I >= $1::timestamptz AND s.%1$I < ($1::timestamptz+interval ''1 day'') AND ($2::timestamptz IS NULL OR s.%1$I>$2::timestamptz OR (s.%1$I=$2::timestamptz AND row_to_json(s)::text>$3)))',ts_col,source_rel) INTO has_more USING p_day::timestamptz,state_cursor_ts,state_cursor_key; IF has_more THEN RAISE EXCEPTION 'backfill row exceeds byte bound' USING ERRCODE='22023'; END IF; ELSE
   EXECUTE format('WITH ordered AS (SELECT s,s.%1$I AS cursor_ts,row_to_json(s)::text AS cursor_key,octet_length(row_to_json(s)::text)::integer AS row_bytes FROM %2$s s WHERE s.%1$I >= $1::timestamptz AND s.%1$I < ($1::timestamptz+interval ''1 day'') AND ($4::timestamptz IS NULL OR s.%1$I>$4::timestamptz OR (s.%1$I=$4::timestamptz AND row_to_json(s)::text>$5)) ORDER BY s.%1$I,row_to_json(s)::text), bounded AS (SELECT o.*,sum(o.row_bytes) OVER (ORDER BY o.cursor_ts,o.cursor_key) AS total_bytes,row_number() OVER (ORDER BY o.cursor_ts,o.cursor_key) AS rn FROM ordered o) INSERT INTO %3$s SELECT (b.s).* FROM bounded b WHERE b.total_bytes<=$3 AND b.rn<=$2 ON CONFLICT DO NOTHING',ts_col,source_rel,destination_rel) USING p_day::timestamptz,p_max_rows,p_max_bytes,state_cursor_ts,state_cursor_key;
 END IF;
 state_cursor_ts:=candidate_ts; state_cursor_key:=candidate_key;
  IF v_rows>0 THEN EXECUTE format('SELECT EXISTS(SELECT 1 FROM %2$s s WHERE s.%1$I >= $1::timestamptz AND s.%1$I < ($1::timestamptz+interval ''1 day'') AND (s.%1$I>$2::timestamptz OR (s.%1$I=$2::timestamptz AND row_to_json(s)::text>$3)))',ts_col,source_rel) INTO has_more USING p_day::timestamptz,state_cursor_ts,state_cursor_key; END IF;
 END IF;
 next_cursor:=CASE WHEN coalesce(has_more,false) THEN format('%s|rows=%s|bytes=%s',p_day,v_rows,v_bytes) ELSE NULL END;
 INSERT INTO system.retention_backfill_state(parent_schema,parent_table,partition_name,range_start,range_end,state,cursor_key,cursor_observed_at,cursor_tie_breaker,rows_copied,bytes_copied)
 VALUES(p_parent_schema,p_parent_table,format('%s_p%s',p_parent_table,to_char(p_day,'YYYYMMDD')),p_day::timestamptz,(p_day+1)::timestamptz,CASE WHEN next_cursor IS NULL THEN 'complete' ELSE 'running' END,next_cursor,state_cursor_ts,state_cursor_key,v_rows,v_bytes)
 ON CONFLICT(parent_schema,parent_table,partition_name) DO UPDATE SET state=EXCLUDED.state,rows_copied=system.retention_backfill_state.rows_copied+EXCLUDED.rows_copied,bytes_copied=system.retention_backfill_state.bytes_copied+EXCLUDED.bytes_copied,cursor_key=EXCLUDED.cursor_key,cursor_observed_at=EXCLUDED.cursor_observed_at,cursor_tie_breaker=EXCLUDED.cursor_tie_breaker,updated_at=clock_timestamp();
 RETURN QUERY SELECT v_rows,v_bytes,next_cursor IS NULL,next_cursor;
END $backfill$;
REVOKE ALL ON FUNCTION control.run_m10_backfill(name,name,date,integer,integer) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.run_m10_backfill(name,name,date,integer,integer) TO sqlobserver_collector;

DROP FUNCTION IF EXISTS system.preview_m10_retention(timestamptz);
CREATE OR REPLACE FUNCTION system.preview_m10_retention(p_now_utc timestamptz DEFAULT clock_timestamp(),p_cursor text DEFAULT NULL)
RETURNS TABLE(data_class text,parent_schema name,parent_table name,partition_name name,range_start timestamptz,range_end timestamptz,eligible boolean,reason text)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,system,control SET TimeZone='UTC'
AS $$
 WITH c AS (
   SELECT CASE WHEN p_cursor IS NULL THEN NULL::jsonb ELSE convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb END AS j
 ), rows AS (
 SELECT p.data_class,r.parent_schema,r.parent_table,r.partition_name,r.range_start,r.range_end,
   (p.enabled AND p.retain_for IS NOT NULL AND r.lifecycle_state='attached' AND r.range_end<=p_now_utc-p.retain_for AND r.range_start<=p_now_utc-interval '1 day' AND (SELECT count(*) FROM system.partition_registry newer WHERE newer.parent_schema=r.parent_schema AND newer.parent_table=r.parent_table AND newer.lifecycle_state='attached' AND newer.range_start>=r.range_start) > p.minimum_partitions_to_keep AND (SELECT count(*) FROM system.partition_reader_lease l WHERE l.parent_schema=r.parent_schema AND l.parent_table=r.parent_table AND l.partition_name=r.partition_name AND l.released_at IS NULL AND l.expires_at>p_now_utc)=0 AND NOT EXISTS(SELECT 1 FROM system.retention_backfill_state b WHERE b.parent_schema=r.parent_schema AND b.parent_table=r.parent_table AND b.partition_name=r.partition_name AND b.state<>'complete') AND NOT EXISTS(SELECT 1 FROM control.analytics_job j WHERE (j.status IN ('queued','running','partial') OR (j.status='failed' AND j.attempt>=5)) AND j.job_kind<>'retention') AND EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>p_now_utc)) AS eligible,
   CASE WHEN NOT p.enabled OR p.retain_for IS NULL THEN 'retention_disabled' WHEN r.lifecycle_state<>'attached' THEN 'partition_not_attached' WHEN r.range_end>p_now_utc-p.retain_for THEN 'within_retention_window' WHEN (SELECT count(*) FROM system.partition_registry newer WHERE newer.parent_schema=r.parent_schema AND newer.parent_table=r.parent_table AND newer.lifecycle_state='attached' AND newer.range_start>=r.range_start) <= p.minimum_partitions_to_keep THEN 'minimum_partition_floor' WHEN EXISTS(SELECT 1 FROM system.partition_reader_lease l WHERE l.partition_name=r.partition_name AND l.released_at IS NULL AND l.expires_at>p_now_utc) THEN 'reader_lease_active' WHEN EXISTS(SELECT 1 FROM system.retention_backfill_state b WHERE b.partition_name=r.partition_name AND b.state<>'complete') THEN 'backfill_incomplete' WHEN EXISTS(SELECT 1 FROM control.analytics_job j WHERE (j.status IN ('queued','running','partial') OR (j.status='failed' AND j.attempt>=5)) AND j.job_kind<>'retention') THEN 'dependency_pending' WHEN NOT EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>p_now_utc) THEN 'recovery_attestation_missing' ELSE 'eligible' END
  FROM system.retention_policy p JOIN system.partition_registry r ON r.parent_schema=p.parent_schema AND r.parent_table=p.parent_table CROSS JOIN c
  WHERE p_cursor IS NULL OR (c.j->>'v')='m10.retention.v1' AND (c.j->>'o')='dataClass,parentSchema,parentTable,partitionName'
    AND (p.data_class,r.parent_schema::text,r.parent_table::text,r.partition_name::text) > (c.j->>'d',c.j->>'s1',c.j->>'t',c.j->>'p')
 ) SELECT * FROM rows ORDER BY data_class,parent_schema,parent_table,partition_name;
$$;
REVOKE ALL ON FUNCTION system.preview_m10_retention(timestamptz,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION system.preview_m10_retention(timestamptz,text) TO sqlobserver_server,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION system.detach_m10_partition(p_parent_schema name,p_parent_table name,p_partition_name name,p_execution_id uuid)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC' AS $detach$
DECLARE r system.partition_registry%ROWTYPE; p system.retention_policy%ROWTYPE; op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid; req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex'); existing_digest bytea;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator' OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL THEN RAISE EXCEPTION 'retention detach requires global SecurityAdministrator authorization' USING ERRCODE='42501'; END IF;
 IF p_execution_id IS NULL OR op_id IS NULL OR coalesce(octet_length(req_digest),0)<>32 THEN RAISE EXCEPTION 'retention execution fence is required' USING ERRCODE='22023'; END IF;
 SELECT request_digest INTO existing_digest FROM control.m10_mutation_replay WHERE operation_id=op_id; IF FOUND THEN IF existing_digest<>req_digest THEN RAISE EXCEPTION 'divergent retention replay' USING ERRCODE='40001'; END IF; RETURN true; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(p_parent_schema::text||'.'||p_parent_table::text||'.'||p_partition_name::text,1));
 SELECT * INTO r FROM system.partition_registry WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table AND partition_name=p_partition_name FOR UPDATE;
 IF NOT FOUND OR r.lifecycle_state<>'attached' THEN RAISE EXCEPTION 'partition registry entry is not attached' USING ERRCODE='55000'; END IF;
 SELECT * INTO p FROM system.retention_policy WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table;
 IF NOT FOUND OR NOT p.enabled OR p.retain_for IS NULL OR r.partition_schema<>r.parent_schema
    OR r.range_end>clock_timestamp()-p.retain_for OR r.range_start>clock_timestamp()-interval '1 day'
    OR (SELECT count(*) FROM system.partition_registry newer WHERE newer.parent_schema=r.parent_schema AND newer.parent_table=r.parent_table AND newer.lifecycle_state='attached' AND newer.range_start>=r.range_start)<=p.minimum_partitions_to_keep
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease l WHERE l.parent_schema=p_parent_schema AND l.parent_table=p_parent_table AND l.partition_name=p_partition_name AND l.released_at IS NULL AND l.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_backfill_state b WHERE b.parent_schema=p_parent_schema AND b.parent_table=p_parent_table AND b.partition_name=p_partition_name AND b.state<>'complete')
    OR EXISTS(SELECT 1 FROM control.analytics_job j WHERE (j.status IN ('queued','running','partial') OR (j.status='failed' AND j.attempt>=5)) AND j.job_kind<>'retention')
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM system.retention_execution e WHERE e.execution_id=p_execution_id)
 THEN RAISE EXCEPTION 'retention prerequisites are not satisfied' USING ERRCODE='55000'; END IF;
 EXECUTE format('ALTER TABLE %I.%I DETACH PARTITION %I.%I',p_parent_schema,p_parent_table,p_parent_schema,p_partition_name);
 UPDATE system.partition_registry SET lifecycle_state='detached',detached_at=clock_timestamp() WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table AND partition_name=p_partition_name;
 INSERT INTO system.retention_execution(execution_id,data_class,parent_schema,parent_table,partition_name,range_start,range_end,state,policy_revision,registry_revision,detached_at,drop_after) VALUES(p_execution_id,p.data_class,p_parent_schema,p_parent_table,p_partition_name,r.range_start,r.range_end,'grace',p.policy_revision,sha256(convert_to(p_parent_schema::text||'|'||p_parent_table::text||'|'||p_partition_name::text||'|'||r.range_start::text||'|'||r.range_end::text,'UTF8')),clock_timestamp(),clock_timestamp()+interval '24 hours');
  INSERT INTO control.m10_mutation_replay(operation_id,action_name,instance_id,request_digest,result_digest,result) VALUES(op_id,'retention_detach',nullif(current_setting('sqlobserver.target_scope',true),'')::uuid,req_digest,sha256(req_digest),jsonb_build_object('executionId',p_execution_id,'state','grace'));
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(clock_timestamp(),op_id,'user',current_setting('sqlobserver.actor_sid',true),'retention.partition.detach','allowed','succeeded','partition',p_partition_name::text,nullif(current_setting('sqlobserver.retention_correlation_id',true),'')::uuid,req_digest,jsonb_build_object('dataClass',p.data_class,'policyRevision',p.policy_revision,'dropAfter',clock_timestamp()+interval '24 hours','changeReason',current_setting('sqlobserver.retention_change_reason',true)));
 RETURN true;
END $detach$;
CREATE OR REPLACE FUNCTION system.drop_m10_partition(p_execution_id uuid) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system,control,audit SET TimeZone='UTC' AS $drop$
DECLARE e system.retention_execution%ROWTYPE; op_id uuid:=nullif(current_setting('sqlobserver.retention_operation_id',true),'')::uuid; req_digest bytea:=decode(nullif(current_setting('sqlobserver.retention_request_digest',true),''),'hex'); existing_digest bytea;
BEGIN IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator' OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL OR op_id IS NULL OR coalesce(octet_length(req_digest),0)<>32 THEN RAISE EXCEPTION 'retention drop requires global SecurityAdministrator authorization' USING ERRCODE='42501'; END IF; SELECT request_digest INTO existing_digest FROM control.m10_mutation_replay WHERE operation_id=op_id; IF FOUND THEN IF existing_digest<>req_digest THEN RAISE EXCEPTION 'divergent retention replay' USING ERRCODE='40001'; END IF; RETURN true; END IF; SELECT * INTO e FROM system.retention_execution WHERE execution_id=p_execution_id FOR UPDATE; IF NOT FOUND OR e.state<>'grace' OR e.drop_after>clock_timestamp() THEN RETURN false; END IF; PERFORM pg_advisory_xact_lock(hashtextextended(e.parent_schema::text||'.'||e.parent_table::text||'.'||e.partition_name::text,1));
 IF NOT EXISTS(SELECT 1 FROM system.retention_policy p WHERE p.data_class=e.data_class AND p.policy_revision=e.policy_revision AND p.enabled AND p.retain_for IS NOT NULL)
    OR NOT EXISTS(SELECT 1 FROM system.partition_registry r WHERE r.parent_schema=e.parent_schema AND r.parent_table=e.parent_table AND r.partition_name=e.partition_name AND r.lifecycle_state='detached' AND sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text||'|'||r.partition_name::text||'|'||r.range_start::text||'|'||r.range_end::text,'UTF8'))=e.registry_revision)
    OR EXISTS(SELECT 1 FROM system.partition_reader_lease WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table AND partition_name=e.partition_name AND released_at IS NULL AND expires_at>clock_timestamp())
    OR NOT EXISTS(SELECT 1 FROM system.recovery_attestation a WHERE a.valid AND a.expires_at>clock_timestamp())
    OR EXISTS(SELECT 1 FROM control.analytics_job j WHERE (j.status IN ('queued','running','partial') OR (j.status='failed' AND j.attempt>=5)) AND j.job_kind<>'retention') THEN RETURN false; END IF;
 EXECUTE format('DROP TABLE IF EXISTS %I.%I', e.parent_schema,e.partition_name); UPDATE system.partition_registry SET lifecycle_state='dropped',dropped_at=clock_timestamp() WHERE parent_schema=e.parent_schema AND parent_table=e.parent_table AND partition_name=e.partition_name; UPDATE system.retention_execution SET state='dropped',dropped_at=clock_timestamp() WHERE execution_id=p_execution_id; INSERT INTO control.m10_mutation_replay(operation_id,action_name,instance_id,request_digest,result_digest,result) VALUES(op_id,'retention_drop',nullif(current_setting('sqlobserver.target_scope',true),'')::uuid,req_digest,sha256(req_digest),jsonb_build_object('executionId',p_execution_id,'state','dropped')); INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(clock_timestamp(),op_id,'user',current_setting('sqlobserver.actor_sid',true),'retention.partition.drop','allowed','succeeded','partition',e.partition_name::text,nullif(current_setting('sqlobserver.retention_correlation_id',true),'')::uuid,req_digest,jsonb_build_object('dataClass',e.data_class,'policyRevision',e.policy_revision,'changeReason',current_setting('sqlobserver.retention_change_reason',true))); RETURN true; EXCEPTION WHEN SQLSTATE '40001' THEN RAISE; WHEN OTHERS THEN INSERT INTO system.retention_drop_retry(execution_id,error_code,error_detail,next_attempt_at) VALUES(p_execution_id,SQLSTATE,SQLERRM,clock_timestamp()+interval '1 hour'); UPDATE system.retention_execution SET state='retry',attempt=attempt+1,last_error=SQLERRM WHERE execution_id=p_execution_id; RETURN false; END $drop$;
REVOKE ALL ON FUNCTION system.detach_m10_partition(name,name,name,uuid),system.drop_m10_partition(uuid) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.detach_m10_partition(name,name,name,uuid),system.drop_m10_partition(uuid) TO sqlobserver_server;

-- Keep all metadata ledgers unpartitioned and indexed for bounded maintenance.
CREATE INDEX IF NOT EXISTS ix_collection_run_m10_unpartitioned ON telemetry.collection_run(instance_id,started_at DESC);
CREATE INDEX IF NOT EXISTS ix_collection_outcome_m10_unpartitioned ON telemetry.collection_run_outcome(completed_at DESC);
CREATE INDEX IF NOT EXISTS ix_visibility_gap_m10_unpartitioned ON telemetry.visibility_gap(instance_id,gap_started_at DESC);
ALTER TABLE telemetry.visibility_gap ADD COLUMN IF NOT EXISTS topology_fingerprint bytea;
ALTER TABLE telemetry.visibility_gap ADD COLUMN IF NOT EXISTS gap_evidence jsonb;
ALTER TABLE telemetry.visibility_gap DROP CONSTRAINT IF EXISTS ck_m10_replication_gap_evidence;
ALTER TABLE telemetry.visibility_gap ADD CONSTRAINT ck_m10_replication_gap_evidence CHECK
 (collector_id<>'replication.health' OR (octet_length(topology_fingerprint)=32 AND gap_evidence IS NOT NULL AND jsonb_typeof(gap_evidence)='object'));

-- The retention API below compiles against policy_revision.  Upgrade the
-- existing M3 policy table before defining any function that references it.
ALTER TABLE system.retention_policy ADD COLUMN IF NOT EXISTS policy_revision bigint NOT NULL DEFAULT 1;
ALTER TABLE system.retention_policy DROP CONSTRAINT IF EXISTS ck_retention_policy_revision;
ALTER TABLE system.retention_policy ADD CONSTRAINT ck_retention_policy_revision CHECK (policy_revision>0);

REVOKE ALL ON TABLE control.host_binding,control.host_profile,control.replication_profile,control.replication_distribution_binding,telemetry.host_metric_snapshot_v2,telemetry.replication_snapshot_v2,telemetry.m10_commit_replay,analytics.metric_catalog,analytics.metric_rollup_v2,analytics.metric_baseline,analytics.metric_forecast,analytics.evidence_packet_v2,analytics.incident_thread,analytics.incident_generation,control.analytics_job,control.analytics_watermark,control.analytics_replay,system.retention_policy_history,system.recovery_attestation,system.retention_execution,system.retention_backfill_state,system.partition_reader_lease,system.retention_drop_retry FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION system.update_m10_retention_policy(p_data_class text,p_enabled boolean,p_retain_for interval,p_minimum_partitions integer,p_expected_revision bigint,p_changed_by text,p_change_reason text)
RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system AS $m10_policy_update$
DECLARE next_revision bigint;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'') <> 'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'') <> 'global'
 THEN RAISE EXCEPTION 'global retention mutation requires SecurityAdministrator authorization context' USING ERRCODE='42501'; END IF;
 IF p_data_class IS NULL OR p_data_class NOT LIKE 'm10_%' OR p_minimum_partitions<1 OR p_expected_revision<1 OR p_change_reason IS NULL OR length(p_change_reason)>512 OR p_changed_by IS NULL OR length(p_changed_by)>256 OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day')) THEN RAISE EXCEPTION 'retention policy bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM set_config('sqlobserver.retention_change_reason',p_change_reason,true);
 PERFORM set_config('sqlobserver.retention_changed_by',p_changed_by,true);
 UPDATE system.retention_policy SET enabled=p_enabled,retain_for=p_retain_for,minimum_partitions_to_keep=p_minimum_partitions,policy_revision=policy_revision+1,updated_by=p_changed_by,updated_at=clock_timestamp() WHERE data_class=p_data_class AND policy_revision=p_expected_revision RETURNING policy_revision INTO next_revision;
 IF NOT FOUND THEN RAISE EXCEPTION 'retention policy revision conflict' USING ERRCODE='40001'; END IF;
 RETURN next_revision;
END $m10_policy_update$;
CREATE OR REPLACE FUNCTION system.record_m10_recovery_attestation(p_attestation_id uuid,p_attested_by text,p_backup_set_reference text,p_expires_at timestamptz,p_digest bytea)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system SET TimeZone='UTC' AS $m10_attestation$
BEGIN IF p_attestation_id IS NULL OR p_attested_by IS NULL OR length(p_attested_by)>256 OR p_backup_set_reference IS NULL OR length(p_backup_set_reference)>512 OR p_expires_at<=clock_timestamp() OR octet_length(p_digest)<>32 THEN RAISE EXCEPTION 'recovery attestation bounds rejected' USING ERRCODE='22023'; END IF; INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest) VALUES(p_attestation_id,clock_timestamp(),p_attested_by,p_backup_set_reference,p_expires_at,p_digest) ON CONFLICT(attestation_id) DO NOTHING; RETURN FOUND; END $m10_attestation$;
REVOKE ALL ON FUNCTION system.update_m10_retention_policy(text,boolean,interval,integer,bigint,text,text),system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.update_m10_retention_policy(text,boolean,interval,integer,bigint,text,text),system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea) TO sqlobserver_server;

CREATE OR REPLACE FUNCTION system.get_m10_retention_policy(p_data_class text)
RETURNS TABLE(data_class text,enabled boolean,retain_for interval,minimum_partitions_to_keep integer,policy_revision bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,system AS $$
 SELECT data_class,enabled,retain_for,minimum_partitions_to_keep,policy_revision
 FROM system.retention_policy WHERE data_class=p_data_class;
$$;
REVOKE ALL ON FUNCTION system.get_m10_retention_policy(text) FROM PUBLIC,sqlobserver_collector;
GRANT EXECUTE ON FUNCTION system.get_m10_retention_policy(text) TO sqlobserver_server,sqlobserver_auditor;

-- Retention callers never read the protected execution/policy tables.  These
-- fixed scalar resolvers are the only revision probes exposed to the server;
-- detach/drop remain the only mutators and retain their authorization fence.
CREATE OR REPLACE FUNCTION system.get_m10_retention_execution_revision(p_execution_id uuid)
RETURNS bigint LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,system AS $$
 SELECT policy_revision FROM system.retention_execution WHERE execution_id=p_execution_id;
$$;
CREATE OR REPLACE FUNCTION system.get_m10_retention_policy_revision(p_parent_schema name,p_parent_table name,p_data_class text)
RETURNS bigint LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,system AS $$
 SELECT policy_revision FROM system.retention_policy
 WHERE parent_schema=p_parent_schema AND parent_table=p_parent_table AND data_class=p_data_class;
$$;
REVOKE ALL ON FUNCTION system.get_m10_retention_execution_revision(uuid),system.get_m10_retention_policy_revision(name,name,text) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.get_m10_retention_execution_revision(uuid),system.get_m10_retention_policy_revision(name,name,text) TO sqlobserver_server;

-- Forward repair: analytics rows carry the complete computation identity.  A
-- missing revision/interval is not recoverable from a row, so upgrades retain
-- it in a quarantine ledger and remove it from the live relation.  In
-- particular, this repair never fabricates revision 1 or relabels a bucket.
CREATE TABLE IF NOT EXISTS analytics.metric_rollup_v2_quarantine
(
 quarantine_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 quarantined_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 reason text NOT NULL,
 row_data jsonb NOT NULL
);
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS target_revision bigint;
ALTER TABLE analytics.metric_rollup_v2 ALTER COLUMN target_revision DROP DEFAULT;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS rollup_interval text;
ALTER TABLE analytics.metric_rollup_v2 ALTER COLUMN rollup_interval DROP DEFAULT;
DROP TRIGGER IF EXISTS m10_rollup_append_only ON analytics.metric_rollup_v2;
DO $m10_rollup_repair$
BEGIN
 IF EXISTS (SELECT 1 FROM analytics.metric_rollup_v2 WHERE target_revision IS NULL OR rollup_interval IS NULL) THEN
  INSERT INTO analytics.metric_rollup_v2_quarantine(reason,row_data)
  SELECT CASE WHEN target_revision IS NULL THEN 'missing_target_revision' ELSE 'missing_rollup_interval' END,to_jsonb(r)
  FROM analytics.metric_rollup_v2 r
  WHERE target_revision IS NULL OR rollup_interval IS NULL;
  DELETE FROM analytics.metric_rollup_v2 WHERE target_revision IS NULL OR rollup_interval IS NULL;
 END IF;
 IF EXISTS (SELECT 1 FROM analytics.metric_rollup_v2 r
            WHERE NOT EXISTS (SELECT 1 FROM control.observation_target t
                              WHERE t.instance_id=r.instance_id AND t.revision=r.target_revision)) THEN
  INSERT INTO analytics.metric_rollup_v2_quarantine(reason,row_data)
  SELECT 'target_revision_not_authoritative',to_jsonb(r)
  FROM analytics.metric_rollup_v2 r
  WHERE NOT EXISTS (SELECT 1 FROM control.observation_target t
                    WHERE t.instance_id=r.instance_id AND t.revision=r.target_revision);
  DELETE FROM analytics.metric_rollup_v2 r
  WHERE NOT EXISTS (SELECT 1 FROM control.observation_target t
                    WHERE t.instance_id=r.instance_id AND t.revision=r.target_revision);
 END IF;
END $m10_rollup_repair$;
ALTER TABLE analytics.metric_rollup_v2 ALTER COLUMN target_revision SET NOT NULL;
ALTER TABLE analytics.metric_rollup_v2 ALTER COLUMN rollup_interval SET NOT NULL;
CREATE TRIGGER m10_rollup_append_only BEFORE UPDATE OR DELETE ON analytics.metric_rollup_v2 FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
DO $m10_rollup_interval_check$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_m10_rollup_interval') THEN
  ALTER TABLE analytics.metric_rollup_v2 ADD CONSTRAINT ck_m10_rollup_interval CHECK (rollup_interval IN ('5m','hour','day'));
 END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='fk_m10_rollup_target_revision') THEN
  ALTER TABLE analytics.metric_rollup_v2 ADD CONSTRAINT fk_m10_rollup_target_revision
    FOREIGN KEY (instance_id,target_revision) REFERENCES control.observation_target(instance_id,revision);
 END IF;
END $m10_rollup_interval_check$;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS dimensions jsonb NOT NULL DEFAULT '{}'::jsonb;
-- Identity columns on an upgraded relation start nullable so the repair can
-- quarantine rows which cannot prove their identity; no synthetic hash or
-- generation is assigned to legacy telemetry.
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS dimension_hash bytea;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS expected_count integer NOT NULL DEFAULT 0;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS reset_count integer NOT NULL DEFAULT 0;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS gap_count integer NOT NULL DEFAULT 0;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS truncated boolean NOT NULL DEFAULT false;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS source_cutoff_utc timestamptz;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS catalog_version integer NOT NULL DEFAULT 1;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS algorithm_version text NOT NULL DEFAULT 'rollup-v1';
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS last_value double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS counter_delta double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS rate_per_second double precision;
ALTER TABLE analytics.metric_rollup_v2 ADD COLUMN IF NOT EXISTS generation bigint;
DO $m10_rollup_identity_repair$
BEGIN
 IF EXISTS (SELECT 1 FROM analytics.metric_rollup_v2
            WHERE dimension_hash IS NULL OR octet_length(dimension_hash)<>32 OR generation IS NULL OR generation<1) THEN
  INSERT INTO analytics.metric_rollup_v2_quarantine(reason,row_data)
  SELECT CASE WHEN dimension_hash IS NULL OR octet_length(dimension_hash)<>32 THEN 'missing_dimension_hash' ELSE 'missing_generation' END,to_jsonb(r)
  FROM analytics.metric_rollup_v2 r
  WHERE dimension_hash IS NULL OR octet_length(dimension_hash)<>32 OR generation IS NULL OR generation<1;
  DELETE FROM analytics.metric_rollup_v2
  WHERE dimension_hash IS NULL OR octet_length(dimension_hash)<>32 OR generation IS NULL OR generation<1;
 END IF;
END $m10_rollup_identity_repair$;
ALTER TABLE analytics.metric_rollup_v2 ALTER COLUMN dimension_hash SET NOT NULL;
ALTER TABLE analytics.metric_rollup_v2 ALTER COLUMN generation SET NOT NULL;
-- A pre-0014 relation may have rows which collapse onto the new composite
-- identity.  Do not choose a winner (which would silently lose telemetry):
-- reject the upgrade with a deterministic duplicate-key error and leave the
-- old relation untouched for an operator to reconcile.
DO $m10_rollup_identity_collision$
BEGIN
 IF EXISTS (
   SELECT 1
   FROM analytics.metric_rollup_v2
   GROUP BY bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,dimension_hash,generation
   HAVING count(*)>1
 ) THEN
   RAISE EXCEPTION 'metric_rollup_v2 identity collision; upgrade requires deterministic reconciliation'
     USING ERRCODE='23505';
 END IF;
END $m10_rollup_identity_collision$;
ALTER TABLE analytics.metric_rollup_v2 DROP CONSTRAINT IF EXISTS metric_rollup_v2_pkey;
ALTER TABLE analytics.metric_rollup_v2 ADD PRIMARY KEY (bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,dimension_hash,generation);
ALTER TABLE analytics.metric_rollup_v2 DROP CONSTRAINT IF EXISTS ck_m10_rollup_metadata;
ALTER TABLE analytics.metric_rollup_v2 ADD CONSTRAINT ck_m10_rollup_metadata CHECK
 (target_revision>0 AND generation>0 AND octet_length(dimension_hash)=32 AND jsonb_typeof(dimensions)='object'
  AND octet_length(dimensions::text)<=16384 AND expected_count>=0 AND sample_count>=0 AND reset_count>=0 AND gap_count>=0
  AND catalog_version>0 AND algorithm_version ~ '^[a-z0-9._-]{1,64}$');
CREATE INDEX IF NOT EXISTS ix_rollup_target_dimension_time ON analytics.metric_rollup_v2(instance_id,metric_key,dimension_hash,bucket_start DESC);
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS target_revision bigint;
ALTER TABLE analytics.metric_baseline ALTER COLUMN target_revision DROP DEFAULT;
DROP TRIGGER IF EXISTS m10_baseline_append_only ON analytics.metric_baseline;
DO $m10_baseline_revision_repair$
BEGIN
 IF EXISTS (SELECT 1 FROM analytics.metric_baseline b WHERE b.target_revision IS NULL OR NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=b.instance_id AND t.revision=b.target_revision)) THEN
  INSERT INTO analytics.metric_rollup_v2_quarantine(reason,row_data) SELECT 'baseline_target_revision_not_authoritative',to_jsonb(b) FROM analytics.metric_baseline b WHERE b.target_revision IS NULL OR NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=b.instance_id AND t.revision=b.target_revision);
  DELETE FROM analytics.metric_baseline b WHERE b.target_revision IS NULL OR NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=b.instance_id AND t.revision=b.target_revision);
 END IF;
END $m10_baseline_revision_repair$;
ALTER TABLE analytics.metric_baseline ALTER COLUMN target_revision SET NOT NULL;
CREATE TRIGGER m10_baseline_append_only BEFORE UPDATE OR DELETE ON analytics.metric_baseline FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS hour_of_week integer;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS complete_days integer;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS median double precision;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS mad double precision;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS p10 double precision;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS p90 double precision;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS coverage double precision;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS confidence double precision;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS dimensions jsonb NOT NULL DEFAULT '{}'::jsonb;
-- A column default cannot reference another column.  Keep the upgrade
-- explicit and temporarily remove the append-only trigger while repairing
-- identities for rows created by pre-M10 versions.
DROP TRIGGER IF EXISTS m10_baseline_append_only ON analytics.metric_baseline;
ALTER TABLE analytics.metric_baseline ADD COLUMN IF NOT EXISTS dimension_hash bytea;
UPDATE analytics.metric_baseline
   SET dimension_hash=sha256(convert_to(dimensions::text,'UTF8'));
ALTER TABLE analytics.metric_baseline ALTER COLUMN dimension_hash SET NOT NULL;
ALTER TABLE analytics.metric_baseline DROP CONSTRAINT IF EXISTS ck_m10_baseline_dimensions;
ALTER TABLE analytics.metric_baseline ADD CONSTRAINT ck_m10_baseline_dimensions CHECK (jsonb_typeof(dimensions)='object' AND octet_length(dimensions::text)<=16384 AND octet_length(dimension_hash)=32);
ALTER TABLE analytics.metric_baseline DROP CONSTRAINT IF EXISTS metric_baseline_pkey;
ALTER TABLE analytics.metric_baseline ADD PRIMARY KEY (instance_id,target_revision,metric_key,window_start,dimension_hash,generation);
CREATE TRIGGER m10_baseline_append_only BEFORE UPDATE OR DELETE ON analytics.metric_baseline FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
ALTER TABLE analytics.metric_forecast ADD COLUMN IF NOT EXISTS target_revision bigint;
ALTER TABLE analytics.metric_forecast ALTER COLUMN target_revision DROP DEFAULT;
DROP TRIGGER IF EXISTS m10_forecast_append_only ON analytics.metric_forecast;
DO $m10_forecast_revision_repair$
BEGIN
 IF EXISTS (SELECT 1 FROM analytics.metric_forecast f WHERE f.target_revision IS NULL OR NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=f.instance_id AND t.revision=f.target_revision)) THEN
  INSERT INTO analytics.metric_rollup_v2_quarantine(reason,row_data) SELECT 'forecast_target_revision_not_authoritative',to_jsonb(f) FROM analytics.metric_forecast f WHERE f.target_revision IS NULL OR NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=f.instance_id AND t.revision=f.target_revision);
  DELETE FROM analytics.metric_forecast f WHERE f.target_revision IS NULL OR NOT EXISTS (SELECT 1 FROM control.observation_target t WHERE t.instance_id=f.instance_id AND t.revision=f.target_revision);
 END IF;
END $m10_forecast_revision_repair$;
ALTER TABLE analytics.metric_forecast ALTER COLUMN target_revision SET NOT NULL;
CREATE TRIGGER m10_forecast_append_only BEFORE UPDATE OR DELETE ON analytics.metric_forecast FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
ALTER TABLE analytics.metric_forecast ADD COLUMN IF NOT EXISTS residual double precision;
ALTER TABLE analytics.metric_forecast ADD COLUMN IF NOT EXISTS slope_per_day double precision;
ALTER TABLE analytics.metric_forecast DROP CONSTRAINT IF EXISTS metric_forecast_pkey;
ALTER TABLE analytics.metric_forecast ADD PRIMARY KEY (forecast_id,instance_id,target_revision);
ALTER TABLE analytics.evidence_packet_v2 ADD COLUMN IF NOT EXISTS identity_digest bytea NOT NULL DEFAULT sha256(convert_to('','UTF8'));
ALTER TABLE analytics.evidence_packet_v2 ADD COLUMN IF NOT EXISTS source_cutoff_digest bytea NOT NULL DEFAULT sha256(convert_to('','UTF8'));
ALTER TABLE analytics.incident_generation ADD COLUMN IF NOT EXISTS supersedes_previous boolean NOT NULL DEFAULT false;
ALTER TABLE analytics.incident_generation ADD COLUMN IF NOT EXISTS instance_id uuid;
ALTER TABLE analytics.incident_generation ADD COLUMN IF NOT EXISTS target_revision bigint;
DROP TRIGGER IF EXISTS m10_incident_generation_append_only ON analytics.incident_generation;
UPDATE analytics.incident_generation g
SET instance_id=t.instance_id,target_revision=t.target_revision
FROM analytics.incident_thread t
WHERE t.thread_id=g.thread_id AND (g.instance_id IS NULL OR g.target_revision IS NULL);
ALTER TABLE analytics.incident_generation ALTER COLUMN instance_id SET NOT NULL;
ALTER TABLE analytics.incident_generation ALTER COLUMN target_revision SET NOT NULL;
CREATE TRIGGER m10_incident_generation_append_only BEFORE UPDATE OR DELETE ON analytics.incident_generation FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
ALTER TABLE analytics.incident_generation DROP CONSTRAINT IF EXISTS incident_generation_pkey;
ALTER TABLE analytics.incident_generation ADD PRIMARY KEY (instance_id,target_revision,thread_id,generation);
DO $m10_identity_constraints$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='uq_m10_forecast_target_identity') THEN
  ALTER TABLE analytics.metric_forecast ADD CONSTRAINT uq_m10_forecast_target_identity UNIQUE (forecast_id,instance_id,target_revision);
 END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='uq_m10_incident_target_identity') THEN
  ALTER TABLE analytics.incident_thread ADD CONSTRAINT uq_m10_incident_target_identity UNIQUE (thread_id,instance_id,target_revision);
 END IF;
END $m10_identity_constraints$;
DO $m10_generation_identity$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='fk_m10_incident_generation_target_revision') THEN
  ALTER TABLE analytics.incident_generation ADD CONSTRAINT fk_m10_incident_generation_target_revision
    FOREIGN KEY (thread_id,instance_id,target_revision)
    REFERENCES analytics.incident_thread(thread_id,instance_id,target_revision);
 END IF;
END $m10_generation_identity$;
-- M10 row identity always includes the target revision.  These parents are
-- new in 0014, while the DROP/ADD form also repairs a partially-applied
-- migration without copying history.
ALTER TABLE telemetry.host_metric_snapshot_v2 DROP CONSTRAINT IF EXISTS host_metric_snapshot_v2_pkey;
ALTER TABLE telemetry.host_metric_snapshot_v2 ADD PRIMARY KEY (observed_at,instance_id,target_revision,run_id,metric_key,dimensions);
ALTER TABLE telemetry.m10_commit_replay DROP CONSTRAINT IF EXISTS m10_commit_replay_pkey;
ALTER TABLE telemetry.m10_commit_replay ADD PRIMARY KEY (run_id,instance_id,target_revision);
ALTER TABLE telemetry.replication_snapshot_v2 DROP CONSTRAINT IF EXISTS replication_snapshot_v2_pkey;
ALTER TABLE telemetry.replication_snapshot_v2 ADD PRIMARY KEY (observed_at,instance_id,target_revision,run_id,topology_fingerprint);
ALTER TABLE analytics.evidence_packet_v2 DROP CONSTRAINT IF EXISTS evidence_packet_v2_pkey;
ALTER TABLE analytics.evidence_packet_v2 ADD PRIMARY KEY (occurred_at,instance_id,target_revision,packet_id);
CREATE OR REPLACE VIEW analytics.metric_rollup AS SELECT * FROM analytics.metric_rollup_v2;
CREATE OR REPLACE VIEW reporting.m10_rollup_compat AS SELECT * FROM analytics.metric_rollup_v2;

-- Fixed application write/query functions.  JSON envelopes are intentionally
-- boring: callers cannot inject identifiers or SQL, and every operation is
-- fenced by target scope, worker lease, replay digest, and hard row/byte caps.
CREATE OR REPLACE FUNCTION control.get_m10_surface_generation(p_instance_id uuid,p_target_revision bigint)
RETURNS bigint LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control AS $$
  SELECT greatest(
    p_target_revision,
    coalesce((SELECT max(r.generation) FROM analytics.metric_rollup_v2 r WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision),1),
    coalesce((SELECT max(b.binding_revision) FROM control.host_binding b WHERE b.instance_id=p_instance_id AND b.target_revision=p_target_revision),1),
    coalesce((SELECT max(hp.profile_revision) FROM control.host_profile hp WHERE hp.instance_id=p_instance_id AND hp.target_revision=p_target_revision),1),
    coalesce((SELECT count(*) FROM telemetry.host_metric_snapshot_v2 h WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision),0),
    coalesce((SELECT count(*) FROM telemetry.replication_snapshot_v2 r WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision),0),
    coalesce((SELECT count(*) FROM events.diagnostic_event d WHERE d.instance_id=p_instance_id),0),
    coalesce((SELECT count(*) FROM analytics.evidence_packet_v2 e WHERE e.instance_id=p_instance_id AND e.target_revision=p_target_revision),0),
    coalesce((SELECT max(b.generation) FROM analytics.metric_baseline b WHERE b.instance_id=p_instance_id AND b.target_revision=p_target_revision),1),
    coalesce((SELECT max(f.source_generation) FROM analytics.metric_forecast f WHERE f.instance_id=p_instance_id AND f.target_revision=p_target_revision),1),
    coalesce((SELECT max(i.current_generation) FROM analytics.incident_thread i WHERE i.instance_id=p_instance_id AND i.target_revision=p_target_revision),1),
    coalesce((SELECT count(*) FROM analytics.incident_generation i WHERE i.instance_id=p_instance_id AND i.target_revision=p_target_revision),0),
    coalesce((SELECT count(*) FROM control.analytics_job j WHERE j.instance_id=p_instance_id AND j.target_revision=p_target_revision),0),
    coalesce((SELECT max(w.generation) FROM control.analytics_watermark w WHERE w.instance_id=p_instance_id),1))
  WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
    AND p_instance_id::text=current_setting('sqlobserver.target_scope',true);
$$;
REVOKE ALL ON FUNCTION control.get_m10_surface_generation(uuid,bigint) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.get_m10_surface_generation(uuid,bigint) TO sqlobserver_server,sqlobserver_collector;
CREATE OR REPLACE FUNCTION control.assert_m10_surface_fence(p_instance_id uuid,p_target_revision bigint,p_generation bigint,p_snapshot_utc timestamptz,p_source_cutoff_utc timestamptz)
RETURNS boolean LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control AS $m10_surface_fence$
BEGIN
  IF p_instance_id IS NULL OR p_target_revision<1 OR p_generation<1 OR p_snapshot_utc IS NULL OR p_source_cutoff_utc IS NULL
    OR p_source_cutoff_utc>p_snapshot_utc OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision
  THEN RAISE EXCEPTION 'M10 surface fence is invalid or stale' USING ERRCODE='40001'; END IF;
  IF p_generation <> control.get_m10_surface_generation(p_instance_id,p_target_revision)
  THEN RAISE EXCEPTION 'M10 surface generation is stale' USING ERRCODE='40001'; END IF;
 RETURN true;
END $m10_surface_fence$;
REVOKE ALL ON FUNCTION control.assert_m10_surface_fence(uuid,bigint,bigint,timestamptz,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.assert_m10_surface_fence(uuid,bigint,bigint,timestamptz,timestamptz) TO sqlobserver_server;

-- Forecast capacity is derived only from repository host-volume evidence. The
-- metric allowlist intentionally excludes paths, volume names, and inferred
-- filesystem data; absent or non-finite evidence yields NULL.
CREATE OR REPLACE FUNCTION reporting.get_m10_forecast_capacity(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_snapshot_utc timestamptz)
RETURNS double precision LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,analytics,control SET TimeZone='UTC' AS $$
 SELECT CASE WHEN max(h.metric_value) FILTER (WHERE h.metric_value>0 AND h.metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))>0
             THEN max(h.metric_value) FILTER (WHERE h.metric_value>0 AND h.metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))
             ELSE NULL END
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE p_instance_id IS NOT NULL AND p_target_revision>0
   AND p_metric_key IN ('host.volume.free_bytes','host.volume.total_bytes')
   AND p_snapshot_utc IS NOT NULL AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision
   AND h.metric_key='host.volume.total_bytes' AND h.observed_at<=p_snapshot_utc
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text
   AND control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND EXISTS (SELECT 1 FROM analytics.metric_catalog c WHERE c.metric_key=h.metric_key AND c.source_kind='host' AND c.enabled)
$$;
REVOKE ALL ON FUNCTION reporting.get_m10_forecast_capacity(uuid,bigint,text,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_m10_forecast_capacity(uuid,bigint,text,timestamptz) TO sqlobserver_server;

DROP FUNCTION IF EXISTS analytics.commit_metric_rollups(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea);
CREATE OR REPLACE FUNCTION analytics.commit_metric_rollups
 (p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_rows jsonb,p_result_digest bytea)
 RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,committed_at timestamptz)
 LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC'
AS $m10_rollup_write$
DECLARE n integer:=0; now_utc timestamptz:=clock_timestamp();
BEGIN
  IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0
    OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32
    OR jsonb_typeof(p_rows)<>'array' OR jsonb_array_length(p_rows)>100000 OR octet_length(p_rows::text)>8388608
    OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text
    OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision
  THEN RAISE EXCEPTION 'M10 rollup write bounds or target scope rejected' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
   IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND (j.job_kind='backfill' OR j.job_kind='rollup') AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN
    RAISE EXCEPTION 'M10 rollup job fence conflict' USING ERRCODE='40001';
  END IF;
   IF NOT control.record_m10_analytics_replay(p_operation_id,'rollup',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('rows',p_rows)) THEN
    RETURN QUERY SELECT 'replayed',0,0,now_utc; RETURN;
  END IF;
 INSERT INTO analytics.metric_rollup_v2(bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,sample_count,value,visibility_state,dimensions,dimension_hash,expected_count,reset_count,gap_count,truncated,source_cutoff_utc,catalog_version,algorithm_version,last_value,counter_delta,rate_per_second,generation)
 SELECT (x->>'bucketStartUtc')::timestamptz,p_instance_id,p_target_revision,coalesce(x->>'interval',x->>'rollupInterval'),x->>'metricKey',coalesce(x->>'aggregation','avg'),coalesce((x->>'sampleCount')::integer,0),(x->>'value')::double precision,coalesce(x->>'visibilityState','complete'),coalesce(x->'dimensions','{}'::jsonb),decode(x->>'dimensionHash','hex'),coalesce((x->>'expectedCount')::integer,0),coalesce((x->>'resetCount')::integer,0),coalesce((x->>'gapCount')::integer,0),coalesce((x->>'truncated')::boolean,false),(x->>'sourceCutoffUtc')::timestamptz,coalesce((x->>'catalogVersion')::integer,1),coalesce(x->>'algorithmVersion','rollup-v1'),(x->>'lastValue')::double precision,(x->>'counterDelta')::double precision,(x->>'ratePerSecond')::double precision,coalesce((x->>'generation')::bigint,1)
 FROM jsonb_array_elements(p_rows) x WHERE x->>'dimensionHash' IS NOT NULL AND (x->>'interval' IS NOT NULL OR x->>'rollupInterval' IS NOT NULL) ON CONFLICT (bucket_start,instance_id,target_revision,rollup_interval,metric_key,aggregation,dimension_hash,generation) DO NOTHING;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN QUERY SELECT 'committed',n,0,now_utc;
END $m10_rollup_write$;

DROP FUNCTION IF EXISTS analytics.list_metric_rollups(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz);
DROP FUNCTION IF EXISTS analytics.list_metric_rollups(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint);
CREATE OR REPLACE FUNCTION analytics.list_metric_rollups
 (p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz,p_rollup_interval text,
  p_after_bucket timestamptz DEFAULT NULL,p_after_metric text DEFAULT NULL,p_after_dimensions bytea DEFAULT NULL,p_after_generation bigint DEFAULT NULL)
RETURNS SETOF analytics.metric_rollup_v2 LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $$
 SELECT r.* FROM analytics.metric_rollup_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.metric_key=p_metric_key AND r.rollup_interval=p_rollup_interval
   AND p_rollup_interval IN ('5m','hour','day') AND r.bucket_start>=p_from_utc AND r.bucket_start<p_to_utc
   AND r.computed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 1000
   AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days'
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND ((p_after_bucket IS NULL AND p_after_metric IS NULL AND p_after_dimensions IS NULL AND p_after_generation IS NULL)
        OR (p_after_bucket IS NOT NULL AND p_after_metric IS NOT NULL AND octet_length(p_after_dimensions)=32 AND p_after_generation>0))
   AND (p_after_bucket IS NULL OR (r.bucket_start,r.metric_key,r.dimension_hash,r.generation) >
        (p_after_bucket,coalesce(p_after_metric,''),coalesce(p_after_dimensions,decode('', 'hex')),coalesce(p_after_generation,0)))
 ORDER BY r.bucket_start,r.metric_key,r.dimension_hash,r.generation LIMIT p_limit;
$$;
-- Derivation and dimension-aware callers must be able to request the full
-- bounded input set without losing a dimension at the legacy 1,000-row cap.
DROP FUNCTION IF EXISTS analytics.list_metric_rollups_scoped(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint,bytea);
CREATE OR REPLACE FUNCTION analytics.list_metric_rollups_scoped
 (p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz,p_rollup_interval text,
  p_after_bucket timestamptz DEFAULT NULL,p_after_metric text DEFAULT NULL,p_after_dimensions bytea DEFAULT NULL,p_after_generation bigint DEFAULT NULL,p_dimension_hash bytea DEFAULT NULL)
RETURNS SETOF analytics.metric_rollup_v2 LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $$
 SELECT r.* FROM analytics.metric_rollup_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.metric_key=p_metric_key AND r.rollup_interval=p_rollup_interval
   AND p_rollup_interval IN ('5m','hour','day') AND r.bucket_start>=p_from_utc AND r.bucket_start<p_to_utc
   AND r.computed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 100000
   AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days'
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND (p_dimension_hash IS NULL OR r.dimension_hash=p_dimension_hash)
   AND ((p_after_bucket IS NULL AND p_after_metric IS NULL AND p_after_dimensions IS NULL AND p_after_generation IS NULL)
        OR (p_after_bucket IS NOT NULL AND p_after_metric IS NOT NULL AND octet_length(p_after_dimensions)=32 AND p_after_generation>0))
   AND (p_after_bucket IS NULL OR (r.bucket_start,r.metric_key,r.dimension_hash,r.generation) >
        (p_after_bucket,coalesce(p_after_metric,''),coalesce(p_after_dimensions,decode('', 'hex')),coalesce(p_after_generation,0)))
 ORDER BY r.bucket_start,r.metric_key,r.dimension_hash,r.generation LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.list_metric_rollups(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION analytics.commit_metric_rollups(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION analytics.list_metric_rollups(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint) TO sqlobserver_server;
REVOKE ALL ON FUNCTION analytics.list_metric_rollups_scoped(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint,bytea) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION analytics.list_metric_rollups_scoped(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz,text,timestamptz,text,bytea,bigint,bytea) TO sqlobserver_server,sqlobserver_collector;
CREATE OR REPLACE FUNCTION reporting.list_metric_series(p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb) LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 SELECT h.observed_at,h.metric_key,h.metric_value,h.dimensions FROM telemetry.host_metric_snapshot_v2 h WHERE h.instance_id=p_instance_id AND h.metric_key=p_metric_key AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 1000 AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days' AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY h.observed_at,h.run_id,h.metric_key LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.list_metric_series(uuid,timestamptz,timestamptz,text,integer,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE OR REPLACE FUNCTION reporting.list_metric_series(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb) LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 SELECT h.observed_at,h.metric_key,h.metric_value,h.dimensions FROM telemetry.host_metric_snapshot_v2 h WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND h.metric_key=p_metric_key AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 1000 AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days' AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY h.observed_at,h.run_id,h.metric_key LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_metric_series(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz) TO sqlobserver_server;

-- Baselines, forecasts, evidence, and incidents use one common replay fence.
DROP FUNCTION IF EXISTS analytics.commit_metric_baselines(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea);
DROP FUNCTION IF EXISTS analytics.commit_metric_forecast(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea);
CREATE OR REPLACE FUNCTION analytics.commit_metric_baselines(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_rows jsonb,p_result_digest bytea) RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $m10_baseline_write$
DECLARE n integer:=0;
BEGIN
 IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_rows)<>'array' OR jsonb_array_length(p_rows)>100000 OR octet_length(p_rows::text)>8388608 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 baseline write bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='baseline' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'M10 baseline job fence conflict' USING ERRCODE='40001'; END IF;
 IF NOT control.record_m10_analytics_replay(p_operation_id,'baseline',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('rows',p_rows)) THEN RETURN 0; END IF;
  INSERT INTO analytics.metric_baseline(instance_id,target_revision,metric_key,hour_of_week,complete_days,window_start,window_end,sample_count,mean,stddev,median,mad,p10,p90,coverage,confidence,lower_bound,upper_bound,visibility_state,generation,dimensions,dimension_hash)
  SELECT p_instance_id,p_target_revision,x->>'metricKey',(x->>'hourOfWeek')::integer,(x->>'completeDays')::integer,(x->>'windowStartUtc')::timestamptz,(x->>'windowEndUtc')::timestamptz,coalesce((x->>'sampleCount')::integer,0),(x->>'mean')::double precision,(x->>'stddev')::double precision,(x->>'median')::double precision,(x->>'mad')::double precision,(x->>'p10')::double precision,(x->>'p90')::double precision,(x->>'coverage')::double precision,(x->>'confidence')::double precision,(x->>'lowerBound')::double precision,(x->>'upperBound')::double precision,coalesce(x->>'visibilityState','complete'),coalesce((x->>'generation')::bigint,1),coalesce(x->'dimensions','{}'::jsonb),coalesce(decode(nullif(x->>'dimensionsSha256',''),'hex'),sha256(convert_to(coalesce(x->'dimensions','{}'::jsonb)::text,'UTF8')))
  FROM jsonb_array_elements(p_rows) x WHERE x->>'windowStartUtc' IS NOT NULL AND x->>'windowEndUtc' IS NOT NULL ON CONFLICT (instance_id,target_revision,metric_key,window_start,dimension_hash,generation) DO NOTHING;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $m10_baseline_write$;
CREATE OR REPLACE FUNCTION analytics.commit_metric_forecast(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_row jsonb,p_result_digest bytea) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $m10_forecast_write$
BEGIN
 IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_row)<>'object' OR octet_length(p_row::text)>262144 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 forecast write bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='forecast' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'M10 forecast job fence conflict' USING ERRCODE='40001'; END IF;
 IF NOT control.record_m10_analytics_replay(p_operation_id,'forecast',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('row',p_row)) THEN RETURN false; END IF;
  INSERT INTO analytics.metric_forecast(forecast_id,instance_id,target_revision,metric_key,horizon_start,horizon_end,model,predicted_value,lower_bound,upper_bound,confidence,residual,slope_per_day,source_generation,visibility_state,dimensions,dimension_hash) VALUES(coalesce((p_row->>'forecastId')::uuid,left(encode(sha256(convert_to('forecast|'||p_operation_id::text||'|'||coalesce(p_row->>'metricKey','')||'|'||coalesce(p_row->>'horizonStartUtc',''),'UTF8')),'hex'),32)::uuid),p_instance_id,p_target_revision,p_row->>'metricKey',(p_row->>'horizonStartUtc')::timestamptz,(p_row->>'horizonEndUtc')::timestamptz,coalesce(p_row->>'model','forecast-v1'),coalesce((p_row->>'estimate')::double precision,(p_row->>'predictedValue')::double precision),(p_row->>'lowerBound')::double precision,(p_row->>'upperBound')::double precision,(p_row->>'confidence')::numeric,(p_row->>'residual')::double precision,(p_row->>'slopePerDay')::double precision,coalesce((p_row->>'sourceGeneration')::bigint,1),coalesce(p_row->>'visibilityState','complete'),coalesce(p_row->'dimensions','{}'::jsonb),coalesce(decode(nullif(p_row->>'dimensionsSha256',''),'hex'),sha256(convert_to(coalesce(p_row->'dimensions','{}'::jsonb)::text,'UTF8')))) ON CONFLICT DO NOTHING;
 RETURN true;
END $m10_forecast_write$;
DROP FUNCTION IF EXISTS analytics.commit_evidence_packet(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea);
CREATE OR REPLACE FUNCTION analytics.commit_evidence_packet(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_row jsonb,p_result_digest bytea) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $m10_evidence_write$
DECLARE stored_evidence jsonb;
BEGIN
  IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_row)<>'object' OR octet_length(p_row::text)>262144 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 evidence write bounds rejected' USING ERRCODE='22023'; END IF;
 IF (p_row->>'packetId') IS NULL OR (p_row->>'targetId') IS DISTINCT FROM p_instance_id::text OR (p_row->>'targetRevision')::bigint IS DISTINCT FROM p_target_revision OR jsonb_typeof(coalesce(p_row->'references','[]'::jsonb))<>'array' OR jsonb_typeof(coalesce(p_row->'tombstones','[]'::jsonb))<>'array' OR p_row->>'trigger' IS NULL OR p_row->>'algorithm' IS NULL OR p_row->>'generation' IS NULL OR (p_row->>'generation')::bigint<1 OR p_row->>'state' IS NULL OR p_row->>'truncated' IS NULL THEN RAISE EXCEPTION 'M10 evidence identity fields are incomplete' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
  IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='evidence' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'M10 evidence job fence conflict' USING ERRCODE='40001'; END IF;
  IF NOT control.record_m10_analytics_replay(p_operation_id,'evidence',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('row',p_row)) THEN RETURN false; END IF;
 stored_evidence := coalesce(p_row->'evidence','{}'::jsonb) || jsonb_build_object('packetId',p_row->'packetId','targetId',p_row->'targetId','targetRevision',p_row->'targetRevision','trigger',p_row->'trigger','algorithm',p_row->'algorithm','generation',(p_row->>'generation')::bigint,'state',p_row->'state','truncated',(p_row->>'truncated')::boolean,'references',coalesce(p_row->'references','[]'::jsonb),'tombstones',coalesce(p_row->'tombstones','[]'::jsonb),'evidenceKind',p_row->'evidenceKind','sourceDigest',p_row->'sourceDigest','sourceCutoffSha256',p_row->'sourceCutoffSha256','sourceCutoffUtc',p_row->'sourceCutoffUtc','sourceRunId',p_row->'sourceRunId');
 INSERT INTO analytics.evidence_packet_v2(occurred_at,packet_id,instance_id,target_revision,evidence_kind,source_run_id,source_digest,identity_digest,source_cutoff_digest,source_cutoff_utc,evidence,confidence,visibility_state) VALUES(coalesce((p_row->>'occurredAtUtc')::timestamptz,clock_timestamp()),(p_row->>'packetId')::uuid,p_instance_id,p_target_revision,coalesce(p_row->>'evidenceKind','metric'),(p_row->>'sourceRunId')::uuid,coalesce(decode(nullif(p_row->>'sourceDigest',''),'hex'),sha256(convert_to(coalesce(p_row->>'identitySha256','')||'|'||coalesce(p_row->>'sourceCutoffSha256',''),'UTF8'))),coalesce(decode(nullif(p_row->>'identitySha256',''),'hex'),sha256(convert_to(coalesce(p_row->>'identitySha256',''),'UTF8'))),coalesce(decode(nullif(p_row->>'sourceCutoffSha256',''),'hex'),sha256(convert_to(coalesce(p_row->>'sourceCutoffSha256',''),'UTF8'))),(p_row->>'sourceCutoffUtc')::timestamptz,stored_evidence,coalesce((p_row->>'confidence')::numeric,0),coalesce(p_row->>'state',p_row->>'visibilityState','complete')) ON CONFLICT DO NOTHING;
 RETURN true;
END $m10_evidence_write$;
DROP FUNCTION IF EXISTS analytics.commit_incident_thread(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea);
CREATE OR REPLACE FUNCTION analytics.commit_incident_thread(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_row jsonb,p_result_digest bytea) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $m10_incident_write$
BEGIN IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_row)<>'object' OR octet_length(p_row::text)>262144 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 incident write bounds rejected' USING ERRCODE='22023'; END IF; PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token); IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='correlation' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'M10 incident job fence conflict' USING ERRCODE='40001'; END IF; IF NOT control.record_m10_analytics_replay(p_operation_id,'incident',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('row',p_row)) THEN RETURN false; END IF; INSERT INTO analytics.incident_thread(thread_id,instance_id,target_revision,opened_at,closed_at,state,summary,current_generation) VALUES((p_row->>'threadId')::uuid,p_instance_id,p_target_revision,(p_row->>'openedAtUtc')::timestamptz,(p_row->>'closedAtUtc')::timestamptz,coalesce(p_row->>'state','open'),coalesce(p_row->'summary','{}'::jsonb),coalesce((p_row->>'currentGeneration')::bigint,1)) ON CONFLICT DO NOTHING; RETURN true; END $m10_incident_write$;
REVOKE ALL ON FUNCTION analytics.commit_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.commit_metric_forecast(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.commit_evidence_packet(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.commit_incident_thread(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
 GRANT EXECUTE ON FUNCTION analytics.commit_metric_baselines(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.commit_metric_forecast(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.commit_evidence_packet(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea),analytics.commit_incident_thread(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;
CREATE OR REPLACE FUNCTION reporting.list_metric_baselines(p_instance_id uuid,p_metric_key text,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.metric_baseline LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT b.* FROM analytics.metric_baseline b WHERE b.instance_id=p_instance_id AND b.metric_key=p_metric_key AND b.window_start>=p_from_utc AND b.window_end<=p_to_utc AND b.computed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 200 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY b.window_start,b.generation LIMIT p_limit;
$$;
CREATE OR REPLACE FUNCTION reporting.list_metric_baselines(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.metric_baseline LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT b.* FROM analytics.metric_baseline b WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND b.instance_id=p_instance_id AND b.target_revision=p_target_revision AND b.metric_key=p_metric_key AND b.window_start>=p_from_utc AND b.window_end<=p_to_utc AND b.computed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 200 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY b.window_start,b.generation LIMIT p_limit;
$$;
CREATE OR REPLACE FUNCTION reporting.list_incidents(p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.incident_thread LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT i.* FROM analytics.incident_thread i WHERE i.instance_id=p_instance_id AND i.opened_at>=p_from_utc AND i.opened_at<p_to_utc AND i.opened_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 100 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY i.opened_at,i.thread_id LIMIT p_limit;
$$;
CREATE OR REPLACE FUNCTION reporting.list_incidents(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.incident_thread LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT i.* FROM analytics.incident_thread i WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND i.instance_id=p_instance_id AND i.target_revision=p_target_revision AND i.opened_at>=p_from_utc AND i.opened_at<p_to_utc AND i.opened_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 100 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) ORDER BY i.opened_at,i.thread_id LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.list_metric_baselines(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz),reporting.list_incidents(uuid,bigint,timestamptz,timestamptz,integer,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_metric_baselines(uuid,bigint,text,timestamptz,timestamptz,integer,timestamptz),reporting.list_incidents(uuid,bigint,timestamptz,timestamptz,integer,timestamptz) TO sqlobserver_server,sqlobserver_collector;
DROP FUNCTION IF EXISTS analytics.commit_incident_generation(uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea);
CREATE OR REPLACE FUNCTION analytics.commit_incident_generation(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_row jsonb,p_result_digest bytea) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,analytics,control SET TimeZone='UTC' AS $m10_generation_write$
BEGIN
  IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<=0 OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_row)<>'object' OR octet_length(p_row::text)>262144 OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text OR control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'M10 incident generation bounds rejected' USING ERRCODE='22023'; END IF;
 IF p_row->>'threadId' IS NULL OR p_row->>'generation' IS NULL OR (p_row->>'generation')::bigint<1 OR p_row->>'observedAtUtc' IS NULL OR p_row->>'state' IS NULL OR p_row->>'correlationDigest' !~ '^[0-9a-fA-F]{64}$' OR p_row->>'supersedesPrevious' IS NULL OR jsonb_typeof(coalesce(p_row->'details','{}'::jsonb))<>'object' THEN RAISE EXCEPTION 'M10 incident generation identity fields are incomplete' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token); IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='correlation' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.status='running' AND j.work_key=p_work_key AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'M10 incident generation job fence conflict' USING ERRCODE='40001'; END IF; IF NOT EXISTS(SELECT 1 FROM analytics.incident_thread t WHERE t.thread_id=(p_row->>'threadId')::uuid AND t.instance_id=p_instance_id AND t.target_revision=p_target_revision) THEN RAISE EXCEPTION 'incident thread target revision mismatch' USING ERRCODE='40001'; END IF; IF NOT control.record_m10_analytics_replay(p_operation_id,'incident-generation',p_job_id,p_instance_id,p_target_revision,p_work_key,p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,jsonb_build_object('row',p_row)) THEN RETURN false; END IF; INSERT INTO analytics.incident_generation(instance_id,target_revision,thread_id,generation,observed_at,state,evidence_packet_id,correlation_digest,supersedes_previous,details) VALUES(p_instance_id,p_target_revision,(p_row->>'threadId')::uuid,(p_row->>'generation')::bigint,(p_row->>'observedAtUtc')::timestamptz,p_row->>'state',(p_row->>'evidencePacketId')::uuid,decode(p_row->>'correlationDigest','hex'),(p_row->>'supersedesPrevious')::boolean,coalesce(p_row->'details','{}'::jsonb)) ON CONFLICT (instance_id,target_revision,thread_id,generation) DO NOTHING; RETURN true; END $m10_generation_write$;
 REVOKE ALL ON FUNCTION reporting.list_metric_baselines(uuid,text,timestamptz,timestamptz,integer,timestamptz),reporting.list_incidents(uuid,timestamptz,timestamptz,integer,timestamptz),analytics.commit_incident_generation(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
 GRANT EXECUTE ON FUNCTION analytics.commit_incident_generation(uuid,uuid,uuid,bigint,text,uuid,bigint,bytea,jsonb,bytea) TO sqlobserver_collector;

CREATE OR REPLACE FUNCTION control.enqueue_m10_analytics_job(p_job_id uuid,p_job_kind text,p_instance_id uuid,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_job_enqueue$
BEGIN
 IF p_job_id IS NULL OR p_job_kind NOT IN ('rollup','baseline','forecast','correlation','evidence','incident','retention') OR p_work_key IS NULL OR length(p_work_key)>256 OR p_fencing_token<=0 OR p_job_kind<>'retention' AND p_instance_id IS NULL THEN RAISE EXCEPTION 'analytics job bounds rejected' USING ERRCODE='22023'; END IF;
 INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,status,work_key,owner_execution_id,fencing_token) VALUES(p_job_id,p_job_kind,p_instance_id,(SELECT revision FROM control.observation_target WHERE instance_id=p_instance_id),'queued',p_work_key,p_owner_execution_id,p_fencing_token) ON CONFLICT(job_id) DO NOTHING;
 RETURN true;
END $m10_job_enqueue$;
CREATE OR REPLACE FUNCTION control.complete_m10_analytics_job(p_job_id uuid,p_status text,p_error text DEFAULT NULL)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_job_complete$
BEGIN IF p_job_id IS NULL OR p_status NOT IN ('succeeded','partial','failed','cancelled') OR p_error IS NOT NULL AND length(p_error)>1024 THEN RAISE EXCEPTION 'analytics job completion bounds rejected' USING ERRCODE='22023'; END IF; UPDATE control.analytics_job SET status=p_status,last_error=p_error,completed_at=clock_timestamp() WHERE job_id=p_job_id AND status IN ('queued','running'); RETURN FOUND; END $m10_job_complete$;
CREATE OR REPLACE FUNCTION control.replay_m10_analytics_job(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_request_digest bytea,p_result_digest bytea,p_result jsonb)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_job_replay$
BEGIN IF p_operation_id IS NULL OR p_job_id IS NULL OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_result)<>'object' OR octet_length(p_result::text)>8388608 THEN RAISE EXCEPTION 'analytics replay bounds rejected' USING ERRCODE='22023'; END IF; INSERT INTO control.analytics_replay(operation_id,job_id,instance_id,request_digest,result_digest,result) VALUES(p_operation_id,p_job_id,p_instance_id,p_request_digest,p_result_digest,p_result) ON CONFLICT(operation_id) DO NOTHING; IF NOT FOUND AND EXISTS(SELECT 1 FROM control.analytics_replay WHERE operation_id=p_operation_id AND (request_digest<>p_request_digest OR result_digest<>p_result_digest)) THEN RAISE EXCEPTION 'divergent analytics replay digest' USING ERRCODE='40001'; END IF; RETURN true; END $m10_job_replay$;
REVOKE ALL ON FUNCTION control.enqueue_m10_analytics_job(uuid,text,uuid,text,uuid,bigint),control.complete_m10_analytics_job(uuid,text,text),control.replay_m10_analytics_job(uuid,uuid,uuid,bytea,bytea,jsonb) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.enqueue_m10_analytics_job(uuid,text,uuid,text,uuid,bigint) TO sqlobserver_server;

-- Server-facing M10 mutations and projections.  The server receives only
-- these fixed, bounded functions; protected base tables remain inaccessible.
CREATE TABLE IF NOT EXISTS control.m10_mutation_replay
(
 operation_id uuid PRIMARY KEY, action_name text NOT NULL, instance_id uuid,
 request_digest bytea NOT NULL, result_digest bytea NOT NULL, result jsonb NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK(octet_length(request_digest)=32 AND octet_length(result_digest)=32 AND jsonb_typeof(result)='object')
);
ALTER TABLE control.m10_mutation_replay ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.m10_mutation_replay FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS m10_mutation_replay_scope ON control.m10_mutation_replay;
DROP POLICY IF EXISTS m10_mutation_replay_owner ON control.m10_mutation_replay;
CREATE POLICY m10_mutation_replay_scope ON control.m10_mutation_replay USING(instance_id IS NOT NULL AND instance_id::text=current_setting('sqlobserver.target_scope',true)) WITH CHECK(instance_id IS NOT NULL AND instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY m10_mutation_replay_owner ON control.m10_mutation_replay FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
DROP TRIGGER IF EXISTS m10_mutation_replay_append_only ON control.m10_mutation_replay;
CREATE TRIGGER m10_mutation_replay_append_only BEFORE UPDATE OR DELETE ON control.m10_mutation_replay FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

-- Binding revisions are mutable only through the audited definer function,
-- while every prior value is retained in an append-only history ledger.
CREATE TABLE IF NOT EXISTS control.host_binding_history
(
 history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 instance_id uuid NOT NULL, host_id uuid NOT NULL, target_revision bigint NOT NULL,
 binding_revision bigint NOT NULL, host_name text NOT NULL,
 identity_fingerprint bytea NOT NULL, binding_state text NOT NULL,
 first_seen_at timestamptz NOT NULL, last_seen_at timestamptz NOT NULL,
 recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE control.host_binding_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE control.host_binding_history FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS m10_host_binding_history_scope ON control.host_binding_history;
DROP POLICY IF EXISTS m10_host_binding_history_owner ON control.host_binding_history;
CREATE POLICY m10_host_binding_history_scope ON control.host_binding_history FOR SELECT USING (instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY m10_host_binding_history_owner ON control.host_binding_history FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE OR REPLACE FUNCTION control.capture_m10_host_binding_history() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_binding_history$
BEGIN
 INSERT INTO control.host_binding_history(instance_id,host_id,target_revision,binding_revision,host_name,identity_fingerprint,binding_state,first_seen_at,last_seen_at)
 VALUES(OLD.instance_id,OLD.host_id,OLD.target_revision,OLD.binding_revision,OLD.host_name,OLD.identity_fingerprint,OLD.binding_state,OLD.first_seen_at,OLD.last_seen_at);
 RETURN NEW;
END $m10_binding_history$;
DROP TRIGGER IF EXISTS m10_host_binding_append_only ON control.host_binding;
CREATE TRIGGER m10_host_binding_history BEFORE UPDATE ON control.host_binding FOR EACH ROW EXECUTE FUNCTION control.capture_m10_host_binding_history();
REVOKE ALL ON FUNCTION control.capture_m10_host_binding_history() FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- The collector resolves a host only through the control-plane binding and
-- the latest profile for the exact target revision.  Configuration files are
-- deliberately not an authority for this identity.
CREATE OR REPLACE FUNCTION control.resolve_m10_host_target(p_instance_id uuid,p_target_revision bigint)
RETURNS TABLE(host_id uuid,target_revision bigint,binding_revision bigint,host_name text,binding_state text,identity_fingerprint bytea,profile_revision bigint,capability_state text,profile jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,control AS $m10_host_resolver$
 SELECT b.host_id,b.target_revision,b.binding_revision,b.host_name,b.binding_state,b.identity_fingerprint,
        p.profile_revision,p.capability_state,p.profile
 FROM control.host_binding b
 JOIN control.observation_target t ON t.instance_id=b.instance_id AND t.revision=b.target_revision
 JOIN LATERAL (SELECT hp.profile_revision,hp.capability_state,hp.profile
               FROM control.host_profile hp
               WHERE hp.instance_id=b.instance_id AND hp.target_revision=b.target_revision
                 AND hp.host_id=b.host_id AND hp.binding_revision=b.binding_revision
               ORDER BY hp.profile_revision DESC LIMIT 1) p ON true
 WHERE b.instance_id=p_instance_id AND b.target_revision=p_target_revision
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision;
$m10_host_resolver$;
REVOKE ALL ON FUNCTION control.resolve_m10_host_target(uuid,bigint) FROM PUBLIC,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.resolve_m10_host_target(uuid,bigint) TO sqlobserver_server,sqlobserver_collector;

DROP FUNCTION IF EXISTS control.update_m10_host_binding(uuid,uuid,text,bytea,bigint,bigint,bigint,jsonb,uuid,bytea,text,uuid,text);
CREATE OR REPLACE FUNCTION control.update_m10_host_binding
 (p_instance_id uuid,p_host_id uuid,p_host_name text,p_identity_fingerprint bytea,p_expected_target_revision bigint,p_expected_binding_revision bigint,p_profile_revision bigint,p_profile jsonb,p_operation_id uuid,p_request_digest bytea,p_actor_sid text,p_correlation_id uuid,p_change_reason text)
RETURNS TABLE(operation_id uuid,state text,target_revision bigint,binding_revision bigint,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control,audit AS $m10_bind$
DECLARE t control.observation_target%ROWTYPE; b control.host_binding%ROWTYPE; now_utc timestamptz:=clock_timestamp(); result_digest bytea; existing_request bytea; existing_result jsonb;
BEGIN
  IF coalesce(current_setting('sqlobserver.role',true),'') <> 'TargetAdministrator' OR coalesce(current_setting('sqlobserver.target_scope',true),'') IS DISTINCT FROM p_instance_id::text OR p_instance_id IS NULL OR p_host_id IS NULL OR p_operation_id IS NULL OR octet_length(p_request_digest)<>32 OR octet_length(p_identity_fingerprint)<>32 OR p_expected_target_revision<1 OR p_expected_binding_revision<0 OR p_profile_revision<1 OR p_host_name IS NULL OR octet_length(p_host_name) NOT BETWEEN 1 AND 512 OR p_profile IS NULL OR jsonb_typeof(p_profile)<>'object' OR octet_length(p_profile::text)>65536 OR (p_profile - 'osFamily' - 'osVersion' - 'cpuCount' - 'memoryBytes' - 'capabilityState')<>'{}'::jsonb OR coalesce(p_profile->>'osFamily','')='' OR length(p_profile->>'osFamily')>128 OR coalesce(p_profile->>'osVersion','')='' OR length(p_profile->>'osVersion')>128 OR (p_profile->>'cpuCount')::integer IS NULL OR (p_profile->>'cpuCount')::integer NOT BETWEEN 1 AND 65536 OR (p_profile->>'memoryBytes')::bigint IS NULL OR (p_profile->>'memoryBytes')::bigint NOT BETWEEN 0 AND 1152921504606846976 OR coalesce(p_profile->>'capabilityState','') NOT IN ('available','partial','unsupported','permission_denied') OR p_actor_sid IS NULL OR p_correlation_id IS NULL OR p_change_reason IS NULL OR length(p_change_reason) NOT BETWEEN 1 AND 512 THEN RAISE EXCEPTION 'host binding authorization or bounds rejected' USING ERRCODE='42501'; END IF;
 SELECT * INTO t FROM control.observation_target WHERE instance_id=p_instance_id FOR UPDATE;
 IF NOT FOUND OR t.revision<>p_expected_target_revision OR t.lifecycle_state NOT IN ('pending_discovery','active') THEN RAISE EXCEPTION 'host binding target revision conflict' USING ERRCODE='40001'; END IF;
 IF EXISTS(SELECT 1 FROM control.m10_mutation_replay r WHERE r.operation_id=p_operation_id) THEN SELECT r.request_digest,r.result INTO existing_request,existing_result FROM control.m10_mutation_replay r WHERE r.operation_id=p_operation_id; IF existing_request<>p_request_digest THEN RAISE EXCEPTION 'divergent host binding replay' USING ERRCODE='40001'; END IF; RETURN QUERY SELECT p_operation_id,'replayed',t.revision,(existing_result->>'bindingRevision')::bigint,now_utc; RETURN; END IF;
 SELECT * INTO b FROM control.host_binding WHERE instance_id=p_instance_id AND target_revision=t.revision FOR UPDATE;
 IF FOUND AND b.binding_revision<>p_expected_binding_revision THEN RAISE EXCEPTION 'host binding revision conflict' USING ERRCODE='40001'; END IF;
 IF NOT FOUND THEN INSERT INTO control.host_binding(instance_id,host_id,target_revision,binding_revision,host_name,identity_fingerprint,binding_state) VALUES(p_instance_id,p_host_id,t.revision,1,p_host_name,p_identity_fingerprint,'active') RETURNING * INTO b;
 ELSE UPDATE control.host_binding SET host_id=p_host_id,host_name=p_host_name,identity_fingerprint=p_identity_fingerprint,binding_revision=b.binding_revision+1,binding_state='active',last_seen_at=now_utc WHERE instance_id=p_instance_id AND target_revision=t.revision RETURNING * INTO b; END IF;
 IF p_profile_revision<>b.binding_revision THEN RAISE EXCEPTION 'host profile revision must equal binding revision' USING ERRCODE='22023'; END IF;
 INSERT INTO control.host_profile(instance_id,target_revision,host_id,binding_revision,profile_revision,os_family,os_version,cpu_count,memory_bytes,capability_state,profile)
 VALUES(p_instance_id,t.revision,p_host_id,b.binding_revision,p_profile_revision,p_profile->>'osFamily',p_profile->>'osVersion',(p_profile->>'cpuCount')::integer,(p_profile->>'memoryBytes')::bigint,p_profile->>'capabilityState',p_profile)
 ON CONFLICT(instance_id,target_revision,host_id,binding_revision,profile_revision) DO NOTHING;
 IF EXISTS (SELECT 1 FROM control.host_profile hp WHERE hp.instance_id=p_instance_id AND hp.target_revision=t.revision AND hp.host_id=p_host_id AND hp.binding_revision=b.binding_revision AND hp.profile_revision=p_profile_revision AND hp.profile IS DISTINCT FROM p_profile) THEN RAISE EXCEPTION 'divergent host profile revision' USING ERRCODE='40001'; END IF;
 result_digest:=sha256(convert_to(p_operation_id::text||'|'||b.binding_revision::text,'UTF8'));
 INSERT INTO control.m10_mutation_replay(operation_id,action_name,instance_id,request_digest,result_digest,result) VALUES(p_operation_id,'host_binding',p_instance_id,p_request_digest,result_digest,jsonb_build_object('bindingRevision',b.binding_revision,'targetRevision',t.revision));
 INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(now_utc,p_operation_id,'user',p_actor_sid,'host.binding.update','allowed','succeeded','observation_target',p_instance_id::text,p_correlation_id,p_request_digest,jsonb_build_object('changeReason',p_change_reason,'bindingRevision',b.binding_revision));
 RETURN QUERY SELECT p_operation_id,'committed',t.revision,b.binding_revision,now_utc;
END $m10_bind$;

CREATE OR REPLACE FUNCTION control.enqueue_m10_backfill
 (p_instance_id uuid,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_expected_target_revision bigint,p_operation_id uuid,p_request_digest bytea,p_actor_sid text,p_correlation_id uuid,p_change_reason text)
RETURNS TABLE(operation_id uuid,state text,target_revision bigint,job_count integer,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control,audit AS $m10_backfill_enqueue$
DECLARE t control.observation_target%ROWTYPE; d date; n integer:=0; now_utc timestamptz:=clock_timestamp(); job_id uuid; result_digest bytea; existing_request bytea; existing_result jsonb;
BEGIN
  IF coalesce(current_setting('sqlobserver.role',true),'') <> 'TargetAdministrator' OR coalesce(current_setting('sqlobserver.target_scope',true),'') IS DISTINCT FROM p_instance_id::text OR p_instance_id IS NULL OR p_operation_id IS NULL OR octet_length(p_request_digest)<>32 OR p_expected_target_revision<1 OR p_from_utc IS NULL OR p_to_utc IS NULL OR p_from_utc>=p_to_utc OR extract(timezone from p_from_utc)<>0 OR extract(timezone from p_to_utc)<>0 OR p_to_utc-p_from_utc>interval '90 days' OR p_metric_key IS NOT NULL AND (length(p_metric_key)>128 OR p_metric_key !~ '^[a-z][a-z0-9._-]*$') OR p_actor_sid IS NULL OR p_correlation_id IS NULL OR p_change_reason IS NULL OR length(p_change_reason) NOT BETWEEN 1 AND 512 THEN RAISE EXCEPTION 'backfill authorization or bounds rejected' USING ERRCODE='42501'; END IF;
 SELECT * INTO t FROM control.observation_target WHERE instance_id=p_instance_id FOR SHARE;
 IF NOT FOUND OR t.revision<>p_expected_target_revision THEN RAISE EXCEPTION 'backfill target revision conflict' USING ERRCODE='40001'; END IF;
 IF EXISTS(SELECT 1 FROM control.m10_mutation_replay r WHERE r.operation_id=p_operation_id) THEN SELECT r.request_digest,r.result INTO existing_request,existing_result FROM control.m10_mutation_replay r WHERE r.operation_id=p_operation_id; IF existing_request<>p_request_digest THEN RAISE EXCEPTION 'divergent backfill replay' USING ERRCODE='40001'; END IF; RETURN QUERY SELECT p_operation_id,'replayed',t.revision,coalesce((existing_result->>'jobCount')::integer,0),now_utc; RETURN; END IF;
 d:=p_from_utc::date; WHILE d::timestamptz<p_to_utc LOOP job_id:=left(encode(sha256(convert_to('m10-backfill|'||p_operation_id::text||'|'||d::text,'UTF8')),'hex'),32)::uuid; INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,cursor,status,work_key,owner_execution_id,fencing_token) VALUES(job_id,'backfill',p_instance_id,t.revision,greatest(p_from_utc,d::timestamptz),least(p_to_utc,(d+1)::timestamptz),p_metric_key,NULL,'queued','analytics/backfill',p_operation_id,1) ON CONFLICT(job_id) DO NOTHING; n:=n+1; d:=d+1; END LOOP;
 result_digest:=sha256(convert_to(p_operation_id::text||'|'||n::text,'UTF8')); INSERT INTO control.m10_mutation_replay(operation_id,action_name,instance_id,request_digest,result_digest,result) VALUES(p_operation_id,'analytics_backfill',p_instance_id,p_request_digest,result_digest,jsonb_build_object('jobCount',n,'targetRevision',t.revision)); INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(now_utc,p_operation_id,'user',p_actor_sid,'analytics.backfill.enqueue','allowed','succeeded','observation_target',p_instance_id::text,p_correlation_id,p_request_digest,jsonb_build_object('fromUtc',p_from_utc,'toUtc',p_to_utc,'jobCount',n,'changeReason',p_change_reason)); RETURN QUERY SELECT p_operation_id,'queued',t.revision,n,now_utc;
END $m10_backfill_enqueue$;

CREATE OR REPLACE FUNCTION reporting.list_m10_analytics_jobs(p_instance_id uuid,p_target_revision bigint,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS TABLE(job_id uuid,job_kind text,status text,requested_at timestamptz,started_at timestamptz,completed_at timestamptz,attempt integer,target_revision bigint,next_cursor timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
 SELECT j.job_id,j.job_kind,j.status,j.requested_at,j.started_at,j.completed_at,j.attempt,j.target_revision,j.requested_at FROM control.analytics_job j JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND (p_cursor IS NULL OR j.requested_at>p_cursor) AND j.requested_at<=p_snapshot_utc ORDER BY j.requested_at,j.job_id LIMIT least(greatest(p_limit,1),100);
$$;

CREATE OR REPLACE FUNCTION reporting.search_m10_diagnostics(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,event_id uuid,event_kind text,severity smallint,safe_metadata jsonb,collected_at timestamptz,target_revision bigint,next_cursor timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,events,control SET TimeZone='UTC' AS $$
 SELECT d.occurred_at,d.event_id,d.event_kind,d.severity,d.safe_metadata,d.collected_at,p_target_revision,d.occurred_at FROM events.diagnostic_event d JOIN control.observation_target t ON t.instance_id=d.instance_id AND t.revision=p_target_revision WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND d.instance_id=p_instance_id AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND p_from_utc>=p_snapshot_utc-interval '7 days' AND p_to_utc>p_from_utc AND p_to_utc<=p_snapshot_utc AND (p_cursor IS NULL OR d.occurred_at>p_cursor) ORDER BY d.occurred_at,d.event_id LIMIT least(greatest(p_limit,1),100);
$$;

-- Retention attestation is global and replay/audit fenced; the legacy five-
-- argument function remains available for old callers but is denied below.
CREATE OR REPLACE FUNCTION system.record_m10_recovery_attestation(p_attestation_id uuid,p_attested_by text,p_backup_set_reference text,p_expires_at timestamptz,p_digest bytea,p_request_digest bytea,p_correlation_id uuid,p_change_reason text)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system,audit AS $m10_attestation_v2$
DECLARE now_utc timestamptz:=clock_timestamp(); existing system.recovery_attestation%ROWTYPE;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator' OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' OR nullif(current_setting('sqlobserver.actor_sid',true),'') IS NULL OR p_attestation_id IS NULL OR p_request_digest IS NULL OR octet_length(p_request_digest)<>32 OR p_attested_by IS NULL OR length(p_attested_by)>256 OR p_backup_set_reference IS NULL OR length(p_backup_set_reference)>512 OR p_expires_at<=now_utc OR p_expires_at>now_utc+interval '5 years' OR octet_length(p_digest)<>32 OR p_correlation_id IS NULL OR p_change_reason IS NULL OR length(p_change_reason)>512 THEN RAISE EXCEPTION 'recovery attestation authorization or bounds rejected' USING ERRCODE='42501'; END IF;
 SELECT * INTO existing FROM system.recovery_attestation WHERE attestation_id=p_attestation_id; IF FOUND THEN IF existing.attestation_digest<>p_digest OR existing.attested_by<>p_attested_by OR existing.backup_set_reference<>p_backup_set_reference OR existing.expires_at<>p_expires_at OR existing.actor_sid IS DISTINCT FROM current_setting('sqlobserver.actor_sid',true) OR existing.correlation_id IS DISTINCT FROM p_correlation_id OR existing.change_reason IS DISTINCT FROM p_change_reason OR existing.request_digest IS DISTINCT FROM p_request_digest THEN RAISE EXCEPTION 'divergent recovery attestation replay' USING ERRCODE='40001'; END IF; RETURN false; END IF;
 INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest,actor_sid,correlation_id,change_reason,request_digest) VALUES(p_attestation_id,now_utc,p_attested_by,p_backup_set_reference,p_expires_at,p_digest,current_setting('sqlobserver.actor_sid',true),p_correlation_id,p_change_reason,p_request_digest); INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(now_utc,p_attestation_id,'user',current_setting('sqlobserver.actor_sid',true),'retention.attestation.record','allowed','succeeded','retention_attestation',p_attestation_id::text,p_correlation_id,p_request_digest,jsonb_build_object('expiresAtUtc',p_expires_at,'attestedBy',p_attested_by,'changeReason',p_change_reason)); RETURN true;
END $m10_attestation_v2$;
CREATE OR REPLACE FUNCTION system.record_m10_recovery_attestation(p_attestation_id uuid,p_attested_by text,p_backup_set_reference text,p_expires_at timestamptz,p_digest bytea,p_request_digest bytea,p_actor_sid text,p_correlation_id uuid,p_change_reason text)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,system,audit AS $m10_attestation_v3$
DECLARE now_utc timestamptz:=clock_timestamp(); existing system.recovery_attestation%ROWTYPE;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator' OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' OR p_attestation_id IS NULL OR p_request_digest IS NULL OR octet_length(p_request_digest)<>32 OR p_actor_sid IS NULL OR length(p_actor_sid)=0 OR p_attested_by IS NULL OR length(p_attested_by)>256 OR p_backup_set_reference IS NULL OR length(p_backup_set_reference)>512 OR p_expires_at<=now_utc OR p_expires_at>now_utc+interval '5 years' OR octet_length(p_digest)<>32 OR p_correlation_id IS NULL OR p_change_reason IS NULL OR length(p_change_reason) NOT BETWEEN 1 AND 512 THEN RAISE EXCEPTION 'recovery attestation authorization or bounds rejected' USING ERRCODE='42501'; END IF;
  SELECT * INTO existing FROM system.recovery_attestation WHERE attestation_id=p_attestation_id; IF FOUND THEN IF existing.attestation_digest<>p_digest OR existing.attested_by<>p_attested_by OR existing.backup_set_reference<>p_backup_set_reference OR existing.expires_at<>p_expires_at OR existing.actor_sid IS DISTINCT FROM p_actor_sid OR existing.correlation_id IS DISTINCT FROM p_correlation_id OR existing.change_reason IS DISTINCT FROM p_change_reason OR existing.request_digest IS DISTINCT FROM p_request_digest THEN RAISE EXCEPTION 'divergent recovery attestation replay' USING ERRCODE='40001'; END IF; RETURN false; END IF;
  INSERT INTO system.recovery_attestation(attestation_id,attested_at,attested_by,backup_set_reference,expires_at,attestation_digest,actor_sid,correlation_id,change_reason,request_digest) VALUES(p_attestation_id,now_utc,p_attested_by,p_backup_set_reference,p_expires_at,p_digest,p_actor_sid,p_correlation_id,p_change_reason,p_request_digest);
 INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,parameter_digest,safe_details) VALUES(now_utc,p_attestation_id,'user',p_actor_sid,'retention.attestation.record','allowed','succeeded','retention_attestation',p_attestation_id::text,p_correlation_id,p_request_digest,jsonb_build_object('expiresAtUtc',p_expires_at,'attestedBy',p_attested_by,'changeReason',p_change_reason)); RETURN true;
END $m10_attestation_v3$;
REVOKE ALL ON FUNCTION control.update_m10_host_binding(uuid,uuid,text,bytea,bigint,bigint,bigint,jsonb,uuid,bytea,text,uuid,text),control.enqueue_m10_backfill(uuid,timestamptz,timestamptz,text,bigint,uuid,bytea,text,uuid,text),system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea,bytea,uuid,text) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.update_m10_host_binding(uuid,uuid,text,bytea,bigint,bigint,bigint,jsonb,uuid,bytea,text,uuid,text),control.enqueue_m10_backfill(uuid,timestamptz,timestamptz,text,bigint,uuid,bytea,text,uuid,text),reporting.list_m10_analytics_jobs(uuid,bigint,integer,timestamptz,timestamptz),system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea,bytea,text,uuid,text) TO sqlobserver_server;
REVOKE ALL ON FUNCTION system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea,bytea,uuid,text) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea,bytea,text,uuid,text) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea,bytea,text,uuid,text) TO sqlobserver_server;

CREATE OR REPLACE FUNCTION reporting.list_m10_host_status(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS TABLE(host_id uuid,target_revision bigint,binding_revision bigint,host_name text,binding_state text,capability_state text,observed_at timestamptz,next_cursor timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
 SELECT b.host_id,b.target_revision,b.binding_revision,b.host_name,b.binding_state,coalesce(p.capability_state,'unknown'),coalesce(p.observed_at,b.last_seen_at),coalesce(p.observed_at,b.last_seen_at) FROM control.host_binding b JOIN control.observation_target t ON t.instance_id=b.instance_id AND t.revision=p_target_revision LEFT JOIN LATERAL (SELECT capability_state,observed_at FROM control.host_profile hp WHERE hp.instance_id=b.instance_id AND hp.target_revision=b.target_revision AND hp.host_id=b.host_id AND hp.binding_revision=b.binding_revision ORDER BY hp.profile_revision DESC LIMIT 1) p ON true WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND b.instance_id=p_instance_id AND b.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND (p_cursor IS NULL OR coalesce(p.observed_at,b.last_seen_at)>p_cursor) AND coalesce(p.observed_at,b.last_seen_at)<=p_snapshot_utc AND p_from_utc<p_to_utc AND coalesce(p.observed_at,b.last_seen_at)>=p_from_utc AND coalesce(p.observed_at,b.last_seen_at)<p_to_utc ORDER BY coalesce(p.observed_at,b.last_seen_at) DESC,b.host_id LIMIT least(greatest(p_limit,1),200);
$$;
CREATE OR REPLACE FUNCTION reporting.list_m10_host_metrics_scoped(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS SETOF telemetry.host_metric_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$ SELECT h.* FROM telemetry.host_metric_snapshot_v2 h JOIN control.observation_target t ON t.instance_id=h.instance_id AND t.revision=p_target_revision WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc AND (p_cursor IS NULL OR h.observed_at>p_cursor) ORDER BY h.observed_at,h.run_id,h.metric_key LIMIT least(greatest(p_limit,1),200) $$;
CREATE OR REPLACE FUNCTION reporting.list_m10_replication_scoped(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS SETOF telemetry.replication_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$ SELECT r.* FROM telemetry.replication_snapshot_v2 r JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=p_target_revision WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND (p_cursor IS NULL OR r.observed_at>p_cursor) ORDER BY r.observed_at,r.run_id LIMIT least(greatest(p_limit,1),200) $$;
CREATE OR REPLACE FUNCTION reporting.list_m10_incidents_scoped(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS TABLE(thread_id uuid,opened_at timestamptz,closed_at timestamptz,current_generation bigint,target_revision bigint,next_cursor timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$ SELECT i.thread_id,i.opened_at,i.closed_at,i.current_generation,i.target_revision,i.opened_at FROM analytics.incident_thread i JOIN control.observation_target t ON t.instance_id=i.instance_id AND t.revision=p_target_revision WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND i.instance_id=p_instance_id AND i.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND i.opened_at>=p_from_utc AND i.opened_at<p_to_utc AND i.opened_at<=p_snapshot_utc AND (p_cursor IS NULL OR i.opened_at>p_cursor) ORDER BY i.opened_at,i.thread_id LIMIT least(greatest(p_limit,1),100) $$;
CREATE OR REPLACE FUNCTION reporting.list_m10_evidence_packets(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor timestamptz,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,packet_id uuid,evidence_kind text,source_run_id uuid,source_digest bytea,confidence numeric,visibility_state text,target_revision bigint,next_cursor timestamptz)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$ SELECT e.occurred_at,e.packet_id,e.evidence_kind,e.source_run_id,e.source_digest,e.confidence,e.visibility_state,e.target_revision,e.occurred_at FROM analytics.evidence_packet_v2 e JOIN control.observation_target t ON t.instance_id=e.instance_id AND t.revision=p_target_revision WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND e.instance_id=p_instance_id AND e.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND e.occurred_at>=p_from_utc AND e.occurred_at<p_to_utc AND e.occurred_at<=p_snapshot_utc AND (p_cursor IS NULL OR e.occurred_at>p_cursor) ORDER BY e.occurred_at,e.packet_id LIMIT least(greatest(p_limit,1),200) $$;
REVOKE ALL ON FUNCTION reporting.list_m10_host_status(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_host_metrics_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_replication_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_incidents_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_evidence_packets(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_m10_host_status(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_host_metrics_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_replication_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_incidents_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz),reporting.list_m10_evidence_packets(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz) TO sqlobserver_server;
REVOKE ALL ON FUNCTION system.record_m10_recovery_attestation(uuid,text,text,timestamptz,bytea) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- Hosted backfill control plane.  Claims, cursor advancement, completion,
-- and replay all carry the worker lease fence; the page reader is a fixed
-- bounded projection over raw metrics and never accepts identifiers/SQL.
CREATE OR REPLACE FUNCTION control.claim_m10_analytics_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,from_utc timestamptz,to_utc timestamptz,metric_key text,cursor text,current_day_utc timestamptz,cursor_source_kind text,cursor_observed_at timestamptz,cursor_source_id uuid,cursor_metric_key text,cursor_dimension_hash bytea,cursor_ordinal integer,cursor_target_id uuid,cursor_target_revision bigint,cursor_day_utc timestamptz,cursor_catalog_version integer)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_claim_jobs$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/backfill' OR p_owner_execution_id IS NULL OR p_fencing_token<=0 OR p_limit NOT BETWEEN 1 AND 2 THEN RAISE EXCEPTION 'analytics backfill claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
   SELECT j.job_id FROM control.analytics_job j
   JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
   WHERE j.job_kind='backfill' AND j.from_utc IS NOT NULL AND j.to_utc IS NOT NULL
     AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
          (SELECT 1 FROM control.worker_lease l
           WHERE l.work_key=j.work_key AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
   ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
   UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5
   RETURNING j.job_id
 ), claimed AS (
   UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,started_at=clock_timestamp(),attempt=j.attempt+1
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5
   RETURNING j.*
  ) SELECT c.job_id,c.instance_id,c.target_revision,c.from_utc,c.to_utc,c.metric_key,c.cursor,c.current_day_utc,c.cursor_source_kind,c.cursor_observed_at,c.cursor_source_id,c.cursor_metric_key,c.cursor_dimension_hash,c.cursor_ordinal,c.cursor_target_id,c.cursor_target_revision,c.cursor_day_utc,c.cursor_catalog_version FROM claimed c;
END $m10_claim_jobs$;

-- Production derivations use a separate lease lane. A fixed scheduler derives
-- bounded daily jobs from authoritative target revisions; deterministic IDs
-- make repeated due-work scheduling idempotent.
-- Keep bucket selection independently testable: p_now is the only clock input,
-- and the UTC function returns the latest completed 5m/hour/day bucket.
CREATE OR REPLACE FUNCTION control.m10_rollup_due_buckets(p_now timestamptz)
RETURNS TABLE(interval_name text,bucket_start timestamptz,width interval)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_due_buckets$
 SELECT '5m'::text,
        date_trunc('hour',p_now)+(floor(extract(minute FROM p_now)/5)::integer)*interval '5 minutes'-interval '5 minutes',
        interval '5 minutes'
 WHERE p_now IS NOT NULL
 UNION ALL
 SELECT 'hour'::text,date_trunc('hour',p_now)-interval '1 hour',interval '1 hour'
 WHERE p_now IS NOT NULL
 UNION ALL
 SELECT 'day'::text,date_trunc('day',p_now)-interval '1 day',interval '1 day'
 WHERE p_now IS NOT NULL;
$m10_due_buckets$;
REVOKE ALL ON FUNCTION control.m10_rollup_due_buckets(timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE OR REPLACE FUNCTION control.schedule_m10_derivation_jobs(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control,telemetry SET TimeZone='UTC' AS $m10_schedule_derivation$
DECLARE inserted_count integer:=0; schedule_now timestamptz:=clock_timestamp(); day_end timestamptz:=date_trunc('day',schedule_now);
BEGIN
 IF p_owner_execution_id IS NULL OR p_fencing_token<=0 THEN RAISE EXCEPTION 'analytics derivation schedule bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/derivation',p_owner_execution_id,p_fencing_token);
 WITH targets AS (
   SELECT t.instance_id,t.revision FROM control.observation_target t
   WHERE t.lifecycle_state IN ('pending_discovery','active') AND t.revision>0 ORDER BY t.instance_id LIMIT 128
   ), dimension_candidates AS (
     SELECT x.instance_id,x.revision,r.dimension_hash
     FROM targets x JOIN analytics.metric_rollup_v2 r ON r.instance_id=x.instance_id AND r.target_revision=x.revision
     WHERE r.metric_key='host.volume.free_bytes' AND r.rollup_interval='day' AND r.visibility_state='complete'
       AND r.truncated=false AND r.value IS NOT NULL
       AND r.bucket_start >= day_end-interval '29 days' AND r.bucket_start < day_end-interval '1 day'
       AND r.bucket_start+interval '1 day' <= day_end-interval '1 day'
     GROUP BY x.instance_id,x.revision,r.dimension_hash
   ), dimensions AS (
     SELECT instance_id,revision,dimension_hash
     FROM (SELECT c.*,row_number() OVER (PARTITION BY c.instance_id,c.revision ORDER BY c.dimension_hash) AS dimension_ordinal FROM dimension_candidates c) bounded
     WHERE dimension_ordinal<=256
  ), due AS (
    SELECT left(encode(sha256(convert_to('m10-derivation|'||x.instance_id::text||'|'||x.revision::text||'|'||k.job_kind||'|'||day_end::text,'UTF8')),'hex'),32)::uuid job_id,
            x.instance_id,x.revision,k.job_kind,
            CASE WHEN k.job_kind='evidence' THEN day_end-interval '1 day' ELSE day_end-interval '29 days' END from_utc,
            CASE WHEN k.job_kind='evidence' THEN day_end ELSE day_end-interval '1 day' END to_utc,
            CASE WHEN k.job_kind='evidence' THEN day_end ELSE day_end-interval '1 day' END source_cutoff_utc,
           k.metric_key,NULL::bytea AS dimensions_hash
    FROM targets x CROSS JOIN (VALUES
       ('baseline'::text,'host.cpu.percent'::text),
       ('evidence'::text,NULL::text),
       ('correlation'::text,NULL::text)) k(job_kind,metric_key)
    UNION ALL
    SELECT left(encode(sha256(convert_to('m10-derivation|'||d.instance_id::text||'|'||d.revision::text||'|forecast|'||encode(d.dimension_hash,'hex')||'|'||day_end::text,'UTF8')),'hex'),32)::uuid,
           d.instance_id,d.revision,'forecast',day_end-interval '29 days',day_end-interval '1 day',day_end-interval '1 day','host.volume.free_bytes',d.dimension_hash
    FROM dimensions d
  )
   INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,dimensions_hash)
   SELECT d.job_id,d.job_kind,d.instance_id,d.revision,d.from_utc,d.to_utc,d.metric_key,d.source_cutoff_utc,1,'queued','analytics/derivation',d.dimensions_hash FROM due d
  ON CONFLICT(job_id) DO NOTHING;
  -- Live rollups use the same derivation lane as baselines/forecasts so the
  -- existing worker can claim all three UTC granularities in one bounded pass.
  WITH targets AS (
    SELECT t.instance_id,t.revision FROM control.observation_target t
    WHERE t.lifecycle_state IN ('pending_discovery','active') AND t.revision>0 ORDER BY t.instance_id LIMIT 128
  ), buckets AS (
    SELECT b.interval_name,b.bucket_start,b.width
    FROM control.m10_rollup_due_buckets(schedule_now) b
  )
  INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,metric_key,source_cutoff_utc,generation,status,work_key,rollup_interval)
  SELECT left(encode(sha256(convert_to('m10-rollup|'||t.instance_id::text||'|'||t.revision::text||'|'||c.metric_key||'|'||b.interval_name||'|'||b.bucket_start::text,'UTF8')),'hex'),32)::uuid,'rollup',t.instance_id,t.revision,b.bucket_start,b.bucket_start+b.width,c.metric_key,b.bucket_start+b.width,1,'queued','analytics/derivation',b.interval_name
  FROM targets t CROSS JOIN analytics.metric_catalog c CROSS JOIN buckets b WHERE c.enabled
  ON CONFLICT(job_id) DO NOTHING;
  GET DIAGNOSTICS inserted_count=ROW_COUNT;
 RETURN inserted_count;
END $m10_schedule_derivation$;

-- Claims include queued, partial, and safely expired work; an exhausted claim
-- is atomically made
-- terminal so it cannot remain permanently unclaimable.
CREATE OR REPLACE FUNCTION control.claim_m10_derivation_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,from_utc timestamptz,to_utc timestamptz,source_cutoff_utc timestamptz,generation bigint,metric_key text,horizon_seconds double precision)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_claim_derivation$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/derivation' OR p_owner_execution_id IS NULL OR p_fencing_token<=0 OR p_limit NOT BETWEEN 1 AND 2 THEN RAISE EXCEPTION 'analytics derivation claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
   SELECT j.job_id FROM control.analytics_job j
   JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
    WHERE j.work_key='analytics/derivation'
      AND j.job_kind IN ('baseline','forecast','evidence','correlation')
     AND j.from_utc IS NOT NULL AND j.to_utc IS NOT NULL
     AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
       (SELECT 1 FROM control.worker_lease l WHERE l.work_key=j.work_key AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
   ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
   UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5 RETURNING j.job_id
 ), claimed AS (
   UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,started_at=clock_timestamp(),attempt=j.attempt+1
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5 RETURNING j.*
 ) SELECT c.job_id,c.instance_id,c.target_revision,c.job_kind,c.from_utc,c.to_utc,coalesce(c.source_cutoff_utc,c.to_utc),c.generation,c.metric_key,c.horizon_seconds FROM claimed c;
END $m10_claim_derivation$;

CREATE OR REPLACE FUNCTION control.complete_m10_derivation_job(p_job_id uuid,p_status text,p_error text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_complete_derivation$
DECLARE changed integer;
BEGIN
 IF p_job_id IS NULL OR p_status NOT IN ('succeeded','partial','failed','cancelled') OR p_error IS NOT NULL AND length(p_error)>1024 OR p_owner_execution_id IS NULL OR p_fencing_token<=0 THEN RAISE EXCEPTION 'analytics derivation completion bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/derivation',p_owner_execution_id,p_fencing_token);
   UPDATE control.analytics_job SET status=p_status,last_error=p_error,completed_at=clock_timestamp() WHERE job_id=p_job_id AND work_key='analytics/derivation' AND job_kind IN ('rollup','baseline','forecast','evidence','correlation') AND status='running' AND owner_execution_id=p_owner_execution_id AND fencing_token=p_fencing_token;
 GET DIAGNOSTICS changed=ROW_COUNT; IF changed<>1 THEN RAISE EXCEPTION 'analytics derivation completion lease conflict' USING ERRCODE='40001'; END IF; RETURN true;
END $m10_complete_derivation$;

-- Fixed, target-scoped derivation inputs. Evidence starts from immutable,
-- bounded diagnostic envelopes rather than generated packets, avoiding a
-- circular first-run dependency. Only typed identity references are exposed.
-- EvidenceV1 reference allowlist: metric, alert, activity, deadlock, replication, host, health.
CREATE OR REPLACE FUNCTION reporting.read_m10_evidence_derivation_inputs(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,"references" jsonb,tombstones jsonb)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT d.occurred_at,
         jsonb_build_array(jsonb_build_object('type','health','identity',d.event_id::text,'occurredAtUtc',d.occurred_at)),
        '[]'::jsonb
 FROM events.diagnostic_event d
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND d.instance_id=p_instance_id
   AND d.occurred_at>=p_from_utc AND d.occurred_at<p_to_utc AND d.occurred_at<=p_snapshot_utc
   AND (p_metric_key IS NULL OR d.safe_metadata->>'metricKey'=p_metric_key)
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days' AND p_limit BETWEEN 1 AND 100000
 ORDER BY d.occurred_at,d.event_id LIMIT p_limit;
$$;

CREATE OR REPLACE FUNCTION reporting.read_m10_incident_derivation_inputs(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(packet_id uuid,occurred_at timestamptz,evidence_kind text,source_run_id uuid,source_digest bytea,identity_digest bytea,source_cutoff_digest bytea,source_cutoff_utc timestamptz,evidence jsonb,confidence numeric,visibility_state text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT e.packet_id,e.occurred_at,e.evidence_kind,e.source_run_id,e.source_digest,e.identity_digest,e.source_cutoff_digest,e.source_cutoff_utc,e.evidence,e.confidence,e.visibility_state
 FROM analytics.evidence_packet_v2 e
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND e.instance_id=p_instance_id AND e.target_revision=p_target_revision
   AND e.occurred_at>=p_from_utc AND e.occurred_at<p_to_utc AND e.occurred_at<=p_snapshot_utc
   AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
   AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days' AND p_limit BETWEEN 1 AND 100000
 ORDER BY e.occurred_at,e.packet_id LIMIT p_limit;
$$;

REVOKE ALL ON FUNCTION control.schedule_m10_derivation_jobs(uuid,bigint),control.claim_m10_derivation_jobs(text,uuid,bigint,integer),control.complete_m10_derivation_job(uuid,text,text,uuid,bigint),reporting.read_m10_evidence_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz),reporting.read_m10_incident_derivation_inputs(uuid,bigint,timestamptz,timestamptz,integer,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
 GRANT EXECUTE ON FUNCTION control.schedule_m10_derivation_jobs(uuid,bigint),control.claim_m10_derivation_jobs(text,uuid,bigint,integer),control.complete_m10_derivation_job(uuid,text,text,uuid,bigint),reporting.read_m10_evidence_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz),reporting.read_m10_incident_derivation_inputs(uuid,bigint,timestamptz,timestamptz,integer,timestamptz) TO sqlobserver_collector;

CREATE OR REPLACE FUNCTION control.ensure_m10_backfill_partition(p_instance_id uuid,p_target_revision bigint,p_day_start_utc timestamptz,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control,analytics,system AS $m10_backfill_partition$
DECLARE partition_name name:=format('metric_rollup_v2_p%s',to_char(p_day_start_utc AT TIME ZONE 'UTC','YYYYMMDD'));
BEGIN
 IF p_instance_id IS NULL OR p_target_revision<1 OR p_day_start_utc IS NULL OR extract(timezone from p_day_start_utc)<>0 OR date_trunc('day',p_day_start_utc)<>p_day_start_utc OR p_day_start_utc>=date_trunc('day',clock_timestamp()) OR p_owner_execution_id IS NULL OR p_fencing_token<1 THEN RAISE EXCEPTION 'historical partition bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/backfill',p_owner_execution_id,p_fencing_token);
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_kind='backfill' AND j.status='running' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token AND j.from_utc<=p_day_start_utc AND j.to_utc>p_day_start_utc) THEN RAISE EXCEPTION 'historical partition lease conflict' USING ERRCODE='40001'; END IF;
 IF control.resolve_m10_target_revision(p_instance_id,p_target_revision) IS DISTINCT FROM p_target_revision THEN RAISE EXCEPTION 'historical partition target revision conflict' USING ERRCODE='40001'; END IF;
 EXECUTE format('CREATE TABLE IF NOT EXISTS analytics.%I PARTITION OF analytics.metric_rollup_v2 FOR VALUES FROM (%L) TO (%L)',partition_name,p_day_start_utc,p_day_start_utc+interval '1 day');
 EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON analytics.%I (instance_id,target_revision,metric_key,bucket_start)',left('ix_'||partition_name||'_target',63),partition_name);
 INSERT INTO system.partition_registry(parent_schema,parent_table,partition_schema,partition_name,partition_granularity,range_start,range_end)
 VALUES('analytics','metric_rollup_v2','analytics',partition_name,'day',p_day_start_utc,p_day_start_utc+interval '1 day') ON CONFLICT DO NOTHING;
 UPDATE control.analytics_job SET current_day_utc=p_day_start_utc WHERE job_kind='backfill' AND status='running' AND instance_id=p_instance_id AND target_revision=p_target_revision AND owner_execution_id=p_owner_execution_id AND fencing_token=p_fencing_token AND from_utc<=p_day_start_utc AND to_utc>p_day_start_utc;
 RETURN true;
END $m10_backfill_partition$;

CREATE OR REPLACE FUNCTION reporting.read_m10_backfill_page(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_cursor text,p_max_rows integer,p_max_bytes integer)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb,next_cursor text,has_more boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $m10_backfill_page$
DECLARE current_revision bigint; cursor_at timestamptz; cursor_id uuid;
BEGIN
 IF p_instance_id IS NULL OR p_target_revision<1 OR p_from_utc IS NULL OR p_to_utc IS NULL OR p_from_utc>=p_to_utc OR p_to_utc-p_from_utc>interval '1 day' OR p_max_rows NOT BETWEEN 1 AND 100000 OR p_max_bytes NOT BETWEEN 1 AND 8388608 OR p_metric_key IS NOT NULL AND p_metric_key !~ '^[a-z][a-z0-9._-]{0,127}$' OR p_cursor IS NOT NULL AND octet_length(p_cursor)>512 THEN RAISE EXCEPTION 'analytics backfill page bounds rejected' USING ERRCODE='22023'; END IF;
 SELECT t.revision INTO current_revision FROM control.observation_target t WHERE t.instance_id=p_instance_id FOR SHARE;
 IF current_revision IS NULL OR current_revision<>p_target_revision THEN RAISE EXCEPTION 'analytics backfill target revision conflict' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NOT NULL THEN
  BEGIN cursor_at:=split_part(p_cursor,'|',1)::timestamptz; cursor_id:=split_part(p_cursor,'|',2)::uuid; EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END; END IF;
  -- Raw M2 rows without collection-run provenance are compatibility/quarantine
  -- data.  A target revision can only be attributed from the immutable M4+
  -- collection_run row; never infer it from the current target revision.
  IF EXISTS (SELECT 1 FROM telemetry.raw_metric_sample s JOIN telemetry.collection_run cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision WHERE s.instance_id=p_instance_id AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND (p_metric_key IS NULL OR s.metric_key=p_metric_key) AND (p_cursor IS NULL OR s.observed_at>cursor_at OR (s.observed_at=cursor_at AND s.sample_id>cursor_id)) AND octet_length(row_to_json(s)::text)>p_max_bytes) THEN RAISE EXCEPTION 'analytics backfill row exceeds byte bound' USING ERRCODE='22023'; END IF;
 RETURN QUERY
 WITH ordered AS (
   SELECT s.observed_at,s.sample_id,s.metric_key,s.metric_value,s.dimensions,
          octet_length(row_to_json(s)::text)::bigint AS row_bytes
    FROM telemetry.raw_metric_sample s
    JOIN telemetry.collection_run cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision
    WHERE s.instance_id=p_instance_id AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc
     AND (p_metric_key IS NULL OR s.metric_key=p_metric_key)
     AND (p_cursor IS NULL OR s.observed_at>cursor_at OR (s.observed_at=cursor_at AND s.sample_id>cursor_id))
 ), bounded AS (
   SELECT o.*,row_number() OVER (ORDER BY o.observed_at,o.sample_id) AS rn,
          sum(o.row_bytes) OVER (ORDER BY o.observed_at,o.sample_id) AS total_bytes
   FROM ordered o
 ), selected AS (
   SELECT b.* FROM bounded b WHERE b.rn<=p_max_rows AND b.total_bytes<=p_max_bytes
 ), last_row AS (
   SELECT s.observed_at,s.sample_id FROM selected s ORDER BY s.observed_at DESC,s.sample_id DESC LIMIT 1
 )
 SELECT s.observed_at,s.metric_key,s.metric_value,s.dimensions,
   CASE WHEN EXISTS (SELECT 1 FROM ordered o,last_row l WHERE o.observed_at>l.observed_at OR (o.observed_at=l.observed_at AND o.sample_id>l.sample_id))
        THEN l.observed_at::text||'|'||l.sample_id::text ELSE NULL END,
   EXISTS (SELECT 1 FROM ordered o,last_row l WHERE o.observed_at>l.observed_at OR (o.observed_at=l.observed_at AND o.sample_id>l.sample_id))
 FROM selected s CROSS JOIN last_row l ORDER BY s.observed_at,s.sample_id;
END $m10_backfill_page$;

-- Raw, host, and replication rows share a deterministic, time-first
-- total-order cursor (timestamp, source kind, source id, metric, dimension
-- hash, ordinal). This keeps a bucket contiguous across all three sources.
-- Revision, job, day, and catalog scope are validated before the union.
DROP FUNCTION IF EXISTS reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer);
DROP FUNCTION IF EXISTS reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer);
CREATE OR REPLACE FUNCTION reporting.read_m10_backfill_page(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_metric_key text,p_cursor text,p_max_rows integer,p_max_bytes integer,p_job_id uuid,p_source_catalog_version integer)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb,next_cursor text,has_more boolean,safe_cursor text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $m10_backfill_union$
DECLARE current_revision bigint; cursor_json jsonb; cursor_kind text; cursor_at timestamptz; cursor_id uuid; cursor_metric text; cursor_dimension_hash bytea; cursor_ordinal integer; cursor_target uuid; cursor_revision bigint; cursor_day timestamptz; cursor_catalog integer;
BEGIN
 IF p_instance_id IS NULL OR p_target_revision<1 OR p_job_id IS NULL OR p_source_catalog_version<>1 OR p_from_utc IS NULL OR p_to_utc IS NULL OR p_from_utc>=p_to_utc OR p_to_utc-p_from_utc>interval '1 day' OR p_max_rows NOT BETWEEN 1 AND 100000 OR p_max_bytes NOT BETWEEN 1 AND 8388608 OR p_metric_key IS NOT NULL AND p_metric_key !~ '^[a-z][a-z0-9._-]{0,127}$' OR p_cursor IS NOT NULL AND octet_length(p_cursor)>4096 THEN RAISE EXCEPTION 'analytics backfill page bounds rejected' USING ERRCODE='22023'; END IF;
 SELECT t.revision INTO current_revision FROM control.observation_target t WHERE t.instance_id=p_instance_id FOR SHARE;
 IF current_revision IS NULL OR current_revision<>p_target_revision OR p_instance_id::text<>current_setting('sqlobserver.target_scope',true) THEN RAISE EXCEPTION 'analytics backfill target revision conflict' USING ERRCODE='40001'; END IF;
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job j WHERE j.job_id=p_job_id AND j.job_kind='backfill' AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND j.from_utc<=p_from_utc AND j.to_utc>=p_to_utc) THEN RAISE EXCEPTION 'analytics backfill job fence conflict' USING ERRCODE='40001'; END IF;
 IF p_cursor IS NOT NULL THEN BEGIN
   cursor_json:=convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb;
   cursor_kind:=cursor_json->>'sourceKind'; cursor_at:=(cursor_json->>'observedAtUtc')::timestamptz; cursor_id:=(cursor_json->>'sourceId')::uuid; cursor_metric:=cursor_json->>'metricKey'; cursor_dimension_hash:=decode(cursor_json->>'dimensionHash','hex'); cursor_ordinal:=(cursor_json->>'ordinal')::integer; cursor_target:=(cursor_json->>'targetId')::uuid; cursor_revision:=(cursor_json->>'targetRevision')::bigint; cursor_day:=(cursor_json->>'dayStartUtc')::timestamptz; cursor_catalog:=(cursor_json->>'catalogVersion')::integer;
   IF cursor_json->>'v' IS DISTINCT FROM 'm10.backfill.v2' OR cursor_kind NOT IN ('raw','host','replication') OR cursor_id IS NULL OR cursor_metric IS NULL OR octet_length(cursor_dimension_hash)<>32 OR cursor_ordinal<1 OR cursor_target<>p_instance_id OR cursor_revision<>p_target_revision OR cursor_day<>p_from_utc OR cursor_catalog<>p_source_catalog_version OR cursor_json->>'jobId'<>p_job_id::text THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END IF;
 EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END; END IF;
 -- A page must never turn an over-sized first row into an empty, terminal
 -- page. Check every source before the bounded union so the job fails
 -- explicitly and remains retryable instead of silently advancing.
  IF EXISTS (SELECT 1 FROM telemetry.raw_metric_sample s JOIN telemetry.collection_run cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision WHERE s.instance_id=p_instance_id AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND (p_metric_key IS NULL OR s.metric_key=p_metric_key) AND octet_length(row_to_json(s)::text)>p_max_bytes)
    OR EXISTS (SELECT 1 FROM telemetry.host_metric_snapshot_v2 h WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND (p_metric_key IS NULL OR h.metric_key=p_metric_key) AND octet_length(row_to_json(h)::text)>p_max_bytes)
    OR EXISTS (SELECT 1 FROM telemetry.replication_snapshot_v2 r WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND octet_length(row_to_json(r)::text)>p_max_bytes)
 THEN RAISE EXCEPTION 'analytics backfill row exceeds byte bound' USING ERRCODE='22023'; END IF;
 RETURN QUERY
WITH source_rows AS (
  -- Historical raw samples are admitted only when their metric and
  -- dimensions exactly match an enabled catalog-v1 entry.  Their source
  -- kind remains raw so the cursor cannot collide with v2 host/replication
  -- rows carrying the same timestamp and identifier.
   SELECT 'raw'::text source_kind,s.observed_at,s.sample_id source_id,s.metric_key,s.metric_value,s.dimensions,sha256(convert_to(s.dimensions::text,'UTF8')) dimension_hash,1::integer source_ordinal
   FROM telemetry.raw_metric_sample s
   JOIN telemetry.collection_run cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision
   JOIN analytics.metric_catalog c ON c.metric_key=s.metric_key AND c.enabled
  WHERE s.instance_id=p_instance_id AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND (p_metric_key IS NULL OR s.metric_key=p_metric_key)
    AND NOT EXISTS (SELECT 1 FROM jsonb_object_keys(s.dimensions) k WHERE NOT (c.definition->'dimensions' ? k))
  UNION ALL
  SELECT 'host'::text source_kind,h.observed_at,h.run_id source_id,h.metric_key,h.metric_value,h.dimensions,sha256(convert_to(h.dimensions::text,'UTF8')) dimension_hash,2::integer source_ordinal
   FROM telemetry.host_metric_snapshot_v2 h JOIN analytics.metric_catalog c ON c.metric_key=h.metric_key AND c.source_kind='host' AND c.enabled
   WHERE h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND (p_metric_key IS NULL OR h.metric_key=p_metric_key)
     AND NOT EXISTS (SELECT 1 FROM jsonb_object_keys(h.dimensions) k WHERE NOT (c.definition->'dimensions' ? k))
   UNION ALL SELECT 'replication',r.observed_at,r.run_id,'replication.pending_commands',r.pending_commands::double precision,'{}'::jsonb,sha256(convert_to('{}','UTF8')),3
   FROM telemetry.replication_snapshot_v2 r JOIN analytics.metric_catalog c ON c.metric_key='replication.pending_commands' AND c.source_kind='replication' AND c.enabled
   WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.pending_commands IS NOT NULL AND (p_metric_key IS NULL OR p_metric_key='replication.pending_commands')
   UNION ALL SELECT 'replication',r.observed_at,r.run_id,'replication.latency_seconds',r.latency_seconds,'{}'::jsonb,sha256(convert_to('{}','UTF8')),4
   FROM telemetry.replication_snapshot_v2 r JOIN analytics.metric_catalog c ON c.metric_key='replication.latency_seconds' AND c.source_kind='replication' AND c.enabled
   WHERE r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.latency_seconds IS NOT NULL AND (p_metric_key IS NULL OR p_metric_key='replication.latency_seconds')
 ), ordered AS (SELECT s.*,octet_length(row_to_json(s)::text)::bigint row_bytes FROM source_rows s WHERE p_cursor IS NULL OR (s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal)>(cursor_at,cursor_kind,cursor_id,cursor_metric,cursor_dimension_hash,cursor_ordinal)), bounded AS (SELECT o.*,row_number() OVER (ORDER BY o.observed_at,o.source_kind,o.source_id,o.metric_key,o.dimension_hash,o.source_ordinal) rn,sum(o.row_bytes) OVER (ORDER BY o.observed_at,o.source_kind,o.source_id,o.metric_key,o.dimension_hash,o.source_ordinal) total_bytes FROM ordered o), selected AS (SELECT * FROM bounded WHERE rn<=p_max_rows AND total_bytes<=p_max_bytes), last_row AS (SELECT * FROM selected ORDER BY observed_at DESC,source_kind DESC,source_id DESC,metric_key DESC,dimension_hash DESC,source_ordinal DESC LIMIT 1), remaining AS (SELECT EXISTS(SELECT 1 FROM ordered o,last_row l WHERE (o.observed_at,o.source_kind,o.source_id,o.metric_key,o.dimension_hash,o.source_ordinal)>(l.observed_at,l.source_kind,l.source_id,l.metric_key,l.dimension_hash,l.source_ordinal)) has_more), safe_rows AS (SELECT s.* FROM selected s CROSS JOIN last_row l CROSS JOIN remaining m WHERE (m.has_more AND (s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal)<(l.observed_at,l.source_kind,l.source_id,l.metric_key,l.dimension_hash,l.source_ordinal) AND date_trunc('hour',s.observed_at)+floor(extract(minute FROM s.observed_at)/5)*interval '5 minutes' < date_trunc('hour',l.observed_at)+floor(extract(minute FROM l.observed_at)/5)*interval '5 minutes') OR (NOT m.has_more AND date_trunc('hour',s.observed_at)+floor(extract(minute FROM s.observed_at)/5)*interval '5 minutes'+interval '5 minutes'<=p_to_utc)), safe_last AS (SELECT * FROM safe_rows ORDER BY observed_at DESC,source_kind DESC,source_id DESC,metric_key DESC,dimension_hash DESC,source_ordinal DESC LIMIT 1)
 SELECT s.observed_at,s.metric_key,s.metric_value,s.dimensions,CASE WHEN remaining.has_more THEN encode(convert_to(jsonb_build_object('v','m10.backfill.v2','sourceKind',l.source_kind,'observedAtUtc',l.observed_at,'sourceId',l.source_id,'metricKey',l.metric_key,'dimensionHash',encode(l.dimension_hash,'hex'),'ordinal',l.source_ordinal,'targetId',p_instance_id,'targetRevision',p_target_revision,'jobId',p_job_id,'dayStartUtc',p_from_utc,'catalogVersion',p_source_catalog_version)::text,'UTF8'),'base64') END,remaining.has_more,CASE WHEN sl.source_id IS NOT NULL THEN encode(convert_to(jsonb_build_object('v','m10.backfill.v2','sourceKind',sl.source_kind,'observedAtUtc',sl.observed_at,'sourceId',sl.source_id,'metricKey',sl.metric_key,'dimensionHash',encode(sl.dimension_hash,'hex'),'ordinal',sl.source_ordinal,'targetId',p_instance_id,'targetRevision',p_target_revision,'jobId',p_job_id,'dayStartUtc',p_from_utc,'catalogVersion',p_source_catalog_version)::text,'UTF8'),'base64') END FROM selected s CROSS JOIN last_row l CROSS JOIN remaining LEFT JOIN safe_last sl ON true WHERE (s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal)<=(l.observed_at,l.source_kind,l.source_id,l.metric_key,l.dimension_hash,l.source_ordinal) ORDER BY s.observed_at,s.source_kind,s.source_id,s.metric_key,s.dimension_hash,s.source_ordinal;
END $m10_backfill_union$;

CREATE OR REPLACE FUNCTION control.advance_m10_backfill_cursor(p_job_id uuid,p_day_start_utc timestamptz,p_cursor text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_advance_cursor$
DECLARE changed integer; c jsonb; source_kind text; observed_at timestamptz; source_id uuid; metric_key text; dimension_hash bytea; ordinal integer; target_id uuid; target_revision bigint; cursor_day timestamptz; catalog_version integer;
BEGIN
 IF p_job_id IS NULL OR p_day_start_utc IS NULL OR extract(timezone from p_day_start_utc)<>0 OR p_cursor IS NOT NULL AND octet_length(p_cursor)>4096 OR p_owner_execution_id IS NULL OR p_fencing_token<=0 THEN RAISE EXCEPTION 'analytics backfill cursor bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/backfill',p_owner_execution_id,p_fencing_token);
 IF p_cursor IS NOT NULL THEN BEGIN
   c:=convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb;
   source_kind:=c->>'sourceKind'; observed_at:=(c->>'observedAtUtc')::timestamptz; source_id:=(c->>'sourceId')::uuid; metric_key:=c->>'metricKey'; dimension_hash:=decode(c->>'dimensionHash','hex'); ordinal:=(c->>'ordinal')::integer; target_id:=(c->>'targetId')::uuid; target_revision:=(c->>'targetRevision')::bigint; cursor_day:=(c->>'dayStartUtc')::timestamptz; catalog_version:=(c->>'catalogVersion')::integer;
   IF c->>'v' IS DISTINCT FROM 'm10.backfill.v2' OR source_kind NOT IN ('raw','host','replication') OR source_id IS NULL OR metric_key IS NULL OR octet_length(dimension_hash)<>32 OR ordinal<1 OR target_id IS NULL OR target_revision<1 OR cursor_day<>p_day_start_utc OR catalog_version<>1 OR c->>'jobId'<>p_job_id::text THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END IF;
 EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END; END IF;
 UPDATE control.analytics_job j SET cursor=p_cursor,cursor_source_kind=source_kind,cursor_observed_at=observed_at,cursor_source_id=source_id,cursor_metric_key=metric_key,cursor_dimension_hash=dimension_hash,cursor_ordinal=ordinal,cursor_target_id=target_id,cursor_target_revision=target_revision,cursor_day_utc=cursor_day,cursor_catalog_version=catalog_version WHERE j.job_id=p_job_id AND j.job_kind='backfill' AND j.status='running' AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token AND j.from_utc<=p_day_start_utc AND j.to_utc>p_day_start_utc AND (p_cursor IS NULL OR (j.instance_id=target_id AND j.target_revision=target_revision));
 GET DIAGNOSTICS changed=ROW_COUNT; IF changed<>1 THEN RAISE EXCEPTION 'analytics backfill cursor lease conflict' USING ERRCODE='40001'; END IF; RETURN true;
END $m10_advance_cursor$;

CREATE OR REPLACE FUNCTION control.complete_m10_analytics_job(p_job_id uuid,p_status text,p_error text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_complete_fenced$
DECLARE changed integer;
BEGIN
 IF p_job_id IS NULL OR p_status NOT IN ('succeeded','partial','failed','cancelled') OR p_error IS NOT NULL AND length(p_error)>1024 OR p_owner_execution_id IS NULL OR p_fencing_token<=0 THEN RAISE EXCEPTION 'analytics job completion bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/backfill',p_owner_execution_id,p_fencing_token);
 UPDATE control.analytics_job SET status=p_status,last_error=p_error,completed_at=clock_timestamp() WHERE job_id=p_job_id AND status='running' AND owner_execution_id=p_owner_execution_id AND fencing_token=p_fencing_token;
 GET DIAGNOSTICS changed=ROW_COUNT; IF changed<>1 THEN RAISE EXCEPTION 'analytics job completion lease conflict' USING ERRCODE='40001'; END IF; RETURN true;
END $m10_complete_fenced$;

DROP FUNCTION IF EXISTS control.replay_m10_analytics_job(uuid,uuid,uuid,bytea,bytea,jsonb);
CREATE OR REPLACE FUNCTION control.replay_m10_analytics_job(p_operation_id uuid,p_job_id uuid,p_instance_id uuid,p_target_revision bigint,p_owner_execution_id uuid,p_fencing_token bigint,p_request_digest bytea,p_result_digest bytea,p_result jsonb)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_replay_fenced$
BEGIN
 IF p_operation_id IS NULL OR p_job_id IS NULL OR p_instance_id IS NULL OR p_target_revision<1 OR p_owner_execution_id IS NULL OR p_fencing_token<=0 OR octet_length(p_request_digest)<>32 OR octet_length(p_result_digest)<>32 OR jsonb_typeof(p_result)<>'object' OR octet_length(p_result::text)>8388608 THEN RAISE EXCEPTION 'analytics replay bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/backfill',p_owner_execution_id,p_fencing_token);
 IF NOT EXISTS (SELECT 1 FROM control.analytics_job WHERE job_id=p_job_id AND instance_id=p_instance_id AND target_revision=p_target_revision AND owner_execution_id=p_owner_execution_id AND fencing_token=p_fencing_token) THEN RAISE EXCEPTION 'analytics replay job fence conflict' USING ERRCODE='40001'; END IF;
 PERFORM control.record_m10_analytics_replay(p_operation_id,'backfill.job.replay',p_job_id,p_instance_id,p_target_revision,'analytics/backfill',p_owner_execution_id,p_fencing_token,p_request_digest,p_result_digest,p_result);
 RETURN true;
END $m10_replay_fenced$;

REVOKE ALL ON FUNCTION control.claim_m10_analytics_jobs(text,uuid,bigint,integer),control.ensure_m10_backfill_partition(uuid,bigint,timestamptz,uuid,bigint),reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer),control.advance_m10_backfill_cursor(uuid,timestamptz,text,uuid,bigint),control.complete_m10_analytics_job(uuid,text,text,uuid,bigint),control.replay_m10_analytics_job(uuid,uuid,uuid,bigint,uuid,bigint,bytea,bytea,jsonb) FROM PUBLIC,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.claim_m10_analytics_jobs(text,uuid,bigint,integer),control.ensure_m10_backfill_partition(uuid,bigint,timestamptz,uuid,bigint),reporting.read_m10_backfill_page(uuid,bigint,timestamptz,timestamptz,text,text,integer,integer,uuid,integer),control.advance_m10_backfill_cursor(uuid,timestamptz,text,uuid,bigint),control.complete_m10_analytics_job(uuid,text,text,uuid,bigint),control.replay_m10_analytics_job(uuid,uuid,uuid,bigint,uuid,bigint,bytea,bytea,jsonb) TO sqlobserver_collector;

CREATE OR REPLACE FUNCTION reporting.get_m10_incident_evidence(p_instance_id uuid,p_target_revision bigint,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,packet_id uuid,evidence_kind text,source_run_id uuid,source_digest bytea,identity_digest bytea,source_cutoff_digest bytea,source_cutoff_utc timestamptz,evidence jsonb,confidence numeric,visibility_state text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT e.occurred_at,e.packet_id,e.evidence_kind,e.source_run_id,e.source_digest,e.identity_digest,e.source_cutoff_digest,e.source_cutoff_utc,e.evidence,e.confidence,e.visibility_state
 FROM analytics.evidence_packet_v2 e JOIN analytics.incident_generation g ON g.evidence_packet_id=e.packet_id
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND e.instance_id=p_instance_id AND e.target_revision=p_target_revision AND g.thread_id=p_thread_id AND g.target_revision=p_target_revision AND e.occurred_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 256 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
 ORDER BY e.occurred_at,e.packet_id LIMIT p_limit;
$$;
CREATE OR REPLACE FUNCTION reporting.get_m10_incident_generations(p_instance_id uuid,p_target_revision bigint,p_thread_id uuid,p_limit integer,p_snapshot_utc timestamptz)
RETURNS TABLE(thread_id uuid,generation bigint,observed_at timestamptz,correlation_digest bytea,supersedes_previous boolean,evidence_packet_id uuid)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT g.thread_id,g.generation,g.observed_at,g.correlation_digest,g.supersedes_previous,g.evidence_packet_id
 FROM analytics.incident_generation g WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND g.instance_id=p_instance_id AND g.target_revision=p_target_revision AND g.thread_id=p_thread_id AND g.observed_at<=p_snapshot_utc AND p_limit BETWEEN 1 AND 256 AND p_instance_id::text=current_setting('sqlobserver.target_scope',true)
 ORDER BY g.generation LIMIT p_limit;
$$;
REVOKE ALL ON FUNCTION reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz),reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_m10_incident_evidence(uuid,bigint,uuid,integer,timestamptz),reporting.get_m10_incident_generations(uuid,bigint,uuid,integer,timestamptz) TO sqlobserver_server;

-- Revision/snapshot-safe M10 reporting surface.  Every continuation carries
-- the complete tie key; timestamp-only predicates are intentionally absent.
DROP FUNCTION IF EXISTS reporting.list_m10_host_status(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.list_m10_host_status(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor_at timestamptz,p_cursor_host_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(host_id uuid,target_revision bigint,binding_revision bigint,host_name text,binding_state text,capability_state text,observed_at timestamptz,generation bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
 SELECT b.host_id,b.target_revision,b.binding_revision,b.host_name,b.binding_state,coalesce(p.capability_state,'unavailable'),coalesce(p.observed_at,b.last_seen_at),coalesce(p.profile_revision,b.binding_revision)
 FROM control.host_binding b JOIN control.observation_target t ON t.instance_id=b.instance_id AND t.revision=b.target_revision
 LEFT JOIN LATERAL (SELECT hp.capability_state,hp.observed_at,hp.profile_revision FROM control.host_profile hp WHERE hp.instance_id=b.instance_id AND hp.target_revision=b.target_revision AND hp.host_id=b.host_id AND hp.binding_revision=b.binding_revision ORDER BY hp.profile_revision DESC LIMIT 1) p ON true
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND b.instance_id=p_instance_id AND b.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND p_from_utc<p_to_utc AND coalesce(p.observed_at,b.last_seen_at)>=p_from_utc AND coalesce(p.observed_at,b.last_seen_at)<p_to_utc AND coalesce(p.observed_at,b.last_seen_at)<=p_snapshot_utc
   AND (p_cursor_at IS NULL OR coalesce(p.observed_at,b.last_seen_at)<p_cursor_at OR (coalesce(p.observed_at,b.last_seen_at)=p_cursor_at AND b.host_id>p_cursor_host_id))
 ORDER BY coalesce(p.observed_at,b.last_seen_at) DESC,b.host_id LIMIT least(greatest(p_limit,1),200);
$$;
DROP FUNCTION IF EXISTS reporting.list_m10_host_metrics_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.list_m10_host_metrics_scoped(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor_at timestamptz,p_cursor_run_id uuid,p_cursor_metric_key text,p_cursor_dimensions jsonb,p_snapshot_utc timestamptz)
RETURNS SETOF telemetry.host_metric_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 SELECT h.* FROM telemetry.host_metric_snapshot_v2 h WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc AND (p_cursor_at IS NULL OR (h.observed_at,h.run_id,h.metric_key,h.dimensions)>(p_cursor_at,p_cursor_run_id,p_cursor_metric_key,p_cursor_dimensions)) ORDER BY h.observed_at,h.run_id,h.metric_key,h.dimensions LIMIT least(greatest(p_limit,1),200);
$$;
DROP FUNCTION IF EXISTS reporting.list_m10_replication_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.list_m10_replication_scoped(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor_at timestamptz,p_cursor_run_id uuid,p_cursor_topology_fingerprint bytea,p_snapshot_utc timestamptz)
RETURNS SETOF telemetry.replication_snapshot_v2 LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,telemetry,control SET TimeZone='UTC' AS $$
 SELECT r.* FROM telemetry.replication_snapshot_v2 r WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND (p_cursor_at IS NULL OR (r.observed_at,r.run_id,r.topology_fingerprint)>(p_cursor_at,p_cursor_run_id,p_cursor_topology_fingerprint)) ORDER BY r.observed_at,r.run_id,r.topology_fingerprint LIMIT least(greatest(p_limit,1),200);
$$;
DROP FUNCTION IF EXISTS reporting.list_m10_incidents_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.list_m10_incidents_scoped(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor_at timestamptz,p_cursor_thread_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(thread_id uuid,opened_at timestamptz,closed_at timestamptz,current_generation bigint,target_revision bigint) LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT i.thread_id,i.opened_at,i.closed_at,i.current_generation,i.target_revision FROM analytics.incident_thread i WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND i.instance_id=p_instance_id AND i.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND i.opened_at>=p_from_utc AND i.opened_at<p_to_utc AND i.opened_at<=p_snapshot_utc AND (p_cursor_at IS NULL OR (i.opened_at,i.thread_id)>(p_cursor_at,p_cursor_thread_id)) ORDER BY i.opened_at,i.thread_id LIMIT least(greatest(p_limit,1),100);
$$;
DROP FUNCTION IF EXISTS reporting.list_m10_evidence_packets(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.list_m10_evidence_packets(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor_at timestamptz,p_cursor_packet_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,packet_id uuid,evidence_kind text,source_run_id uuid,source_digest bytea,confidence numeric,visibility_state text,target_revision bigint) LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT e.occurred_at,e.packet_id,e.evidence_kind,e.source_run_id,e.source_digest,e.confidence,e.visibility_state,e.target_revision FROM analytics.evidence_packet_v2 e WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND e.instance_id=p_instance_id AND e.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND e.occurred_at>=p_from_utc AND e.occurred_at<p_to_utc AND e.occurred_at<=p_snapshot_utc AND (p_cursor_at IS NULL OR (e.occurred_at,e.packet_id)>(p_cursor_at,p_cursor_packet_id)) ORDER BY e.occurred_at,e.packet_id LIMIT least(greatest(p_limit,1),200);
$$;
REVOKE ALL ON FUNCTION reporting.list_m10_host_status(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz),reporting.list_m10_host_metrics_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,text,jsonb,timestamptz),reporting.list_m10_replication_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,bytea,timestamptz),reporting.list_m10_incidents_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz),reporting.list_m10_evidence_packets(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_m10_host_status(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz),reporting.list_m10_host_metrics_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,text,jsonb,timestamptz),reporting.list_m10_replication_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,bytea,timestamptz),reporting.list_m10_incidents_scoped(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz),reporting.list_m10_evidence_packets(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz) TO sqlobserver_server;

DROP FUNCTION IF EXISTS reporting.list_m10_analytics_jobs(uuid,bigint,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.list_m10_analytics_jobs(p_instance_id uuid,p_target_revision bigint,p_limit integer,p_cursor_at timestamptz,p_cursor_job_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(job_id uuid,job_kind text,status text,requested_at timestamptz,started_at timestamptz,completed_at timestamptz,attempt integer,target_revision bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,control SET TimeZone='UTC' AS $$
 SELECT j.job_id,j.job_kind,j.status,j.requested_at,j.started_at,j.completed_at,j.attempt,j.target_revision FROM control.analytics_job j WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND j.instance_id=p_instance_id AND j.target_revision=p_target_revision AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND j.requested_at<=p_snapshot_utc AND (p_cursor_at IS NULL OR (j.requested_at,j.job_id)>(p_cursor_at,p_cursor_job_id)) ORDER BY j.requested_at,j.job_id LIMIT least(greatest(p_limit,1),100);
$$;
DROP FUNCTION IF EXISTS reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz);
CREATE OR REPLACE FUNCTION reporting.search_m10_diagnostics(p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,p_limit integer,p_cursor_at timestamptz,p_cursor_event_id uuid,p_snapshot_utc timestamptz)
RETURNS TABLE(occurred_at timestamptz,event_id uuid,event_kind text,severity smallint,safe_metadata jsonb,collected_at timestamptz,target_revision bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog,reporting,events,control SET TimeZone='UTC' AS $$
 SELECT d.occurred_at,d.event_id,d.event_kind,d.severity,d.safe_metadata,d.collected_at,p_target_revision FROM events.diagnostic_event d WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision AND d.instance_id=p_instance_id AND p_instance_id::text=current_setting('sqlobserver.target_scope',true) AND p_from_utc>=p_snapshot_utc-interval '7 days' AND p_to_utc>p_from_utc AND p_to_utc<=p_snapshot_utc AND (p_cursor_at IS NULL OR (d.occurred_at,d.event_id)>(p_cursor_at,p_cursor_event_id)) ORDER BY d.occurred_at,d.event_id LIMIT least(greatest(p_limit,1),100);
$$;
REVOKE ALL ON FUNCTION reporting.list_m10_analytics_jobs(uuid,bigint,integer,timestamptz,uuid,timestamptz),reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_m10_analytics_jobs(uuid,bigint,integer,timestamptz,uuid,timestamptz),reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz) TO sqlobserver_server;

-- Forward-only M10 dataflow additions.  Rollup jobs are target/revision
-- scoped, deterministic, and scheduled only for completed UTC buckets.  The
-- separate lease lane lets the live 5m/hour/day workers coexist with bounded
-- historical backfill and derivation workers.
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS rollup_interval text;
ALTER TABLE control.analytics_job ADD COLUMN IF NOT EXISTS dimensions_hash bytea;
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS ck_m10_rollup_interval;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_m10_rollup_interval
 CHECK (rollup_interval IS NULL OR rollup_interval IN ('5m','hour','day'));
ALTER TABLE control.analytics_job DROP CONSTRAINT IF EXISTS ck_m10_dimensions_hash;
ALTER TABLE control.analytics_job ADD CONSTRAINT ck_m10_dimensions_hash
 CHECK (dimensions_hash IS NULL OR octet_length(dimensions_hash)=32);

CREATE OR REPLACE FUNCTION control.schedule_m10_rollup_jobs(p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_schedule_rollups$
DECLARE now_utc timestamptz:=clock_timestamp(); five_start timestamptz; hour_start timestamptz; day_start timestamptz; inserted_count integer:=0;
BEGIN
 IF p_owner_execution_id IS NULL OR p_fencing_token<1 THEN RAISE EXCEPTION 'analytics rollup schedule bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/rollup',p_owner_execution_id,p_fencing_token);
 five_start:=date_trunc('hour',now_utc)+(extract(minute FROM now_utc)::integer/5)*interval '5 minutes'-interval '5 minutes';
 hour_start:=date_trunc('hour',now_utc)-interval '1 hour';
 day_start:=date_trunc('day',now_utc)-interval '1 day';
 WITH targets AS (
   SELECT t.instance_id,t.revision FROM control.observation_target t
   WHERE t.lifecycle_state IN ('pending_discovery','active') AND t.revision>0 ORDER BY t.instance_id LIMIT 128
 ), due AS (
   SELECT x.instance_id,x.revision,v.rollup_interval,v.bucket_start,v.bucket_end,
          left(encode(sha256(convert_to('m10-rollup|'||x.instance_id::text||'|'||x.revision::text||'|'||v.rollup_interval||'|'||v.bucket_start::text,'UTF8')),'hex'),32)::uuid AS job_id
   FROM targets x CROSS JOIN (VALUES
      ('5m'::text,five_start,five_start+interval '5 minutes'),
      ('hour'::text,hour_start,hour_start+interval '1 hour'),
      ('day'::text,day_start,day_start+interval '1 day')) v(rollup_interval,bucket_start,bucket_end)
 )
 INSERT INTO control.analytics_job(job_id,job_kind,instance_id,target_revision,from_utc,to_utc,rollup_interval,source_cutoff_utc,generation,status,work_key)
 SELECT d.job_id,'rollup',d.instance_id,d.revision,d.bucket_start,d.bucket_end,d.rollup_interval,d.bucket_end,1,'queued','analytics/rollup' FROM due d
 ON CONFLICT(job_id) DO NOTHING;
 GET DIAGNOSTICS inserted_count=ROW_COUNT; RETURN inserted_count;
END $m10_schedule_rollups$;

CREATE OR REPLACE FUNCTION control.claim_m10_rollup_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,rollup_interval text,from_utc timestamptz,to_utc timestamptz,source_cutoff_utc timestamptz,generation bigint)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_claim_rollups$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/rollup' OR p_owner_execution_id IS NULL OR p_fencing_token<1 OR p_limit NOT BETWEEN 1 AND 2 THEN RAISE EXCEPTION 'analytics rollup claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
   SELECT j.job_id FROM control.analytics_job j JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
   WHERE j.job_kind='rollup' AND j.rollup_interval IN ('5m','hour','day')
     AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
       (SELECT 1 FROM control.worker_lease l WHERE l.work_key=j.work_key AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
   ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
   UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5 RETURNING j.job_id
 ), claimed AS (
   UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,started_at=clock_timestamp(),attempt=j.attempt+1
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5 RETURNING j.*
 ) SELECT c.job_id,c.instance_id,c.target_revision,c.rollup_interval,c.from_utc,c.to_utc,coalesce(c.source_cutoff_utc,c.to_utc),c.generation FROM claimed c;
END $m10_claim_rollups$;

CREATE OR REPLACE FUNCTION control.complete_m10_rollup_job(p_job_id uuid,p_status text,p_error text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_complete_rollup$
DECLARE changed integer;
BEGIN
 IF p_job_id IS NULL OR p_status NOT IN ('succeeded','partial','failed','cancelled') OR p_error IS NOT NULL AND length(p_error)>1024 OR p_owner_execution_id IS NULL OR p_fencing_token<1 THEN RAISE EXCEPTION 'analytics rollup completion bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/rollup',p_owner_execution_id,p_fencing_token);
 UPDATE control.analytics_job SET status=p_status,last_error=p_error,completed_at=clock_timestamp() WHERE job_id=p_job_id AND job_kind='rollup' AND status='running' AND work_key='analytics/rollup' AND owner_execution_id=p_owner_execution_id AND fencing_token=p_fencing_token;
 GET DIAGNOSTICS changed=ROW_COUNT; IF changed<>1 THEN RAISE EXCEPTION 'analytics rollup completion lease conflict' USING ERRCODE='40001'; END IF; RETURN true;
END $m10_complete_rollup$;

-- Rollup commits may target either a live bucket or a historical backfill;
-- both paths retain the same explicit job/revision/lease fence.
-- (The condition is repeated in the function body below to keep old callers
-- and the M10 backfill contract source-compatible.)

-- Dimension identity is persisted for forecasts as well as rollups.  Existing
-- rows receive the empty-dimension identity; no historical row is guessed or
-- moved between target revisions.
ALTER TABLE analytics.metric_forecast ADD COLUMN IF NOT EXISTS dimensions jsonb NOT NULL DEFAULT '{}'::jsonb;
-- A column default cannot reference another column.  Repair pre-M10 rows
-- explicitly while the append-only trigger is temporarily removed.
DROP TRIGGER IF EXISTS m10_forecast_append_only ON analytics.metric_forecast;
ALTER TABLE analytics.metric_forecast ADD COLUMN IF NOT EXISTS dimension_hash bytea;
UPDATE analytics.metric_forecast
   SET dimension_hash=sha256(convert_to(dimensions::text,'UTF8'));
ALTER TABLE analytics.metric_forecast ALTER COLUMN dimension_hash SET NOT NULL;
DO $m10_forecast_dimension_constraints$
BEGIN
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='ck_m10_forecast_dimensions') THEN
   ALTER TABLE analytics.metric_forecast ADD CONSTRAINT ck_m10_forecast_dimensions CHECK (jsonb_typeof(dimensions)='object' AND octet_length(dimensions::text)<=16384 AND octet_length(dimension_hash)=32);
 END IF;
END $m10_forecast_dimension_constraints$;
CREATE INDEX IF NOT EXISTS ix_m10_forecast_target_metric_dimension ON analytics.metric_forecast(instance_id,target_revision,metric_key,dimension_hash,horizon_start);
CREATE TRIGGER m10_forecast_append_only BEFORE UPDATE OR DELETE ON analytics.metric_forecast FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();

CREATE OR REPLACE FUNCTION reporting.get_m10_forecast_scoped(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_dimensions jsonb,p_horizon interval,p_snapshot_utc timestamptz)
RETURNS SETOF analytics.metric_forecast LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,analytics,control SET TimeZone='UTC' AS $$
 SELECT f.* FROM analytics.metric_forecast f
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text
   AND f.instance_id=p_instance_id AND f.target_revision=p_target_revision AND f.metric_key=p_metric_key
   AND f.dimension_hash=sha256(convert_to(coalesce(p_dimensions,'{}'::jsonb)::text,'UTF8'))
   AND f.horizon_start<=p_snapshot_utc+p_horizon AND f.horizon_end>p_snapshot_utc
   AND p_horizon BETWEEN interval '1 hour' AND interval '366 days' AND p_snapshot_utc IS NOT NULL
 ORDER BY f.horizon_start,f.forecast_id LIMIT 201;
$$;

CREATE OR REPLACE FUNCTION reporting.get_m10_forecast_capacity_scoped(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_dimensions jsonb,p_snapshot_utc timestamptz)
RETURNS double precision LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,analytics,control SET TimeZone='UTC' AS $$
 SELECT CASE WHEN max(h.metric_value) FILTER (WHERE h.metric_value>0 AND h.metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))>0
   THEN max(h.metric_value) FILTER (WHERE h.metric_value>0 AND h.metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)) ELSE NULL END
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE p_instance_id IS NOT NULL AND p_target_revision>0 AND p_metric_key IN ('host.volume.free_bytes','host.volume.total_bytes')
   AND p_snapshot_utc IS NOT NULL AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision
   AND h.metric_key='host.volume.total_bytes' AND h.dimensions=coalesce(p_dimensions,'{}'::jsonb) AND h.observed_at<=p_snapshot_utc
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text
   AND control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
$$;

 REVOKE ALL ON FUNCTION control.schedule_m10_rollup_jobs(uuid,bigint),control.claim_m10_rollup_jobs(text,uuid,bigint,integer),control.complete_m10_rollup_job(uuid,text,text,uuid,bigint),reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz),reporting.get_m10_forecast_capacity_scoped(uuid,bigint,text,jsonb,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
 -- The derivation lane above is canonical; retain these definitions only for
 -- upgrade compatibility, with no runtime role able to invoke them.
 GRANT EXECUTE ON FUNCTION reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz) TO sqlobserver_server;
 GRANT EXECUTE ON FUNCTION reporting.get_m10_forecast_capacity_scoped(uuid,bigint,text,jsonb,timestamptz) TO sqlobserver_server,sqlobserver_collector;

-- The analytics adapter consumes this fixed projection for live rollups. It
-- reads target/revision-fenced raw and host/replication telemetry; no caller
-- supplied SQL or unscoped target data is admitted to this path.
CREATE OR REPLACE FUNCTION reporting.read_m10_rollup_derivation_inputs(
 p_instance_id uuid,p_target_revision bigint,p_from_utc timestamptz,p_to_utc timestamptz,
 p_metric_key text,p_limit integer,p_snapshot_utc timestamptz,p_rollup_interval text,
 p_dimension_hash bytea)
RETURNS TABLE(observed_at timestamptz,metric_key text,metric_value double precision,dimensions jsonb,reset boolean,complete boolean)
LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,analytics,control SET TimeZone='UTC' AS $$
 WITH source_rows AS (
 SELECT s.observed_at,s.metric_key,s.metric_value,s.dimensions,false AS reset,true AS complete
 FROM telemetry.raw_metric_sample s
 JOIN telemetry.collection_run cr ON cr.run_id=s.collection_run_id AND cr.instance_id=s.instance_id AND cr.target_revision=p_target_revision
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND s.instance_id=p_instance_id
   AND s.observed_at>=p_from_utc AND s.observed_at<p_to_utc AND s.observed_at<=p_snapshot_utc
   AND (p_metric_key IS NULL OR s.metric_key=p_metric_key)
   AND (p_dimension_hash IS NULL OR sha256(convert_to(s.dimensions::text,'UTF8'))=p_dimension_hash)
 UNION ALL
 SELECT h.observed_at,h.metric_key,h.metric_value,h.dimensions,false,true
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision
   AND h.observed_at>=p_from_utc AND h.observed_at<p_to_utc AND h.observed_at<=p_snapshot_utc
   AND (p_metric_key IS NULL OR h.metric_key=p_metric_key)
   AND (p_dimension_hash IS NULL OR sha256(convert_to(h.dimensions::text,'UTF8'))=p_dimension_hash)
 UNION ALL
 SELECT r.observed_at,'replication.pending_commands',r.pending_commands::double precision,'{}'::jsonb,false,true
 FROM telemetry.replication_snapshot_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND r.pending_commands IS NOT NULL
   AND (p_metric_key IS NULL OR p_metric_key='replication.pending_commands')
   AND (p_dimension_hash IS NULL OR p_dimension_hash=sha256(convert_to('{}','UTF8')))
 UNION ALL
 SELECT r.observed_at,'replication.latency_seconds',r.latency_seconds,'{}'::jsonb,false,true
 FROM telemetry.replication_snapshot_v2 r
 WHERE control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text AND r.instance_id=p_instance_id AND r.target_revision=p_target_revision
   AND r.observed_at>=p_from_utc AND r.observed_at<p_to_utc AND r.observed_at<=p_snapshot_utc AND r.latency_seconds IS NOT NULL
   AND (p_metric_key IS NULL OR p_metric_key='replication.latency_seconds')
   AND (p_dimension_hash IS NULL OR p_dimension_hash=sha256(convert_to('{}','UTF8')))
 ) SELECT observed_at,metric_key,metric_value,dimensions,reset,complete FROM source_rows
 WHERE p_rollup_interval IN ('5m','hour','day') AND p_to_utc>p_from_utc AND p_to_utc-p_from_utc<=interval '90 days' AND p_limit BETWEEN 1 AND 100000
 ORDER BY observed_at,metric_key,dimensions LIMIT p_limit;
$$;

-- Compatibility overload retained for callers that have not yet selected a
-- dimension; the five-argument contract is the authoritative one used by M10.
CREATE OR REPLACE FUNCTION reporting.get_m10_forecast_capacity(p_instance_id uuid,p_target_revision bigint,p_metric_key text,p_dimension_hash bytea,p_snapshot_utc timestamptz)
RETURNS double precision LANGUAGE sql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,telemetry,analytics,control SET TimeZone='UTC' AS $$
 SELECT CASE WHEN max(h.metric_value) FILTER (WHERE h.metric_value>0 AND h.metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision))>0
   THEN max(h.metric_value) FILTER (WHERE h.metric_value>0 AND h.metric_value NOT IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision)) ELSE NULL END
 FROM telemetry.host_metric_snapshot_v2 h
 WHERE p_instance_id IS NOT NULL AND p_target_revision>0 AND p_metric_key IN ('host.volume.free_bytes','host.volume.total_bytes')
   AND p_snapshot_utc IS NOT NULL AND h.instance_id=p_instance_id AND h.target_revision=p_target_revision
   AND h.metric_key='host.volume.total_bytes' AND h.observed_at<=p_snapshot_utc
   AND p_dimension_hash IS NOT NULL AND sha256(convert_to(h.dimensions::text,'UTF8'))=p_dimension_hash
   AND current_setting('sqlobserver.target_scope',true)=p_instance_id::text
   AND control.resolve_m10_target_revision(p_instance_id,p_target_revision)=p_target_revision
$$;

 REVOKE ALL ON FUNCTION reporting.read_m10_rollup_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea),reporting.get_m10_forecast_capacity(uuid,bigint,text,bytea,timestamptz) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
 GRANT EXECUTE ON FUNCTION reporting.read_m10_rollup_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea),reporting.get_m10_forecast_capacity(uuid,bigint,text,bytea,timestamptz) TO sqlobserver_collector;

-- Replace the original ten-column claim projection with the M10 dataflow
-- projection.  The drop is required because PostgreSQL does not allow a
-- CREATE OR REPLACE to change a function's composite return type.
DROP FUNCTION IF EXISTS control.claim_m10_derivation_jobs(text,uuid,bigint,integer);
CREATE OR REPLACE FUNCTION control.claim_m10_derivation_jobs(p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,p_limit integer)
RETURNS TABLE(job_id uuid,instance_id uuid,target_revision bigint,job_kind text,from_utc timestamptz,to_utc timestamptz,source_cutoff_utc timestamptz,generation bigint,metric_key text,horizon_seconds double precision,dimensions_hash bytea,rollup_interval text)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control SET TimeZone='UTC' AS $m10_claim_derivation_v2$
BEGIN
 IF p_work_key IS DISTINCT FROM 'analytics/derivation' OR p_owner_execution_id IS NULL OR p_fencing_token<1 OR p_limit NOT BETWEEN 1 AND 2 THEN RAISE EXCEPTION 'analytics derivation claim bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
 RETURN QUERY
 WITH candidates AS (
   SELECT j.job_id FROM control.analytics_job j JOIN control.observation_target t ON t.instance_id=j.instance_id AND t.revision=j.target_revision
   WHERE j.work_key='analytics/derivation' AND j.job_kind IN ('rollup','baseline','forecast','evidence','correlation')
     AND j.from_utc IS NOT NULL AND j.to_utc IS NOT NULL
     AND (j.status IN ('queued','partial') OR (j.status='running' AND NOT EXISTS
       (SELECT 1 FROM control.worker_lease l WHERE l.work_key=j.work_key AND l.released_at IS NULL AND l.expires_at>clock_timestamp())))
   ORDER BY j.requested_at,j.job_id LIMIT p_limit FOR UPDATE SKIP LOCKED
 ), exhausted AS (
   UPDATE control.analytics_job j SET status='failed',last_error='maximum_attempts_exceeded',completed_at=clock_timestamp(),owner_execution_id=NULL,fencing_token=NULL
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt>=5 RETURNING j.job_id
 ), claimed AS (
   UPDATE control.analytics_job j SET status='running',owner_execution_id=p_owner_execution_id,fencing_token=p_fencing_token,started_at=clock_timestamp(),attempt=j.attempt+1
   FROM candidates c WHERE j.job_id=c.job_id AND j.attempt<5 RETURNING j.*
 ) SELECT c.job_id,c.instance_id,c.target_revision,c.job_kind,c.from_utc,c.to_utc,coalesce(c.source_cutoff_utc,c.to_utc),c.generation,c.metric_key,c.horizon_seconds,c.dimensions_hash,c.rollup_interval FROM claimed c;
END $m10_claim_derivation_v2$;
REVOKE ALL ON FUNCTION control.claim_m10_derivation_jobs(text,uuid,bigint,integer) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
 GRANT EXECUTE ON FUNCTION control.claim_m10_derivation_jobs(text,uuid,bigint,integer) TO sqlobserver_collector;
