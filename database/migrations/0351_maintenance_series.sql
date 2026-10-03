-- Finite civil schedules are fully materialized; there is no rolling expander.
-- Individual exceptions and removed dates survive every later series revision.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

CREATE TABLE alerting.maintenance_series(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),series_id uuid PRIMARY KEY,
 revision bigint NOT NULL CHECK(revision>0),definition jsonb NOT NULL,cancelled_from_date date,
 created_at timestamptz NOT NULL,updated_at timestamptz NOT NULL,expansion_version text NOT NULL,
 UNIQUE(instance_id,series_id));
CREATE TABLE alerting.maintenance_series_occurrence(
 instance_id uuid NOT NULL,series_id uuid NOT NULL,local_date date NOT NULL,
 window_id uuid NOT NULL UNIQUE REFERENCES alerting.maintenance_window(window_id),
 occurrence_sequence bigserial NOT NULL UNIQUE,series_revision bigint NOT NULL,
 exception_kind text NOT NULL DEFAULT 'none' CHECK(exception_kind IN('none','edited','cancelled','removed')),
 PRIMARY KEY(series_id,local_date),FOREIGN KEY(instance_id,series_id) REFERENCES alerting.maintenance_series(instance_id,series_id));
CREATE INDEX ix_maintenance_series_occurrence_page ON alerting.maintenance_series_occurrence(instance_id,series_id,local_date DESC,occurrence_sequence DESC);
CREATE TABLE alerting.maintenance_series_materialization(
 instance_id uuid NOT NULL,series_id uuid NOT NULL,series_revision bigint NOT NULL,local_date date NOT NULL,
 window_id uuid NOT NULL,starts_at timestamptz NOT NULL,ends_at timestamptz NOT NULL,
 utc_offset_minutes integer NOT NULL,ambiguous boolean NOT NULL,expansion_version text NOT NULL,
 PRIMARY KEY(series_id,series_revision,local_date),
 FOREIGN KEY(instance_id,series_id) REFERENCES alerting.maintenance_series(instance_id,series_id));
CREATE TABLE alerting.maintenance_series_operation(
 operation_id uuid PRIMARY KEY,instance_id uuid NOT NULL,series_id uuid NOT NULL,action_name text NOT NULL,
 actor_sid text NOT NULL,request_digest bytea NOT NULL CHECK(octet_length(request_digest)=32),
 audit_id uuid NOT NULL,applied_at timestamptz NOT NULL,result_revision bigint NOT NULL,
 created_occurrences integer NOT NULL,updated_occurrences integer NOT NULL,cancelled_occurrences integer NOT NULL,preserved_exceptions integer NOT NULL,
 FOREIGN KEY(instance_id,series_id) REFERENCES alerting.maintenance_series(instance_id,series_id));
-- Only the owner can establish this transaction-local trigger bypass. A caller
-- cannot manufacture it through a custom GUC or a legacy one-off function.
CREATE TABLE alerting.maintenance_series_mutation_guard(
 transaction_id xid8 NOT NULL,series_id uuid NOT NULL,instance_id uuid NOT NULL,applied_at timestamptz NOT NULL,
 PRIMARY KEY(transaction_id,series_id));

ALTER TABLE alerting.maintenance_series ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_occurrence ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_occurrence FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_materialization ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_materialization FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_operation ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_operation FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_mutation_guard ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_series_mutation_guard FORCE ROW LEVEL SECURITY;
CREATE POLICY maintenance_series_scope ON alerting.maintenance_series USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY maintenance_series_owner ON alerting.maintenance_series FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY maintenance_series_occurrence_scope ON alerting.maintenance_series_occurrence USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY maintenance_series_occurrence_owner ON alerting.maintenance_series_occurrence FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY maintenance_series_materialization_scope ON alerting.maintenance_series_materialization USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY maintenance_series_materialization_owner ON alerting.maintenance_series_materialization FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY maintenance_series_operation_scope ON alerting.maintenance_series_operation USING(instance_id::text=current_setting('sqlobserver.target_scope',true));
CREATE POLICY maintenance_series_operation_owner ON alerting.maintenance_series_operation FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
CREATE POLICY maintenance_series_guard_owner ON alerting.maintenance_series_mutation_guard FOR ALL TO sqlobserver_migrator USING(true) WITH CHECK(true);
REVOKE ALL ON alerting.maintenance_series,alerting.maintenance_series_occurrence,alerting.maintenance_series_materialization,
 alerting.maintenance_series_operation,alerting.maintenance_series_mutation_guard FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON SEQUENCE alerting.maintenance_series_occurrence_occurrence_sequence_seq FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

