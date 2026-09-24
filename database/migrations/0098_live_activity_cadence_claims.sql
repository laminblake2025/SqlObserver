-- Reserve the next sampling interval in the same transaction as the fenced
-- lease, so collector slots can be released as soon as a capture completes.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

CREATE TABLE live_activity.cadence_state (
 target_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id),
 next_due_at timestamptz NOT NULL,
 consecutive_failures smallint NOT NULL DEFAULT 0 CHECK(consecutive_failures BETWEEN 0 AND 4)
);
REVOKE ALL ON live_activity.cadence_state FROM PUBLIC, sqlobserver_server, sqlobserver_collector;

CREATE FUNCTION live_activity.claim_target(p_target uuid,p_revision bigint,p_owner uuid,p_ttl interval)
RETURNS TABLE(acquired boolean,fencing_token bigint,acquired_at timestamptz,
 renewed_at timestamptz,expires_at timestamptz,repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path=pg_catalog,live_activity SET TimeZone='UTC' AS $$
DECLARE v_lease record; v_valid uuid; v_reserved boolean=false;
BEGIN
 IF p_target IS NULL OR p_revision IS NULL OR p_revision<1 OR p_owner IS NULL THEN
  RAISE EXCEPTION 'Target, revision, and owner are required' USING ERRCODE='22023';
 END IF;

 -- Lease locking precedes the target and cadence rows, matching commit_capture's
 -- lease-first lock order. A failed reservation releases this lease immediately.
 SELECT * INTO v_lease FROM control.acquire_worker_lease(
  'collector/live-activity/'||p_target::text,p_owner,p_ttl);
 IF NOT v_lease.acquired THEN
  RETURN QUERY SELECT false,NULL::bigint,NULL::timestamptz,NULL::timestamptz,
   NULL::timestamptz,v_lease.repository_time;
  RETURN;
 END IF;

 SELECT t.instance_id INTO v_valid FROM control.observation_target t
 WHERE t.instance_id=p_target AND t.revision=p_revision
  AND t.lifecycle_state='active' AND t.host_name IS NOT NULL FOR SHARE;
 IF FOUND THEN
  INSERT INTO live_activity.cadence_state AS cadence(target_id,next_due_at,consecutive_failures)
  VALUES(p_target,v_lease.repository_time+interval '10 seconds',0)
  ON CONFLICT(target_id) DO UPDATE SET next_due_at=EXCLUDED.next_due_at
  WHERE cadence.next_due_at<=v_lease.repository_time;
  v_reserved=FOUND;
 END IF;

 IF NOT v_reserved THEN
  PERFORM control.release_worker_lease('collector/live-activity/'||p_target::text,
   p_owner,v_lease.fencing_token);
  RETURN QUERY SELECT false,NULL::bigint,NULL::timestamptz,NULL::timestamptz,
   NULL::timestamptz,clock_timestamp();
  RETURN;
 END IF;

 RETURN QUERY SELECT true,v_lease.fencing_token,v_lease.acquired_at,
  v_lease.renewed_at,v_lease.expires_at,v_lease.repository_time;
END $$;
REVOKE ALL ON FUNCTION live_activity.claim_target(uuid,bigint,uuid,interval) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION live_activity.claim_target(uuid,bigint,uuid,interval) TO sqlobserver_collector;

-- Keep failure backoff durable across collector processes. A successful
-- cadence capture clears it; triggered deadlock captures do not affect it.
CREATE OR REPLACE FUNCTION live_activity.failed(p_target uuid,p_owner uuid,p_fence bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
DECLARE v_now timestamptz;
BEGIN
 PERFORM control.assert_worker_lease('collector/live-activity/'||p_target::text,p_owner,p_fence);
 v_now=clock_timestamp();
 INSERT INTO live_activity.collection_state(target_id,failed) VALUES(p_target,true)
 ON CONFLICT(target_id) DO UPDATE SET failed=true;
 INSERT INTO live_activity.cadence_state AS cadence(target_id,next_due_at,consecutive_failures)
 VALUES(p_target,v_now+interval '10 seconds',1)
 ON CONFLICT(target_id) DO UPDATE
 SET consecutive_failures=least(cadence.consecutive_failures+1,4),
  next_due_at=v_now+(CASE least(cadence.consecutive_failures+1,4)
   WHEN 1 THEN interval '10 seconds' WHEN 2 THEN interval '20 seconds'
   WHEN 3 THEN interval '40 seconds' ELSE interval '80 seconds' END);
END $$;

CREATE OR REPLACE FUNCTION live_activity.commit_capture(p_target uuid,p_revision bigint,p_owner uuid,p_fence bigint,
 p_id uuid,p_observed timestamptz,p_truncated boolean,p_rows jsonb,p_payloads jsonb,p_databases jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
BEGIN
 PERFORM pg_advisory_xact_lock_shared(731940042::bigint);
 PERFORM live_activity.commit_capture_v1(p_target,p_revision,p_owner,p_fence,
  p_id,p_observed,p_truncated,p_rows,p_payloads,p_databases);
 UPDATE live_activity.cadence_state SET consecutive_failures=0 WHERE target_id=p_target;
END $$;

CREATE OR REPLACE FUNCTION live_activity.targets() RETURNS SETOF control.observation_target
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
 SELECT t.*
 FROM control.observation_target t
 LEFT JOIN live_activity.cadence_state cadence ON cadence.target_id=t.instance_id
 LEFT JOIN LATERAL (
  SELECT s.observed_at FROM live_activity.snapshot s
  WHERE s.target_id=t.instance_id AND s.revision=t.revision
  ORDER BY s.observed_at DESC LIMIT 1
 ) latest ON true
 LEFT JOIN control.worker_lease lease
  ON lease.work_key='collector/live-activity/'||t.instance_id::text
 WHERE t.lifecycle_state='active' AND t.host_name IS NOT NULL
  AND (cadence.next_due_at IS NULL OR cadence.next_due_at<=clock_timestamp())
 ORDER BY CASE WHEN lease.released_at IS NULL AND lease.expires_at>clock_timestamp()
  THEN 1 ELSE 0 END,
  cadence.next_due_at NULLS FIRST, latest.observed_at NULLS FIRST,t.instance_id
 LIMIT 10;
$$;
