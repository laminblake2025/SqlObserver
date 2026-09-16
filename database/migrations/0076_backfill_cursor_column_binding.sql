-- Keep decoded cursor variables distinct from analytics_job columns.
-- Preserve exact job/target/revision/lease fences and opaque cursor validation.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout='5s';
CREATE OR REPLACE FUNCTION control.advance_m10_backfill_cursor(p_job_id uuid,p_day_start_utc timestamptz,p_cursor text,p_owner_execution_id uuid,p_fencing_token bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER VOLATILE SET search_path=pg_catalog,control AS $m10_advance_cursor$
DECLARE v_changed integer; v_c jsonb; v_source_kind text; v_observed_at timestamptz; v_source_id uuid; v_metric_key text; v_dimension_hash bytea; v_ordinal integer; v_target_id uuid; v_target_revision bigint; v_cursor_day timestamptz; v_catalog_version integer;
BEGIN
 IF p_job_id IS NULL OR p_day_start_utc IS NULL OR extract(timezone from p_day_start_utc)<>0 OR p_cursor IS NOT NULL AND octet_length(p_cursor)>4096 OR p_owner_execution_id IS NULL OR p_fencing_token<=0 THEN RAISE EXCEPTION 'analytics backfill cursor bounds rejected' USING ERRCODE='22023'; END IF;
 PERFORM control.assert_worker_lease('analytics/backfill',p_owner_execution_id,p_fencing_token);
 IF p_cursor IS NOT NULL THEN BEGIN
   v_c:=convert_from(decode(replace(replace(p_cursor,'-','+'),'_','/')||repeat('=',(4-length(p_cursor)%4)%4),'base64'),'UTF8')::jsonb;
   v_source_kind:=v_c->>'sourceKind'; v_observed_at:=(v_c->>'observedAtUtc')::timestamptz; v_source_id:=(v_c->>'sourceId')::uuid; v_metric_key:=v_c->>'metricKey'; v_dimension_hash:=decode(v_c->>'dimensionHash','hex'); v_ordinal:=(v_c->>'ordinal')::integer; v_target_id:=(v_c->>'targetId')::uuid; v_target_revision:=(v_c->>'targetRevision')::bigint; v_cursor_day:=(v_c->>'dayStartUtc')::timestamptz; v_catalog_version:=(v_c->>'catalogVersion')::integer;
   IF v_c->>'v' IS DISTINCT FROM 'm10.backfill.v2' OR v_source_kind NOT IN ('raw','host','replication') OR v_source_id IS NULL OR v_metric_key IS NULL OR octet_length(v_dimension_hash)<>32 OR v_ordinal<1 OR v_target_id IS NULL OR v_target_revision<1 OR v_cursor_day<>p_day_start_utc OR v_catalog_version<>1 OR v_c->>'jobId'<>p_job_id::text THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END IF;
 EXCEPTION WHEN OTHERS THEN RAISE EXCEPTION 'analytics backfill cursor is invalid' USING ERRCODE='22023'; END; END IF;
 UPDATE control.analytics_job j SET cursor=p_cursor,cursor_source_kind=v_source_kind,cursor_observed_at=v_observed_at,cursor_source_id=v_source_id,cursor_metric_key=v_metric_key,cursor_dimension_hash=v_dimension_hash,cursor_ordinal=v_ordinal,cursor_target_id=v_target_id,cursor_target_revision=v_target_revision,cursor_day_utc=v_cursor_day,cursor_catalog_version=v_catalog_version WHERE j.job_id=p_job_id AND j.job_kind='backfill' AND j.status='running' AND j.owner_execution_id=p_owner_execution_id AND j.fencing_token=p_fencing_token AND j.from_utc<=p_day_start_utc AND j.to_utc>p_day_start_utc AND (p_cursor IS NULL OR (j.instance_id=v_target_id AND j.target_revision=v_target_revision));
 GET DIAGNOSTICS v_changed=ROW_COUNT; IF v_changed<>1 THEN RAISE EXCEPTION 'analytics backfill cursor lease conflict' USING ERRCODE='40001'; END IF; RETURN true;
END $m10_advance_cursor$;