CREATE FUNCTION alerting.maintenance_series_window_id(p_series_id uuid,p_local_date date)
RETURNS uuid LANGUAGE sql IMMUTABLE STRICT SET search_path=pg_catalog AS $$
 SELECT substr(encode(sha256(convert_to(p_series_id::text||'|'||to_char(p_local_date,'YYYY-MM-DD'),'UTF8')),'hex'),1,32)::uuid
$$;

CREATE FUNCTION alerting.maintenance_series_definition_digest(p_definition jsonb)
RETURNS text LANGUAGE plpgsql IMMUTABLE SET search_path=pg_catalog AS $$
DECLARE d jsonb:=p_definition;weekdays text;
BEGIN
 SELECT coalesce(string_agg(x.value::text,',' ORDER BY x.value::integer),'') INTO weekdays FROM jsonb_array_elements_text(d->'weekdays') x;
 RETURN encode(sha256(convert_to(concat_ws('|',d->>'frequency',d->>'interval',d->>'startDate',d->>'endDate',d->>'localStartTime',
  d->>'durationMinutes',d->>'timeZoneId',weekdays,coalesce(d->>'dayOfMonth','<null>'),d->>'gapPolicy',d->>'foldPolicy',d->>'reason'),'UTF8')),'hex');
END $$;

CREATE FUNCTION alerting.maintenance_series_expand(p_series_id uuid,p_definition jsonb)
RETURNS TABLE(window_id uuid,local_date date,starts_at timestamptz,ends_at timestamptz,utc_offset_minutes integer,ambiguous boolean)
LANGUAGE plpgsql STABLE SET search_path=pg_catalog,alerting SET TimeZone='UTC' AS $$
DECLARE d jsonb:=p_definition;anchor date;last_date date;zone text;freq text;every_n integer;duration integer;day_of_month integer;weekdays integer[];local_time time;
BEGIN
 IF jsonb_typeof(d) IS DISTINCT FROM 'object' OR (SELECT count(*) FROM jsonb_object_keys(d))<>12
  OR NOT d ?& ARRAY['frequency','interval','startDate','endDate','localStartTime','durationMinutes','timeZoneId','weekdays','dayOfMonth','gapPolicy','foldPolicy','reason']
  OR d->>'frequency' NOT IN('daily','weekly','monthly') OR d->>'gapPolicy' IS DISTINCT FROM 'skip'
  OR d->>'foldPolicy' NOT IN('earlier','later') OR d->>'localStartTime' !~ '^([01][0-9]|2[0-3]):[0-5][0-9]$'
  OR d->>'startDate' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR d->>'endDate' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
  OR d->>'reason' IS NULL OR btrim(d->>'reason')='' OR octet_length(d->>'reason')>512 OR d->>'reason' ~ '[[:cntrl:]]'
  OR jsonb_typeof(d->'weekdays') IS DISTINCT FROM 'array' THEN RAISE EXCEPTION 'invalid series definition' USING ERRCODE='22023'; END IF;
 IF EXISTS(SELECT 1 FROM jsonb_each(d) x WHERE x.key IN('frequency','startDate','endDate','localStartTime','timeZoneId','gapPolicy','foldPolicy','reason') AND jsonb_typeof(x.value)<>'string')
  OR jsonb_typeof(d->'interval')<>'number' OR (d->'interval')::text !~ '^[1-9][0-9]*$'
  OR jsonb_typeof(d->'durationMinutes')<>'number' OR (d->'durationMinutes')::text !~ '^[1-9][0-9]*$'
  OR jsonb_typeof(d->'dayOfMonth') NOT IN('number','null') OR (jsonb_typeof(d->'dayOfMonth')='number' AND (d->'dayOfMonth')::text !~ '^[1-9][0-9]*$')
  OR EXISTS(SELECT 1 FROM jsonb_array_elements(d->'weekdays') x WHERE jsonb_typeof(x)<>'number' OR x::text !~ '^[0-6]$') THEN
  RAISE EXCEPTION 'canonical definition JSON types required' USING ERRCODE='22023'; END IF;
 anchor:=(d->>'startDate')::date;last_date:=(d->>'endDate')::date;zone:=d->>'timeZoneId';freq:=d->>'frequency';every_n:=(d->>'interval')::integer;
 duration:=(d->>'durationMinutes')::integer;day_of_month:=(d->>'dayOfMonth')::integer;local_time:=(d->>'localStartTime')::time;
 SELECT coalesce(array_agg(x.value::integer),ARRAY[]::integer[]) INTO weekdays FROM jsonb_array_elements_text(d->'weekdays') x;
 IF extract(year FROM anchor)>9994 OR last_date<anchor OR last_date>(anchor+interval '5 years')::date
  OR duration NOT BETWEEN 1 AND 10080 OR every_n<1 OR every_n>(CASE freq WHEN 'daily' THEN 365 WHEN 'weekly' THEN 52 ELSE 12 END)
  OR array_length(weekdays,1)>7 OR EXISTS(SELECT 1 FROM unnest(weekdays) x WHERE x NOT BETWEEN 0 AND 6)
  OR cardinality(weekdays)<>(SELECT count(DISTINCT x) FROM unnest(weekdays) x)
  OR (freq='weekly' AND (cardinality(weekdays)=0 OR day_of_month IS NOT NULL))
  OR (freq<>'weekly' AND cardinality(weekdays)<>0)
  OR (freq='monthly' AND (day_of_month IS NULL OR day_of_month NOT BETWEEN 1 AND 31)) OR (freq<>'monthly' AND day_of_month IS NOT NULL)
  OR zone IS NULL OR length(zone)>128 OR zone !~ '^[A-Za-z0-9/_+-]+$' OR zone LIKE 'Etc/GMT%'
  OR NOT EXISTS(SELECT 1 FROM pg_timezone_names z WHERE z.name=zone) THEN RAISE EXCEPTION 'invalid bounded civil schedule' USING ERRCODE='22023'; END IF;
 RETURN QUERY WITH dates AS(
  SELECT anchor+g.n AS day FROM generate_series(0,last_date-anchor) g(n)
 ),eligible AS(
  SELECT dates.day,dates.day+local_time AS local_stamp FROM dates WHERE
   CASE freq WHEN 'daily' THEN (dates.day-anchor)%every_n=0
    WHEN 'weekly' THEN ((dates.day-(anchor-extract(dow FROM anchor)::integer))/7)%every_n=0 AND extract(dow FROM dates.day)::integer=ANY(weekdays)
    ELSE ((extract(year FROM dates.day)::integer-extract(year FROM anchor)::integer)*12+extract(month FROM dates.day)::integer-extract(month FROM anchor)::integer)%every_n=0 AND extract(day FROM dates.day)::integer=day_of_month END
 ),offsets AS(
  SELECT DISTINCT e.day,e.local_stamp,extract(epoch FROM (((e.local_stamp AT TIME ZONE zone)+shift.delta) AT TIME ZONE zone)-(((e.local_stamp AT TIME ZONE zone)+shift.delta) AT TIME ZONE 'UTC'))/60 AS minutes
  FROM eligible e CROSS JOIN(VALUES(interval '-1 day'),(interval '0 day'),(interval '1 day')) shift(delta)
 ),candidates AS(
  SELECT o.day,o.minutes,((o.local_stamp-o.minutes*interval '1 minute') AT TIME ZONE 'UTC') AS utc FROM offsets o
  WHERE (((o.local_stamp-o.minutes*interval '1 minute') AT TIME ZONE 'UTC') AT TIME ZONE zone)=o.local_stamp
 ),picked AS(
  SELECT c.day,CASE WHEN d->>'foldPolicy'='earlier' THEN min(c.utc) ELSE max(c.utc) END AS utc,count(*)>1 AS fold FROM candidates c GROUP BY c.day
 ) SELECT alerting.maintenance_series_window_id(p_series_id,p.day),p.day,p.utc,p.utc+duration*interval '1 minute',
  (extract(epoch FROM ((p.utc AT TIME ZONE zone)-(p.utc AT TIME ZONE 'UTC')))/60)::integer,p.fold FROM picked p ORDER BY p.day;
