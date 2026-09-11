-- Match cleanup throughput to ten targets with up to 512 distinct payloads each per cycle.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;
-- Readers never take this lock. Shared commit ownership prevents orphan cleanup
-- from racing a new reference to a deduplicated protected payload.
ALTER FUNCTION live_activity.commit_capture(uuid,bigint,uuid,bigint,uuid,timestamptz,boolean,jsonb,jsonb,jsonb) RENAME TO commit_capture_v1;
REVOKE ALL ON FUNCTION live_activity.commit_capture_v1(uuid,bigint,uuid,bigint,uuid,timestamptz,boolean,jsonb,jsonb,jsonb) FROM PUBLIC,sqlobserver_collector;
CREATE FUNCTION live_activity.commit_capture(p_target uuid,p_revision bigint,p_owner uuid,p_fence bigint,
 p_id uuid,p_observed timestamptz,p_truncated boolean,p_rows jsonb,p_payloads jsonb,p_databases jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
BEGIN
 PERFORM pg_advisory_xact_lock_shared(731940042::bigint);
 PERFORM live_activity.commit_capture_v1(p_target,p_revision,p_owner,p_fence,p_id,p_observed,p_truncated,p_rows,p_payloads,p_databases);
END $$;
REVOKE ALL ON FUNCTION live_activity.commit_capture(uuid,bigint,uuid,bigint,uuid,timestamptz,boolean,jsonb,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION live_activity.commit_capture(uuid,bigint,uuid,bigint,uuid,timestamptz,boolean,jsonb,jsonb,jsonb) TO sqlobserver_collector;
CREATE INDEX payload_maintenance_order ON live_activity.payload(created_at,target_id,id);
CREATE TABLE live_activity.maintenance_cursor (
 singleton integer PRIMARY KEY CHECK(singleton=1), created_at timestamptz, target_id uuid, payload_id uuid
);
INSERT INTO live_activity.maintenance_cursor(singleton) VALUES(1);
REVOKE ALL ON live_activity.maintenance_cursor FROM PUBLIC,sqlobserver_server,sqlobserver_collector;
CREATE OR REPLACE FUNCTION live_activity.cleanup() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
DECLARE v_created timestamptz; v_target uuid; v_payload uuid;
BEGIN
 -- One maintenance transaction across collector processes; collection uses separate target leases.
 IF NOT pg_try_advisory_xact_lock(731940042::bigint) THEN RETURN; END IF;
 DELETE FROM live_activity.snapshot WHERE id IN (SELECT id FROM live_activity.snapshot
 WHERE observed_at<=clock_timestamp()-interval '24 hours' OR (minute_at IS NULL AND received_at<clock_timestamp()-interval '5 minutes')
 ORDER BY received_at LIMIT 120);
 SELECT created_at,target_id,payload_id INTO v_created,v_target,v_payload FROM live_activity.maintenance_cursor WHERE singleton=1;
 -- Bound candidates examined, not just rows deleted. The persisted cursor makes
 -- progress past still-referenced payloads without rescanning the entire repository.
 WITH candidates AS MATERIALIZED (
  SELECT q.created_at,q.target_id,q.id FROM live_activity.payload q
  WHERE (q.created_at,q.target_id,q.id)>(coalesce(v_created,'-infinity'::timestamptz),
    coalesce(v_target,'00000000-0000-0000-0000-000000000000'::uuid),coalesce(v_payload,'00000000-0000-0000-0000-000000000000'::uuid))
  AND q.created_at<clock_timestamp()-interval '5 minutes'
  ORDER BY q.created_at,q.target_id,q.id LIMIT 8192
 ), removed AS (
  DELETE FROM live_activity.payload p USING candidates c WHERE p.target_id=c.target_id AND p.id=c.id
  AND NOT EXISTS(SELECT 1 FROM live_activity.observation o JOIN live_activity.snapshot s ON s.id=o.snapshot_id
    WHERE s.target_id=p.target_id AND o.data->>'queryId'=p.id::text) RETURNING p.id
 ) SELECT created_at,target_id,id INTO v_created,v_target,v_payload FROM candidates ORDER BY created_at DESC,target_id DESC,id DESC LIMIT 1;
 UPDATE live_activity.maintenance_cursor SET created_at=v_created,target_id=v_target,payload_id=v_payload WHERE singleton=1;
END $$;
REVOKE ALL ON FUNCTION live_activity.cleanup() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION live_activity.cleanup() TO sqlobserver_collector;
