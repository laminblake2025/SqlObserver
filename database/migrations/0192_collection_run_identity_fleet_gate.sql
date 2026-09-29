-- Extend existing parity certificates and the writer-fenced fleet proof to
-- require the global run identity bridge before any child FK cutover.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE system.collection_run_v2_parity_window
 ADD COLUMN identity_rows bigint CHECK(identity_rows>=0),
 ADD COLUMN identity_sha256 text CHECK(identity_sha256 ~ '^[0-9a-f]{64}$'),
 ADD COLUMN identity_successor_sha256 text
  CHECK(identity_successor_sha256 ~ '^[0-9a-f]{64}$'),
 ADD COLUMN identity_parity boolean NOT NULL DEFAULT false;

CREATE OR REPLACE FUNCTION control.record_collection_run_v2_parity_window(
 p_instance_id uuid,p_collector_id text,p_from_utc timestamptz,
 p_to_utc timestamptz,p_max_rows integer)
RETURNS TABLE(source_rows bigint,successor_rows bigint,
 source_sha256 text,successor_sha256 text,parity boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,control,system SET row_security=on
SET TimeZone='UTC'
AS $record$
DECLARE
 v_day date;
 v_revision bigint;
 v_result record;
 v_identity record;
BEGIN
 IF p_from_utc IS NULL OR p_to_utc IS NULL OR p_to_utc<=p_from_utc
    OR (p_from_utc AT TIME ZONE 'UTC')::date<>
       ((p_to_utc-interval '1 microsecond') AT TIME ZONE 'UTC')::date THEN
  RAISE EXCEPTION 'collection run parity window must fit one UTC day'
   USING ERRCODE='22023';
 END IF;
 v_day:=(p_from_utc AT TIME ZONE 'UTC')::date;
 -- Read generation before verification. A concurrent insert that commits
 -- during or after verification makes this certificate stale.
 SELECT generation.revision INTO v_revision
 FROM system.collection_run_v2_day_generation AS generation
 WHERE generation.instance_id=p_instance_id AND generation.utc_day=v_day;
 v_revision:=coalesce(v_revision,0);
 SELECT * INTO v_result FROM control.verify_collection_run_v2_window(
  p_instance_id,p_collector_id,p_from_utc,p_to_utc,p_max_rows);
 SELECT * INTO v_identity FROM control.verify_collection_run_identity_window(
  p_instance_id,p_collector_id,p_from_utc,p_to_utc,p_max_rows);
 INSERT INTO system.collection_run_v2_parity_window AS parity_row
  (instance_id,collector_id,from_utc,to_utc,utc_day,day_revision,
   max_rows,source_rows,successor_rows,source_sha256,successor_sha256,
   identity_rows,identity_sha256,identity_successor_sha256,
   identity_parity,parity)
 VALUES(p_instance_id,p_collector_id,p_from_utc,p_to_utc,v_day,v_revision,
  p_max_rows,v_result.source_rows,v_result.successor_rows,
  v_result.source_sha256,v_result.successor_sha256,
  v_identity.identity_rows,v_identity.identity_sha256,
  v_identity.successor_sha256,v_identity.parity,v_result.parity)
 ON CONFLICT(instance_id,collector_id,from_utc,to_utc) DO UPDATE SET
  day_revision=excluded.day_revision,max_rows=excluded.max_rows,
  source_rows=excluded.source_rows,successor_rows=excluded.successor_rows,
  source_sha256=excluded.source_sha256,
  successor_sha256=excluded.successor_sha256,
  identity_rows=excluded.identity_rows,
  identity_sha256=excluded.identity_sha256,
  identity_successor_sha256=excluded.identity_successor_sha256,
  identity_parity=excluded.identity_parity,parity=excluded.parity,
  verified_at=clock_timestamp();
 RETURN QUERY SELECT v_result.source_rows,v_result.successor_rows,
  v_result.source_sha256,v_result.successor_sha256,
  v_result.parity AND v_identity.parity;
END $record$;
REVOKE ALL ON FUNCTION control.record_collection_run_v2_parity_window(
 uuid,text,timestamptz,timestamptz,integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.record_collection_run_v2_parity_window(
 uuid,text,timestamptz,timestamptz,integer) TO sqlobserver_migrator;

CREATE OR REPLACE FUNCTION control.collection_run_v2_parity_coverage(
 p_instance_id uuid,p_collector_id text)
RETURNS TABLE(from_utc timestamptz,to_utc timestamptz,
 covered_through_utc timestamptz,all_covered boolean,
 verified_windows bigint,failed_or_stale_windows bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL RESTRICTED
SET search_path=pg_catalog,control,telemetry,system SET row_security=on
SET TimeZone='UTC'
AS $coverage$
DECLARE
 v_first timestamptz;
 v_last timestamptz;
 v_from timestamptz;
 v_to timestamptz;
 v_cursor timestamptz;
 v_verified bigint;
 v_failed bigint;
 v_complete boolean;
 v_window record;
BEGIN
 IF p_instance_id IS NULL OR p_collector_id IS NULL
    OR octet_length(p_collector_id) NOT BETWEEN 1 AND 128
    OR NOT pg_has_role(session_user,'sqlobserver_migrator','member')
    OR NOT control.query_performance_target_claim(p_instance_id) THEN
  RAISE EXCEPTION 'collection run parity coverage target scope missing'
   USING ERRCODE='42501';
 END IF;
 SELECT state.completed_at IS NOT NULL INTO v_complete
 FROM system.collection_run_v2_backfill AS state
 WHERE state.instance_id=p_instance_id;
 v_complete:=coalesce(v_complete,false) AND EXISTS (
  SELECT 1 FROM system.collection_run_identity_backfill AS state
  WHERE state.instance_id=p_instance_id
    AND state.completed_at IS NOT NULL);
 SELECT min(bounds.first_at),max(bounds.last_at) INTO v_first,v_last
 FROM (
  SELECT min(run.started_at) AS first_at,max(run.started_at) AS last_at
  FROM telemetry.collection_run AS run
  WHERE run.instance_id=p_instance_id AND run.collector_id=p_collector_id
  UNION ALL
  SELECT min(run.started_at),max(run.started_at)
  FROM telemetry.collection_run_v2 AS run
  WHERE run.instance_id=p_instance_id AND run.collector_id=p_collector_id
 ) AS bounds;
 SELECT count(*) FILTER(WHERE parity_row.parity AND parity_row.identity_parity AND
                         parity_row.day_revision=coalesce(generation.revision,0)),
        count(*) FILTER(WHERE NOT parity_row.parity OR NOT parity_row.identity_parity OR
                         parity_row.day_revision<>coalesce(generation.revision,0))
 INTO v_verified,v_failed
 FROM system.collection_run_v2_parity_window AS parity_row
 LEFT JOIN system.collection_run_v2_day_generation AS generation
  ON generation.instance_id=parity_row.instance_id
 AND generation.utc_day=parity_row.utc_day
 WHERE parity_row.instance_id=p_instance_id
   AND parity_row.collector_id=p_collector_id;
 IF v_first IS NULL THEN
  RETURN QUERY SELECT NULL::timestamptz,NULL::timestamptz,
   NULL::timestamptz,v_complete,v_verified,v_failed;
  RETURN;
 END IF;
 v_from:=date_trunc('day',v_first AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
 v_to:=(date_trunc('day',v_last AT TIME ZONE 'UTC')+
        interval '1 day') AT TIME ZONE 'UTC';
 v_cursor:=v_from;
 FOR v_window IN
  SELECT parity_row.from_utc,parity_row.to_utc
  FROM system.collection_run_v2_parity_window AS parity_row
  LEFT JOIN system.collection_run_v2_day_generation AS generation
   ON generation.instance_id=parity_row.instance_id
  AND generation.utc_day=parity_row.utc_day
  WHERE parity_row.instance_id=p_instance_id
    AND parity_row.collector_id=p_collector_id AND parity_row.parity
    AND parity_row.identity_parity
    AND parity_row.day_revision=coalesce(generation.revision,0)
    AND parity_row.to_utc>v_from AND parity_row.from_utc<v_to
  ORDER BY parity_row.from_utc,parity_row.to_utc
 LOOP
  EXIT WHEN v_window.from_utc>v_cursor;
  v_cursor:=greatest(v_cursor,v_window.to_utc);
  EXIT WHEN v_cursor>=v_to;
 END LOOP;
 RETURN QUERY SELECT v_from,v_to,least(v_cursor,v_to),
  v_complete AND v_cursor>=v_to,v_verified,v_failed;
END $coverage$;
REVOKE ALL ON FUNCTION control.collection_run_v2_parity_coverage(uuid,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.collection_run_v2_parity_coverage(uuid,text)
 TO sqlobserver_migrator;

CREATE OR REPLACE FUNCTION control.verify_collection_run_v2_fleet_fenced(
 p_max_pairs integer DEFAULT 10000)
RETURNS TABLE(target_count bigint,collector_count bigint,pair_count bigint)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog,control,telemetry,system SET row_security=on
SET TimeZone='UTC'
AS $fence$
DECLARE
 v_target_count bigint;
 v_collector_count bigint;
 v_pairs bigint:=0;
 v_instance_id uuid;
 v_collector_id text;
 v_coverage record;
 v_prior_scope text:=current_setting('sqlobserver.target_scope',true);
BEGIN
 IF NOT pg_has_role(session_user,'sqlobserver_migrator','member') THEN
  RAISE EXCEPTION 'collection run fleet check requires migrator'
   USING ERRCODE='42501';
 END IF;
 IF p_max_pairs IS NULL OR p_max_pairs NOT BETWEEN 1 AND 100000 THEN
  RAISE EXCEPTION 'collection run fleet check pair bound invalid'
   USING ERRCODE='22023';
 END IF;

 -- SHARE conflicts with the ROW EXCLUSIVE locks taken by every relevant
 -- insert or ledger update. These locks last through the caller transaction.
 LOCK TABLE control.observation_target IN SHARE MODE;
 LOCK TABLE control.collector_contract IN SHARE MODE;
 LOCK TABLE telemetry.collection_run IN SHARE MODE;
 LOCK TABLE telemetry.collection_run_v2 IN SHARE MODE;
 LOCK TABLE telemetry.collection_run_identity IN SHARE MODE;
 LOCK TABLE system.partition_registry IN SHARE MODE;
 LOCK TABLE system.collection_run_v2_backfill IN SHARE MODE;
 LOCK TABLE system.collection_run_identity_backfill IN SHARE MODE;
 LOCK TABLE system.collection_run_v2_day_generation IN SHARE MODE;
 LOCK TABLE system.collection_run_v2_parity_window IN SHARE MODE;

 IF EXISTS (
  SELECT 1 FROM (VALUES
   ('telemetry.collection_run'::regclass,'collection_run_append_only'),
   ('telemetry.collection_run'::regclass,'collection_run_v2_mirror'),
   ('telemetry.collection_run'::regclass,
    'collection_run_v2_source_generation'),
   ('telemetry.collection_run_v2'::regclass,
    'collection_run_v2_append_only'),
   ('telemetry.collection_run_v2'::regclass,
    'collection_run_v2_successor_generation'),
   ('telemetry.collection_run_v2'::regclass,
    'collection_run_identity_mirror'),
   ('telemetry.collection_run_identity'::regclass,
    'collection_run_identity_append_only')
  ) AS required(relation_id,trigger_name)
  LEFT JOIN pg_trigger AS trigger_row
   ON trigger_row.tgrelid=required.relation_id
  AND trigger_row.tgname=required.trigger_name
  AND trigger_row.tgenabled IN ('O','A')
  WHERE trigger_row.oid IS NULL
 ) OR EXISTS (
  SELECT 1 FROM (VALUES
   ('telemetry.collection_run'::regclass),
   ('telemetry.collection_run_v2'::regclass),
   ('telemetry.collection_run_identity'::regclass)
  ) AS required(relation_id)
  LEFT JOIN pg_class AS relation ON relation.oid=required.relation_id
  WHERE relation.oid IS NULL OR NOT relation.relrowsecurity
     OR NOT relation.relforcerowsecurity
 ) OR EXISTS (
  SELECT 1 FROM system.partition_registry AS registry
  WHERE registry.parent_schema='telemetry'
    AND registry.parent_table='collection_run_v2'
    AND registry.lifecycle_state<>'attached'
 ) OR NOT EXISTS (
  SELECT 1 FROM pg_constraint AS fk
 WHERE fk.conname='fk_collection_run_identity_v2'
    AND fk.conrelid='telemetry.collection_run_identity'::regclass
    AND fk.confrelid='telemetry.collection_run_v2'::regclass
    AND fk.contype='f' AND fk.convalidated
 ) OR EXISTS (
  SELECT 1 FROM pg_constraint AS fk
  JOIN pg_trigger AS fk_trigger ON fk_trigger.tgconstraint=fk.oid
  WHERE fk.conname='fk_collection_run_identity_v2'
    AND fk.conrelid='telemetry.collection_run_identity'::regclass
    AND fk.confrelid='telemetry.collection_run_v2'::regclass
    AND fk_trigger.tgenabled NOT IN ('O','A')
 ) THEN
  RAISE EXCEPTION 'collection run mirror, identity, scope, or partition gate is disabled'
   USING ERRCODE='55000';
 END IF;

 SELECT count(*) INTO v_target_count FROM control.observation_target;
 SELECT count(*) INTO v_collector_count FROM (
  SELECT DISTINCT contract.collector_id
  FROM control.collector_contract AS contract) AS catalog;
 IF v_collector_count=0 THEN
  RAISE EXCEPTION 'collection run collector catalog is empty'
   USING ERRCODE='55000';
 END IF;
 IF v_target_count*v_collector_count>p_max_pairs THEN
  RAISE EXCEPTION 'collection run fleet check exceeds pair bound'
   USING ERRCODE='54000';
 END IF;
 FOR v_instance_id,v_collector_id IN
  SELECT target.instance_id,catalog.collector_id
  FROM control.observation_target AS target
  CROSS JOIN (SELECT DISTINCT contract.collector_id
              FROM control.collector_contract AS contract) AS catalog
  ORDER BY target.instance_id,catalog.collector_id
 LOOP
  PERFORM set_config('sqlobserver.target_scope',v_instance_id::text,true);
  SELECT * INTO v_coverage
  FROM control.collection_run_v2_parity_coverage(
   v_instance_id,v_collector_id);
  IF NOT coalesce(v_coverage.all_covered,false) THEN
   RAISE EXCEPTION 'collection run parity incomplete for target %, collector %',
    v_instance_id,v_collector_id USING ERRCODE='55000';
  END IF;
  v_pairs:=v_pairs+1;
 END LOOP;
 PERFORM set_config('sqlobserver.target_scope',coalesce(v_prior_scope,''),true);
 RETURN QUERY SELECT v_target_count,v_collector_count,v_pairs;
END $fence$;
REVOKE ALL ON FUNCTION control.verify_collection_run_v2_fleet_fenced(integer)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.verify_collection_run_v2_fleet_fenced(integer)
 TO sqlobserver_migrator;