END $$;

CREATE FUNCTION alerting.guard_generated_maintenance_window()
RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE occurrence alerting.maintenance_series_occurrence%ROWTYPE;t timestamptz:=clock_timestamp();internal_time timestamptz;
BEGIN
 SELECT x.* INTO occurrence FROM alerting.maintenance_series_occurrence x WHERE x.window_id=OLD.window_id;
 IF NOT FOUND THEN RETURN NEW; END IF;
 SELECT g.applied_at INTO internal_time FROM alerting.maintenance_series_mutation_guard g
  WHERE g.transaction_id=pg_current_xact_id() AND g.series_id=occurrence.series_id AND g.instance_id=occurrence.instance_id;
 IF OLD.instance_id IS DISTINCT FROM NEW.instance_id OR OLD.window_id IS DISTINCT FROM NEW.window_id
  OR OLD.cancelled_at IS NOT NULL OR OLD.starts_at<=coalesce(internal_time,t) THEN RAISE EXCEPTION 'generated occurrence is immutable or already started' USING ERRCODE='40001'; END IF;
 IF internal_time IS NULL THEN
  IF NEW.cancelled_at IS NULL AND NEW.starts_at<=t THEN RAISE EXCEPTION 'individual generated edits must remain future' USING ERRCODE='40001'; END IF;
  UPDATE alerting.maintenance_series_occurrence x SET exception_kind=CASE WHEN NEW.cancelled_at IS NULL THEN 'edited' ELSE 'cancelled' END WHERE x.window_id=OLD.window_id;
 END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER tr_generated_maintenance_window BEFORE UPDATE ON alerting.maintenance_window FOR EACH ROW EXECUTE FUNCTION alerting.guard_generated_maintenance_window();

