-- Forward-only live activity v1. Apply through the transactional migration runner.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
CREATE SCHEMA live_activity AUTHORIZATION sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;
REVOKE ALL ON SCHEMA live_activity FROM PUBLIC;
GRANT USAGE ON SCHEMA live_activity TO sqlobserver_server, sqlobserver_collector;
CREATE TABLE live_activity.collection_state(target_id uuid PRIMARY KEY REFERENCES control.observation_target(instance_id), failed boolean NOT NULL);

CREATE TABLE live_activity.snapshot (
 id uuid PRIMARY KEY, target_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 revision bigint NOT NULL, observed_at timestamptz NOT NULL, received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
 minute_at timestamptz, truncated boolean NOT NULL, databases jsonb NOT NULL DEFAULT '[]',
 UNIQUE(target_id, revision, minute_at)
);
CREATE INDEX ON live_activity.snapshot(target_id, observed_at DESC);
CREATE INDEX ON live_activity.snapshot(received_at);
CREATE TABLE live_activity.payload (
 target_id uuid NOT NULL, id uuid NOT NULL, protected jsonb NOT NULL,
 created_at timestamptz NOT NULL DEFAULT clock_timestamp(), PRIMARY KEY(target_id,id),
 CHECK (octet_length(protected::text) <= 24000)
);
CREATE TABLE live_activity.observation (
 snapshot_id uuid NOT NULL REFERENCES live_activity.snapshot(id) ON DELETE CASCADE,
 identity text NOT NULL CHECK(length(identity) BETWEEN 1 AND 128),
 data jsonb NOT NULL CHECK(octet_length(data::text) <= 8192),
 PRIMARY KEY(snapshot_id,identity)
);
CREATE TABLE live_activity.query_audit (
 id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, target_id uuid NOT NULL, snapshot_id uuid NOT NULL,
 actor text NOT NULL CHECK(length(actor) <= 184), outcome text NOT NULL CHECK(outcome IN ('denied','unavailable','opened')),
 accessed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX observation_query_reference ON live_activity.observation((data->>'queryId')) WHERE data->>'queryId' IS NOT NULL;
REVOKE ALL ON ALL TABLES IN SCHEMA live_activity FROM PUBLIC, sqlobserver_server, sqlobserver_collector;

CREATE FUNCTION live_activity.targets() RETURNS SETOF control.observation_target
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
 SELECT t.* FROM control.observation_target t WHERE t.lifecycle_state='active' AND t.host_name IS NOT NULL ORDER BY t.instance_id LIMIT 10;
$$;

CREATE FUNCTION live_activity.commit_capture(p_target uuid,p_revision bigint,p_owner uuid,p_fence bigint,
 p_id uuid,p_observed timestamptz,p_truncated boolean,p_rows jsonb,p_payloads jsonb,p_databases jsonb) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
DECLARE v_minute timestamptz; v_previous uuid; v_time timestamptz; v_seconds numeric;
BEGIN
 PERFORM control.assert_worker_lease('collector/live-activity/'||p_target::text,p_owner,p_fence);
 PERFORM 1 FROM control.observation_target WHERE instance_id=p_target AND revision=p_revision AND lifecycle_state='active' FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Target revision unavailable' USING ERRCODE='22023'; END IF;
 IF p_observed < clock_timestamp()-interval '2 minutes' OR p_observed > clock_timestamp()+interval '5 seconds'
 OR jsonb_typeof(p_rows) <> 'array' OR jsonb_array_length(p_rows)>512 OR octet_length(p_rows::text)>4194304
 OR jsonb_typeof(p_payloads) <> 'array' OR jsonb_array_length(p_payloads)>512 OR octet_length(p_payloads::text)>1572864
 OR jsonb_typeof(p_databases) <> 'array' OR jsonb_array_length(p_databases)>1024 OR octet_length(p_databases::text)>524288
 THEN RAISE EXCEPTION 'Invalid capture bounds' USING ERRCODE='22023'; END IF;
 SELECT id,observed_at INTO v_previous,v_time FROM live_activity.snapshot WHERE target_id=p_target AND revision=p_revision ORDER BY observed_at DESC LIMIT 1;
 IF v_time >= p_observed THEN RAISE EXCEPTION 'Observation is not newer' USING ERRCODE='22023'; END IF;
 v_seconds=extract(epoch FROM p_observed-v_time);
 v_minute=date_trunc('minute',p_observed AT TIME ZONE 'UTC') AT TIME ZONE 'UTC';
 IF EXISTS(SELECT 1 FROM live_activity.snapshot WHERE target_id=p_target AND revision=p_revision AND minute_at=v_minute) THEN v_minute=NULL; END IF;
 INSERT INTO live_activity.snapshot(id,target_id,revision,observed_at,minute_at,truncated,databases) VALUES(p_id,p_target,p_revision,p_observed,v_minute,p_truncated,p_databases);
 INSERT INTO live_activity.collection_state VALUES(p_target,false) ON CONFLICT(target_id) DO UPDATE SET failed=false;
 INSERT INTO live_activity.payload(target_id,id,protected)
 SELECT p_target,(j->>'id')::uuid,j->'protected' FROM jsonb_array_elements(p_payloads) j ON CONFLICT DO NOTHING;
 INSERT INTO live_activity.observation(snapshot_id,identity,data)
 SELECT p_id,j->>'identity',j || jsonb_build_object('delta',CASE WHEN v_seconds>0 AND v_seconds<=120
 AND NOT p_truncated AND NOT (SELECT truncated FROM live_activity.snapshot WHERE id=v_previous)
 AND (j->>'cpuMs')::bigint >= (o.data->>'cpuMs')::bigint
 AND (j->>'reads')::bigint >= (o.data->>'reads')::bigint
 AND (j->>'writes')::bigint >= (o.data->>'writes')::bigint
 AND (j->>'logicalReads')::bigint >= (o.data->>'logicalReads')::bigint
 THEN jsonb_build_object('seconds',v_seconds,'cpuMs',((j->>'cpuMs')::bigint-(o.data->>'cpuMs')::bigint)::text,
 'reads',((j->>'reads')::bigint-(o.data->>'reads')::bigint)::text,'writes',((j->>'writes')::bigint-(o.data->>'writes')::bigint)::text,
 'logicalReads',((j->>'logicalReads')::bigint-(o.data->>'logicalReads')::bigint)::text) ELSE NULL END)
 FROM jsonb_array_elements(p_rows) j LEFT JOIN live_activity.observation o ON o.snapshot_id=v_previous AND o.identity=j->>'identity';
END $$;

CREATE FUNCTION live_activity.read_page(p_target uuid,p_snapshot uuid,p_filter jsonb,p_offset integer) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, live_activity AS $$
DECLARE v_id uuid; v_time timestamptz; v_truncated boolean; v_now timestamptz=clock_timestamp(); v_rows jsonb; v_dbs jsonb;
BEGIN
 IF p_offset < 0 OR p_offset > 512 OR octet_length(p_filter::text)>2048 THEN RAISE EXCEPTION 'Invalid page' USING ERRCODE='22023'; END IF;
 SELECT s.id,s.observed_at,s.truncated INTO v_id,v_time,v_truncated FROM live_activity.snapshot s
 JOIN control.observation_target t ON t.instance_id=s.target_id AND t.revision=s.revision AND t.lifecycle_state='active'
 WHERE s.target_id=p_target AND (p_snapshot IS NULL OR s.id=p_snapshot) AND s.observed_at>v_now-interval '24 hours'
 ORDER BY s.observed_at DESC LIMIT 1;
 SELECT coalesce(jsonb_agg(d),'[]') INTO v_dbs FROM (
 SELECT DISTINCT (data->>'databaseId')::integer AS id,data->>'databaseName' AS name FROM live_activity.observation
 WHERE snapshot_id=v_id AND data->>'databaseId' IS NOT NULL ORDER BY name,id) d;
 IF coalesce(jsonb_array_length((SELECT databases FROM live_activity.snapshot WHERE id=v_id)),0)>0 THEN
 SELECT databases INTO v_dbs FROM live_activity.snapshot WHERE id=v_id; END IF;
 SELECT coalesce(jsonb_agg(data ORDER BY n),'[]') INTO v_rows FROM (
 SELECT data,row_number() OVER () AS n FROM (
 SELECT data FROM live_activity.observation WHERE snapshot_id=v_id
 AND (p_filter->>'databaseId' IS NULL OR data->>'databaseId'=p_filter->>'databaseId')
 AND (coalesce((p_filter->>'includeIdle')::boolean,false) OR data->>'requestId' IS NOT NULL)
 AND (coalesce((p_filter->>'includeSystem')::boolean,false) OR (data->>'isUser')::boolean)
 AND (NOT coalesce((p_filter->>'blockedOnly')::boolean,false) OR (data->>'blocker')::integer<>0)
 AND (p_filter->>'login' IS NULL OR strpos(lower(coalesce(data->>'login','')),lower(p_filter->>'login'))>0)
 AND (p_filter->>'application' IS NULL OR strpos(lower(coalesce(data->>'application','')),lower(p_filter->>'application'))>0)
 AND (p_filter->>'status' IS NULL OR strpos(lower(data->>'status'),lower(p_filter->>'status'))>0)
 ORDER BY (CASE p_filter->>'sort' WHEN 'cpu' THEN (data->>'cpuMs')::bigint WHEN 'memory' THEN (data->>'memoryBytes')::bigint
 WHEN 'reads' THEN (data->>'reads')::bigint WHEN 'writes' THEN (data->>'writes')::bigint WHEN 'logicalReads' THEN (data->>'logicalReads')::bigint
 WHEN 'elapsed' THEN (data->>'elapsedMs')::bigint ELSE (data->>'sessionId')::bigint END) * CASE WHEN (p_filter->>'descending')::boolean THEN -1 ELSE 1 END,
 identity LIMIT 51 OFFSET p_offset) ordered) numbered;
 RETURN jsonb_build_object('snapshotId',v_id,'observedUtc',v_time,'repositoryTimeUtc',v_now,'truncated',coalesce(v_truncated,false),
 'state',CASE WHEN v_id IS NULL THEN 'unavailable' WHEN p_snapshot IS NULL AND (v_time<v_now-interval '30 seconds' OR
 coalesce((SELECT failed FROM live_activity.collection_state WHERE target_id=p_target),false)) THEN 'stale' ELSE 'available' END,
 'rows',v_rows,'databases',v_dbs,'nextCursor',NULL);
END $$;

CREATE FUNCTION live_activity.history(p_target uuid,p_from timestamptz,p_to timestamptz) RETURNS TABLE(id uuid,observed_at timestamptz,truncated boolean)
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
 SELECT s.id,s.observed_at,s.truncated FROM live_activity.snapshot s JOIN control.observation_target t
 ON t.instance_id=s.target_id AND t.revision=s.revision AND t.lifecycle_state='active'
 WHERE s.target_id=p_target AND s.minute_at IS NOT NULL AND s.observed_at>clock_timestamp()-interval '24 hours'
 AND s.observed_at>=p_from AND s.observed_at<=p_to ORDER BY s.observed_at LIMIT 1441;
$$;

CREATE FUNCTION live_activity.query_payload(p_target uuid,p_snapshot uuid,p_identity text) RETURNS jsonb
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
 SELECT p.protected FROM live_activity.snapshot s JOIN control.observation_target t
 ON t.instance_id=s.target_id AND t.revision=s.revision AND t.lifecycle_state='active'
 JOIN live_activity.observation o ON o.snapshot_id=s.id JOIN live_activity.payload p ON p.target_id=s.target_id AND p.id=(o.data->>'queryId')::uuid
 WHERE s.target_id=p_target AND s.id=p_snapshot AND o.identity=p_identity AND s.observed_at>clock_timestamp()-interval '24 hours';
$$;
CREATE FUNCTION live_activity.audit_query(p_target uuid,p_snapshot uuid,p_actor text,p_outcome text) RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
 INSERT INTO live_activity.query_audit(target_id,snapshot_id,actor,outcome) VALUES(p_target,p_snapshot,p_actor,p_outcome);
$$;
CREATE FUNCTION live_activity.cleanup() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
BEGIN
 DELETE FROM live_activity.snapshot WHERE id IN (SELECT id FROM live_activity.snapshot
 WHERE observed_at<=clock_timestamp()-interval '24 hours' OR (minute_at IS NULL AND received_at<clock_timestamp()-interval '5 minutes')
 ORDER BY received_at LIMIT 120);
 DELETE FROM live_activity.payload p WHERE (p.target_id,p.id) IN (SELECT q.target_id,q.id FROM live_activity.payload q
 WHERE q.created_at<clock_timestamp()-interval '5 minutes' AND NOT EXISTS(SELECT 1 FROM live_activity.observation o
 JOIN live_activity.snapshot s ON s.id=o.snapshot_id WHERE s.target_id=q.target_id AND o.data->>'queryId'=q.id::text) LIMIT 512);
END $$;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA live_activity FROM PUBLIC;
CREATE FUNCTION live_activity.failed(p_target uuid,p_owner uuid,p_fence bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,live_activity AS $$
BEGIN
 PERFORM control.assert_worker_lease('collector/live-activity/'||p_target::text,p_owner,p_fence);
 INSERT INTO live_activity.collection_state VALUES(p_target,true) ON CONFLICT(target_id) DO UPDATE SET failed=true;
END $$;
REVOKE ALL ON FUNCTION live_activity.failed(uuid,uuid,bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION live_activity.failed(uuid,uuid,bigint) TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION live_activity.targets(), live_activity.commit_capture(uuid,bigint,uuid,bigint,uuid,timestamptz,boolean,jsonb,jsonb,jsonb),live_activity.cleanup() TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION live_activity.read_page(uuid,uuid,jsonb,integer),live_activity.history(uuid,timestamptz,timestamptz),
 live_activity.query_payload(uuid,uuid,text),live_activity.audit_query(uuid,uuid,text,text) TO sqlobserver_server;
