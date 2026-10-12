-- Opt-in lifecycle retention. No collector activation or retention-default change.
-- Retired event partitions are a disclosed history boundary, not a healthy empty list.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
CREATE OR REPLACE FUNCTION system.update_m10_retention_policy
 (p_data_class text,p_enabled boolean,p_retain_for interval,p_minimum_partitions integer,
  p_expected_revision bigint,p_changed_by text,p_change_reason text)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER VOLATILE
SET search_path=pg_catalog,system
AS $policy_update$
DECLARE next_revision bigint;
BEGIN
 IF coalesce(current_setting('sqlobserver.role',true),'')<>'SecurityAdministrator'
    OR coalesce(current_setting('sqlobserver.authorization_scope',true),'')<>'global' THEN
  RAISE EXCEPTION 'global retention mutation requires SecurityAdministrator authorization context'
   USING ERRCODE='42501';
 END IF;
 IF p_data_class IS NULL OR p_data_class NOT IN
    ('raw_metric_samples','diagnostic_events',
     'm5_activity','m5_requests','m5_waits','m5_blocking',
     'm9_backup_status','m9_agent_history','m9_agent_occurrences',
     'm9_ag_replicas','m9_ag_databases','m9_tempdb',
     'm10_host_metrics','m10_replication','m10_rollups','m10_evidence',
     'sql_volume_capacity','m7_group_watermarks','m7_group_deltas',
     'm7_query_performance','collection_run_original_copy','collection_run_canonical',
     'm9_ag_replica_evidence','m9_ag_database_evidence','m9_backup_evidence','sql_metric_baselines','m6_deadlock_read_receipts','m7_cache_watermarks','m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links','server_lifecycle_receipts','server_lifecycle_events')
    OR p_minimum_partitions<1 OR p_expected_revision<1
    OR p_change_reason IS NULL OR length(p_change_reason)>512
    OR p_changed_by IS NULL OR length(p_changed_by)>256
    OR (p_enabled AND (p_retain_for IS NULL OR p_retain_for<interval '1 day'))
    OR (p_data_class='m7_query_performance' AND p_enabled
        AND p_retain_for<interval '8 days')
    OR (p_data_class='m6_deadlock_read_receipts' AND (p_minimum_partitions<2 OR (p_enabled AND p_retain_for<interval '30 days')))
    OR (p_data_class='m7_cache_watermarks' AND p_enabled AND p_retain_for<interval '8 days')
    OR (p_data_class IN ('m7_cache_deltas','m7_cache_receipts','m7_cache_content_receipts','m7_cache_content_links') AND p_enabled AND p_retain_for<interval '30 days')
    OR (p_data_class='collection_run_canonical' AND (p_minimum_partitions<>1
        OR (p_retain_for IS NOT NULL AND (p_retain_for<interval '90 days' OR p_retain_for>interval '3650 days'))))
    OR (p_data_class IN ('server_lifecycle_receipts','server_lifecycle_events')
        AND (p_enabled IS NULL OR p_minimum_partitions IS NULL OR p_minimum_partitions NOT BETWEEN 3 AND 10000
          OR (p_retain_for IS NOT NULL AND (p_retain_for<interval '90 days' OR p_retain_for>interval '3650 days'
            OR extract(year FROM p_retain_for)<>0 OR extract(month FROM p_retain_for)<>0))))
    OR (p_data_class='collection_run_original_copy'
        AND (p_minimum_partitions<>1 OR (p_enabled
          AND p_retain_for<interval '30 days'))) THEN
  RAISE EXCEPTION 'retention policy bounds rejected' USING ERRCODE='22023';
 END IF;
 PERFORM set_config('sqlobserver.retention_change_reason',p_change_reason,true);
 PERFORM set_config('sqlobserver.retention_changed_by',p_changed_by,true);
 UPDATE system.retention_policy
 SET enabled=p_enabled,retain_for=p_retain_for,
     minimum_partitions_to_keep=p_minimum_partitions,
     policy_revision=policy_revision+1,updated_by=p_changed_by,
     updated_at=clock_timestamp()
 WHERE data_class=p_data_class AND policy_revision=p_expected_revision
 RETURNING policy_revision INTO next_revision;
 IF NOT FOUND THEN
  RAISE EXCEPTION 'retention policy revision conflict' USING ERRCODE='40001';
 END IF;
 RETURN next_revision;