CREATE FUNCTION alerting.write_maintenance_series(p_target_id uuid,p_series_id uuid,p_expected_revision bigint,p_effective_from date,
 p_definition jsonb,p_occurrences jsonb,p_reason text,p_idempotency_key text,p_actor_sid text,p_correlation_id uuid,p_request_digest text)
RETURNS TABLE(audit_id uuid,recorded_at timestamptz,series_id uuid,revision bigint,applied_at timestamptz,
 created_occurrences integer,updated_occurrences integer,cancelled_occurrences integer,preserved_exceptions integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting,audit SET TimeZone='UTC' AS $$
DECLARE op uuid;action text;digest bytea;expected_hash text;def_hash text;existing alerting.maintenance_series_operation%ROWTYPE;
 current_series alerting.maintenance_series%ROWTYPE;item record;m alerting.maintenance_window%ROWTYPE;mapping alerting.maintenance_series_occurrence%ROWTYPE;
 native_rows jsonb;row_count integer;a uuid:=gen_random_uuid();t timestamptz;next_revision bigint;cutoff date;cancel_fence date;
 made integer:=0;updated integer:=0;cancelled integer:=0;preserved integer:=0;
BEGIN
 PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
 IF p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'series target scope required' USING ERRCODE='42501'; END IF;
 op:=p_idempotency_key::uuid;action:=CASE WHEN p_definition IS NULL THEN 'maintenance.series.cancel' WHEN p_expected_revision IS NULL THEN 'maintenance.series.create' ELSE 'maintenance.series.update' END;
 -- Time-zone observations are excluded from replay identity.
 digest:=sha256(convert_to(jsonb_build_object('target',p_target_id,'series',p_series_id,'revision',p_expected_revision,'cutoff',p_effective_from,
  'definition',p_definition,'reason',p_reason,'actor',p_actor_sid,'action',action,'request',p_request_digest)::text,'UTF8'));
 PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
 SELECT x.* INTO existing FROM alerting.maintenance_series_operation x WHERE x.operation_id=op;
 IF FOUND THEN
  IF existing.request_digest IS DISTINCT FROM digest THEN RAISE EXCEPTION 'series operation payload conflict' USING ERRCODE='40001'; END IF;
  RETURN QUERY SELECT existing.audit_id,existing.applied_at,existing.series_id,existing.result_revision,existing.applied_at,
   existing.created_occurrences,existing.updated_occurrences,existing.cancelled_occurrences,existing.preserved_exceptions; RETURN;
 END IF;
 IF p_series_id IS NULL OR p_series_id='00000000-0000-0000-0000-000000000000'::uuid OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
  OR p_actor_sid IS NULL OR octet_length(p_actor_sid)>184 OR p_actor_sid !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
  OR p_reason IS NULL OR btrim(p_reason)='' OR octet_length(p_reason)>512 OR p_reason ~ '[[:cntrl:]]'
  OR (p_expected_revision IS NULL AND (p_effective_from IS NOT NULL OR p_definition IS NULL)) OR (p_expected_revision IS NOT NULL AND (p_expected_revision<=0 OR p_effective_from IS NULL))
  OR p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-f]{64}$' THEN RAISE EXCEPTION 'series command invalid' USING ERRCODE='22023'; END IF;
 IF p_definition IS NOT NULL THEN
  IF p_definition->>'reason' IS DISTINCT FROM p_reason THEN RAISE EXCEPTION 'series reason mismatch' USING ERRCODE='22023'; END IF;
  SELECT coalesce(jsonb_agg(to_jsonb(x)),'[]'::jsonb),count(*) INTO native_rows,row_count FROM alerting.maintenance_series_expand(p_series_id,p_definition) x;
  IF row_count NOT BETWEEN 1 AND 2048 THEN RAISE EXCEPTION 'series expansion bound invalid' USING ERRCODE='22023'; END IF;
  def_hash:=alerting.maintenance_series_definition_digest(p_definition);
  IF jsonb_typeof(p_occurrences) IS DISTINCT FROM 'array' OR jsonb_array_length(p_occurrences)<>row_count OR octet_length(p_occurrences::text)>1048576
   OR EXISTS(SELECT 1 FROM jsonb_to_recordset(native_rows) n(window_id uuid,local_date date,starts_at timestamptz,ends_at timestamptz,utc_offset_minutes integer,ambiguous boolean)
    FULL JOIN jsonb_to_recordset(p_occurrences) o("windowId" uuid,"localDate" date,"startsAtUtc" timestamptz,"endsAtUtc" timestamptz,"utcOffsetMinutes" integer,ambiguous boolean,"expansionVersion" text)
     ON n.local_date=o."localDate" AND n.window_id=o."windowId"
    WHERE n.local_date IS NULL OR o."localDate" IS NULL OR n.starts_at IS DISTINCT FROM o."startsAtUtc" OR n.ends_at IS DISTINCT FROM o."endsAtUtc"
     OR n.utc_offset_minutes IS DISTINCT FROM o."utcOffsetMinutes" OR n.ambiguous IS DISTINCT FROM o.ambiguous OR o."expansionVersion" IS DISTINCT FROM 'civil-v1')
   OR (SELECT count(DISTINCT o->>'localDate') FROM jsonb_array_elements(p_occurrences) o)<>row_count THEN
   RAISE EXCEPTION 'canonical expansion or time-zone rules changed' USING ERRCODE='40001'; END IF;
 END IF;
 expected_hash:=encode(sha256(convert_to(action||'|'||p_target_id::text||'|'||p_series_id::text||'|'||coalesce(p_expected_revision::text,'<null>')
  ||'|'||coalesce(to_char(p_effective_from,'YYYY-MM-DD'),'<null>')||'|'||coalesce(def_hash,'<null>')||'|'||p_reason,'UTF8')),'hex');
 IF p_request_digest IS DISTINCT FROM expected_hash THEN RAISE EXCEPTION 'series request digest mismatch' USING ERRCODE='22023'; END IF;
 SELECT s.* INTO current_series FROM alerting.maintenance_series s WHERE s.series_id=p_series_id FOR UPDATE;
 IF FOUND THEN
  IF current_series.instance_id<>p_target_id THEN RAISE EXCEPTION 'series belongs to another target' USING ERRCODE='42501'; END IF;
  IF p_expected_revision IS NULL OR current_series.revision<>p_expected_revision THEN RAISE EXCEPTION 'series revision conflict' USING ERRCODE='40001'; END IF;
  next_revision:=current_series.revision+1;cancel_fence:=current_series.cancelled_from_date;
 ELSE
  IF p_expected_revision IS NOT NULL THEN RAISE EXCEPTION 'series not found' USING ERRCODE='P0002'; END IF;
  next_revision:=1;
 END IF;
 t:=clock_timestamp();cutoff:=coalesce(p_effective_from,'0001-01-01'::date);
 IF p_definition IS NOT NULL AND p_expected_revision IS NULL AND NOT EXISTS(SELECT 1 FROM jsonb_to_recordset(native_rows) x(starts_at timestamptz) WHERE x.starts_at>t) THEN RAISE EXCEPTION 'series has no future occurrences' USING ERRCODE='22023'; END IF;
 IF p_definition IS NULL THEN cancel_fence:=least(cancel_fence,p_effective_from); END IF;
 INSERT INTO alerting.maintenance_series(instance_id,series_id,revision,definition,cancelled_from_date,created_at,updated_at,expansion_version)
 VALUES(p_target_id,p_series_id,next_revision,coalesce(p_definition,current_series.definition),cancel_fence,coalesce(current_series.created_at,t),t,'civil-v1')
 ON CONFLICT ON CONSTRAINT maintenance_series_pkey DO UPDATE SET revision=EXCLUDED.revision,definition=EXCLUDED.definition,cancelled_from_date=EXCLUDED.cancelled_from_date,updated_at=t;
 INSERT INTO alerting.maintenance_series_mutation_guard VALUES(pg_current_xact_id(),p_series_id,p_target_id,t);
 SELECT count(*) INTO preserved FROM alerting.maintenance_series_occurrence x JOIN alerting.maintenance_window w ON w.window_id=x.window_id
  WHERE x.series_id=p_series_id AND x.local_date>=cutoff AND x.exception_kind<>'none' AND w.starts_at>t;
 -- Obsolete ordinary future rows become permanent tombstones, never deletes.
 FOR item IN SELECT x.window_id FROM alerting.maintenance_series_occurrence x JOIN alerting.maintenance_window w ON w.window_id=x.window_id
  WHERE x.series_id=p_series_id AND x.local_date>=cutoff AND x.exception_kind='none' AND w.starts_at>t AND w.cancelled_at IS NULL
   AND (p_definition IS NULL OR (cancel_fence IS NOT NULL AND x.local_date>=cancel_fence) OR NOT EXISTS(SELECT 1 FROM jsonb_to_recordset(native_rows) n(local_date date) WHERE n.local_date=x.local_date)) FOR UPDATE OF w
 LOOP
  UPDATE alerting.maintenance_window w SET cancelled_at=t,cancelled_actor_sid=p_actor_sid,cancelled_operation_id=op,revision=w.revision+1 WHERE w.window_id=item.window_id;
  UPDATE alerting.maintenance_series_occurrence x SET exception_kind='removed' WHERE x.window_id=item.window_id;cancelled:=cancelled+1;
 END LOOP;
 IF p_definition IS NOT NULL THEN
  FOR item IN SELECT * FROM jsonb_to_recordset(native_rows) x(window_id uuid,local_date date,starts_at timestamptz,ends_at timestamptz,utc_offset_minutes integer,ambiguous boolean)
   WHERE x.local_date>=cutoff AND x.starts_at>t AND (cancel_fence IS NULL OR x.local_date<cancel_fence) ORDER BY x.local_date
  LOOP
   SELECT x.* INTO mapping FROM alerting.maintenance_series_occurrence x WHERE x.series_id=p_series_id AND x.local_date=item.local_date;
   IF FOUND THEN
    SELECT w.* INTO m FROM alerting.maintenance_window w WHERE w.window_id=mapping.window_id FOR UPDATE;
    IF mapping.exception_kind<>'none' OR m.starts_at<=t OR m.cancelled_at IS NOT NULL THEN CONTINUE; END IF;
    UPDATE alerting.maintenance_window w SET starts_at=item.starts_at,ends_at=item.ends_at,reason=p_reason,revision=w.revision+1 WHERE w.window_id=m.window_id;updated:=updated+1;
    UPDATE alerting.maintenance_series_occurrence x SET series_revision=next_revision WHERE x.window_id=m.window_id;
   ELSE
    IF EXISTS(SELECT 1 FROM alerting.maintenance_window w WHERE w.window_id=item.window_id) THEN RAISE EXCEPTION 'generated window identity collision' USING ERRCODE='40001'; END IF;
    INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason) VALUES(item.window_id,p_target_id,item.starts_at,item.ends_at,p_reason);
    INSERT INTO alerting.maintenance_series_occurrence(instance_id,series_id,local_date,window_id,series_revision) VALUES(p_target_id,p_series_id,item.local_date,item.window_id,next_revision);made:=made+1;
   END IF;
   INSERT INTO alerting.maintenance_series_materialization VALUES(p_target_id,p_series_id,next_revision,item.local_date,item.window_id,item.starts_at,item.ends_at,item.utc_offset_minutes,item.ambiguous,'civil-v1');
  END LOOP;
 END IF;
 IF (SELECT count(*) FROM alerting.maintenance_series_occurrence x WHERE x.series_id=p_series_id)>2048 THEN
  RAISE EXCEPTION 'series lifetime occurrence capacity reached; create a new series' USING ERRCODE='40001'; END IF;
 DELETE FROM alerting.maintenance_series_mutation_guard g WHERE g.transaction_id=pg_current_xact_id() AND g.series_id=p_series_id;
 INSERT INTO alerting.maintenance_series_operation VALUES(op,p_target_id,p_series_id,action,p_actor_sid,digest,a,t,next_revision,made,updated,cancelled,preserved);
 INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
 VALUES(t,a,'user',p_actor_sid,action,'allowed','succeeded','alerting',p_target_id::text,p_correlation_id,jsonb_build_object('operationId',op,'targetId',p_target_id,'seriesId',p_series_id,
  'revision',next_revision,'effectiveFromDate',p_effective_from,'definitionDigest',def_hash,'reason',p_reason,'createdOccurrences',made,'updatedOccurrences',updated,'cancelledOccurrences',cancelled,'preservedExceptions',preserved));
 RETURN QUERY SELECT a,t,p_series_id,next_revision,t,made,updated,cancelled,preserved;
