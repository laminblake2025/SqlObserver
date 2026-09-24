-- A bounded fleet inbox read. The Server supplies the exact union of read-role
-- target grants; the repository login is trusted in the same way as the
-- existing per-target projection functions.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE FUNCTION reporting.list_fleet_active_alerts(
 p_target_ids uuid[], p_all_targets boolean, p_max_results integer,
 p_after_at timestamptz, p_after_target uuid, p_after_alert_id uuid,
 p_snapshot_utc timestamptz)
RETURNS TABLE(alert_id uuid,rule_id uuid,target_id uuid,target_name text,
 rule_name text,state smallint,first_observed_at timestamptz,
 fired_at timestamptz,acknowledged_at timestamptz,value double precision,
 reason text,delivery_suppressed boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER
SET search_path=pg_catalog,reporting,control,alerting
AS $inbox$
BEGIN
 IF p_target_ids IS NULL OR cardinality(p_target_ids)>1024
    OR array_position(p_target_ids,NULL::uuid) IS NOT NULL
    OR p_all_targets IS NULL OR p_max_results IS NULL
    OR p_max_results<1 OR p_max_results>100 OR p_snapshot_utc IS NULL
    OR (p_after_at IS NULL AND
        (p_after_target IS NOT NULL OR p_after_alert_id IS NOT NULL))
    OR (p_after_at IS NOT NULL AND
        (p_after_target IS NULL OR p_after_alert_id IS NULL)) THEN
  RAISE EXCEPTION 'fleet alert page bounds rejected' USING ERRCODE='22023';
 END IF;
 RETURN QUERY
 SELECT a.alert_id,a.rule_id,a.target_id,t.display_name,a.rule_name,a.state,
   a.first_observed_at,a.fired_at,a.acknowledged_at,a.value,a.reason,
   a.delivery_suppressed
 FROM reporting.active_alerts a
 JOIN control.observation_target t ON t.instance_id=a.target_id
 WHERE (p_all_targets OR a.target_id=ANY(p_target_ids))
   AND coalesce(a.fired_at,a.first_observed_at)<=p_snapshot_utc
   AND (p_after_at IS NULL OR
     (coalesce(a.fired_at,a.first_observed_at),a.target_id,a.alert_id)
       <(p_after_at,p_after_target,p_after_alert_id))
 ORDER BY coalesce(a.fired_at,a.first_observed_at) DESC,
   a.target_id DESC,a.alert_id DESC
 LIMIT p_max_results+1;
END $inbox$;

REVOKE ALL ON FUNCTION reporting.list_fleet_active_alerts(
 uuid[],boolean,integer,timestamptz,uuid,uuid,timestamptz)
FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION reporting.list_fleet_active_alerts(
 uuid[],boolean,integer,timestamptz,uuid,uuid,timestamptz)
TO sqlobserver_server;
