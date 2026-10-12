-- Administrator-controlled opt-in for the least-privileged server settings scan.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION control.set_configuration_collection_enabled(
    p_instance_id uuid,p_expected_target_revision bigint,
    p_expected_enabled boolean,p_enabled boolean,
    p_actor_identifier text,p_correlation_id uuid)
RETURNS TABLE(result_status text,schedule_revision bigint,collector_enabled boolean)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog SET TimeZone='UTC'
AS $fn$
DECLARE
    v_target_revision bigint;
    v_lifecycle text;
    v_schedule control.collector_schedule%ROWTYPE;
BEGIN
    IF p_instance_id IS NULL OR p_expected_target_revision IS NULL
       OR p_expected_target_revision<1 OR p_expected_enabled IS NULL
       OR p_enabled IS NULL OR p_actor_identifier IS NULL
       OR p_correlation_id IS NULL
    THEN RAISE EXCEPTION 'Configuration collection request is invalid'
        USING ERRCODE='22023'; END IF;

    SELECT target.revision,target.lifecycle_state
      INTO v_target_revision,v_lifecycle
    FROM control.observation_target AS target
    WHERE target.instance_id=p_instance_id FOR UPDATE;
    IF NOT FOUND THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','failed','target_not_found');
        RETURN QUERY SELECT 'not_found'::text,NULL::bigint,NULL::boolean;
        RETURN;
    END IF;
    IF v_lifecycle<>'active' THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict',
            CASE WHEN v_lifecycle='retired' THEN 'target_retired'
                 ELSE 'revision_conflict' END);
        RETURN QUERY SELECT 'inactive'::text,NULL::bigint,NULL::boolean;
        RETURN;
    END IF;
    IF v_target_revision<>p_expected_target_revision THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict','revision_conflict');
        RETURN QUERY SELECT 'revision_conflict'::text,NULL::bigint,NULL::boolean;
        RETURN;
    END IF;

    SELECT * INTO v_schedule FROM control.collector_schedule AS schedule
    WHERE schedule.instance_id=p_instance_id
      AND schedule.collector_id='engine.configurations' FOR UPDATE;
    IF NOT FOUND OR v_schedule.target_revision<>v_target_revision
    THEN RAISE EXCEPTION 'Configuration collector schedule is unavailable'
        USING ERRCODE='55000'; END IF;
    IF v_schedule.enabled<>p_expected_enabled THEN
        PERFORM * FROM audit.append_administrative_activity(
            p_actor_identifier,p_correlation_id,'update_observation_target',
            p_instance_id,'granted','conflict','revision_conflict');
        RETURN QUERY SELECT 'revision_conflict'::text,
            v_schedule.schedule_revision,v_schedule.enabled;
        RETURN;
    END IF;
    IF v_schedule.enabled<>p_enabled THEN
        UPDATE control.collector_schedule AS schedule
        SET enabled=p_enabled,schedule_revision=schedule.schedule_revision+1,
            next_due_at=CASE WHEN p_enabled THEN clock_timestamp()
                             ELSE schedule.next_due_at END,
            updated_at=clock_timestamp()
        WHERE schedule.instance_id=p_instance_id
          AND schedule.collector_id='engine.configurations'
        RETURNING schedule.schedule_revision,schedule.enabled
             INTO v_schedule.schedule_revision,v_schedule.enabled;
    END IF;
    PERFORM * FROM audit.append_administrative_activity(
        p_actor_identifier,p_correlation_id,'update_observation_target',
        p_instance_id,'granted','succeeded','completed');
    RETURN QUERY SELECT 'applied'::text,
        v_schedule.schedule_revision,v_schedule.enabled;
END;
$fn$;

REVOKE ALL ON FUNCTION control.set_configuration_collection_enabled(
    uuid,bigint,boolean,boolean,text,uuid)
    FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION control.set_configuration_collection_enabled(
    uuid,bigint,boolean,boolean,text,uuid) TO sqlobserver_server;