END $$;

CREATE FUNCTION alerting.get_maintenance_series(p_target_id uuid,p_series_id uuid)
RETURNS TABLE(series_id uuid,revision bigint,definition jsonb,cancelled_from_date date,created_at timestamptz,updated_at timestamptz,expansion_version text,server_now timestamptz,occurrence_count integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE t timestamptz:=clock_timestamp();
BEGIN
 IF p_target_id IS NULL OR p_series_id IS NULL OR p_series_id='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'series identity required' USING ERRCODE='22023'; END IF;
 IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'series scope required' USING ERRCODE='42501'; END IF;
 RETURN QUERY SELECT s.series_id,s.revision,s.definition,s.cancelled_from_date,s.created_at,s.updated_at,s.expansion_version,t,
  (SELECT count(*)::integer FROM alerting.maintenance_series_occurrence x WHERE x.instance_id=p_target_id AND x.series_id=s.series_id)
 FROM alerting.maintenance_series s WHERE s.instance_id=p_target_id AND s.series_id=p_series_id;
END $$;

CREATE FUNCTION alerting.list_maintenance_series(p_target_id uuid,p_limit integer,p_before_series_id uuid)
RETURNS TABLE(series_id uuid,revision bigint,definition jsonb,cancelled_from_date date,created_at timestamptz,updated_at timestamptz,expansion_version text,server_now timestamptz,occurrence_count integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE t timestamptz:=clock_timestamp();
BEGIN
 IF p_limit NOT BETWEEN 1 AND 50 OR p_limit IS NULL OR p_before_series_id='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'series page bounds invalid' USING ERRCODE='22023'; END IF;
 IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'series scope required' USING ERRCODE='42501'; END IF;
 IF p_before_series_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM alerting.maintenance_series s WHERE s.series_id=p_before_series_id AND s.instance_id=p_target_id) THEN RAISE EXCEPTION 'series anchor not found' USING ERRCODE='P0002'; END IF;
 RETURN QUERY SELECT s.series_id,s.revision,s.definition,s.cancelled_from_date,s.created_at,s.updated_at,s.expansion_version,t,
  (SELECT count(*)::integer FROM alerting.maintenance_series_occurrence x WHERE x.instance_id=p_target_id AND x.series_id=s.series_id)
 FROM alerting.maintenance_series s WHERE s.instance_id=p_target_id AND (p_before_series_id IS NULL OR s.series_id<p_before_series_id) ORDER BY s.series_id DESC LIMIT p_limit+1;
END $$;

CREATE FUNCTION alerting.list_maintenance_series_occurrences(p_target_id uuid,p_series_id uuid,p_limit integer,p_before_window_id uuid)
RETURNS TABLE(window_id uuid,local_date date,starts_at timestamptz,ends_at timestamptz,reason text,revision bigint,cancelled_at timestamptz,
 series_revision bigint,exception boolean,utc_offset_minutes integer,ambiguous boolean,expansion_version text,server_now timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,alerting AS $$
DECLARE anchor_date date;anchor_sequence bigint;t timestamptz:=clock_timestamp();
BEGIN
 IF p_series_id IS NULL OR p_series_id='00000000-0000-0000-0000-000000000000'::uuid OR p_limit IS NULL OR p_limit NOT BETWEEN 1 AND 50
  OR p_before_window_id='00000000-0000-0000-0000-000000000000'::uuid THEN RAISE EXCEPTION 'occurrence page bounds invalid' USING ERRCODE='22023'; END IF;
 IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'series scope required' USING ERRCODE='42501'; END IF;
 IF NOT EXISTS(SELECT 1 FROM alerting.maintenance_series s WHERE s.instance_id=p_target_id AND s.series_id=p_series_id) THEN RAISE EXCEPTION 'series not found' USING ERRCODE='P0002'; END IF;
 IF p_before_window_id IS NOT NULL THEN SELECT x.local_date,x.occurrence_sequence INTO anchor_date,anchor_sequence FROM alerting.maintenance_series_occurrence x
  WHERE x.instance_id=p_target_id AND x.series_id=p_series_id AND x.window_id=p_before_window_id;
  IF NOT FOUND THEN RAISE EXCEPTION 'occurrence anchor not found' USING ERRCODE='P0002'; END IF;
 END IF;
 RETURN QUERY SELECT x.window_id,x.local_date,w.starts_at,w.ends_at,w.reason,w.revision,w.cancelled_at,x.series_revision,x.exception_kind<>'none',
  m.utc_offset_minutes,m.ambiguous,m.expansion_version,t FROM alerting.maintenance_series_occurrence x JOIN alerting.maintenance_window w ON w.window_id=x.window_id
  JOIN alerting.maintenance_series_materialization m ON m.series_id=x.series_id AND m.series_revision=x.series_revision AND m.local_date=x.local_date
 WHERE x.instance_id=p_target_id AND x.series_id=p_series_id AND (anchor_date IS NULL OR (x.local_date,x.occurrence_sequence)<(anchor_date,anchor_sequence))
 ORDER BY x.local_date DESC,x.occurrence_sequence DESC LIMIT p_limit+1;
END $$;

REVOKE ALL ON FUNCTION alerting.maintenance_series_window_id(uuid,date),alerting.maintenance_series_definition_digest(jsonb),alerting.maintenance_series_expand(uuid,jsonb),alerting.guard_generated_maintenance_window()
 FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION alerting.write_maintenance_series(uuid,uuid,bigint,date,jsonb,jsonb,text,text,text,uuid,text),
 alerting.get_maintenance_series(uuid,uuid),alerting.list_maintenance_series(uuid,integer,uuid),alerting.list_maintenance_series_occurrences(uuid,uuid,integer,uuid)
 FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
GRANT EXECUTE ON FUNCTION alerting.write_maintenance_series(uuid,uuid,bigint,date,jsonb,jsonb,text,text,text,uuid,text),
 alerting.get_maintenance_series(uuid,uuid),alerting.list_maintenance_series(uuid,integer,uuid),alerting.list_maintenance_series_occurrences(uuid,uuid,integer,uuid) TO sqlobserver_server;

CREATE OR REPLACE FUNCTION audit.append_m8_administrative_activity(
    p_actor_identifier text,
    p_correlation_id uuid,
    p_action text,
    p_target_id uuid,
    p_operation_id uuid,
    p_authorization_result text,
    p_operation_outcome text,
    p_reason text,
    p_safe_details jsonb
)
RETURNS TABLE(audit_activity_id uuid, repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, public, audit
SET TimeZone = 'UTC'
AS $$
DECLARE captured_repository_time timestamptz := clock_timestamp(); generated_activity_id uuid := gen_random_uuid();
BEGIN
  IF p_actor_identifier IS NULL OR p_actor_identifier !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
     OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_operation_id IS NULL OR p_operation_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_action NOT IN ('alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.snooze','alert.unsnooze','alert.assign','alert.unassign','maintenance.series.create','maintenance.series.update','maintenance.series.cancel','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
     OR p_authorization_result NOT IN ('allowed','denied')
     OR p_operation_outcome NOT IN ('succeeded','failed','conflict','cancelled','rejected')
     OR p_reason NOT IN ('completed','principal_disabled','required_role_missing','target_out_of_scope','already_exists','revision_conflict','target_not_found','target_retired','discovery_failed','repository_failure','invalid_request','idempotent_replay')
     OR jsonb_typeof(coalesce(p_safe_details,'{}'::jsonb)) <> 'object'
     OR octet_length(coalesce(p_safe_details,'{}'::jsonb)::text) > 2048 THEN
    RAISE EXCEPTION 'invalid M8 outcome audit fields' USING ERRCODE='22023';
  END IF;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
  VALUES(captured_repository_time,generated_activity_id,'user',p_actor_identifier,p_action,p_authorization_result,p_operation_outcome,'alerting',p_target_id::text,p_correlation_id,
         jsonb_build_object('operationId',p_operation_id,'reason',p_reason,'details',coalesce(p_safe_details,'{}'::jsonb)));
  RETURN QUERY SELECT generated_activity_id,captured_repository_time;
END $$;

CREATE OR REPLACE FUNCTION audit.append_denied_m8_administrative_activity(
    p_actor_identifier text,
    p_correlation_id uuid,
    p_action text,
    p_target_id uuid,
    p_reason text
)
RETURNS TABLE(audit_activity_id uuid, repository_time timestamptz)
LANGUAGE plpgsql SECURITY DEFINER VOLATILE PARALLEL UNSAFE
SET search_path = pg_catalog, public, audit
SET TimeZone = 'UTC'
AS $$
DECLARE captured_repository_time timestamptz := clock_timestamp(); generated_activity_id uuid := gen_random_uuid();
BEGIN
  IF p_actor_identifier IS NULL OR p_actor_identifier !~ '^S-1-[0-9]+-[0-9]+(-[0-9]+)*$'
     OR p_correlation_id IS NULL OR p_correlation_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_target_id IS NULL OR p_target_id='00000000-0000-0000-0000-000000000000'::uuid
     OR p_action NOT IN ('register_observation_target','update_observation_target','retire_observation_target','request_capability_rediscovery','alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.snooze','alert.unsnooze','alert.assign','alert.unassign','maintenance.series.create','maintenance.series.update','maintenance.series.cancel','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
     OR p_reason NOT IN ('principal_disabled','required_role_missing','target_out_of_scope','already_exists','revision_conflict','target_not_found','target_retired','discovery_failed','repository_failure','invalid_request','idempotent_replay') THEN
    RAISE EXCEPTION 'invalid M8 denial audit fields' USING ERRCODE='22023';
  END IF;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
  VALUES(captured_repository_time,generated_activity_id,'user',p_actor_identifier,CASE p_action WHEN 'register_observation_target' THEN 'observation_target.register' WHEN 'update_observation_target' THEN 'observation_target.update' WHEN 'retire_observation_target' THEN 'observation_target.retire' WHEN 'request_capability_rediscovery' THEN 'capability.rediscovery_request' ELSE p_action END,'denied','rejected',CASE WHEN p_action LIKE 'alert.%' OR p_action LIKE 'maintenance.series.%' THEN 'alerting' ELSE 'observation_target' END,p_target_id::text,p_correlation_id,jsonb_build_object('reason',p_reason));
  RETURN QUERY SELECT generated_activity_id,captured_repository_time;
END $$;
