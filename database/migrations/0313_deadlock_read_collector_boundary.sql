-- Typed Collector access to the atomic incremental source-proof boundary.
-- Production deadlock catalog and schedules remain v1 until retained-window alert qualification.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE OR REPLACE FUNCTION control.commit_deadlock_collection_run
(
    p_run_id uuid,p_instance_id uuid,p_target_revision bigint,p_collector_id text,
    p_collector_version integer,p_output_schema_version integer,p_schedule_revision bigint,
    p_scheduled_at timestamptz,p_work_key text,p_owner_execution_id uuid,p_fencing_token bigint,
    p_request_digest bytea,p_outcome text,p_reason_code text,p_duration_ms bigint,
    p_attempt_count integer,p_source_row_count integer,p_output_item_count integer,
    p_response_bytes bigint,p_output_bytes bigint,p_loss_kind text,p_minimum_lost_items integer,
    p_loss_count_is_exact boolean,p_minimum_lost_bytes integer,p_next_circuit_state text,
    p_next_consecutive_failures integer,p_occurred_ats timestamptz[],p_event_ids uuid[],
    p_fingerprints bytea[],p_participant_counts integer[],p_relation_counts integer[],
    p_parse_truncated boolean[],p_participant_json jsonb[],p_relation_json jsonb[],p_sizes integer[]
)
RETURNS TABLE(result_status text,inserted_count integer,duplicate_count integer,
    rejected_count integer,persisted_bytes integer,committed_at timestamptz)
LANGUAGE plpgsql VOLATILE SECURITY DEFINER PARALLEL UNSAFE
SET search_path=pg_catalog SET row_security=on SET TimeZone='UTC'
AS $commit$
DECLARE v_result record; v_digest bytea;
BEGIN
    IF p_collector_version IS DISTINCT FROM 1 OR p_output_schema_version IS DISTINCT FROM 1 THEN
        RAISE EXCEPTION 'Incremental deadlocks require the atomic source-proof commit path' USING ERRCODE='22023';
    END IF;
    SELECT r.* INTO STRICT v_result FROM control.commit_deadlock_collection_run_core(
        p_run_id,p_instance_id,p_target_revision,p_collector_id,p_collector_version,
        p_output_schema_version,p_schedule_revision,p_scheduled_at,p_work_key,
        p_owner_execution_id,p_fencing_token,p_request_digest,p_outcome,p_reason_code,
        p_duration_ms,p_attempt_count,p_source_row_count,p_output_item_count,p_response_bytes,
        p_output_bytes,p_loss_kind,p_minimum_lost_items,p_loss_count_is_exact,p_minimum_lost_bytes,
        p_next_circuit_state,p_next_consecutive_failures,p_occurred_ats,p_event_ids,p_fingerprints,
        p_participant_counts,p_relation_counts,p_parse_truncated,p_participant_json,p_relation_json,p_sizes) r;
    IF v_result.result_status='committed' THEN
        SELECT o.completion_digest INTO STRICT v_digest
            FROM telemetry.collection_run_outcome o WHERE o.run_id=p_run_id;
        INSERT INTO alerting.deadlock_source_snapshot(instance_id,target_revision,schedule_revision,
            run_id,completed_at,completion_digest,event_ids,occurred_ats,parse_truncated)
        VALUES(p_instance_id,p_target_revision,p_schedule_revision,p_run_id,v_result.committed_at,
            v_digest,p_event_ids,p_occurred_ats,p_parse_truncated)
        ON CONFLICT(instance_id) DO UPDATE SET target_revision=EXCLUDED.target_revision,
            schedule_revision=EXCLUDED.schedule_revision,run_id=EXCLUDED.run_id,
            completed_at=EXCLUDED.completed_at,completion_digest=EXCLUDED.completion_digest,
            event_ids=EXCLUDED.event_ids,occurred_ats=EXCLUDED.occurred_ats,
            parse_truncated=EXCLUDED.parse_truncated;
        -- Snapshot and canonical outcome roll back together on a lost fence.
        PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing_token);
    END IF;
    -- An old exact replay must never replace the latest snapshot.
    RETURN QUERY SELECT v_result.result_status,v_result.inserted_count,v_result.duplicate_count,
        v_result.rejected_count,v_result.persisted_bytes,v_result.committed_at;
END $commit$;
REVOKE ALL ON FUNCTION control.commit_deadlock_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[])
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_deadlock_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[])
    TO sqlobserver_collector;

REVOKE ALL ON FUNCTION control.commit_deadlock_read_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[],jsonb)
    FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.commit_deadlock_read_collection_run(
    uuid,uuid,bigint,text,integer,integer,bigint,timestamptz,text,uuid,bigint,bytea,
    text,text,bigint,integer,integer,integer,bigint,bigint,text,integer,boolean,
    integer,text,integer,timestamptz[],uuid[],bytea[],integer[],integer[],boolean[],jsonb[],jsonb[],integer[],jsonb)
    TO sqlobserver_collector;