END $policy_update$;

CREATE OR REPLACE FUNCTION reporting.server_lifecycle_history_manifest(
 p_target uuid,p_revision bigint,p_from timestamptz,p_to timestamptz,p_cutoff timestamptz)
RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $manifest$
DECLARE
 v_runs jsonb; v_events jsonb; v_capture timestamptz; v_coverage jsonb;
 v_count integer; v_retained integer; v_complete integer; v_failed integer; v_lost integer;
 v_boundaries integer; v_event_count integer; v_gap boolean; v_cadence jsonb;
 v_interval interval; v_timeout interval; v_schedule_revision bigint; v_schedule_target bigint;
 v_version integer; v_enabled boolean; v_spacing_gap boolean; v_cadence_qualified boolean;
 v_epoch text; v_event_retirement jsonb;
BEGIN
 SELECT s.collection_interval,c.execution_timeout,s.schedule_revision,s.target_revision,s.collector_version,s.enabled
 INTO v_interval,v_timeout,v_schedule_revision,v_schedule_target,v_version,v_enabled
 FROM control.collector_schedule s JOIN control.collector_contract c
 ON c.collector_id=s.collector_id AND c.collector_version=s.collector_version
 WHERE s.instance_id=p_target AND s.collector_id='server.lifecycle';
 v_cadence:=jsonb_build_object('interval',v_interval,'executionTimeout',v_timeout,
  'scheduleRevision',v_schedule_revision,'targetRevision',v_schedule_target,'collectorVersion',v_version,'enabled',v_enabled);

 WITH bounded AS MATERIALIZED (
  SELECT r.run_id,r.target_revision,r.schedule_revision,r.collector_version,r.output_schema_version,r.started_at,
   o.completed_at,o.outcome,o.reason_code,o.loss_kind,o.loss_detected,o.completion_digest
  FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i
   ON i.run_id=r.run_id AND i.started_at=r.started_at
  JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
  WHERE r.instance_id=p_target AND r.collector_id='server.lifecycle'
   AND o.completed_at>=p_from AND o.completed_at<p_to AND o.completed_at<=p_cutoff
  ORDER BY o.completed_at,r.run_id LIMIT 10001
 ), sealed AS (
  SELECT b.*,x.source_observed_at,x.source_schema_version,x.identity_version,x.engine_coverage,
   x.comparison_boundary_reason,x.completion_digest AS receipt_digest,
   (x.run_id IS NOT NULL AND x.source_observed_at IS NOT NULL) AS retained_snapshot,
   COALESCE(x.run_id IS NOT NULL AND x.completion_digest=b.completion_digest AND x.completed_at=b.completed_at
    AND x.target_revision=b.target_revision AND x.outcome=b.outcome AND x.loss_kind=b.loss_kind
    AND b.collector_version=1 AND b.output_schema_version=1 AND x.source_schema_version=1 AND x.identity_version=1
    AND x.engine_coverage=1 AND x.payload->'truncated'='false'::jsonb
    AND (b.outcome='succeeded' AND b.loss_kind='none' OR b.outcome='partial'
     AND b.reason_code='visibility_incomplete' AND b.loss_kind='visibility_incomplete')
    AND x.source_observed_at<=b.completed_at
    AND abs(extract(epoch FROM x.source_observed_at-b.completed_at))<=300,false) AS qualified
  FROM bounded b LEFT JOIN telemetry.server_lifecycle_receipt x
   ON x.instance_id=p_target AND x.run_id=b.run_id AND x.completed_at=b.completed_at
 ), ordered AS (
  SELECT s.*,lag(s.completed_at) OVER(ORDER BY s.completed_at,s.run_id) AS prior_completed FROM sealed s
 )
 SELECT COALESCE(jsonb_agg(jsonb_build_object('runId',run_id,'targetRevision',target_revision,
  'scheduleRevision',schedule_revision,'collectorVersion',collector_version,'outputSchemaVersion',output_schema_version,
  'startedAt',started_at,'completedAt',completed_at,'outcome',outcome,'reason',reason_code,
  'lossKind',loss_kind,'lossDetected',loss_detected,'completionDigest',encode(completion_digest,'hex'),
  'receiptDigest',encode(receipt_digest,'hex'),'sourceObservedAt',source_observed_at,
  'sourceSchemaVersion',source_schema_version,'identityVersion',identity_version,'engineCoverage',engine_coverage,
  'boundary',comparison_boundary_reason,'qualified',qualified) ORDER BY completed_at,run_id),'[]'::jsonb),
  count(*)::integer,count(*) FILTER(WHERE retained_snapshot)::integer,count(*) FILTER(WHERE qualified)::integer,
  count(*) FILTER(WHERE outcome NOT IN ('succeeded','partial'))::integer,count(*) FILTER(WHERE loss_detected)::integer,
  count(*) FILTER(WHERE comparison_boundary_reason IS DISTINCT FROM 'comparable')::integer,
  COALESCE(bool_or(NOT qualified OR target_revision<>p_revision OR
   comparison_boundary_reason IS NULL OR comparison_boundary_reason NOT IN ('initial','comparable')),false),
  COALESCE(bool_and(schedule_revision=v_schedule_revision AND target_revision=v_schedule_target
   AND collector_version=v_version),false),
  COALESCE(bool_or(prior_completed IS NOT NULL AND completed_at-prior_completed>v_interval*2+v_timeout),false)
 INTO v_runs,v_count,v_retained,v_complete,v_failed,v_lost,v_boundaries,v_gap,v_cadence_qualified,v_spacing_gap
 FROM ordered;
 IF v_count>10000 THEN RAISE EXCEPTION 'lifecycle run window exceeds bounds; choose a narrower window' USING ERRCODE='54000'; END IF;

 SELECT min(o.completed_at) INTO v_capture
 FROM telemetry.collection_run_v2 r JOIN telemetry.collection_run_identity i ON i.run_id=r.run_id AND i.started_at=r.started_at
 JOIN telemetry.collection_run_outcome o ON o.run_id=r.run_id
 WHERE r.instance_id=p_target AND r.collector_id='server.lifecycle' AND o.completed_at<=p_cutoff;
 -- This is a recorded capture-attempt boundary, not proof the source was healthy.
 v_gap:=v_gap OR v_count=0 OR v_capture IS NULL OR p_from<v_capture;
 IF v_count>0 THEN
  v_gap:=v_gap OR NOT COALESCE(v_cadence_qualified AND v_enabled AND v_schedule_target=p_revision,false)
   OR v_spacing_gap
   OR (v_runs->0->>'completedAt')::timestamptz-p_from>v_interval*2+v_timeout
   OR LEAST(p_to,p_cutoff)-(v_runs->(v_count-1)->>'completedAt')::timestamptz>v_interval*2+v_timeout;
 END IF;

 SELECT COALESCE(jsonb_agg(jsonb_build_object('event',reporting.server_lifecycle_event_json(e::events.server_lifecycle_event),
  'beforeEngine',e.before_engine,'afterEngine',e.after_engine,
  'beforeCompletionDigest',encode(e.before_completion_digest,'hex'),
  'afterCompletionDigest',encode(e.after_completion_digest,'hex')) ORDER BY e.detected_at,e.event_id),'[]'::jsonb),count(*)::integer
 INTO v_events,v_event_count FROM (
  SELECT * FROM events.server_lifecycle_event e WHERE e.instance_id=p_target
   AND e.detected_at>=p_from AND e.detected_at<p_to AND e.detected_at<=p_cutoff
  ORDER BY e.detected_at,e.event_id LIMIT 10001
 ) e;
 IF v_event_count>10000 THEN RAISE EXCEPTION 'lifecycle event window exceeds bounds; choose a narrower window' USING ERRCODE='54000'; END IF;
 IF v_event_count>v_count THEN RAISE EXCEPTION 'canonical lifecycle event proof is unavailable' USING ERRCODE='55000'; END IF;
 -- Retention operates on whole repository partitions, not per-target event counts.
 -- A verified retired partition is therefore a conservative visibility boundary even
 -- if this target had no remaining events in it. Do not fabricate retired run counts.
 -- Do not filter retirement timestamps by p_cutoff: destruction after capture changes
 -- the retained source represented by a continuation's original cutoff.
 SELECT COALESCE(jsonb_agg(jsonb_build_object('partitionName',r.partition_name,
  'rangeStart',r.range_start,'rangeEnd',r.range_end,'state',r.lifecycle_state,
  'detachedAt',r.detached_at,'droppedAt',r.dropped_at,'executionId',e.execution_id,
  'operationState',e.state,'policyRevision',e.policy_revision,
  'registryRevision',encode(e.registry_revision,'hex'),'executionDetachedAt',e.detached_at,
  'executionDroppedAt',e.dropped_at) ORDER BY r.range_start,r.partition_name),'[]'::jsonb)
 INTO v_event_retirement
 FROM system.partition_registry r
 JOIN LATERAL (
  SELECT x.* FROM system.retention_execution x
  WHERE x.data_class='server_lifecycle_events' AND x.parent_schema=r.parent_schema
   AND x.parent_table=r.parent_table AND x.partition_name=r.partition_name
   AND x.range_start=r.range_start AND x.range_end=r.range_end
   AND x.detached_at IS NOT NULL AND x.detached_at>=r.detached_at
   AND x.registry_revision=sha256(convert_to(r.parent_schema::text||'|'||r.parent_table::text||'|'
    ||r.partition_name::text||'|'||r.range_start::text||'|'||r.range_end::text,'UTF8'))
   AND ((r.lifecycle_state='detached' AND x.state IN ('grace','retry','failed') AND x.dropped_at IS NULL)
    OR (r.lifecycle_state='dropped' AND x.state='dropped' AND x.dropped_at IS NOT NULL AND x.dropped_at>=r.dropped_at))
  ORDER BY x.detached_at DESC,x.execution_id LIMIT 1
 ) e ON true
 WHERE r.parent_schema='events' AND r.parent_table='server_lifecycle_event'
  AND r.partition_schema='events' AND r.partition_granularity='day'
  AND r.range_end=r.range_start+interval '1 day'
  AND r.lifecycle_state IN ('detached','dropped') AND r.detached_at IS NOT NULL
  AND r.range_start<p_to AND r.range_start<=p_cutoff AND r.range_end>p_from;
 v_gap:=v_gap OR jsonb_array_length(v_event_retirement)>0;
 v_coverage:=jsonb_build_object('runCount',v_count,'retainedSnapshotCount',v_retained,
  'completeRunCount',v_complete,'gapRunCount',v_count-v_complete,'failedRunCount',v_failed,
  'lostRunCount',v_lost,'retiredRunCount',0,'comparisonBoundaryCount',v_boundaries,'eventCount',v_event_count,
  'captureBoundaryUtc',v_capture,'hasVisibilityGap',v_gap,'availabilityQualification','unqualified','sourceConsistencyVerified',false);
 v_epoch:=encode(sha256(convert_to(jsonb_build_object('domain','sqlobserver-server-lifecycle-history-v1',
  'targetId',p_target,'targetRevision',p_revision,'fromUtc',p_from,'toUtc',p_to,'cutoffUtc',p_cutoff,
  'cadence',v_cadence,'coverage',v_coverage,'runs',v_runs,'events',v_events,'eventRetirement',v_event_retirement)::text,'UTF8')),'hex');
 RETURN jsonb_build_object('coverage',v_coverage,'events',v_events,'readEpoch',v_epoch);
END $manifest$;

REVOKE ALL ON FUNCTION system.update_m10_retention_policy(text,boolean,interval,integer,bigint,text,text)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION system.update_m10_retention_policy(text,boolean,interval,integer,bigint,text,text)
 TO sqlobserver_server;
REVOKE ALL ON FUNCTION reporting.server_lifecycle_history_manifest(uuid,bigint,timestamptz,timestamptz,timestamptz)
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
