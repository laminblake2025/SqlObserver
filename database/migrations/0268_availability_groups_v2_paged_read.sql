-- Read AG v2 receipts and bounded per-replica evidence without changing API routes.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL idle_in_transaction_session_timeout = '5min';

CREATE FUNCTION reporting.get_availability_groups_v2_header(p_instance_id uuid,p_at_utc timestamptz)
RETURNS TABLE(run_id uuid,target_revision bigint,source_observed_at timestamptz,
 completed_at timestamptz,observation_state smallint,evidence_status text,
 source_rows integer,output_items integer,retained_items bigint,
 loss_kind text,truncated boolean,collector_enabled boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan
AS $header$
DECLARE
 v_revision bigint;
 v_enabled boolean;
 v_run record;
 v_retained bigint;
 v_observed timestamptz;
 v_state smallint;
 v_status text;
BEGIN
 IF p_instance_id IS NULL OR p_at_utc IS NULL OR NOT isfinite(p_at_utc)
 THEN RAISE EXCEPTION 'AG v2 snapshot bounds are invalid' USING ERRCODE='22023'; END IF;
 IF control.query_performance_target_claim(p_instance_id) IS DISTINCT FROM true
 THEN RAISE EXCEPTION 'AG v2 target scope is required' USING ERRCODE='42501'; END IF;
 SELECT target.revision INTO v_revision FROM control.observation_target AS target
 WHERE target.instance_id=p_instance_id AND target.lifecycle_state='active';
 IF NOT FOUND THEN RAISE EXCEPTION 'AG v2 target is inactive' USING ERRCODE='42501'; END IF;
 SELECT schedule.enabled INTO v_enabled FROM control.collector_schedule AS schedule
 WHERE schedule.instance_id=p_instance_id AND schedule.target_revision=v_revision
  AND schedule.collector_id='availability-groups.health' AND schedule.collector_version=2;

 -- A fixed repository completion cutoff keeps pagination on the same published
 -- run even when another run completes. Scan at most the preceding seven days.
 SELECT run.run_id,run.started_at,outcome.completed_at,outcome.outcome,
  outcome.source_row_count,outcome.output_item_count,outcome.loss_kind,outcome.truncated
 INTO v_run FROM telemetry.collection_run_v2 AS run
 JOIN telemetry.collection_run_outcome AS outcome ON outcome.run_id=run.run_id
 WHERE run.instance_id=p_instance_id AND run.target_revision=v_revision
  AND run.collector_id='availability-groups.health' AND run.collector_version=2
  AND run.output_schema_version=2 AND run.started_at BETWEEN p_at_utc-interval '7 days' AND p_at_utc
  AND outcome.completed_at<=p_at_utc
 ORDER BY run.started_at DESC,run.run_id DESC LIMIT 1;
 IF NOT FOUND THEN
  RETURN QUERY SELECT NULL::uuid,v_revision,NULL::timestamptz,NULL::timestamptz,
   6::smallint,'not_collected'::text,0,0,0::bigint,'none'::text,false,coalesce(v_enabled,false);
  RETURN;
 END IF;

 SELECT count(*),min(evidence.observed_at) INTO v_retained,v_observed FROM (
  SELECT replica.observed_at FROM telemetry.availability_replica_evidence AS replica
  WHERE replica.instance_id=p_instance_id AND replica.target_revision=v_revision AND replica.run_id=v_run.run_id
   AND replica.observed_at BETWEEN v_run.started_at-interval '5 minutes' AND v_run.completed_at+interval '5 minutes'
  UNION ALL
  SELECT database.observed_at FROM telemetry.availability_database_replica_evidence AS database
  WHERE database.instance_id=p_instance_id AND database.target_revision=v_revision AND database.run_id=v_run.run_id
   AND database.observed_at BETWEEN v_run.started_at-interval '5 minutes' AND v_run.completed_at+interval '5 minutes'
 ) AS evidence;
 v_state:=CASE WHEN v_run.outcome='permission_denied' THEN 5
  WHEN v_run.outcome='unsupported' THEN 4
  WHEN v_run.outcome NOT IN ('succeeded','partial') THEN 3
  WHEN v_run.loss_kind='visibility_incomplete' THEN 3
  WHEN v_run.truncated OR v_run.loss_kind IN ('source_row_limit','response_byte_limit') THEN 2
  WHEN v_run.source_row_count=0 THEN 6 ELSE 1 END;
 v_status:=CASE WHEN v_run.outcome NOT IN ('succeeded','partial') THEN 'collection_failed'
  WHEN v_retained<>v_run.output_item_count THEN 'evidence_not_retained'
  WHEN v_run.loss_kind='response_byte_limit' THEN 'withheld_by_byte_limit'
  WHEN v_run.source_row_count=0 THEN 'no_data' ELSE 'retained' END;
 IF v_status='evidence_not_retained' THEN v_state:=3; END IF;
 RETURN QUERY SELECT v_run.run_id,v_revision,v_observed,v_run.completed_at,v_state,v_status,
  v_run.source_row_count,v_run.output_item_count,v_retained,v_run.loss_kind,v_run.truncated,coalesce(v_enabled,false);
END $header$;
REVOKE ALL ON FUNCTION reporting.get_availability_groups_v2_header(uuid,timestamptz)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_availability_groups_v2_header(uuid,timestamptz) TO sqlobserver_server;

CREATE FUNCTION reporting.assert_availability_groups_v2_page(p_instance_id uuid,p_at_utc timestamptz,
 p_limit integer,p_expected_revision bigint,p_expected_run_id uuid,
 p_after_group bytea,p_after_replica bytea,p_after_database bytea,p_database_stream boolean)
RETURNS TABLE(snapshot_run_id uuid,revision bigint,observed_at timestamptz,readable boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' AS $page_gate$
DECLARE header record;
BEGIN
 IF p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 100 OR p_expected_revision IS NULL OR p_expected_revision<=0
  OR p_expected_run_id IS NULL OR p_expected_run_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_database_stream IS NULL OR (p_after_group IS NULL)<>(p_after_replica IS NULL)
  OR (p_database_stream AND (p_after_group IS NULL)<>(p_after_database IS NULL))
  OR (NOT p_database_stream AND p_after_database IS NOT NULL)
  OR (p_after_group IS NOT NULL AND (octet_length(p_after_group)<>32 OR octet_length(p_after_replica)<>32))
  OR (p_after_database IS NOT NULL AND octet_length(p_after_database)<>32)
 THEN RAISE EXCEPTION 'AG v2 page bounds or cursor are invalid' USING ERRCODE='22023'; END IF;
 SELECT * INTO header FROM reporting.get_availability_groups_v2_header(p_instance_id,p_at_utc);
 IF header.target_revision<>p_expected_revision OR header.run_id IS DISTINCT FROM p_expected_run_id
 THEN RAISE EXCEPTION 'AG v2 page cursor is stale' USING ERRCODE='40001'; END IF;
 RETURN QUERY SELECT header.run_id,header.target_revision,header.source_observed_at,
  header.evidence_status IN ('retained','evidence_not_retained');
END $page_gate$;
REVOKE ALL ON FUNCTION reporting.assert_availability_groups_v2_page(
 uuid,timestamptz,integer,bigint,uuid,bytea,bytea,bytea,boolean)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION reporting.list_availability_replica_evidence(p_instance_id uuid,p_at_utc timestamptz,
 p_limit integer,p_expected_revision bigint,p_expected_run_id uuid,
 p_after_group bytea DEFAULT NULL,p_after_replica bytea DEFAULT NULL)
RETURNS TABLE(group_fingerprint bytea,replica_fingerprint bytea,observer_replica_fingerprint bytea,
 replica_is_local boolean,visibility_scope smallint,state_observed boolean,
 role text,operational_state text,connected_state text)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $replicas$
DECLARE snapshot record;
BEGIN
 SELECT * INTO snapshot FROM reporting.assert_availability_groups_v2_page(p_instance_id,p_at_utc,
  p_limit,p_expected_revision,p_expected_run_id,p_after_group,p_after_replica,NULL,false);
 IF NOT snapshot.readable THEN RETURN; END IF;
 RETURN QUERY SELECT item.group_fingerprint,item.replica_fingerprint,item.observer_replica_fingerprint,
  item.replica_is_local,item.visibility_scope,item.state_observed,item.role,item.operational_state,item.connected_state
 FROM telemetry.availability_replica_evidence AS item
 WHERE item.instance_id=p_instance_id AND item.target_revision=snapshot.revision
  AND item.run_id=snapshot.snapshot_run_id AND item.observed_at=snapshot.observed_at
  AND (p_after_group IS NULL OR (item.group_fingerprint,item.replica_fingerprint)>(p_after_group,p_after_replica))
 ORDER BY item.group_fingerprint,item.replica_fingerprint LIMIT p_limit+1;
END $replicas$;
REVOKE ALL ON FUNCTION reporting.list_availability_replica_evidence(uuid,timestamptz,integer,bigint,uuid,bytea,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_availability_replica_evidence(uuid,timestamptz,integer,bigint,uuid,bytea,bytea)
 TO sqlobserver_server;

CREATE FUNCTION reporting.list_availability_database_replica_evidence(p_instance_id uuid,p_at_utc timestamptz,
 p_limit integer,p_expected_revision bigint,p_expected_run_id uuid,
 p_after_group bytea DEFAULT NULL,p_after_replica bytea DEFAULT NULL,p_after_database bytea DEFAULT NULL)
RETURNS TABLE(group_fingerprint bytea,replica_fingerprint bytea,database_fingerprint bytea,
 observer_replica_fingerprint bytea,replica_is_local boolean,visibility_scope smallint,state_observed boolean,
 synchronization_state text,synchronization_health text,database_state text,is_suspended boolean,
 log_send_queue_kb bigint,log_send_rate_kb_per_second bigint,redo_queue_kb bigint,redo_rate_kb_per_second bigint,
 source_last_commit_local timestamp without time zone)
LANGUAGE plpgsql STABLE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC' SET plan_cache_mode=force_custom_plan AS $databases$
DECLARE snapshot record;
BEGIN
 SELECT * INTO snapshot FROM reporting.assert_availability_groups_v2_page(p_instance_id,p_at_utc,
  p_limit,p_expected_revision,p_expected_run_id,p_after_group,p_after_replica,p_after_database,true);
 IF NOT snapshot.readable THEN RETURN; END IF;
 RETURN QUERY SELECT item.group_fingerprint,item.replica_fingerprint,item.database_fingerprint,
  item.observer_replica_fingerprint,item.replica_is_local,item.visibility_scope,item.state_observed,
  item.synchronization_state,item.synchronization_health,item.database_state,item.is_suspended,
  item.log_send_queue_kb,item.log_send_rate_kb_per_second,item.redo_queue_kb,item.redo_rate_kb_per_second,
  item.source_last_commit_local
 FROM telemetry.availability_database_replica_evidence AS item
 WHERE item.instance_id=p_instance_id AND item.target_revision=snapshot.revision
  AND item.run_id=snapshot.snapshot_run_id AND item.observed_at=snapshot.observed_at
  AND (p_after_group IS NULL OR (item.group_fingerprint,item.replica_fingerprint,item.database_fingerprint)>
    (p_after_group,p_after_replica,p_after_database))
 ORDER BY item.group_fingerprint,item.replica_fingerprint,item.database_fingerprint LIMIT p_limit+1;
END $databases$;
REVOKE ALL ON FUNCTION reporting.list_availability_database_replica_evidence(
 uuid,timestamptz,integer,bigint,uuid,bytea,bytea,bytea)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_availability_database_replica_evidence(
 uuid,timestamptz,integer,bigint,uuid,bytea,bytea,bytea) TO sqlobserver_server;

-- Keep the legacy AG API isolated from version-2 runs on both run-table routes.
CREATE OR REPLACE FUNCTION reporting.get_latest_m9_run_legacy(p_instance_id uuid,p_collector_id text)
RETURNS TABLE(run_id uuid,target_revision bigint,observed_at timestamptz,state text)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=reporting,telemetry,control,pg_catalog AS $function$
DECLARE
    completed_id uuid;
    completed_at timestamptz;
    unfinished_id uuid;
    unfinished_at timestamptz;
    selected_id uuid;
BEGIN
    IF p_instance_id IS NULL OR p_collector_id IS NULL
       OR p_collector_id NOT IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
       OR p_instance_id::text IS DISTINCT FROM current_setting('sqlobserver.target_scope',true) THEN
        RETURN;
    END IF;

    EXECUTE $query$
        SELECT r.run_id,o.completed_at
        FROM telemetry.collection_run_outcome o
        JOIN telemetry.collection_run r ON r.run_id=o.run_id
        JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
        WHERE r.instance_id=$1 AND r.collector_id=$2 AND (r.collector_id<>'availability-groups.health' OR r.collector_version=1)
        ORDER BY o.completed_at DESC,o.run_id DESC LIMIT 1
    $query$ INTO completed_id,completed_at USING p_instance_id,p_collector_id;

    IF completed_id IS NULL THEN
        -- With no completed run in this statement snapshot, every matching run
        -- is unfinished. No anti-join over historical outcomes is necessary.
        EXECUTE $query$
            SELECT r.run_id,r.started_at FROM telemetry.collection_run r
            JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
            WHERE r.instance_id=$1 AND r.collector_id=$2 AND (r.collector_id<>'availability-groups.health' OR r.collector_version=1)
            ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1
        $query$ INTO unfinished_id,unfinished_at USING p_instance_id,p_collector_id;
    ELSE
        EXECUTE $query$
            SELECT r.run_id,r.started_at FROM telemetry.collection_run r
            JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
            WHERE r.instance_id=$1 AND r.collector_id=$2 AND (r.collector_id<>'availability-groups.health' OR r.collector_version=1) AND r.started_at >= $3
              AND NOT EXISTS (SELECT 1 FROM telemetry.collection_run_outcome o WHERE o.run_id=r.run_id)
            ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1
        $query$ INTO unfinished_id,unfinished_at USING p_instance_id,p_collector_id,completed_at;
    END IF;

    selected_id := CASE WHEN completed_id IS NULL THEN unfinished_id
        WHEN unfinished_id IS NULL THEN completed_id
        WHEN (unfinished_at,unfinished_id) > (completed_at,completed_id) THEN unfinished_id
        ELSE completed_id END;
    IF selected_id IS NULL THEN RETURN; END IF;

    RETURN QUERY
    SELECT r.run_id,r.target_revision,COALESCE(o.completed_at,r.started_at),
        CASE WHEN replay.m9_observation_state=1 THEN 'Complete'
             WHEN replay.m9_observation_state=2 THEN 'Partial'
             WHEN replay.m9_observation_state=3 THEN 'Degraded'
             WHEN replay.m9_observation_state=4 THEN 'Unsupported'
             WHEN replay.m9_observation_state=5 THEN 'PermissionDenied'
             WHEN replay.m9_observation_state=6 THEN 'NoData'
             WHEN replay.m9_observation_state IS NULL THEN
                 CASE WHEN o.outcome='partial' OR o.truncated THEN 'Partial'
                      WHEN o.outcome='permission_denied' THEN 'PermissionDenied'
                      WHEN o.outcome='unsupported' THEN 'Unsupported'
                      WHEN o.outcome IS NULL THEN 'NoData' ELSE 'Degraded' END
             ELSE 'Degraded' END
    FROM telemetry.collection_run r
    LEFT JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
    LEFT JOIN telemetry.m9_commit_replay replay ON replay.run_id=r.run_id
    WHERE r.run_id=selected_id;
END;
$function$;

CREATE OR REPLACE FUNCTION reporting.get_latest_m9_run_v2_shadow(p_instance_id uuid,p_collector_id text)
RETURNS TABLE(run_id uuid,target_revision bigint,observed_at timestamptz,state text)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=reporting,telemetry,control,pg_catalog AS $function$
DECLARE
    completed_id uuid;
    completed_at timestamptz;
    unfinished_id uuid;
    unfinished_at timestamptz;
    selected_id uuid;
BEGIN
    IF p_instance_id IS NULL OR p_collector_id IS NULL
       OR p_collector_id NOT IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
       OR p_instance_id::text IS DISTINCT FROM current_setting('sqlobserver.target_scope',true) THEN
        RETURN;
    END IF;

    EXECUTE $query$
        SELECT r.run_id,o.completed_at
        FROM telemetry.collection_run_outcome o
        JOIN telemetry.collection_run_v2 r ON r.run_id=o.run_id
        JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
        WHERE r.instance_id=$1 AND r.collector_id=$2 AND (r.collector_id<>'availability-groups.health' OR r.collector_version=1)
        ORDER BY o.completed_at DESC,o.run_id DESC LIMIT 1
    $query$ INTO completed_id,completed_at USING p_instance_id,p_collector_id;

    IF completed_id IS NULL THEN
        -- With no completed run in this statement snapshot, every matching run
        -- is unfinished. No anti-join over historical outcomes is necessary.
        EXECUTE $query$
            SELECT r.run_id,r.started_at FROM telemetry.collection_run_v2 r
            JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
            WHERE r.instance_id=$1 AND r.collector_id=$2 AND (r.collector_id<>'availability-groups.health' OR r.collector_version=1)
            ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1
        $query$ INTO unfinished_id,unfinished_at USING p_instance_id,p_collector_id;
    ELSE
        EXECUTE $query$
            SELECT r.run_id,r.started_at FROM telemetry.collection_run_v2 r
            JOIN control.observation_target t ON t.instance_id=r.instance_id AND t.revision=r.target_revision
            WHERE r.instance_id=$1 AND r.collector_id=$2 AND (r.collector_id<>'availability-groups.health' OR r.collector_version=1) AND r.started_at >= $3
              AND NOT EXISTS (SELECT 1 FROM telemetry.collection_run_outcome o WHERE o.run_id=r.run_id)
            ORDER BY r.started_at DESC,r.run_id DESC LIMIT 1
        $query$ INTO unfinished_id,unfinished_at USING p_instance_id,p_collector_id,completed_at;
    END IF;

    selected_id := CASE WHEN completed_id IS NULL THEN unfinished_id
        WHEN unfinished_id IS NULL THEN completed_id
        WHEN (unfinished_at,unfinished_id) > (completed_at,completed_id) THEN unfinished_id
        ELSE completed_id END;
    IF selected_id IS NULL THEN RETURN; END IF;

    RETURN QUERY
    SELECT r.run_id,r.target_revision,COALESCE(o.completed_at,r.started_at),
        CASE WHEN replay.m9_observation_state=1 THEN 'Complete'
             WHEN replay.m9_observation_state=2 THEN 'Partial'
             WHEN replay.m9_observation_state=3 THEN 'Degraded'
             WHEN replay.m9_observation_state=4 THEN 'Unsupported'
             WHEN replay.m9_observation_state=5 THEN 'PermissionDenied'
             WHEN replay.m9_observation_state=6 THEN 'NoData'
             WHEN replay.m9_observation_state IS NULL THEN
                 CASE WHEN o.outcome='partial' OR o.truncated THEN 'Partial'
                      WHEN o.outcome='permission_denied' THEN 'PermissionDenied'
                      WHEN o.outcome='unsupported' THEN 'Unsupported'
                      WHEN o.outcome IS NULL THEN 'NoData' ELSE 'Degraded' END
             ELSE 'Degraded' END
    FROM telemetry.collection_run_v2 r
    LEFT JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
    LEFT JOIN telemetry.m9_commit_replay replay ON replay.run_id=r.run_id
    WHERE r.run_id=selected_id;
END;
$function$;
REVOKE ALL ON FUNCTION reporting.get_latest_m9_run_v2_shadow(uuid,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_server,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.get_latest_m9_run_v2_shadow(uuid,text)
 TO sqlobserver_migrator;
