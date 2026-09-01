-- M8: deterministic alerts, one-off UTC maintenance, and durable delivery outbox.
-- Runtime roles never receive table DML; security-definer functions enforce scope and leases.
SET LOCAL lock_timeout = '5s'; SET LOCAL statement_timeout = '5min'; SET LOCAL idle_in_transaction_session_timeout = '5min'; SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;
-- UUID/hash primitives are provisioned by the repository baseline migrations;
-- this milestone must not attempt extension installation under the migrator
-- role (fresh template0 installs grant no CREATE privilege).

CREATE TABLE IF NOT EXISTS alerting.rule
(
    rule_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    name text NOT NULL,
    kind smallint NOT NULL CHECK (kind IN (1, 2)),
    metric_id text,
    comparison smallint NOT NULL CHECK (comparison BETWEEN 1 AND 5),
    threshold double precision NOT NULL CHECK (threshold NOT IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)),
    hysteresis double precision NOT NULL DEFAULT 0 CHECK (hysteresis >= 0 AND hysteresis NOT IN ('NaN'::double precision, 'Infinity'::double precision, '-Infinity'::double precision)),
    confirmation_count integer NOT NULL CHECK (confirmation_count BETWEEN 1 AND 100),
    confirmation_window interval NOT NULL CHECK (confirmation_window BETWEEN interval '0 seconds' AND interval '7 days'),
    evaluation_interval interval NOT NULL CHECK (evaluation_interval BETWEEN interval '1 second' AND interval '1 hour'),
    enabled boolean NOT NULL DEFAULT true,
    target_scope uuid GENERATED ALWAYS AS (instance_id) STORED NOT NULL,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (instance_id, name),
    UNIQUE (instance_id, rule_id),
    CHECK ((kind = 1 AND metric_id IS NOT NULL) OR (kind = 2 AND metric_id IS NULL))
);
-- The embedded JSON catalog is the product authority; this registry records the
-- exact digest the database will admit, allowing drift to fail closed.
CREATE TABLE IF NOT EXISTS alerting.catalog_registry
(
    catalog_id text PRIMARY KEY,
    catalog_digest text NOT NULL CHECK (catalog_digest ~ '^[0-9a-f]{64}$'),
    contract_version integer NOT NULL CHECK (contract_version > 0),
    registered_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO alerting.catalog_registry(catalog_id,catalog_digest,contract_version)
VALUES ('sqlobserver.m8.alerts','f28dab1e5bd65bf13f972a887b98742fe15eeea1dd7b13ac7ba0e8b6f0485d91',1)
ON CONFLICT (catalog_id) DO NOTHING;
CREATE TABLE IF NOT EXISTS alerting.rule_state
(
    instance_id uuid NOT NULL,
    rule_id uuid NOT NULL REFERENCES alerting.rule(rule_id),
    alert_id uuid,
    state smallint NOT NULL CHECK (state BETWEEN 1 AND 5),
    consecutive_matches integer NOT NULL CHECK (consecutive_matches >= 0),
    first_match_at timestamptz,
    last_observed_at timestamptz,
    fired_at timestamptz,
    acknowledged_at timestamptz,
    resolved_at timestamptz,
    episode_started_at timestamptz,
    acknowledged_by text,
    last_value double precision,
    reason text,
    delivery_suppressed boolean NOT NULL DEFAULT false,
    alert_episode_id uuid,
    last_operation_id uuid,
    evidence_digest bytea CHECK (evidence_digest IS NULL OR octet_length(evidence_digest)=32),
    last_reason text,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    PRIMARY KEY (instance_id, rule_id),
    FOREIGN KEY (instance_id, rule_id) REFERENCES alerting.rule(instance_id, rule_id)
);
CREATE TABLE IF NOT EXISTS alerting.evaluation_queue
(
    operation_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    rule_id uuid NOT NULL,
    observed_at timestamptz NOT NULL,
    observations jsonb NOT NULL CHECK (octet_length(observations::text) <= 65536),
    due_at timestamptz NOT NULL,
    rule_revision bigint NOT NULL DEFAULT 1 CHECK (rule_revision > 0),
    cancel_reason text,
    next_due_at timestamptz,
    claimed_until timestamptz,
    claim_work_key text,
    claim_owner_execution_id uuid,
    claim_fencing bigint,
    completed_at timestamptz,
    evidence_digest bytea NOT NULL CHECK (octet_length(evidence_digest)=32),
    sample_id text NOT NULL DEFAULT '',
    run_id uuid,
    FOREIGN KEY (instance_id, rule_id) REFERENCES alerting.rule(instance_id, rule_id),
    -- Equal timestamp samples are distinct observations.  operation_id is
    -- canonical, while this compound key documents the source identity used
    -- when reconstructing/replaying collector evidence.
    UNIQUE(instance_id, rule_id, sample_id, run_id, observed_at)
);
CREATE TABLE IF NOT EXISTS alerting.maintenance_window
(
    window_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    starts_at timestamptz NOT NULL,
    ends_at timestamptz NOT NULL,
    reason text NOT NULL CHECK (octet_length(reason) BETWEEN 1 AND 512),
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    cancelled_at timestamptz,
    cancelled_actor_sid text,
    cancelled_operation_id uuid,
    CHECK (ends_at > starts_at AND ends_at <= starts_at + interval '7 days')
);
ALTER TABLE alerting.maintenance_window ADD COLUMN IF NOT EXISTS cancelled_at timestamptz;
ALTER TABLE alerting.maintenance_window ADD COLUMN IF NOT EXISTS cancelled_actor_sid text;
ALTER TABLE alerting.maintenance_window ADD COLUMN IF NOT EXISTS cancelled_operation_id uuid;
CREATE TABLE IF NOT EXISTS alerting.destination
(
    destination_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    kind text NOT NULL CHECK (kind IN ('https-webhook','windows-event-log')),
    configuration_reference text NOT NULL CHECK (octet_length(configuration_reference) BETWEEN 1 AND 256 AND position('://' in configuration_reference) = 0),
    enabled boolean NOT NULL DEFAULT true,
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    approved boolean NOT NULL DEFAULT false,
    approved_kind text,
    approved_configuration_reference text,
    approved_revision bigint,
    approval_digest text,
    approval_scope uuid,
    configuration_digest text,
    UNIQUE(instance_id, destination_id)
);
CREATE TABLE IF NOT EXISTS alerting.admin_idempotency
(
    action_name text NOT NULL,
    idempotency_key text NOT NULL CHECK (octet_length(idempotency_key) BETWEEN 1 AND 128),
    request_digest bytea NOT NULL CHECK (octet_length(request_digest)=32),
    target_scope uuid NOT NULL,
    operation_id uuid,
    expected_revision bigint,
    audit_id uuid NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY(action_name, idempotency_key),
    CHECK (expected_revision IS NULL OR expected_revision > 0),
    UNIQUE(target_scope, action_name, operation_id)
);
CREATE TABLE IF NOT EXISTS alerting.delivery_outbox
(
    delivery_id uuid PRIMARY KEY,
    alert_id uuid NOT NULL,
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    rule_id uuid NOT NULL,
    destination_id uuid NOT NULL REFERENCES alerting.destination(destination_id),
    event_kind smallint NOT NULL CHECK (event_kind IN (1,2,3)),
    payload jsonb NOT NULL CHECK (octet_length(payload::text) <= 16384),
    attempt integer NOT NULL DEFAULT 0 CHECK (attempt BETWEEN 0 AND 7),
    due_at timestamptz NOT NULL,
    leased_until timestamptz,
    lease_fencing bigint,
    lease_work_key text,
    lease_owner_execution_id uuid,
    completed_at timestamptz,
    last_error_code text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    operation_id uuid,
    evidence_digest bytea CHECK (evidence_digest IS NULL OR octet_length(evidence_digest)=32),
    destination_revision bigint,
    destination_kind text,
    destination_configuration_reference text,
    destination_approval_revision bigint,
    destination_approval_digest text,
    destination_approval_scope uuid,
    destination_configuration_digest text,
    FOREIGN KEY (instance_id, rule_id) REFERENCES alerting.rule(instance_id, rule_id),
    UNIQUE(alert_id, destination_id, event_kind),
    cancelled_at timestamptz,
    cancel_reason text,
    CHECK ((cancelled_at IS NULL AND cancel_reason IS NULL) OR (cancelled_at IS NOT NULL AND octet_length(cancel_reason) BETWEEN 1 AND 64)),
    FOREIGN KEY (instance_id, destination_id) REFERENCES alerting.destination(instance_id, destination_id)
);
CREATE TABLE IF NOT EXISTS alerting.delivery_attempt
(
    delivery_id uuid NOT NULL REFERENCES alerting.delivery_outbox(delivery_id),
    attempt integer NOT NULL CHECK (attempt BETWEEN 1 AND 8),
    attempted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    outcome text NOT NULL CHECK (outcome IN ('succeeded','retryable_failure','permanent_failure')),
    response_code integer CHECK (response_code IS NULL OR response_code BETWEEN 100 AND 599),
    response_bytes integer CHECK (response_bytes IS NULL OR response_bytes BETWEEN 0 AND 65536),
    error_code text,
    PRIMARY KEY(delivery_id, attempt)
);
ALTER TABLE alerting.evaluation_queue ADD COLUMN IF NOT EXISTS next_due_at timestamptz;
ALTER TABLE alerting.evaluation_queue ADD COLUMN IF NOT EXISTS sample_id text NOT NULL DEFAULT '';
ALTER TABLE alerting.evaluation_queue ADD COLUMN IF NOT EXISTS run_id uuid;
ALTER TABLE alerting.evaluation_queue ADD COLUMN IF NOT EXISTS rule_revision bigint NOT NULL DEFAULT 1;
ALTER TABLE alerting.evaluation_queue ADD COLUMN IF NOT EXISTS cancel_reason text;
ALTER TABLE alerting.evaluation_queue DROP CONSTRAINT IF EXISTS evaluation_queue_instance_id_rule_id_observed_at_key;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS cancelled_at timestamptz;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS cancel_reason text;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS rule_id uuid;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_revision bigint;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_kind text;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_configuration_reference text;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_approval_revision bigint;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_approval_digest text;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_approval_scope uuid;
ALTER TABLE alerting.delivery_outbox ADD COLUMN IF NOT EXISTS destination_configuration_digest text;
ALTER TABLE alerting.admin_idempotency ALTER COLUMN operation_id SET NOT NULL;
ALTER TABLE alerting.delivery_outbox ALTER COLUMN operation_id SET NOT NULL;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS approved boolean NOT NULL DEFAULT false;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS approved_kind text;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS approved_configuration_reference text;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS approved_revision bigint;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS approval_digest text;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS approval_scope uuid;
ALTER TABLE alerting.destination ADD COLUMN IF NOT EXISTS configuration_digest text;
UPDATE alerting.delivery_outbox d SET destination_configuration_digest=z.configuration_digest
FROM alerting.destination z
WHERE d.destination_configuration_digest IS NULL AND z.destination_id=d.destination_id AND z.instance_id=d.instance_id;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM alerting.delivery_outbox WHERE rule_id IS NULL) THEN
    UPDATE alerting.delivery_outbox d SET rule_id = s.rule_id
    FROM alerting.rule_state s WHERE s.instance_id=d.instance_id AND s.alert_id=d.alert_id;
  END IF;
  ALTER TABLE alerting.delivery_outbox ALTER COLUMN rule_id SET NOT NULL;
EXCEPTION WHEN undefined_column THEN NULL;
END $$;
DO $$ BEGIN
  ALTER TABLE alerting.delivery_attempt ADD CONSTRAINT delivery_attempt_number_bounds CHECK (attempt BETWEEN 1 AND 8);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;
DO $$ BEGIN
  ALTER TABLE alerting.delivery_outbox ADD CONSTRAINT delivery_cancel_reason_bounds CHECK ((cancelled_at IS NULL AND cancel_reason IS NULL) OR (cancelled_at IS NOT NULL AND octet_length(cancel_reason) BETWEEN 1 AND 64));
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;
DO $$ BEGIN
  ALTER TABLE alerting.evaluation_queue ADD CONSTRAINT evaluation_queue_rule_target_fk FOREIGN KEY (instance_id,rule_id) REFERENCES alerting.rule(instance_id,rule_id);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;
CREATE TABLE IF NOT EXISTS alerting.state_history
(
    history_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    instance_id uuid NOT NULL,
    rule_id uuid NOT NULL,
    alert_id uuid,
    from_state smallint,
    to_state smallint NOT NULL CHECK (to_state BETWEEN 1 AND 5),
    observed_at timestamptz NOT NULL,
    reason text,
    delivery_suppressed boolean NOT NULL DEFAULT false,
    operation_id uuid,
    evidence_digest bytea CHECK (evidence_digest IS NULL OR octet_length(evidence_digest)=32),
    result_digest bytea CHECK (result_digest IS NULL OR octet_length(result_digest)=32),
    UNIQUE(instance_id, rule_id, observed_at, history_id)
);
ALTER TABLE alerting.state_history ADD COLUMN IF NOT EXISTS result_digest bytea CHECK (result_digest IS NULL OR octet_length(result_digest)=32);
DO $$ BEGIN
  ALTER TABLE alerting.state_history ADD CONSTRAINT state_history_rule_target_fk FOREIGN KEY (instance_id,rule_id) REFERENCES alerting.rule(instance_id,rule_id);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

-- Independent replay evidence: state_history is a transition audit, while
-- this ledger makes exact operation replays idempotent even after state has
-- advanced.  A digest mismatch for an existing operation is always rejected.
CREATE TABLE IF NOT EXISTS alerting.evaluation_replay
(
    operation_id uuid PRIMARY KEY,
    instance_id uuid NOT NULL,
    rule_id uuid NOT NULL,
    rule_revision bigint NOT NULL CHECK (rule_revision > 0),
    target_binding uuid NOT NULL,
    rule_binding jsonb NOT NULL,
    evidence_digest bytea NOT NULL CHECK (octet_length(evidence_digest)=32),
    result_digest bytea NOT NULL CHECK (octet_length(result_digest)=32),
    result jsonb NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (instance_id,rule_id) REFERENCES alerting.rule(instance_id,rule_id)
);
ALTER TABLE alerting.evaluation_replay ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.evaluation_replay FORCE ROW LEVEL SECURITY;
CREATE POLICY alert_replay_target_scope ON alerting.evaluation_replay USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_replay_migrator_write ON alerting.evaluation_replay FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE TABLE IF NOT EXISTS alerting.delivery_suppression_intent
(
    intent_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    delivery_id uuid NOT NULL REFERENCES alerting.delivery_outbox(delivery_id),
    instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
    operation_id uuid,
    reason text NOT NULL CHECK (octet_length(reason) BETWEEN 1 AND 64),
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE alerting.delivery_suppression_intent ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.delivery_suppression_intent FORCE ROW LEVEL SECURITY;
CREATE POLICY alert_suppression_target_scope ON alerting.delivery_suppression_intent USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_suppression_migrator_write ON alerting.delivery_suppression_intent FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
-- Equal-time samples are independent operations.  History replay identity is
-- therefore operation-scoped; timestamp/to_state must never reject a second
-- sample collected at the same instant.
DROP INDEX IF EXISTS ux_alert_history_replay;
CREATE UNIQUE INDEX IF NOT EXISTS ux_alert_history_operation
  ON alerting.state_history(instance_id, rule_id, operation_id)
  WHERE operation_id IS NOT NULL;
CREATE OR REPLACE FUNCTION alerting.reject_history_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'alert history is append-only'; END $$;
CREATE OR REPLACE TRIGGER trg_state_history_append_only BEFORE UPDATE OR DELETE ON alerting.state_history FOR EACH ROW EXECUTE FUNCTION alerting.reject_history_mutation();
CREATE OR REPLACE TRIGGER trg_delivery_attempt_append_only BEFORE UPDATE OR DELETE ON alerting.delivery_attempt FOR EACH ROW EXECUTE FUNCTION alerting.reject_history_mutation();

ALTER TABLE alerting.rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.rule FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.rule_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.rule_state FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.evaluation_queue ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.evaluation_queue FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_window ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.maintenance_window FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.destination ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.destination FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.delivery_outbox ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.delivery_outbox FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.delivery_attempt ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.delivery_attempt FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.state_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.state_history FORCE ROW LEVEL SECURITY;
ALTER TABLE alerting.admin_idempotency ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerting.admin_idempotency FORCE ROW LEVEL SECURITY;

CREATE POLICY alert_rule_target_scope ON alerting.rule USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_rule_state_target_scope ON alerting.rule_state USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_evaluation_queue_target_scope ON alerting.evaluation_queue USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_maintenance_target_scope ON alerting.maintenance_window USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_destination_target_scope ON alerting.destination USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_outbox_target_scope ON alerting.delivery_outbox USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_attempt_target_scope ON alerting.delivery_attempt USING (EXISTS (SELECT 1 FROM alerting.delivery_outbox o WHERE o.delivery_id = alerting.delivery_attempt.delivery_id AND o.instance_id::text = current_setting('sqlobserver.target_scope', true)));
CREATE POLICY alert_history_target_scope ON alerting.state_history USING (instance_id::text = current_setting('sqlobserver.target_scope', true));
CREATE POLICY alert_rule_migrator_write ON alerting.rule FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_state_migrator_write ON alerting.rule_state FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_evaluation_migrator_write ON alerting.evaluation_queue FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_maintenance_migrator_write ON alerting.maintenance_window FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_destination_migrator_write ON alerting.destination FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_outbox_migrator_write ON alerting.delivery_outbox FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_attempt_migrator_write ON alerting.delivery_attempt FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_history_migrator_write ON alerting.state_history FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);
CREATE POLICY alert_idempotency_migrator_write ON alerting.admin_idempotency FOR ALL TO sqlobserver_migrator USING (true) WITH CHECK (true);

CREATE INDEX IF NOT EXISTS ix_alert_rule_target_enabled ON alerting.rule(instance_id, enabled, rule_id);
CREATE INDEX IF NOT EXISTS ix_alert_state_active ON alerting.rule_state(instance_id, state) WHERE state IN (3,4);
DROP INDEX IF EXISTS alerting.ix_alert_maintenance_window;
CREATE INDEX ix_alert_maintenance_window ON alerting.maintenance_window(instance_id, starts_at, ends_at) WHERE cancelled_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_alert_outbox_due ON alerting.delivery_outbox(due_at, delivery_id) WHERE completed_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_alert_evaluation_due ON alerting.evaluation_queue(due_at, operation_id) WHERE completed_at IS NULL;
CREATE INDEX IF NOT EXISTS ix_alert_reconcile_metric_candidate ON telemetry.raw_metric_sample(instance_id, metric_key, observed_at DESC, sample_id DESC);

CREATE OR REPLACE VIEW reporting.active_alerts WITH (security_barrier=true) AS
SELECT s.alert_id, s.rule_id, s.instance_id AS target_id, r.name AS rule_name, s.state,
       s.first_match_at AS first_observed_at, s.fired_at, s.acknowledged_at,
       s.last_value AS value, s.reason, s.delivery_suppressed
FROM alerting.rule_state s JOIN alerting.rule r USING (rule_id, instance_id)
WHERE s.state IN (3,4);

CREATE OR REPLACE FUNCTION reporting.list_active_alerts(p_target_id uuid, p_max_results integer)
RETURNS TABLE(alert_id uuid, rule_id uuid, target_id uuid, rule_name text, state smallint, first_observed_at timestamptz, fired_at timestamptz, acknowledged_at timestamptz, value double precision, reason text, delivery_suppressed boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting, reporting AS $$
BEGIN
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'active alert page limit is outside bounds' USING ERRCODE='22023'; END IF;
  RETURN QUERY SELECT a.alert_id,a.rule_id,a.target_id,a.rule_name,a.state,a.first_observed_at,a.fired_at,a.acknowledged_at,a.value,a.reason,a.delivery_suppressed
  FROM reporting.active_alerts a WHERE a.target_id=p_target_id AND current_setting('sqlobserver.target_scope', true) IS NOT NULL AND p_target_id::text=current_setting('sqlobserver.target_scope', true) ORDER BY a.fired_at DESC NULLS LAST, a.alert_id LIMIT p_max_results;
END
$$;

CREATE OR REPLACE FUNCTION reporting.list_active_alerts(p_target_id uuid, p_max_results integer, p_after_fired timestamptz, p_after_alert_id uuid, p_snapshot_utc timestamptz)
RETURNS TABLE(alert_id uuid, rule_id uuid, target_id uuid, rule_name text, state smallint, first_observed_at timestamptz, fired_at timestamptz, acknowledged_at timestamptz, value double precision, reason text, delivery_suppressed boolean)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting, reporting AS $$
BEGIN
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'active alert page limit is outside bounds' USING ERRCODE='22023'; END IF;
  RETURN QUERY SELECT a.alert_id,a.rule_id,a.target_id,a.rule_name,a.state,a.first_observed_at,a.fired_at,a.acknowledged_at,a.value,a.reason,a.delivery_suppressed
  FROM reporting.active_alerts a
  WHERE a.target_id=p_target_id AND current_setting('sqlobserver.target_scope', true) IS NOT NULL AND p_target_id::text=current_setting('sqlobserver.target_scope', true)
    AND (p_snapshot_utc IS NULL OR coalesce(a.fired_at,a.first_observed_at)<=p_snapshot_utc)
    AND (p_after_fired IS NULL OR coalesce(a.fired_at,a.first_observed_at)<p_after_fired OR (coalesce(a.fired_at,a.first_observed_at)=p_after_fired AND a.alert_id>p_after_alert_id))
  ORDER BY coalesce(a.fired_at,a.first_observed_at) DESC,a.alert_id LIMIT p_max_results+1;
END
$$;

CREATE OR REPLACE FUNCTION alerting.list_rules(p_target_id uuid)
RETURNS TABLE(rule_id uuid,name text,kind integer,metric_id text,comparison integer,threshold double precision,hysteresis double precision,confirmation_count integer,confirmation_window interval,evaluation_interval interval,enabled boolean)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
 SELECT rule_id,name,kind::integer,metric_id,comparison::integer,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled FROM alerting.rule WHERE instance_id=p_target_id AND enabled AND current_setting('sqlobserver.target_scope', true) IS NOT NULL AND p_target_id::text=current_setting('sqlobserver.target_scope', true) ORDER BY rule_id
$$;

CREATE OR REPLACE FUNCTION alerting.sha_uuid(p_seed text)
RETURNS uuid LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog, public AS $$
  SELECT (substr(h,1,12) || '5' || substr(h,14,3) || '8' || substr(h,18,15))::uuid FROM (SELECT encode(sha256(convert_to(p_seed,'UTF8')),'hex') h) x
$$;
CREATE OR REPLACE FUNCTION alerting.canonical_operation_id(p_target_id uuid, p_rule_id uuid, p_observed_at timestamptz, p_source_identity text)
RETURNS uuid LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog, public AS $$
  SELECT alerting.sha_uuid('alert-evaluation|' || p_target_id::text || '|' || p_rule_id::text || '|' || to_char(p_observed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|' || coalesce(p_source_identity,''))
$$;
CREATE OR REPLACE FUNCTION alerting.canonical_operation_id(p_target_id uuid, p_rule_id uuid, p_observed_at timestamptz)
RETURNS uuid LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog, public AS $$
  SELECT alerting.canonical_operation_id(p_target_id,p_rule_id,p_observed_at,'')
$$;
CREATE OR REPLACE FUNCTION alerting.require_canonical_operation_uuid(p_operation text)
RETURNS uuid LANGUAGE plpgsql IMMUTABLE STRICT SET search_path = pg_catalog, public, alerting AS $$
BEGIN
  IF p_operation !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$' OR p_operation <> lower(p_operation) THEN
    RAISE EXCEPTION 'operation identity must be a canonical RFC4122 UUID' USING ERRCODE='22023';
  END IF;
  RETURN p_operation::uuid;
END $$;

CREATE OR REPLACE FUNCTION alerting.canonical_evidence_sha256(p_target_id uuid, p_rule_id uuid, p_source_kind text, p_metric_id text, p_source_collector text, p_source_version text, p_source_schema_version integer, p_source_digest text, p_observed_at timestamptz, p_sample_id text, p_run_id uuid, p_value double precision, p_collector_healthy boolean, p_reason text)
RETURNS bytea LANGUAGE sql IMMUTABLE SET search_path = pg_catalog, public AS $$
  SELECT sha256(convert_to(p_target_id::text || '|' || p_rule_id::text || '|' || coalesce(p_source_kind,'<null>') || '|' || coalesce(p_metric_id,'<null>') || '|' || coalesce(p_source_collector,'<null>') || '|' || coalesce(p_source_version,'<null>') || '|' || coalesce(p_source_schema_version::text,'<null>') || '|' || coalesce(p_source_digest,'<null>') || '|' || to_char(p_observed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|' || coalesce(p_sample_id,'<null>') || '|' || coalesce(p_run_id::text,'<null>') || '|' || coalesce(p_value::text,'<null>') || '|' || coalesce(lower(p_collector_healthy::text),'<null>') || '|' || coalesce(p_reason,'<null>'),'UTF8'))
$$;
CREATE OR REPLACE FUNCTION alerting.canonical_evidence_sha256(p_target_id uuid, p_rule_id uuid, p_observed_at timestamptz, p_value double precision, p_collector_healthy boolean, p_reason text)
RETURNS bytea LANGUAGE sql IMMUTABLE SET search_path = pg_catalog, public AS $$
  SELECT alerting.canonical_evidence_sha256(p_target_id,p_rule_id,NULL,NULL,NULL,NULL,NULL,NULL,p_observed_at,NULL,NULL,p_value,p_collector_healthy,p_reason)
$$;

-- The replay ledger stores this server-shaped document, never the caller's
-- JSON text.  The evaluator validates the fields and then compares this
-- canonical projection with the row reconstructed from rule_state; this
-- makes every state/time/actor/revision/identity field part of replay.
CREATE OR REPLACE FUNCTION alerting.canonical_decision_snapshot(p_decision jsonb)
RETURNS jsonb LANGUAGE sql IMMUTABLE STRICT SET search_path = pg_catalog, public AS $$
  SELECT jsonb_build_object(
    'Observation', jsonb_build_object(
      'TargetId', NULLIF(p_decision->'Observation'->>'TargetId','')::uuid,
      'RuleId', NULLIF(p_decision->'Observation'->>'RuleId','')::uuid,
      'ObservedAtUtc', NULLIF(p_decision->'Observation'->>'ObservedAtUtc','')::timestamptz,
      'Value', NULLIF(p_decision->'Observation'->>'Value','')::double precision,
      'CollectorHealthy', NULLIF(p_decision->'Observation'->>'CollectorHealthy','')::boolean,
      'Reason', p_decision->'Observation'->>'Reason',
      'SampleId', p_decision->'Observation'->>'SampleId',
      'RunId', NULLIF(p_decision->'Observation'->>'RunId','')::uuid,
      'SourceKind', p_decision->'Observation'->>'SourceKind',
      'MetricId', p_decision->'Observation'->>'MetricId',
      'SourceCollector', p_decision->'Observation'->>'SourceCollector',
      'SourceVersion', p_decision->'Observation'->>'SourceVersion',
      'SourceSchemaVersion', NULLIF(p_decision->'Observation'->>'SourceSchemaVersion','')::integer,
      'SourceDigest', p_decision->'Observation'->>'SourceDigest',
      'OperationId', NULLIF(p_decision->'Observation'->>'OperationId','')::uuid,
      'EvidenceDigest', p_decision->'Observation'->>'EvidenceDigest'),
    'State', jsonb_build_object(
      'RuleId', NULLIF(p_decision->'State'->>'RuleId','')::uuid,
      'TargetId', NULLIF(p_decision->'State'->>'TargetId','')::uuid,
      'State', NULLIF(p_decision->'State'->>'State','')::smallint,
      'ConsecutiveMatches', NULLIF(p_decision->'State'->>'ConsecutiveMatches','')::integer,
      'FirstMatchUtc', NULLIF(p_decision->'State'->>'FirstMatchUtc','')::timestamptz,
      'LastObservedUtc', NULLIF(p_decision->'State'->>'LastObservedUtc','')::timestamptz,
      'FiredUtc', NULLIF(p_decision->'State'->>'FiredUtc','')::timestamptz,
      'AcknowledgedUtc', NULLIF(p_decision->'State'->>'AcknowledgedUtc','')::timestamptz,
      'ResolvedUtc', NULLIF(p_decision->'State'->>'ResolvedUtc','')::timestamptz,
      'EpisodeStartedUtc', NULLIF(p_decision->'State'->>'EpisodeStartedUtc','')::timestamptz,
      'AlertId', NULLIF(p_decision->'State'->>'AlertId','')::uuid,
      'EpisodeId', NULLIF(p_decision->'State'->>'EpisodeId','')::uuid,
      'LastOperationId', NULLIF(p_decision->'State'->>'LastOperationId','')::uuid,
      'EvidenceDigest', p_decision->'State'->>'EvidenceDigest',
      'Reason', p_decision->'State'->>'Reason',
      'LastReason', p_decision->'State'->>'LastReason',
      'DeliverySuppressed', coalesce((p_decision->'State'->>'DeliverySuppressed')::boolean,false),
      'AcknowledgedBy', p_decision->'State'->>'AcknowledgedBy',
      'Revision', NULLIF(p_decision->'State'->>'Revision','')::bigint,
      'LastValue', NULLIF(p_decision->'State'->>'LastValue','')::double precision),
    'Event', NULLIF(p_decision->>'Event','')::smallint,
    'DeliverySuppressed', coalesce((p_decision->>'DeliverySuppressed')::boolean,false),
    'Reason', p_decision->>'Reason')
$$;

CREATE OR REPLACE FUNCTION alerting.list_due_evaluations(p_max_results integer)
RETURNS TABLE(operation_id uuid, observations jsonb, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
BEGIN
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation queue limit is outside bounds' USING ERRCODE='22023'; END IF;
  -- Reconcile immutable collector evidence into the durable queue once. A
  -- repeated poll only claims queue rows; it never rescans old observations.
  INSERT INTO alerting.evaluation_queue(operation_id,instance_id,rule_id,observed_at,observations,due_at,next_due_at,evidence_digest,sample_id,run_id,rule_revision)
  SELECT q.operation_id,q.instance_id,q.rule_id,q.observed_at,q.observations,clock_timestamp(),q.observed_at+q.evaluation_interval,q.evidence_digest,q.sample_id,q.run_id,q.rule_revision
  FROM (
    SELECT alerting.canonical_operation_id(r.instance_id,r.rule_id,e.observed_at,e.reason) operation_id,
      r.instance_id,r.rule_id,e.observed_at,r.evaluation_interval,e.sample_id,e.run_id,r.revision AS rule_revision,
      jsonb_build_array(jsonb_build_object('TargetId',r.instance_id,'RuleId',r.rule_id,'ObservedAtUtc',e.observed_at,'Value',e.metric_value,'CollectorHealthy',e.collector_healthy,'Reason',e.reason,'SampleId',e.sample_id,'RunId',e.run_id,'SourceKind',e.source_kind,'MetricId',e.metric_id,'SourceCollector',e.source_collector,'SourceVersion',e.source_version,'SourceSchemaVersion',e.source_schema_version,'SourceDigest',e.source_digest,'OperationId',alerting.canonical_operation_id(r.instance_id,r.rule_id,e.observed_at,e.reason),'EvidenceDigest',encode(alerting.canonical_evidence_sha256(r.instance_id,r.rule_id,e.source_kind,e.metric_id,e.source_collector,e.source_version,e.source_schema_version,e.source_digest,e.observed_at,e.sample_id,e.run_id,e.metric_value,e.collector_healthy,e.reason),'hex'))) observations,
      alerting.canonical_evidence_sha256(r.instance_id,r.rule_id,e.source_kind,e.metric_id,e.source_collector,e.source_version,e.source_schema_version,e.source_digest,e.observed_at,e.sample_id,e.run_id,e.metric_value,e.collector_healthy,e.reason) evidence_digest
    FROM alerting.rule r JOIN LATERAL (
      SELECT m.observed_at AS observed_at,m.sample_id AS sample_id,m.collection_run_id AS run_id,m.metric_value AS metric_value,NULL::boolean AS collector_healthy,'observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text AS reason,'metric_threshold'::text AS source_kind,r.metric_id::text AS metric_id,cr.collector_id AS source_collector,cr.collector_version::text AS source_version,cr.output_schema_version AS source_schema_version,encode(co.completion_digest,'hex') AS source_digest,r.instance_id::text || '|' || r.rule_id::text || '|' || to_char(m.observed_at AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|' || coalesce(m.metric_value::text,'null') || '|null|observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text AS evidence_text FROM telemetry.raw_metric_sample m JOIN telemetry.collection_run cr ON cr.run_id=m.collection_run_id JOIN telemetry.collection_run_outcome co ON co.run_id=cr.run_id WHERE r.kind=1 AND m.collection_run_id IS NOT NULL AND m.instance_id=r.instance_id AND m.metric_key=r.metric_id AND co.outcome IN ('succeeded','partial') AND co.completion_digest IS NOT NULL AND cr.collector_id='engine.core' AND cr.output_schema_version=1 AND m.observed_at>=clock_timestamp()-interval '7 days' AND NOT EXISTS (SELECT 1 FROM alerting.evaluation_queue oldq WHERE oldq.operation_id=alerting.canonical_operation_id(r.instance_id,r.rule_id,m.observed_at,'observed|sample=' || m.sample_id::text || '|run=' || m.collection_run_id::text))
      UNION ALL SELECT CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END AS observed_at,alerting.sha_uuid('collector-health|' || h.collector_id || '|' || h.collector_version::text || '|' || h.schedule_revision::text || '|' || h.schedule_target_revision::text || '|' || h.run_id::text || '|' || coalesce(h.completed_at::text,'') || '|' || coalesce(h.outcome,'') || '|' || h.health_state || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint) AS sample_id,h.run_id,NULL::double precision AS metric_value,(h.health_state='current' AND coh.outcome IN ('succeeded','partial')) AS collector_healthy,'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'') || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint AS reason,'collector_health'::text AS source_kind,NULL::text AS metric_id,h.collector_id AS source_collector,h.collector_version::text AS source_version,h.output_schema_version AS source_schema_version,encode(coh.completion_digest,'hex') AS source_digest,r.instance_id::text || '|' || r.rule_id::text || '|' || to_char((CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END) AT TIME ZONE 'UTC','YYYY-MM-DD"T"HH24:MI:SS.US"Z"') || '|null|' || CASE WHEN h.health_state='current' THEN 'true' ELSE 'false' END || '|collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'' ) || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint AS evidence_text FROM reporting.collector_health_projection h JOIN telemetry.collection_run crh ON crh.run_id=h.run_id JOIN telemetry.collection_run_outcome coh ON coh.run_id=crh.run_id WHERE r.kind=2 AND h.run_id IS NOT NULL AND h.collector_id='engine.core' AND crh.collector_id='engine.core' AND h.collector_id=crh.collector_id AND h.collector_version=crh.collector_version AND h.output_schema_version=crh.output_schema_version AND crh.collector_id='engine.core' AND crh.output_schema_version=1 AND coh.outcome IN ('succeeded','partial','timed_out','transient_failure','permanent_failure','permission_denied','unsupported','output_invalid','lease_lost','circuit_open') AND coh.completion_digest IS NOT NULL AND h.instance_id=r.instance_id AND coalesce(h.completed_at,h.repository_time)>=clock_timestamp()-interval '7 days' AND NOT EXISTS (SELECT 1 FROM alerting.evaluation_queue oldq WHERE oldq.operation_id=alerting.canonical_operation_id(r.instance_id,r.rule_id,CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END,'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'' ) || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from r.evaluation_interval))::bigint))
      -- Keep every equal-time source sample; operation_id includes the sample
      -- identity carried in reason, so no timestamp-only collapse occurs.
      ORDER BY observed_at ASC, sample_id ASC, run_id ASC NULLS LAST, operation_id ASC) e ON true WHERE r.enabled AND (current_setting('sqlobserver.target_scope',true) IS NULL OR r.instance_id::text=current_setting('sqlobserver.target_scope',true)) AND (r.kind=1 OR EXISTS (SELECT 1 FROM reporting.collector_health_projection hp WHERE hp.instance_id=r.instance_id AND hp.collector_id='engine.core'))
  ) q ORDER BY q.observed_at ASC,q.sample_id ASC,q.run_id ASC NULLS LAST,q.operation_id ASC LIMIT p_max_results ON CONFLICT(operation_id) DO NOTHING;
  -- Reconciliation is insert-only. Lease metadata is written exclusively by
  -- the target-scoped fenced claim function below.
  RETURN QUERY SELECT q.operation_id,q.observations,q.due_at FROM alerting.evaluation_queue q
    WHERE q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())
    ORDER BY q.observed_at,q.sample_id,q.run_id NULLS LAST,q.operation_id LIMIT p_max_results;
END;
$$;

-- Queue claims are always fenced to the collector lease. The legacy polling
-- function above is retained for migration compatibility but is not granted
-- to the runtime collector role.
CREATE OR REPLACE FUNCTION alerting.claim_due_evaluations(p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(operation_id uuid, observations jsonb, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
BEGIN
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing <= 0 OR p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'delivery claim arguments are invalid' USING ERRCODE='22023'; END IF;
  IF p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing <= 0 OR p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation queue claim arguments are invalid' USING ERRCODE='22023'; END IF;
  -- Reconciliation is insert-only by canonical operation identity. Once a
  -- row exists, this path never rereads it as a new observation.
  PERFORM alerting.list_due_evaluations(p_max_results);
  RETURN QUERY UPDATE alerting.evaluation_queue q
    SET claimed_until=clock_timestamp()+interval '30 seconds', claim_work_key=p_work_key, claim_owner_execution_id=p_owner_execution_id, claim_fencing=p_fencing, completed_at=NULL
    WHERE q.operation_id IN (SELECT x.operation_id FROM alerting.evaluation_queue x WHERE (current_setting('sqlobserver.target_scope',true) IS NULL OR x.instance_id::text=current_setting('sqlobserver.target_scope',true)) AND x.completed_at IS NULL AND x.due_at<=clock_timestamp() AND (x.claimed_until IS NULL OR x.claimed_until<clock_timestamp() OR x.claim_work_key IS NULL) ORDER BY x.observed_at,x.sample_id,x.run_id NULLS LAST,x.operation_id FOR UPDATE SKIP LOCKED LIMIT p_max_results)
    RETURNING q.operation_id,q.observations,q.due_at;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END;
$$;
CREATE OR REPLACE FUNCTION alerting.get_rule_state(p_target_id uuid, p_rule_id uuid)
RETURNS TABLE(rule_id uuid, instance_id uuid, state integer, consecutive_matches integer, first_match_at timestamptz, last_observed_at timestamptz, fired_at timestamptz, acknowledged_at timestamptz, resolved_at timestamptz, episode_started_at timestamptz, alert_id uuid, alert_episode_id uuid, last_operation_id uuid, evidence_digest bytea, reason text, last_reason text, delivery_suppressed boolean, acknowledged_by text, revision bigint, last_value double precision)
LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
 SELECT s.rule_id,s.instance_id,s.state::integer,s.consecutive_matches,s.first_match_at,s.last_observed_at,s.fired_at,s.acknowledged_at,s.resolved_at,s.episode_started_at,s.alert_id,s.alert_episode_id,s.last_operation_id,s.evidence_digest,s.reason,s.last_reason,s.delivery_suppressed,s.acknowledged_by,s.revision,s.last_value FROM alerting.rule_state s WHERE s.instance_id=p_target_id AND s.rule_id=p_rule_id AND current_setting('sqlobserver.target_scope', true) IS NOT NULL AND p_target_id::text=current_setting('sqlobserver.target_scope', true)
$$;

CREATE OR REPLACE FUNCTION alerting.claim_due_deliveries(p_fencing bigint, p_max_results integer)
RETURNS TABLE(delivery_id uuid, alert_id uuid, destination_id uuid, kind text, configuration_reference text, payload bytea, attempt integer, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
 IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'delivery claim arguments are invalid' USING ERRCODE='22023'; END IF;
 RAISE EXCEPTION 'fenced delivery claim is required' USING ERRCODE='42501';
END $$;

CREATE OR REPLACE FUNCTION alerting.maintenance_active(p_target_id uuid, p_at timestamptz)
RETURNS boolean LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
  IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE='42501'; END IF;
  RETURN EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND p_at>=m.starts_at AND p_at<m.ends_at);
END $$;

CREATE OR REPLACE FUNCTION alerting.maintenance_active_unscoped(p_target_id uuid, p_at timestamptz)
RETURNS boolean LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
  SELECT EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND p_at>=m.starts_at AND p_at<m.ends_at)
$$;

CREATE OR REPLACE FUNCTION alerting.claim_due_deliveries(p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(delivery_id uuid, alert_id uuid, destination_id uuid, target_id uuid, kind text, configuration_reference text, payload bytea, attempt integer, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing <= 0 OR p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'delivery claim arguments are invalid' USING ERRCODE='22023'; END IF;
  -- Four concurrent sends at the bounded ten-second adapter deadline and a
  -- 100-row claim require at most 250 seconds; retain a safety margin.
  RETURN QUERY UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes',lease_fencing=p_fencing,lease_work_key=p_work_key,lease_owner_execution_id=p_owner_execution_id
     WHERE d.delivery_id IN (SELECT x.delivery_id FROM alerting.delivery_outbox x JOIN alerting.destination z ON z.destination_id=x.destination_id AND z.instance_id=x.instance_id AND z.enabled AND z.approved AND z.approval_scope=x.destination_approval_scope AND z.approved_kind=x.destination_kind AND z.approved_configuration_reference=x.destination_configuration_reference AND z.approved_revision=x.destination_approval_revision AND z.approval_digest=x.destination_approval_digest AND z.revision=x.destination_revision WHERE (current_setting('sqlobserver.target_scope',true) IS NULL OR x.instance_id::text=current_setting('sqlobserver.target_scope',true)) AND x.completed_at IS NULL AND x.cancelled_at IS NULL AND x.attempt <= 7 AND x.due_at<=clock_timestamp() AND (x.leased_until IS NULL OR x.leased_until<clock_timestamp()) AND NOT alerting.maintenance_active_unscoped(x.instance_id,clock_timestamp()) ORDER BY x.due_at,x.delivery_id FOR UPDATE OF x SKIP LOCKED LIMIT LEAST(GREATEST(p_max_results,1),100))
    RETURNING d.delivery_id,d.alert_id,d.destination_id,d.instance_id,(SELECT x.kind FROM alerting.destination x WHERE x.destination_id=d.destination_id),(SELECT x.configuration_reference FROM alerting.destination x WHERE x.destination_id=d.destination_id),convert_to(d.payload::text,'UTF8'),d.attempt,d.due_at;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

CREATE OR REPLACE FUNCTION alerting.claim_due_deliveries(p_instance_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(delivery_id uuid, alert_id uuid, destination_id uuid, target_id uuid, kind text, configuration_reference text, payload bytea, attempt integer, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  RETURN QUERY SELECT * FROM alerting.claim_due_deliveries(p_work_key,p_owner_execution_id,p_fencing,p_max_results);
END $$;

CREATE OR REPLACE FUNCTION alerting.complete_delivery(p_delivery_id uuid, p_succeeded boolean, p_permanent boolean, p_reason text, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
 RAISE EXCEPTION 'fenced completion is required' USING ERRCODE='42501';
END $$;

CREATE OR REPLACE FUNCTION alerting.complete_delivery(p_delivery_id uuid, p_succeeded boolean, p_permanent boolean, p_reason text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE result boolean; updated integer;
BEGIN
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  INSERT INTO alerting.delivery_attempt(delivery_id,attempt,outcome,response_code,response_bytes,error_code) SELECT p_delivery_id,attempt+1,CASE WHEN p_succeeded THEN 'succeeded' WHEN p_permanent OR attempt >= 7 OR clock_timestamp() >= created_at+interval '24 hours' THEN 'permanent_failure' ELSE 'retryable_failure' END,NULL,NULL,left(p_reason,64) FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND lease_work_key=p_work_key AND lease_owner_execution_id=p_owner_execution_id AND lease_fencing=p_fencing AND leased_until>clock_timestamp() AND completed_at IS NULL AND cancelled_at IS NULL ON CONFLICT(delivery_id,attempt) DO NOTHING;
  UPDATE alerting.delivery_outbox SET completed_at=CASE WHEN p_succeeded OR p_permanent OR attempt >= 7 OR clock_timestamp() >= created_at+interval '24 hours' THEN clock_timestamp() ELSE NULL END, attempt=CASE WHEN p_succeeded OR p_permanent OR attempt >= 7 OR clock_timestamp() >= created_at+interval '24 hours' THEN attempt ELSE attempt+1 END, due_at=CASE WHEN p_succeeded OR p_permanent OR attempt >= 7 OR clock_timestamp() >= created_at+interval '24 hours' THEN due_at ELSE clock_timestamp()+CASE attempt WHEN 0 THEN interval '5 seconds' WHEN 1 THEN interval '30 seconds' WHEN 2 THEN interval '5 minutes' WHEN 3 THEN interval '30 minutes' WHEN 4 THEN interval '2 hours' WHEN 5 THEN interval '6 hours' WHEN 6 THEN interval '12 hours' ELSE interval '24 hours' END END,last_error_code=CASE WHEN p_succeeded THEN NULL ELSE left(p_reason,64) END,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE delivery_id=p_delivery_id AND lease_work_key=p_work_key AND lease_owner_execution_id=p_owner_execution_id AND lease_fencing=p_fencing AND leased_until>clock_timestamp() AND completed_at IS NULL AND cancelled_at IS NULL;
  GET DIAGNOSTICS updated=ROW_COUNT; result := updated=1;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN result;
END $$;

CREATE OR REPLACE FUNCTION alerting.renew_delivery(p_delivery_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer;
BEGIN
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes'
    WHERE d.delivery_id=p_delivery_id AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp() AND d.completed_at IS NULL AND d.cancelled_at IS NULL
      AND EXISTS (SELECT 1 FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=d.instance_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision)
      AND EXISTS (SELECT 1 FROM alerting.rule r WHERE r.rule_id=d.rule_id AND r.instance_id=d.instance_id AND r.enabled)
      AND NOT alerting.maintenance_active_unscoped(d.instance_id,clock_timestamp());
  GET DIAGNOSTICS changed=ROW_COUNT;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.cancel_delivery(p_delivery_id uuid, p_reason text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer;
BEGIN
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  UPDATE alerting.delivery_outbox SET cancelled_at=clock_timestamp(),cancel_reason=left(p_reason,64),completed_at=clock_timestamp(),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE delivery_id=p_delivery_id AND lease_work_key=p_work_key AND lease_owner_execution_id=p_owner_execution_id AND lease_fencing=p_fencing AND leased_until>clock_timestamp() AND completed_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.cancel_delivery_admin(p_delivery_id uuid, p_target_id uuid, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_reason text, p_request_digest text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; changed integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'delivery target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='cancel_delivery' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_delivery_id::text || p_target_id::text || coalesce(p_reason,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason=left(coalesce(p_reason,'administrative_cancel'),64),completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND completed_at IS NULL AND cancelled_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT; IF changed=0 THEN RAISE EXCEPTION 'delivery was already completed, cancelled, or target-bound incorrectly' USING ERRCODE='40001'; END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,audit_id,recorded_at) VALUES('cancel_delivery',p_idempotency_key,sha256(convert_to(p_delivery_id::text || p_target_id::text || coalesce(p_reason,'') || coalesce(p_request_digest,''),'UTF8')),p_target_id,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,'alert.delivery.cancel','allowed','succeeded',p_correlation_id,jsonb_build_object('deliveryId',p_delivery_id));
  RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.is_delivery_suppressed(p_delivery_id uuid)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
DECLARE changed integer;
BEGIN
  UPDATE alerting.delivery_outbox d SET due_at=COALESCE((SELECT max(m.ends_at) FROM alerting.maintenance_window m WHERE m.instance_id=d.instance_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at),clock_timestamp()),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
    WHERE d.delivery_id=p_delivery_id AND EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.instance_id=d.instance_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at) AND d.completed_at IS NULL AND d.cancelled_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT;
  RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.get_maintenance_window(p_target_id uuid, p_at_utc timestamptz)
RETURNS TABLE(window_id uuid, instance_id uuid, starts_at timestamptz, ends_at timestamptz, reason text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
  IF current_setting('sqlobserver.target_scope', true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE='42501'; END IF;
  RETURN QUERY
  SELECT m.window_id,m.instance_id,m.starts_at,m.ends_at,m.reason FROM alerting.maintenance_window m
  WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND m.starts_at<=p_at_utc AND m.ends_at>p_at_utc
  ORDER BY m.starts_at DESC,m.window_id DESC LIMIT 1
;
END $$;

-- M8 denial audit entry point. This extension is owned by this migration so the
-- legacy 0007 allowlist and checksum remain immutable.
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
     OR p_action NOT IN ('register_observation_target','update_observation_target','retire_observation_target','request_capability_rediscovery','alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
     OR p_reason NOT IN ('principal_disabled','required_role_missing','target_out_of_scope','already_exists','revision_conflict','target_not_found','target_retired','discovery_failed','repository_failure','invalid_request','idempotent_replay') THEN
    RAISE EXCEPTION 'invalid M8 denial audit fields' USING ERRCODE='22023';
  END IF;
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
  VALUES(captured_repository_time,generated_activity_id,'user',p_actor_identifier,CASE p_action WHEN 'register_observation_target' THEN 'observation_target.register' WHEN 'update_observation_target' THEN 'observation_target.update' WHEN 'retire_observation_target' THEN 'observation_target.retire' WHEN 'request_capability_rediscovery' THEN 'capability.rediscovery_request' ELSE p_action END,'denied','rejected',CASE WHEN p_action LIKE 'alert.%' THEN 'alerting' ELSE 'observation_target' END,p_target_id::text,p_correlation_id,jsonb_build_object('reason',p_reason));
  RETURN QUERY SELECT generated_activity_id,captured_repository_time;
END $$;

-- M8 outcome audit entry point. Successful mutations remain audited by their
-- transactional mutation function; this wrapper is used for bounded failure
-- and conflict outcomes (and is independently usable by the audit port).
ALTER TABLE audit.activity DROP CONSTRAINT IF EXISTS ck_audit_outcome;
ALTER TABLE audit.activity ADD CONSTRAINT ck_audit_outcome CHECK (outcome IN ('succeeded','failed','conflict','cancelled','rejected'));
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
     OR p_action NOT IN ('alert.rule.create','alert.rule.update','alert.rule.retire','alert.maintenance.create','alert.maintenance.update','alert.maintenance.retire','alert.acknowledge','alert.destination.configure','alert.destination.approve','alert.destination.update','alert.destination.retire','alert.delivery.cancel')
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

CREATE OR REPLACE FUNCTION alerting.upsert_rule(p_rule jsonb, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid; t timestamptz; existing_digest bytea; updated integer; id uuid := (p_rule->>'RuleId')::uuid; target uuid := (p_rule->>'TargetId')::uuid;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR target::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'rule target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(target::text,0));
  IF EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id<>target) THEN RAISE EXCEPTION 'rule identity target mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_rule' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_rule::text,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_rule->>'Action'='CreateAlertRule' AND EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id=target) THEN RAISE EXCEPTION 'rule already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_rule->>'Action'<>'CreateAlertRule' AND NOT EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND instance_id=target) THEN RAISE EXCEPTION 'rule does not exist' USING ERRCODE='P0002'; END IF;
  IF p_rule->>'Action'='UpdateAlertRule' AND (p_rule->>'ExpectedRevision') IS NULL THEN RAISE EXCEPTION 'update requires expected revision' USING ERRCODE='22023'; END IF;
  IF p_rule->>'CatalogDigest' IS DISTINCT FROM (SELECT c.catalog_digest FROM alerting.catalog_registry c WHERE c.catalog_id='sqlobserver.m8.alerts') THEN RAISE EXCEPTION 'alert catalog digest drift detected' USING ERRCODE='55000'; END IF;
  IF p_rule->>'SourceCollector' IS DISTINCT FROM 'engine.core' OR (p_rule->>'SourceSchemaVersion')::integer IS DISTINCT FROM 1 OR NOT ((p_rule->>'Name'='metric.threshold' AND (p_rule->>'Kind')::smallint=1 AND p_rule->>'MetricId'='engine.user_connections' AND (p_rule->>'Comparison')::smallint=2 AND (p_rule->>'Threshold')::double precision=1 AND (p_rule->>'Hysteresis')::double precision=0 AND (p_rule->>'ConfirmationCount')::integer=1 AND (p_rule->>'ConfirmationWindow')::interval=interval '0 seconds' AND (p_rule->>'EvaluationInterval')::interval=interval '15 seconds') OR (p_rule->>'Name'='collector.health' AND (p_rule->>'Kind')::smallint=2 AND COALESCE(p_rule->>'MetricId','')='' AND (p_rule->>'Comparison')::smallint=3 AND (p_rule->>'Threshold')::double precision=1 AND (p_rule->>'Hysteresis')::double precision=0 AND (p_rule->>'ConfirmationCount')::integer=2 AND (p_rule->>'ConfirmationWindow')::interval=interval '5 minutes' AND (p_rule->>'EvaluationInterval')::interval=interval '30 seconds')) THEN RAISE EXCEPTION 'alert rule is outside the approved catalog contract' USING ERRCODE='22023'; END IF;
  IF (p_rule->>'ExpectedRevision') IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.rule WHERE rule_id=id AND revision<>(p_rule->>'ExpectedRevision')::bigint) THEN RAISE EXCEPTION 'rule revision conflict' USING ERRCODE='40001'; END IF;
  IF p_rule->>'Action'='CreateAlertRule' THEN
    INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled)
    VALUES(id,target,p_rule->>'Name',(p_rule->>'Kind')::smallint,NULLIF(p_rule->>'MetricId','')::text,(p_rule->>'Comparison')::smallint,(p_rule->>'Threshold')::double precision,(p_rule->>'Hysteresis')::double precision,(p_rule->>'ConfirmationCount')::integer,(p_rule->>'ConfirmationWindow')::interval,(p_rule->>'EvaluationInterval')::interval,COALESCE((p_rule->>'Enabled')::boolean,true))
    ON CONFLICT(rule_id) DO NOTHING;
  ELSE
    INSERT INTO alerting.rule(rule_id,instance_id,name,kind,metric_id,comparison,threshold,hysteresis,confirmation_count,confirmation_window,evaluation_interval,enabled)
    VALUES(id,target,p_rule->>'Name',(p_rule->>'Kind')::smallint,NULLIF(p_rule->>'MetricId','')::text,(p_rule->>'Comparison')::smallint,(p_rule->>'Threshold')::double precision,(p_rule->>'Hysteresis')::double precision,(p_rule->>'ConfirmationCount')::integer,(p_rule->>'ConfirmationWindow')::interval,(p_rule->>'EvaluationInterval')::interval,COALESCE((p_rule->>'Enabled')::boolean,true))
    ON CONFLICT(rule_id) DO UPDATE SET name=EXCLUDED.name,kind=EXCLUDED.kind,metric_id=EXCLUDED.metric_id,comparison=EXCLUDED.comparison,threshold=EXCLUDED.threshold,hysteresis=EXCLUDED.hysteresis,confirmation_count=EXCLUDED.confirmation_count,confirmation_window=EXCLUDED.confirmation_window,evaluation_interval=EXCLUDED.evaluation_interval,enabled=EXCLUDED.enabled,updated_at=clock_timestamp(),revision=alerting.rule.revision+1 WHERE alerting.rule.instance_id=target AND alerting.rule.revision=(p_rule->>'ExpectedRevision')::bigint;
  END IF;
  GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'rule revision conflict' USING ERRCODE='40001'; END IF;
  a := gen_random_uuid(); t := clock_timestamp();
  IF COALESCE((p_rule->>'Enabled')::boolean,true)=false THEN
    INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,delivery_suppressed,operation_id) SELECT instance_id,rule_id,alert_id,state,5,t,'rule_disabled',false,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid ELSE gen_random_uuid() END FROM alerting.rule_state WHERE instance_id=target AND rule_id=id AND state IN (3,4);
    UPDATE alerting.rule_state SET state=5,resolved_at=t,delivery_suppressed=false,reason='rule_disabled',last_reason='rule_disabled',revision=revision+1 WHERE instance_id=target AND rule_id=id AND state IN (3,4);
    UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason='rule_disabled',completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE instance_id=target AND rule_id=id AND completed_at IS NULL AND cancelled_at IS NULL;
    UPDATE alerting.evaluation_queue SET completed_at=t,claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=NULL WHERE instance_id=target AND rule_id=id AND completed_at IS NULL;
  ELSIF p_rule->>'Action'='UpdateAlertRule' THEN
    UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason='rule_revision_changed',completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE instance_id=target AND rule_id=id AND completed_at IS NULL AND cancelled_at IS NULL;
  END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_rule',p_idempotency_key,sha256(convert_to(p_rule::text,'UTF8')),target,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,NULLIF(p_rule->>'ExpectedRevision','')::bigint,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,CASE WHEN COALESCE((p_rule->>'Enabled')::boolean,true)=false THEN 'alert.rule.retire' WHEN (p_rule->>'ExpectedRevision') IS NULL THEN 'alert.rule.create' ELSE 'alert.rule.update' END,'allowed','succeeded','alert_rule',id::text,p_correlation_id,'{}');
  RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.upsert_maintenance(p_window jsonb, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; updated integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR (p_window->>'TargetId') <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE='42501'; END IF;
  -- Serialize maintenance transitions with the collector's session-scoped
  -- dispatch permit for this target. The permit remains held across the
  -- adapter side effect; this transaction therefore cannot start a window
  -- between the final readiness check and the send.
  PERFORM pg_advisory_xact_lock(hashtextextended(p_window->>'TargetId',0));
  IF EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id<>(p_window->>'TargetId')::uuid) THEN RAISE EXCEPTION 'maintenance identity target mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_maintenance' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_window::text,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_window->>'Action'='CreateMaintenanceWindow' AND EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id=(p_window->>'TargetId')::uuid) THEN RAISE EXCEPTION 'maintenance window already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_window->>'Action'<>'CreateMaintenanceWindow' AND NOT EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id=(p_window->>'TargetId')::uuid) THEN RAISE EXCEPTION 'maintenance window does not exist' USING ERRCODE='P0002'; END IF;
  IF p_window->>'Action'<>'CreateMaintenanceWindow' AND EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND instance_id=(p_window->>'TargetId')::uuid AND cancelled_at IS NOT NULL) THEN RAISE EXCEPTION 'cancelled maintenance window cannot be updated or reactivated' USING ERRCODE='40001'; END IF;
  IF p_window->>'Action'='UpdateMaintenanceWindow' AND (p_window->>'ExpectedRevision') IS NULL THEN RAISE EXCEPTION 'update requires expected revision' USING ERRCODE='22023'; END IF;
  IF (p_window->>'ExpectedRevision') IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.maintenance_window WHERE window_id=(p_window->>'Id')::uuid AND revision<>(p_window->>'ExpectedRevision')::bigint) THEN RAISE EXCEPTION 'maintenance revision conflict' USING ERRCODE='40001'; END IF;
  IF p_window->>'Action'='CreateMaintenanceWindow' THEN
    INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason) VALUES((p_window->>'Id')::uuid,(p_window->>'TargetId')::uuid,(p_window->>'StartsAtUtc')::timestamptz,(p_window->>'EndsAtUtc')::timestamptz,p_window->>'Reason') ON CONFLICT(window_id) DO NOTHING;
  ELSE
    INSERT INTO alerting.maintenance_window(window_id,instance_id,starts_at,ends_at,reason) VALUES((p_window->>'Id')::uuid,(p_window->>'TargetId')::uuid,(p_window->>'StartsAtUtc')::timestamptz,(p_window->>'EndsAtUtc')::timestamptz,p_window->>'Reason') ON CONFLICT(window_id) DO UPDATE SET starts_at=EXCLUDED.starts_at,ends_at=EXCLUDED.ends_at,reason=EXCLUDED.reason,revision=alerting.maintenance_window.revision+1 WHERE alerting.maintenance_window.instance_id=(p_window->>'TargetId')::uuid AND alerting.maintenance_window.revision=(p_window->>'ExpectedRevision')::bigint;
  END IF;
  GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'maintenance revision conflict' USING ERRCODE='40001'; END IF;
  IF (p_window->>'StartsAtUtc')::timestamptz <= t AND (p_window->>'EndsAtUtc')::timestamptz > t THEN
    INSERT INTO alerting.delivery_suppression_intent(delivery_id,instance_id,operation_id,reason)
      SELECT d.delivery_id,d.instance_id,d.operation_id,'maintenance_started:' || (p_window->>'Id')
      FROM alerting.delivery_outbox d
      WHERE d.instance_id=(p_window->>'TargetId')::uuid AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.due_at < (p_window->>'EndsAtUtc')::timestamptz;
    UPDATE alerting.delivery_outbox SET leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL,due_at=GREATEST(due_at,(p_window->>'EndsAtUtc')::timestamptz)
      WHERE instance_id=(p_window->>'TargetId')::uuid AND completed_at IS NULL AND cancelled_at IS NULL;
  END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_maintenance',p_idempotency_key,sha256(convert_to(p_window::text,'UTF8')),(p_window->>'TargetId')::uuid,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,NULLIF(p_window->>'ExpectedRevision','')::bigint,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,CASE WHEN (p_window->>'ExpectedRevision') IS NULL THEN 'alert.maintenance.create' ELSE 'alert.maintenance.update' END,'allowed','succeeded',p_correlation_id,'{}'); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.acknowledge(p_alert_id uuid, p_target_id uuid, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_expected_episode_id uuid, p_request_digest text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; changed integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'acknowledgement target scope mismatch' USING ERRCODE='42501'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='acknowledge' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_alert_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_expected_episode_id::text,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'acknowledgement revision is invalid' USING ERRCODE='22023'; END IF;
  IF NOT EXISTS (SELECT 1 FROM alerting.rule_state WHERE alert_id=p_alert_id AND instance_id=p_target_id) THEN RAISE EXCEPTION 'alert target mismatch'; END IF;
  IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.rule_state s WHERE s.alert_id=p_alert_id AND s.instance_id=p_target_id AND s.revision<>p_expected_revision) THEN RAISE EXCEPTION 'acknowledgement revision conflict' USING ERRCODE='40001'; END IF;
  IF p_expected_episode_id IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.rule_state s WHERE s.alert_id=p_alert_id AND s.instance_id=p_target_id AND s.alert_episode_id IS DISTINCT FROM p_expected_episode_id) THEN RAISE EXCEPTION 'acknowledgement episode conflict' USING ERRCODE='40001'; END IF;
  UPDATE alerting.rule_state SET state=4,acknowledged_at=t,acknowledged_by=p_actor_sid,revision=revision+1 WHERE alert_id=p_alert_id AND instance_id=p_target_id AND state=3 AND (p_expected_revision IS NULL OR revision=p_expected_revision) AND (p_expected_episode_id IS NULL OR alert_episode_id=p_expected_episode_id);
  GET DIAGNOSTICS changed=ROW_COUNT; IF changed=0 THEN RAISE EXCEPTION 'alert acknowledgement was already applied or revision-conflicted' USING ERRCODE='40001'; END IF;
  INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,operation_id) SELECT instance_id,rule_id,alert_id,3,4,t,'acknowledged',CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END FROM alerting.rule_state WHERE alert_id=p_alert_id AND instance_id=p_target_id AND acknowledged_at=t;
  INSERT INTO alerting.delivery_outbox(delivery_id,alert_id,instance_id,rule_id,destination_id,event_kind,payload,due_at,operation_id,destination_revision,destination_kind,destination_configuration_reference,destination_approval_revision,destination_approval_digest,destination_approval_scope,destination_configuration_digest) SELECT gen_random_uuid(),s.alert_id,s.instance_id,s.rule_id,d.destination_id,3,jsonb_build_object('schemaVersion',1,'alertId',s.alert_id,'targetId',s.instance_id,'event','acknowledged'),t,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,d.revision,d.kind,d.configuration_reference,d.approved_revision,d.approval_digest,d.approval_scope,d.configuration_digest FROM alerting.rule_state s JOIN alerting.destination d ON d.instance_id=s.instance_id AND d.enabled AND d.approved AND d.approval_scope=s.instance_id AND d.approved_kind=d.kind AND d.approved_configuration_reference=d.configuration_reference AND d.approved_revision IS NOT NULL AND d.approval_digest IS NOT NULL WHERE s.alert_id=p_alert_id AND s.acknowledged_at=t AND NOT alerting.maintenance_active_unscoped(s.instance_id,t) ON CONFLICT(alert_id,destination_id,event_kind) DO NOTHING;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('acknowledge',p_idempotency_key,sha256(convert_to(p_alert_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_expected_episode_id::text,'') || coalesce(p_request_digest,''),'UTF8')),p_target_id,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,'alert.acknowledge','allowed','succeeded',p_correlation_id,'{}'); RETURN QUERY SELECT a,t;
END $$;

DROP FUNCTION IF EXISTS alerting.upsert_destination(uuid,text,text,boolean,text,text,uuid,bigint,text,boolean);
CREATE OR REPLACE FUNCTION alerting.upsert_destination(p_destination_id uuid, p_kind text, p_configuration_reference text, p_enabled boolean, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_request_digest text, p_approve boolean, p_action text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; updated integer; next_revision integer; expected_approval_digest bytea;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL THEN RAISE EXCEPTION 'destination target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(current_setting('sqlobserver.target_scope'),0));
  IF p_action NOT IN ('alert.destination.configure','alert.destination.update','alert.destination.retire') THEN RAISE EXCEPTION 'destination mutation action is invalid' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND p_expected_revision IS NOT NULL THEN RAISE EXCEPTION 'destination create cannot carry an expected revision' USING ERRCODE='22023'; END IF;
  IF p_action<>'alert.destination.configure' AND (p_expected_revision IS NULL OR p_expected_revision<=0) THEN RAISE EXCEPTION 'destination update requires a positive expected revision' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND EXISTS (SELECT 1 FROM alerting.destination WHERE destination_id=p_destination_id AND instance_id=current_setting('sqlobserver.target_scope')::uuid) THEN RAISE EXCEPTION 'destination already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_action<>'alert.destination.configure' AND NOT EXISTS (SELECT 1 FROM alerting.destination WHERE destination_id=p_destination_id AND instance_id=current_setting('sqlobserver.target_scope')::uuid) THEN RAISE EXCEPTION 'destination does not exist' USING ERRCODE='P0002'; END IF;
  IF EXISTS (SELECT 1 FROM alerting.destination WHERE destination_id=p_destination_id AND instance_id<>current_setting('sqlobserver.target_scope')::uuid) THEN RAISE EXCEPTION 'destination identity target mismatch' USING ERRCODE='42501'; END IF;
  IF p_kind NOT IN ('https-webhook','windows-event-log') OR position('://' in p_configuration_reference) > 0 THEN RAISE EXCEPTION 'destination catalog binding is invalid' USING ERRCODE='22023'; END IF;
  SELECT COALESCE((SELECT (d.revision + 1)::integer FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=current_setting('sqlobserver.target_scope')::uuid),1) INTO next_revision;
  expected_approval_digest := sha256(convert_to(current_setting('sqlobserver.target_scope') || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || next_revision::text,'UTF8'));
  IF p_approve AND (p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-fA-F]{64}$' OR decode(lower(p_request_digest),'hex') IS DISTINCT FROM expected_approval_digest) THEN RAISE EXCEPTION 'destination approval digest does not match reconciled configuration' USING ERRCODE='22023'; END IF;
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF (SELECT i.target_scope FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key) IS DISTINCT FROM current_setting('sqlobserver.target_scope')::uuid OR existing_digest IS DISTINCT FROM sha256(convert_to(current_setting('sqlobserver.target_scope') || '|' || p_destination_id::text || p_kind || p_configuration_reference || p_enabled::text || p_approve::text || p_action || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'destination revision is invalid' USING ERRCODE='22023'; END IF;
  IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.revision<>p_expected_revision) THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
  IF p_action='alert.destination.configure' THEN
    -- Configure is insert-only.  DO NOTHING turns a concurrent second create
    -- into the bounded conflict below without mutating the existing row.
    INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope) VALUES(p_destination_id,current_setting('sqlobserver.target_scope')::uuid,p_kind,p_configuration_reference,p_enabled,p_approve,CASE WHEN p_approve THEN p_kind ELSE NULL END,CASE WHEN p_approve THEN p_configuration_reference ELSE NULL END,CASE WHEN p_approve THEN next_revision ELSE NULL END,CASE WHEN p_approve THEN encode(expected_approval_digest,'hex') ELSE NULL END,CASE WHEN p_approve THEN current_setting('sqlobserver.target_scope')::uuid ELSE NULL END) ON CONFLICT(destination_id) DO NOTHING;
  ELSE
    INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope) VALUES(p_destination_id,current_setting('sqlobserver.target_scope')::uuid,p_kind,p_configuration_reference,p_enabled,p_approve,CASE WHEN p_approve THEN p_kind ELSE NULL END,CASE WHEN p_approve THEN p_configuration_reference ELSE NULL END,CASE WHEN p_approve THEN next_revision ELSE NULL END,CASE WHEN p_approve THEN encode(expected_approval_digest,'hex') ELSE NULL END,CASE WHEN p_approve THEN current_setting('sqlobserver.target_scope')::uuid ELSE NULL END) ON CONFLICT(destination_id) DO UPDATE SET kind=EXCLUDED.kind,configuration_reference=EXCLUDED.configuration_reference,enabled=EXCLUDED.enabled,revision=alerting.destination.revision+1,approved=EXCLUDED.approved,approved_kind=EXCLUDED.approved_kind,approved_configuration_reference=EXCLUDED.approved_configuration_reference,approved_revision=CASE WHEN EXCLUDED.approved THEN alerting.destination.revision+1 ELSE NULL END,approval_digest=EXCLUDED.approval_digest,approval_scope=EXCLUDED.approval_scope WHERE p_expected_revision IS NULL OR alerting.destination.revision=p_expected_revision;
  END IF;
  GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
  IF p_action<>'alert.destination.configure' OR NOT p_enabled THEN UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason=CASE WHEN NOT p_enabled THEN 'destination_disabled' ELSE 'destination_revision_changed' END,completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE destination_id=p_destination_id AND instance_id=current_setting('sqlobserver.target_scope')::uuid AND completed_at IS NULL AND cancelled_at IS NULL; END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_destination',p_idempotency_key,sha256(convert_to(current_setting('sqlobserver.target_scope') || '|' || p_destination_id::text || p_kind || p_configuration_reference || p_enabled::text || p_approve::text || p_action || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')),current_setting('sqlobserver.target_scope')::uuid,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,p_action,'allowed','succeeded',p_correlation_id,'{}'); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.cancel_maintenance(p_window_id uuid, p_target_id uuid, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_request_digest text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; changed integer; was_active boolean; window_ends timestamptz;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF current_setting('sqlobserver.target_scope', true) IS NULL OR p_target_id::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'maintenance target scope mismatch' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
  SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='cancel_maintenance' AND i.idempotency_key=p_idempotency_key;
  IF a IS NOT NULL THEN IF existing_digest IS DISTINCT FROM sha256(convert_to(p_window_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict'; END IF; RETURN QUERY SELECT a,t; RETURN; END IF;
  IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'maintenance revision is invalid' USING ERRCODE='22023'; END IF;
  SELECT m.starts_at <= t AND t < m.ends_at, m.ends_at INTO was_active,window_ends FROM alerting.maintenance_window m WHERE m.window_id=p_window_id AND m.instance_id=p_target_id AND m.cancelled_at IS NULL FOR UPDATE;
  IF NOT FOUND THEN
    IF EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.window_id=p_window_id AND m.instance_id=p_target_id AND m.cancelled_at IS NOT NULL) THEN RAISE EXCEPTION 'maintenance cancellation was already applied or revision-conflicted' USING ERRCODE='40001';
    ELSE RAISE EXCEPTION 'maintenance window does not exist' USING ERRCODE='P0002'; END IF;
  END IF;
  IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.window_id=p_window_id AND m.instance_id=p_target_id AND m.revision<>p_expected_revision) THEN RAISE EXCEPTION 'maintenance revision conflict' USING ERRCODE='40001'; END IF;
  UPDATE alerting.maintenance_window SET cancelled_at=t,cancelled_actor_sid=p_actor_sid,cancelled_operation_id=CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,revision=revision+1 WHERE window_id=p_window_id AND instance_id=p_target_id AND cancelled_at IS NULL AND (p_expected_revision IS NULL OR revision=p_expected_revision);
  GET DIAGNOSTICS changed=ROW_COUNT; IF changed=0 THEN RAISE EXCEPTION 'maintenance cancellation was already applied or revision-conflicted' USING ERRCODE='40001'; END IF;
  IF was_active THEN
    UPDATE alerting.delivery_outbox d SET due_at=LEAST(d.due_at,t),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
      WHERE d.instance_id=p_target_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.due_at>=window_ends
        AND EXISTS (SELECT 1 FROM alerting.delivery_suppression_intent i WHERE i.delivery_id=d.delivery_id AND i.reason='maintenance_started:' || p_window_id::text);
  END IF;
  INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('cancel_maintenance',p_idempotency_key,sha256(convert_to(p_window_id::text || p_target_id::text || coalesce(p_expected_revision::text,'') || coalesce(p_request_digest,''),'UTF8')),p_target_id,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details)
    VALUES(t,a,'user',p_actor_sid,'alert.maintenance.retire','allowed','succeeded','maintenance_window',p_window_id::text,p_correlation_id,jsonb_build_object('operationId',CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,'targetId',p_target_id)); RETURN QUERY SELECT a,t;
END $$;

CREATE OR REPLACE FUNCTION alerting.assert_evaluation_claims(p_instance_id uuid, p_observations jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
BEGIN
  IF p_instance_id IS NULL OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_lease_fencing IS NULL OR p_lease_fencing <= 0 THEN
    RAISE EXCEPTION 'evaluation claim identity is required' USING ERRCODE='22023';
  END IF;
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_observations) o
    WHERE (o->>'TargetId')::uuid IS DISTINCT FROM p_instance_id
       OR NOT EXISTS (
         SELECT 1 FROM alerting.evaluation_queue q
         WHERE q.instance_id=p_instance_id
           AND q.operation_id=(o->>'OperationId')::uuid
           AND q.rule_id=(o->>'RuleId')::uuid
           AND q.observed_at=(o->>'ObservedAtUtc')::timestamptz
           AND q.completed_at IS NULL
           AND q.claimed_until > clock_timestamp()
           AND q.claim_work_key=p_work_key
           AND q.claim_owner_execution_id=p_owner_execution_id
           AND q.claim_fencing=p_lease_fencing
           AND q.evidence_digest=decode(o->>'EvidenceDigest','hex')
           AND q.sample_id IS NOT DISTINCT FROM NULLIF(o->>'SampleId','')
           AND q.run_id IS NOT DISTINCT FROM NULLIF(o->>'RunId','')::uuid
           AND q.rule_revision=(SELECT r.revision FROM alerting.rule r WHERE r.instance_id=q.instance_id AND r.rule_id=q.rule_id)
           AND q.observations = jsonb_build_array(o)
       )
       AND NOT EXISTS (
         SELECT 1 FROM alerting.evaluation_replay replay
         WHERE replay.operation_id=(o->>'OperationId')::uuid
           AND replay.instance_id=p_instance_id
           AND replay.rule_id=(o->>'RuleId')::uuid
           AND replay.target_binding=p_instance_id
           AND replay.evidence_digest=decode(o->>'EvidenceDigest','hex')
           AND replay.rule_binding=(SELECT to_jsonb(r) FROM alerting.rule r WHERE r.instance_id=p_instance_id AND r.rule_id=(o->>'RuleId')::uuid)
       )
  ) THEN RAISE EXCEPTION 'evaluation evidence is not an exact unexpired claim' USING ERRCODE='40001'; END IF;
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(p_observations) o
    WHERE CASE WHEN o->>'SourceKind'='metric_threshold' THEN NOT EXISTS (
      SELECT 1 FROM telemetry.raw_metric_sample m
      JOIN telemetry.collection_run cr ON cr.run_id=m.collection_run_id
      JOIN telemetry.collection_run_outcome co ON co.run_id=cr.run_id
      WHERE m.instance_id=p_instance_id AND m.sample_id=(o->>'SampleId')::text
        AND m.collection_run_id=(o->>'RunId')::uuid AND m.metric_key=o->>'MetricId'
        AND m.observed_at=(o->>'ObservedAtUtc')::timestamptz
        AND m.metric_value=(o->>'Value')::double precision
        AND cr.collector_id=o->>'SourceCollector' AND cr.collector_version::text=o->>'SourceVersion'
        AND cr.output_schema_version=(o->>'SourceSchemaVersion')::integer
        AND encode(co.completion_digest,'hex')=o->>'SourceDigest'
        AND co.outcome IN ('succeeded','partial')
    ) WHEN o->>'SourceKind'='collector_health' THEN NOT EXISTS (
      SELECT 1 FROM reporting.collector_health_projection h
      JOIN telemetry.collection_run cr ON cr.run_id=h.run_id
      JOIN telemetry.collection_run_outcome co ON co.run_id=cr.run_id
      WHERE h.instance_id=p_instance_id AND h.run_id=(o->>'RunId')::uuid
        AND h.collector_id=o->>'SourceCollector' AND h.collector_version::text=o->>'SourceVersion'
        AND h.output_schema_version=(o->>'SourceSchemaVersion')::integer
        AND encode(co.completion_digest,'hex')=o->>'SourceDigest'
        AND (o->>'ObservedAtUtc')::timestamptz = CASE WHEN h.health_state='stale' AND h.completed_at IS NOT NULL THEN h.completed_at + h.collection_interval * 2 ELSE coalesce(h.completed_at,h.circuit_open_until,h.next_due_at,h.last_started_at,h.repository_time) END
        AND (o->>'SampleId')::uuid = alerting.sha_uuid('collector-health|' || h.collector_id || '|' || h.collector_version::text || '|' || h.schedule_revision::text || '|' || h.schedule_target_revision::text || '|' || h.run_id::text || '|' || coalesce(h.completed_at::text,'') || '|' || coalesce(h.outcome,'') || '|' || h.health_state || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from (SELECT r.evaluation_interval FROM alerting.rule r WHERE r.instance_id=p_instance_id AND r.kind=2 AND r.enabled LIMIT 1)))::bigint)
        AND o->>'Reason' = 'collector_' || h.health_state || '|' || h.collector_id || '|v' || h.collector_version::text || '|schedule=' || h.schedule_revision::text || '|target=' || h.schedule_target_revision::text || '|run=' || h.run_id::text || '|completed=' || coalesce(h.completed_at::text,'') || '|outcome=' || coalesce(h.outcome,'') || '|bucket=' || floor(extract(epoch from h.repository_time)/extract(epoch from (SELECT r.evaluation_interval FROM alerting.rule r WHERE r.instance_id=p_instance_id AND r.kind=2 AND r.enabled LIMIT 1)))::bigint
        AND (o->>'CollectorHealthy')::boolean = (h.health_state='current' AND co.outcome IN ('succeeded','partial'))
    ) ELSE true END
  ) THEN RAISE EXCEPTION 'evaluation evidence source binding is not immutable telemetry' USING ERRCODE='22023'; END IF;
END $$;

CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue(p_observations jsonb, p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer, changed_count integer, suppressed_count integer, completed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
BEGIN
  RAISE EXCEPTION 'fenced alert evaluation is required' USING ERRCODE='42501';
END $$;

CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue_internal(p_observations jsonb, p_decisions jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer, changed_count integer, suppressed_count integer, completed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE d jsonb; o jsonb; target uuid; rule uuid; observed timestamptz; next_state smallint; expected_state smallint; prior_state smallint; prior_operation uuid; prior_digest bytea; history_digest bytea; replay_result_digest bytea; replay_rule_revision bigint; replay_target_binding uuid; replay_rule_binding jsonb; computed_evidence bytea; operation uuid; evidence bytea; event_kind smallint; suppressed boolean; matches boolean; value double precision; consecutive integer; expected_consecutive integer; expected_first timestamptz; prior_last_observed timestamptz; prior_first_match timestamptz; changed integer := 0; hidden integer := 0; n integer := 0; evaluated_operations uuid[] := ARRAY[]::uuid[]; now_utc timestamptz := clock_timestamp(); existing_alert uuid; next_alert uuid; existing_revision bigint; canonical_result jsonb; canonical_result_digest bytea; rule_config alerting.rule%ROWTYPE;
BEGIN
  IF p_lease_fencing IS NULL OR p_lease_fencing <= 0 OR jsonb_array_length(p_decisions) > 10000 OR jsonb_array_length(p_decisions) <> jsonb_array_length(p_observations) THEN RAISE EXCEPTION 'invalid alert evaluation lease or one-to-one batch'; END IF;
  IF (SELECT count(*) FROM jsonb_array_elements(p_decisions) d WHERE d->'Observation'->>'OperationId' IS NULL) > 0 OR (SELECT count(DISTINCT d->'Observation'->>'OperationId') FROM jsonb_array_elements(p_decisions) d) <> jsonb_array_length(p_decisions) THEN RAISE EXCEPTION 'alert operation identities must be unique'; END IF;
  IF (SELECT count(DISTINCT o->>'OperationId') FROM jsonb_array_elements(p_observations) o) <> jsonb_array_length(p_observations) OR EXISTS (SELECT 1 FROM jsonb_array_elements(p_decisions) d WHERE NOT EXISTS (SELECT 1 FROM jsonb_array_elements(p_observations) o WHERE o->>'OperationId'=d->'Observation'->>'OperationId' AND o->>'TargetId'=d->'Observation'->>'TargetId' AND o->>'RuleId'=d->'Observation'->>'RuleId' AND o->>'ObservedAtUtc'=d->'Observation'->>'ObservedAtUtc' AND coalesce(o->>'Value','')=coalesce(d->'Observation'->>'Value','') AND coalesce(o->>'CollectorHealthy','')=coalesce(d->'Observation'->>'CollectorHealthy','') AND coalesce(o->>'EvidenceDigest','')=coalesce(d->'Observation'->>'EvidenceDigest',''))) THEN RAISE EXCEPTION 'observation and decision bindings do not match'; END IF;
  FOR d IN SELECT value FROM jsonb_array_elements(p_decisions) LOOP
    o := d->'Observation'; target := (o->>'TargetId')::uuid; rule := (o->>'RuleId')::uuid; observed := (o->>'ObservedAtUtc')::timestamptz; next_state := (d->'State'->>'State')::smallint; operation := (o->>'OperationId')::uuid; evidence := decode(o->>'EvidenceDigest','hex'); event_kind := NULLIF(d->>'Event','')::smallint;
    IF jsonb_typeof(d->'State') <> 'object' OR (SELECT count(*) FROM jsonb_object_keys(d->'State')) <> 20 OR EXISTS (SELECT 1 FROM jsonb_object_keys(d->'State') k WHERE k NOT IN ('RuleId','TargetId','State','ConsecutiveMatches','FirstMatchUtc','LastObservedUtc','FiredUtc','AcknowledgedUtc','ResolvedUtc','EpisodeStartedUtc','AlertId','EpisodeId','LastOperationId','EvidenceDigest','Reason','LastReason','DeliverySuppressed','AcknowledgedBy','Revision','LastValue')) THEN RAISE EXCEPTION 'alert decision state snapshot is malformed' USING ERRCODE='22023'; END IF;
    IF current_setting('sqlobserver.target_scope', true) IS NULL OR target::text <> current_setting('sqlobserver.target_scope', true) THEN RAISE EXCEPTION 'alert target scope mismatch' USING ERRCODE='42501'; END IF;
    IF operation IS NULL OR octet_length(evidence) <> 32 THEN RAISE EXCEPTION 'alert operation evidence is required' USING ERRCODE='22023'; END IF;
    IF operation IS DISTINCT FROM alerting.canonical_operation_id(target,rule,observed,o->>'Reason') THEN RAISE EXCEPTION 'non-canonical alert operation identity' USING ERRCODE='22023'; END IF;
    computed_evidence := alerting.canonical_evidence_sha256(target,rule,o->>'SourceKind',o->>'MetricId',o->>'SourceCollector',o->>'SourceVersion',NULLIF(o->>'SourceSchemaVersion','')::integer,o->>'SourceDigest',observed,o->>'SampleId',NULLIF(o->>'RunId','')::uuid,NULLIF(o->>'Value','')::double precision,NULLIF(o->>'CollectorHealthy','')::boolean,o->>'Reason');
    IF evidence IS DISTINCT FROM computed_evidence THEN RAISE EXCEPTION 'non-canonical alert evidence digest' USING ERRCODE='22023'; END IF;
    -- Check the independent replay ledger before touching rule state. Exact
    -- operation+digest replays return without mutation; divergent evidence is
    -- a conflict even when the current state has moved on.
    SELECT r.evidence_digest,r.result_digest,r.rule_revision,r.target_binding,r.rule_binding INTO history_digest,replay_result_digest,replay_rule_revision,replay_target_binding,replay_rule_binding FROM alerting.evaluation_replay r WHERE r.operation_id=operation AND r.instance_id=target AND r.rule_id=rule;
    IF FOUND THEN
      IF history_digest IS DISTINCT FROM evidence OR replay_result_digest IS DISTINCT FROM sha256(convert_to(alerting.canonical_decision_snapshot(d)::text,'UTF8')) OR replay_target_binding IS DISTINCT FROM target OR replay_rule_revision IS DISTINCT FROM (SELECT r.revision FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule) OR replay_rule_binding IS DISTINCT FROM (SELECT to_jsonb(r) FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule) THEN RAISE EXCEPTION 'divergent alert replay' USING ERRCODE='40001'; END IF;
      CONTINUE;
    END IF;
    SELECT h.evidence_digest INTO history_digest FROM alerting.state_history h WHERE h.instance_id=target AND h.rule_id=rule AND h.operation_id=operation LIMIT 1;
    IF FOUND THEN
      IF history_digest IS DISTINCT FROM evidence OR EXISTS (SELECT 1 FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule AND (s.last_operation_id IS DISTINCT FROM operation OR s.state IS DISTINCT FROM next_state OR s.consecutive_matches IS DISTINCT FROM (d->'State'->>'ConsecutiveMatches')::integer OR s.first_match_at IS DISTINCT FROM NULLIF(d->'State'->>'FirstMatchUtc','')::timestamptz OR s.last_observed_at IS DISTINCT FROM NULLIF(d->'State'->>'LastObservedUtc','')::timestamptz OR s.alert_id IS DISTINCT FROM NULLIF(d->'State'->>'AlertId','')::uuid OR s.alert_episode_id IS DISTINCT FROM NULLIF(d->'State'->>'EpisodeId','')::uuid OR s.resolved_at IS DISTINCT FROM NULLIF(d->'State'->>'ResolvedUtc','')::timestamptz OR s.episode_started_at IS DISTINCT FROM NULLIF(d->'State'->>'EpisodeStartedUtc','')::timestamptz OR s.acknowledged_by IS DISTINCT FROM d->'State'->>'AcknowledgedBy' OR s.last_value IS DISTINCT FROM NULLIF(d->'State'->>'LastValue','')::double precision OR s.reason IS DISTINCT FROM d->'State'->>'Reason' OR s.last_reason IS DISTINCT FROM d->'State'->>'LastReason' OR s.delivery_suppressed IS DISTINCT FROM (d->'State'->>'DeliverySuppressed')::boolean OR s.revision IS DISTINCT FROM (d->'State'->>'Revision')::bigint)) THEN RAISE EXCEPTION 'divergent alert history replay' USING ERRCODE='40001'; END IF;
      CONTINUE;
    END IF;
    SELECT r.* INTO rule_config FROM alerting.rule r WHERE r.instance_id=target AND r.rule_id=rule AND r.enabled;
    IF NOT FOUND OR (rule_config.kind=1 AND o->>'Value' IS NULL) OR (rule_config.kind=2 AND o->>'CollectorHealthy' IS NULL) THEN RAISE EXCEPTION 'alert observation rule binding is invalid' USING ERRCODE='22023'; END IF;
    IF rule_config.threshold IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision) OR rule_config.hysteresis IN ('NaN'::double precision,'Infinity'::double precision,'-Infinity'::double precision) OR rule_config.hysteresis < 0 OR rule_config.confirmation_count < 1 THEN RAISE EXCEPTION 'alert rule configuration is invalid' USING ERRCODE='22023'; END IF;
    SELECT s.state,s.last_operation_id,s.evidence_digest,s.alert_id,s.revision,s.last_observed_at,s.first_match_at INTO prior_state,prior_operation,prior_digest,existing_alert,existing_revision,prior_last_observed,prior_first_match FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule FOR UPDATE;
    consecutive := COALESCE((d->'State'->>'ConsecutiveMatches')::integer,0);
    IF next_state NOT BETWEEN 1 AND 5 OR consecutive < 0 OR consecutive > rule_config.confirmation_count THEN RAISE EXCEPTION 'alert decision bounds are invalid' USING ERRCODE='22023'; END IF;
    value := CASE WHEN rule_config.kind=2 THEN CASE WHEN lower(o->>'CollectorHealthy')='true' THEN 1 ELSE 0 END ELSE NULLIF(o->>'Value','')::double precision END;
    IF prior_last_observed IS NOT NULL AND observed < prior_last_observed THEN RAISE EXCEPTION 'alert observation time regressed' USING ERRCODE='22023'; END IF;
    IF rule_config.comparison=1 THEN matches := value > (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold-rule_config.hysteresis ELSE rule_config.threshold END);
    ELSIF rule_config.comparison=2 THEN matches := value >= (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold-rule_config.hysteresis ELSE rule_config.threshold END);
    ELSIF rule_config.comparison=3 THEN matches := value < (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold+rule_config.hysteresis ELSE rule_config.threshold END);
    ELSIF rule_config.comparison=4 THEN matches := value <= (CASE WHEN prior_state IN (3,4) THEN rule_config.threshold+rule_config.hysteresis ELSE rule_config.threshold END);
    ELSE matches := abs(value-rule_config.threshold) <= rule_config.hysteresis;
    END IF;
    expected_consecutive := CASE WHEN matches THEN LEAST(rule_config.confirmation_count,COALESCE((SELECT s.consecutive_matches FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule),0)+1) ELSE 0 END;
    expected_first := CASE WHEN matches THEN COALESCE(prior_first_match,observed) ELSE NULL END;
    IF matches AND expected_first IS NOT NULL AND observed-expected_first > rule_config.confirmation_window THEN expected_consecutive := 1; expected_first := observed; END IF;
    expected_state := CASE WHEN NOT matches THEN CASE WHEN prior_state IN (3,4) THEN 5 ELSE 1 END WHEN COALESCE(prior_state,1) IN (1,5) THEN CASE WHEN expected_consecutive >= rule_config.confirmation_count THEN 3 ELSE 2 END WHEN prior_state=2 THEN CASE WHEN expected_consecutive >= rule_config.confirmation_count THEN 3 ELSE 2 END ELSE COALESCE(prior_state,1) END;
    IF next_state IS DISTINCT FROM expected_state OR consecutive IS DISTINCT FROM expected_consecutive OR NULLIF(d->'State'->>'FirstMatchUtc','')::timestamptz IS DISTINCT FROM expected_first THEN RAISE EXCEPTION 'alert decision state does not match repository evaluation' USING ERRCODE='22023'; END IF;
    IF event_kind IS DISTINCT FROM (CASE WHEN expected_state=3 AND COALESCE(prior_state,1) NOT IN (3,4) THEN 1 WHEN expected_state=5 AND prior_state IN (3,4) THEN 2 WHEN expected_state=4 AND prior_state=3 THEN 3 ELSE NULL END) THEN RAISE EXCEPTION 'alert event kind does not match legal transition' USING ERRCODE='22023'; END IF;
    IF next_state IN (3,4) AND NOT matches THEN RAISE EXCEPTION 'alert decision does not satisfy rule threshold' USING ERRCODE='22023'; END IF;
    IF next_state=3 AND consecutive < rule_config.confirmation_count THEN RAISE EXCEPTION 'alert firing confirmation is incomplete' USING ERRCODE='22023'; END IF;
    IF next_state=4 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'acknowledgement transition is illegal' USING ERRCODE='22023'; END IF;
    IF next_state=5 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'resolution transition is illegal' USING ERRCODE='22023'; END IF;
    IF next_state=1 AND COALESCE(prior_state,1) IN (3,4) THEN RAISE EXCEPTION 'illegal alert transition' USING ERRCODE='22023'; END IF;
    IF next_state=2 AND COALESCE(prior_state,1) NOT IN (1,2,5) THEN RAISE EXCEPTION 'illegal pending transition' USING ERRCODE='22023'; END IF;
    IF next_state=3 AND COALESCE(prior_state,1) NOT IN (1,2,3,5) THEN RAISE EXCEPTION 'illegal firing transition' USING ERRCODE='22023'; END IF;
    IF next_state=4 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'illegal acknowledgement transition' USING ERRCODE='22023'; END IF;
    IF next_state=5 AND prior_state NOT IN (3,4) THEN RAISE EXCEPTION 'illegal resolution transition' USING ERRCODE='22023'; END IF;
    IF prior_state IS NOT NULL AND existing_revision IS NULL THEN RAISE EXCEPTION 'alert state revision is required' USING ERRCODE='22023'; END IF;
    IF prior_operation = operation THEN
      IF prior_digest IS DISTINCT FROM evidence THEN RAISE EXCEPTION 'divergent alert replay' USING ERRCODE='40001'; END IF;
      CONTINUE;
    END IF;
    suppressed := alerting.maintenance_active_unscoped(target,observed);
    next_alert := CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state=3 AND prior_state=5 THEN alerting.sha_uuid('alert|' || target::text || '|' || rule::text || '|' || operation::text) WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) AND existing_alert IS NULL THEN alerting.sha_uuid('alert|' || target::text || '|' || rule::text || '|' || operation::text) ELSE existing_alert END;
    IF COALESCE(d->'State'->>'AlertId','') IS DISTINCT FROM COALESCE(next_alert::text,'') OR COALESCE(d->'State'->>'EpisodeId','') IS DISTINCT FROM COALESCE((CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) THEN operation ELSE (SELECT s.alert_episode_id FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END)::text,'') OR COALESCE(d->'State'->>'LastOperationId','') IS DISTINCT FROM operation::text OR COALESCE(lower(d->'State'->>'EvidenceDigest'),'') IS DISTINCT FROM encode(evidence,'hex') OR COALESCE((d->'State'->>'DeliverySuppressed')::boolean,false) IS DISTINCT FROM suppressed OR (d->'State'->>'State')::smallint IS DISTINCT FROM next_state OR (d->'State'->>'ConsecutiveMatches')::integer IS DISTINCT FROM expected_consecutive OR NULLIF(d->'State'->>'LastValue','')::double precision IS DISTINCT FROM NULLIF(o->>'Value','')::double precision OR d->'State'->>'Reason' IS DISTINCT FROM o->>'Reason' OR d->'State'->>'LastReason' IS DISTINCT FROM o->>'Reason' THEN RAISE EXCEPTION 'alert decision state identity does not match repository evaluation' USING ERRCODE='22023'; END IF;
    INSERT INTO alerting.rule_state(instance_id,rule_id,alert_id,state,consecutive_matches,first_match_at,last_observed_at,fired_at,acknowledged_at,resolved_at,episode_started_at,acknowledged_by,last_value,reason,last_reason,delivery_suppressed,alert_episode_id,last_operation_id,evidence_digest,revision)
    VALUES(target,rule,next_alert,next_state,expected_consecutive,expected_first,observed,CASE WHEN prior_state=5 THEN CASE WHEN next_state=3 THEN observed ELSE NULL END WHEN next_state=3 THEN CASE WHEN prior_state=3 THEN (SELECT s.fired_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE observed END ELSE CASE WHEN prior_state IN (3,4) THEN (SELECT s.fired_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE NULL END END,CASE WHEN prior_state=5 THEN NULL WHEN next_state=4 THEN CASE WHEN prior_state=4 THEN (SELECT s.acknowledged_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE observed END ELSE CASE WHEN prior_state=4 THEN (SELECT s.acknowledged_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE NULL END END,CASE WHEN prior_state=5 THEN NULL WHEN next_state=5 THEN observed ELSE (SELECT s.resolved_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END,CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state=3 AND prior_state=5 THEN observed WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) THEN observed ELSE (SELECT s.episode_started_at FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END,CASE WHEN prior_state=5 THEN NULL WHEN next_state=4 OR prior_state=4 THEN (SELECT s.acknowledged_by FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) ELSE NULL END,(o->>'Value')::double precision,o->>'Reason',d->'State'->>'LastReason',suppressed,CASE WHEN next_state IN (1,2) THEN NULL WHEN next_state=3 AND prior_state=5 THEN operation WHEN next_state IN (3,4) AND (prior_state IS DISTINCT FROM 3 AND prior_state IS DISTINCT FROM 4) THEN operation ELSE (SELECT s.alert_episode_id FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule) END,operation,evidence,coalesce(existing_revision,1)+1)
    ON CONFLICT(instance_id,rule_id) DO UPDATE SET alert_id=EXCLUDED.alert_id,state=EXCLUDED.state,consecutive_matches=EXCLUDED.consecutive_matches,first_match_at=EXCLUDED.first_match_at,last_observed_at=EXCLUDED.last_observed_at,fired_at=EXCLUDED.fired_at,acknowledged_at=EXCLUDED.acknowledged_at,resolved_at=EXCLUDED.resolved_at,episode_started_at=EXCLUDED.episode_started_at,acknowledged_by=EXCLUDED.acknowledged_by,last_value=EXCLUDED.last_value,reason=EXCLUDED.reason,last_reason=EXCLUDED.last_reason,delivery_suppressed=EXCLUDED.delivery_suppressed,alert_episode_id=EXCLUDED.alert_episode_id,last_operation_id=EXCLUDED.last_operation_id,evidence_digest=EXCLUDED.evidence_digest,revision=alerting.rule_state.revision+1;
    -- Reconstruct the complete result from the validated SQL row.  The
    -- caller's JSON is only an input assertion; it is never persisted.
    SELECT jsonb_build_object(
      'Observation', jsonb_build_object(
        'TargetId', target, 'RuleId', rule, 'ObservedAtUtc', observed,
        'Value', NULLIF(o->>'Value','')::double precision,
        'CollectorHealthy', NULLIF(o->>'CollectorHealthy','')::boolean,
        'Reason', o->>'Reason', 'SampleId', o->>'SampleId',
        'RunId', NULLIF(o->>'RunId','')::uuid,
        'SourceKind', o->>'SourceKind', 'MetricId', o->>'MetricId',
        'SourceCollector', o->>'SourceCollector', 'SourceVersion', o->>'SourceVersion',
        'SourceSchemaVersion', NULLIF(o->>'SourceSchemaVersion','')::integer,
        'SourceDigest', o->>'SourceDigest', 'OperationId', operation,
        'EvidenceDigest', encode(evidence,'hex')),
      'State', jsonb_build_object(
        'RuleId', s.rule_id, 'TargetId', s.instance_id, 'State', s.state,
        'ConsecutiveMatches', s.consecutive_matches, 'FirstMatchUtc', s.first_match_at,
        'LastObservedUtc', s.last_observed_at, 'FiredUtc', s.fired_at,
        'AcknowledgedUtc', s.acknowledged_at, 'ResolvedUtc', s.resolved_at,
        'EpisodeStartedUtc', s.episode_started_at, 'AlertId', s.alert_id,
        'EpisodeId', s.alert_episode_id, 'LastOperationId', s.last_operation_id,
        'EvidenceDigest', encode(s.evidence_digest,'hex'), 'Reason', s.reason,
        'LastReason', s.last_reason, 'DeliverySuppressed', s.delivery_suppressed,
        'AcknowledgedBy', s.acknowledged_by, 'Revision', s.revision, 'LastValue', s.last_value),
      'Event', event_kind, 'DeliverySuppressed', s.delivery_suppressed, 'Reason', s.reason)
      INTO canonical_result
      FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule;
    canonical_result_digest := sha256(convert_to(canonical_result::text,'UTF8'));
    IF alerting.canonical_decision_snapshot(d) IS DISTINCT FROM canonical_result THEN
      RAISE EXCEPTION 'alert decision snapshot does not match server state' USING ERRCODE='22023';
    END IF;
    IF EXISTS (SELECT 1 FROM alerting.state_history h WHERE h.instance_id=target AND h.rule_id=rule AND h.operation_id=operation AND (h.evidence_digest IS DISTINCT FROM evidence OR h.result_digest IS DISTINCT FROM canonical_result_digest)) THEN RAISE EXCEPTION 'divergent alert history payload' USING ERRCODE='40001'; END IF;
    INSERT INTO alerting.state_history(instance_id,rule_id,alert_id,from_state,to_state,observed_at,reason,delivery_suppressed,operation_id,evidence_digest,result_digest) SELECT target,rule,s.alert_id,prior_state,next_state,observed,s.reason,s.delivery_suppressed,operation,evidence,canonical_result_digest FROM alerting.rule_state s WHERE s.instance_id=target AND s.rule_id=rule;
    INSERT INTO alerting.evaluation_replay(operation_id,instance_id,rule_id,rule_revision,target_binding,rule_binding,evidence_digest,result_digest,result) VALUES(operation,target,rule,rule_config.revision,target,to_jsonb(rule_config),evidence,canonical_result_digest,canonical_result) ON CONFLICT(operation_id) DO NOTHING;
    n := n + 1; evaluated_operations := array_append(evaluated_operations, operation); IF prior_state IS DISTINCT FROM next_state THEN changed := changed + 1; END IF; IF suppressed THEN hidden := hidden + 1; END IF;
    IF event_kind IS NOT NULL THEN INSERT INTO alerting.delivery_outbox(delivery_id,alert_id,instance_id,rule_id,destination_id,event_kind,payload,due_at,operation_id,evidence_digest,destination_revision,destination_kind,destination_configuration_reference,destination_approval_revision,destination_approval_digest,destination_approval_scope,destination_configuration_digest) SELECT gen_random_uuid(),s.alert_id,target,rule,x.destination_id,event_kind,jsonb_build_object('schemaVersion',1,'operationId',operation,'evidenceDigest',encode(evidence,'hex'),'alertId',s.alert_id,'ruleId',rule,'targetId',target,'event',event_kind,'reasonCode',d->>'Reason','suppressed',suppressed),CASE WHEN suppressed THEN COALESCE((SELECT max(m.ends_at) FROM alerting.maintenance_window m WHERE m.instance_id=target AND m.cancelled_at IS NULL AND observed>=m.starts_at AND observed<m.ends_at),now_utc) ELSE now_utc END,operation,evidence,x.revision,x.kind,x.configuration_reference,x.approved_revision,x.approval_digest,x.approval_scope,x.configuration_digest FROM alerting.rule_state s JOIN alerting.destination x ON x.instance_id=target AND x.enabled AND x.approved AND x.approval_scope=target AND x.approved_kind=x.kind AND x.approved_configuration_reference=x.configuration_reference AND x.approved_revision IS NOT NULL AND x.approval_digest IS NOT NULL AND x.instance_id=target WHERE s.instance_id=target AND s.rule_id=rule ON CONFLICT(alert_id,destination_id,event_kind) DO NOTHING; END IF;
  END LOOP;
  UPDATE alerting.evaluation_queue q SET completed_at=now_utc,claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL,next_due_at=now_utc+r.evaluation_interval,due_at=now_utc+r.evaluation_interval
  FROM alerting.rule r
  WHERE r.rule_id=q.rule_id AND r.instance_id=q.instance_id
    AND q.completed_at IS NULL AND q.claimed_until>now_utc
    AND q.claim_work_key=p_work_key AND q.claim_owner_execution_id=p_owner_execution_id AND q.claim_fencing=p_lease_fencing
    AND q.operation_id=ANY(evaluated_operations)
    AND EXISTS (SELECT 1 FROM jsonb_array_elements(p_decisions) decision WHERE (decision->'Observation'->>'OperationId')::uuid=q.operation_id AND (decision->'Observation'->>'TargetId')::uuid=q.instance_id AND (decision->'Observation'->>'RuleId')::uuid=q.rule_id AND (decision->'Observation'->>'ObservedAtUtc')::timestamptz=q.observed_at AND decode(decision->'Observation'->>'EvidenceDigest','hex')=q.evidence_digest);
  INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,subject_kind,subject_identifier,correlation_id,safe_details) VALUES(now_utc,gen_random_uuid(),'service','collector','alerting.evaluate','allowed','succeeded','alert_evaluation',p_lease_fencing::text,gen_random_uuid(),jsonb_build_object('evaluated_count',n,'changed_count',changed));
  RETURN QUERY SELECT n,changed,hidden,now_utc;
END $$;

CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue(p_observations jsonb, p_decisions jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer, changed_count integer, suppressed_count integer, completed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit, control AS $$
BEGIN
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_lease_fencing);
  PERFORM alerting.assert_evaluation_claims(NULLIF(current_setting('sqlobserver.target_scope',true),'')::uuid,p_observations,p_work_key,p_owner_execution_id,p_lease_fencing);
  RETURN QUERY SELECT * FROM alerting.evaluate_and_enqueue_internal(p_observations,p_decisions,p_work_key,p_owner_execution_id,p_lease_fencing);
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_lease_fencing);
END $$;

CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue(p_instance_id uuid, p_observations jsonb, p_decisions jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer, changed_count integer, suppressed_count integer, completed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control, audit AS $$
BEGIN
  IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN RAISE EXCEPTION 'evaluation target scope is required' USING ERRCODE='42501'; END IF;
  RETURN QUERY SELECT * FROM alerting.evaluate_and_enqueue(p_observations,p_decisions,p_work_key,p_owner_execution_id,p_lease_fencing);
END $$;

REVOKE ALL ON TABLE alerting.rule,alerting.rule_state,alerting.evaluation_queue,alerting.maintenance_window,alerting.destination,alerting.delivery_outbox,alerting.delivery_attempt,alerting.state_history,alerting.evaluation_replay,alerting.delivery_suppression_intent,alerting.admin_idempotency,alerting.catalog_registry FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON TABLE reporting.active_alerts FROM PUBLIC;
GRANT USAGE ON SCHEMA alerting TO sqlobserver_server, sqlobserver_collector;
REVOKE ALL ON FUNCTION reporting.list_active_alerts(uuid,integer) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION audit.append_denied_m8_administrative_activity(text,uuid,text,uuid,text) FROM PUBLIC,sqlobserver_server;
GRANT EXECUTE ON FUNCTION audit.append_denied_m8_administrative_activity(text,uuid,text,uuid,text) TO sqlobserver_server;
REVOKE ALL ON FUNCTION audit.append_m8_administrative_activity(text,uuid,text,uuid,uuid,text,text,text,jsonb) FROM PUBLIC,sqlobserver_server;
GRANT EXECUTE ON FUNCTION audit.append_m8_administrative_activity(text,uuid,text,uuid,uuid,text,text,text,jsonb) TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION reporting.list_active_alerts(uuid,integer,timestamptz,uuid,timestamptz), alerting.upsert_rule(jsonb,text,text,uuid), alerting.upsert_maintenance(jsonb,text,text,uuid), alerting.acknowledge(uuid,uuid,text,text,uuid,bigint,uuid,text), alerting.upsert_destination(uuid,text,text,boolean,text,text,uuid,bigint,text,boolean,text), alerting.cancel_delivery_admin(uuid,uuid,text,text,uuid,text,text) TO sqlobserver_server;
GRANT EXECUTE ON FUNCTION alerting.list_rules(uuid), alerting.get_rule_state(uuid,uuid) TO sqlobserver_collector;
REVOKE ALL ON FUNCTION alerting.evaluate_and_enqueue(jsonb,bigint), alerting.evaluate_and_enqueue_internal(jsonb,jsonb,text,uuid,bigint), alerting.assert_evaluation_claims(uuid,jsonb,text,uuid,bigint), alerting.claim_due_deliveries(bigint,integer), alerting.complete_delivery(uuid,boolean,boolean,text,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION alerting.claim_due_deliveries(uuid,text,uuid,bigint,integer), alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint), alerting.renew_delivery(uuid,text,uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
REVOKE ALL ON FUNCTION alerting.list_due_evaluations(integer) FROM PUBLIC,sqlobserver_collector;
GRANT EXECUTE ON FUNCTION alerting.get_maintenance_window(uuid,timestamptz), alerting.maintenance_active(uuid,timestamptz) TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION alerting.claim_due_deliveries(uuid,text,uuid,bigint,integer) TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint) TO sqlobserver_collector;
GRANT EXECUTE ON FUNCTION alerting.cancel_maintenance(uuid,uuid,text,text,uuid,bigint,text) TO sqlobserver_server;

-- M8 closure: runtime claim/completion APIs are target-scoped.  The old
-- overloads are revoked and removed so a collector can never infer a target
-- by selecting a protected outbox row.  A worker receives target_id in each
-- claim result and carries it through every lease operation.
CREATE OR REPLACE FUNCTION alerting.list_due_evaluation_targets(p_max_results integer)
RETURNS TABLE(target_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
 IF p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'evaluation target limit is outside bounds' USING ERRCODE='22023'; END IF;
 RETURN QUERY SELECT DISTINCT q.instance_id FROM alerting.evaluation_queue q WHERE q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp()) ORDER BY q.instance_id LIMIT p_max_results+1;
END $$;
-- The coordinator reconciles new immutable metric/health evidence before a
-- target list is read.  It is invoked under the fenced evaluation worker
-- lease, so fresh evidence cannot be missed between target discovery and
-- scoped claims.
CREATE OR REPLACE FUNCTION alerting.reconcile_due_evaluations(p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
DECLARE discovered integer;
BEGIN
  IF p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing <= 0 OR p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation reconciliation arguments are invalid' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  SELECT count(*) INTO discovered FROM alerting.list_due_evaluations(p_max_results);
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN discovered;
END $$;
CREATE OR REPLACE FUNCTION alerting.list_due_delivery_targets(p_max_results integer)
RETURNS TABLE(target_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
 IF p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'delivery target limit is outside bounds' USING ERRCODE='22023'; END IF;
 RETURN QUERY SELECT DISTINCT d.instance_id FROM alerting.delivery_outbox d WHERE d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.attempt<=7 AND d.due_at<=clock_timestamp() AND (d.leased_until IS NULL OR d.leased_until<clock_timestamp()) ORDER BY d.instance_id LIMIT p_max_results+1;
END $$;
DROP FUNCTION IF EXISTS alerting.claim_due_evaluations(text,uuid,bigint,integer);
DROP FUNCTION IF EXISTS alerting.claim_due_deliveries(text,uuid,bigint,integer);
DROP FUNCTION IF EXISTS alerting.claim_due_deliveries(bigint,integer);
DROP FUNCTION IF EXISTS alerting.complete_delivery(uuid,boolean,boolean,text,bigint);
DROP FUNCTION IF EXISTS alerting.complete_delivery(uuid,boolean,boolean,text,text,uuid,bigint);
DROP FUNCTION IF EXISTS alerting.renew_delivery(uuid,text,uuid,bigint);
DROP FUNCTION IF EXISTS alerting.cancel_delivery(uuid,text,text,uuid,bigint);
DROP FUNCTION IF EXISTS alerting.is_delivery_suppressed(uuid);

CREATE OR REPLACE FUNCTION alerting.claim_due_evaluations(p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(operation_id uuid, observations jsonb, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'evaluation target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation queue claim arguments are invalid' USING ERRCODE='22023'; END IF;
  UPDATE alerting.evaluation_queue q SET completed_at=clock_timestamp(),cancel_reason='rule_revision_stale',claimed_until=NULL,claim_work_key=NULL,claim_owner_execution_id=NULL,claim_fencing=NULL
    WHERE q.instance_id=p_target_id AND q.completed_at IS NULL AND EXISTS (SELECT 1 FROM alerting.rule r WHERE r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.revision<>q.rule_revision);
  -- Reconciliation is coordinator-owned and already completed before target
  -- discovery; a target claim never performs an unscoped evidence scan.
  RETURN QUERY UPDATE alerting.evaluation_queue q
    SET claimed_until=clock_timestamp()+interval '30 seconds',claim_work_key=p_work_key,claim_owner_execution_id=p_owner_execution_id,claim_fencing=p_fencing,completed_at=NULL
    WHERE q.instance_id=p_target_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp() OR q.claim_work_key IS NULL)
    AND q.operation_id IN (SELECT x.operation_id FROM alerting.evaluation_queue x JOIN alerting.rule xr ON xr.instance_id=x.instance_id AND xr.rule_id=x.rule_id WHERE x.instance_id=p_target_id AND x.completed_at IS NULL AND x.due_at<=clock_timestamp() AND (x.claimed_until IS NULL OR x.claimed_until<clock_timestamp() OR x.claim_work_key IS NULL) AND xr.enabled AND xr.revision=x.rule_revision ORDER BY x.observed_at,x.sample_id,x.run_id NULLS LAST,x.operation_id FOR UPDATE OF x SKIP LOCKED LIMIT p_max_results)
    RETURNING q.operation_id,q.observations,q.due_at;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

CREATE OR REPLACE FUNCTION alerting.claim_due_deliveries(p_instance_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(delivery_id uuid, alert_id uuid, destination_id uuid, target_id uuid, kind text, configuration_reference text, payload bytea, attempt integer, due_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'delivery claim arguments are invalid' USING ERRCODE='22023'; END IF;
  RETURN QUERY UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes',lease_fencing=p_fencing,lease_work_key=p_work_key,lease_owner_execution_id=p_owner_execution_id
    WHERE d.instance_id=p_instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.attempt<=7 AND d.due_at<=clock_timestamp() AND (d.leased_until IS NULL OR d.leased_until<clock_timestamp())
      AND NOT alerting.maintenance_active_unscoped(d.instance_id,clock_timestamp())
      AND EXISTS (SELECT 1 FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=d.instance_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision)
    AND d.delivery_id IN (SELECT x.delivery_id FROM alerting.delivery_outbox x JOIN alerting.rule xr ON xr.instance_id=x.instance_id AND xr.rule_id=x.rule_id AND xr.enabled JOIN alerting.destination xz ON xz.destination_id=x.destination_id AND xz.instance_id=x.instance_id AND xz.enabled AND xz.approved AND xz.approval_scope=x.destination_approval_scope AND xz.approved_kind=x.destination_kind AND xz.approved_configuration_reference=x.destination_configuration_reference AND xz.approved_revision=x.destination_approval_revision AND xz.approval_digest=x.destination_approval_digest AND xz.configuration_digest=x.destination_configuration_digest AND xz.revision=x.destination_revision WHERE x.instance_id=p_instance_id AND x.completed_at IS NULL AND x.cancelled_at IS NULL AND x.attempt BETWEEN 0 AND 7 AND x.due_at<=clock_timestamp() AND (x.leased_until IS NULL OR x.leased_until<clock_timestamp()) AND NOT alerting.maintenance_active_unscoped(x.instance_id,clock_timestamp()) ORDER BY x.due_at,x.delivery_id FOR UPDATE OF x SKIP LOCKED LIMIT p_max_results)
    RETURNING d.delivery_id,d.alert_id,d.destination_id,d.instance_id,(SELECT z.kind FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_instance_id),(SELECT z.configuration_reference FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_instance_id),convert_to(d.payload::text,'UTF8'),d.attempt,d.due_at;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

-- Idempotent recovery for an ambiguous claim commit. It only clears rows
-- carrying the exact target/key/owner/fence tuple and never changes attempts,
-- completion, cancellation, or a newer claimant's fields.
CREATE OR REPLACE FUNCTION alerting.recover_delivery_claims(p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer; asserted boolean := false;
BEGIN
  IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  BEGIN
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    asserted := true;
  EXCEPTION WHEN SQLSTATE '55000' THEN
    asserted := false;
  END;
  UPDATE alerting.delivery_outbox d
     SET due_at=LEAST(d.due_at,clock_timestamp()),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
   WHERE d.instance_id=p_target_id
     AND d.completed_at IS NULL AND d.cancelled_at IS NULL
     AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing
     AND (asserted OR NOT EXISTS (
       SELECT 1 FROM control.worker_lease l
        WHERE l.work_key=p_work_key AND l.owner_execution_id=p_owner_execution_id AND l.fencing_token=p_fencing
          AND l.released_at IS NULL AND l.expires_at>clock_timestamp()));
  GET DIAGNOSTICS changed=ROW_COUNT;
  RETURN changed>0;
END $$;

REVOKE ALL ON FUNCTION alerting.recover_delivery_claims(uuid,text,uuid,bigint) FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;

-- The old global helper was an ownerless mutator.  Retain its implementation
-- only under an internal name reachable from the fenced coordinator, and
-- remove the public legacy name before granting runtime functions.
ALTER FUNCTION alerting.list_due_evaluations(integer) RENAME TO reconcile_due_evidence_internal;
REVOKE ALL ON FUNCTION alerting.reconcile_due_evidence_internal(integer) FROM PUBLIC,sqlobserver_server,sqlobserver_collector;
CREATE OR REPLACE FUNCTION alerting.reconcile_due_evaluations(p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
DECLARE discovered integer;
BEGIN
  IF p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing <= 0 OR p_max_results IS NULL OR p_max_results < 1 OR p_max_results > 100 THEN RAISE EXCEPTION 'evaluation reconciliation arguments are invalid' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  SELECT count(*) INTO discovered FROM alerting.reconcile_due_evidence_internal(p_max_results);
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN discovered;
END $$;

CREATE OR REPLACE FUNCTION alerting.complete_delivery(p_delivery_id uuid, p_target_id uuid, p_succeeded boolean, p_permanent boolean, p_reason text, p_response_code integer, p_response_bytes integer, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE result boolean; updated integer;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  IF p_response_code IS NOT NULL AND (p_response_code<100 OR p_response_code>599) OR p_response_bytes IS NOT NULL AND (p_response_bytes<0 OR p_response_bytes>65536) THEN RAISE EXCEPTION 'delivery response bounds are invalid' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  INSERT INTO alerting.delivery_attempt(delivery_id,attempt,outcome,response_code,response_bytes,error_code)
    SELECT p_delivery_id,attempt+1,CASE WHEN p_succeeded THEN 'succeeded' WHEN p_permanent OR attempt>=7 OR clock_timestamp()>=created_at+interval '24 hours' THEN 'permanent_failure' ELSE 'retryable_failure' END,p_response_code,p_response_bytes,left(p_reason,64)
    FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND lease_work_key=p_work_key AND lease_owner_execution_id=p_owner_execution_id AND lease_fencing=p_fencing AND leased_until>clock_timestamp() AND completed_at IS NULL AND cancelled_at IS NULL ON CONFLICT(delivery_id,attempt) DO NOTHING;
  UPDATE alerting.delivery_outbox SET completed_at=CASE WHEN p_succeeded OR p_permanent OR attempt>=7 OR clock_timestamp()>=created_at+interval '24 hours' THEN clock_timestamp() ELSE NULL END,attempt=CASE WHEN p_succeeded OR p_permanent OR attempt>=7 OR clock_timestamp()>=created_at+interval '24 hours' THEN attempt ELSE attempt+1 END,due_at=CASE WHEN p_succeeded OR p_permanent OR attempt>=7 OR clock_timestamp()>=created_at+interval '24 hours' THEN due_at ELSE clock_timestamp()+CASE attempt WHEN 0 THEN interval '5 seconds' WHEN 1 THEN interval '30 seconds' WHEN 2 THEN interval '5 minutes' WHEN 3 THEN interval '30 minutes' WHEN 4 THEN interval '2 hours' WHEN 5 THEN interval '6 hours' WHEN 6 THEN interval '12 hours' ELSE interval '24 hours' END END,last_error_code=CASE WHEN p_succeeded THEN NULL ELSE left(p_reason,64) END,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND lease_work_key=p_work_key AND lease_owner_execution_id=p_owner_execution_id AND lease_fencing=p_fencing AND leased_until>clock_timestamp() AND completed_at IS NULL AND cancelled_at IS NULL;
  GET DIAGNOSTICS updated=ROW_COUNT; result:=updated=1; PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing); RETURN result;
END $$;

CREATE OR REPLACE FUNCTION alerting.renew_delivery(p_delivery_id uuid, p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  UPDATE alerting.delivery_outbox d SET leased_until=clock_timestamp()+interval '5 minutes' WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp() AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND EXISTS (SELECT 1 FROM alerting.destination z WHERE z.destination_id=d.destination_id AND z.instance_id=p_target_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision) AND EXISTS (SELECT 1 FROM alerting.rule r WHERE r.rule_id=d.rule_id AND r.instance_id=p_target_id AND r.enabled) AND NOT alerting.maintenance_active_unscoped(p_target_id,clock_timestamp());
  GET DIAGNOSTICS changed=ROW_COUNT; PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing); RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.cancel_delivery(p_delivery_id uuid, p_target_id uuid, p_reason text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing); UPDATE alerting.delivery_outbox SET cancelled_at=clock_timestamp(),cancel_reason=left(p_reason,64),completed_at=clock_timestamp(),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND lease_work_key=p_work_key AND lease_owner_execution_id=p_owner_execution_id AND lease_fencing=p_fencing AND leased_until>clock_timestamp() AND completed_at IS NULL; GET DIAGNOSTICS changed=ROW_COUNT; PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing); RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.is_delivery_suppressed(p_delivery_id uuid, p_target_id uuid)
RETURNS boolean LANGUAGE plpgsql VOLATILE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
DECLARE changed integer;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  UPDATE alerting.delivery_outbox d SET due_at=COALESCE((SELECT max(m.ends_at) FROM alerting.maintenance_window m WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at),clock_timestamp()),leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND EXISTS (SELECT 1 FROM alerting.maintenance_window m WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at) AND d.completed_at IS NULL AND d.cancelled_at IS NULL; GET DIAGNOSTICS changed=ROW_COUNT; RETURN changed=1;
END $$;

DROP FUNCTION IF EXISTS alerting.is_delivery_suppressed(uuid,uuid);
CREATE OR REPLACE FUNCTION alerting.defer_delivery(p_delivery_id uuid, p_target_id uuid, p_reason text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer; active_window_id uuid; active_window_ends timestamptz;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
  -- Overlapping windows are deterministic: the earliest start, then UUID,
  -- owns this defer intent. Cancellation can therefore release only work
  -- attributed to that exact active window.
  SELECT m.window_id,m.ends_at INTO active_window_id,active_window_ends
    FROM alerting.maintenance_window m
    WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at
    ORDER BY m.starts_at,m.window_id LIMIT 1 FOR UPDATE;
  IF active_window_id IS NULL THEN
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    RETURN false;
  END IF;
  UPDATE alerting.delivery_outbox d
    SET due_at=active_window_ends,
        leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL,last_error_code=left(coalesce(p_reason,'maintenance'),64)
    WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp() AND d.completed_at IS NULL AND d.cancelled_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT;
  INSERT INTO alerting.delivery_suppression_intent(delivery_id,instance_id,operation_id,reason)
    SELECT d.delivery_id,d.instance_id,d.operation_id,'maintenance_started:' || active_window_id::text FROM alerting.delivery_outbox d WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND changed=1;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN changed=1;
END $$;

CREATE OR REPLACE FUNCTION alerting.recheck_delivery(p_delivery_id uuid, p_target_id uuid)
RETURNS text LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, public, alerting AS $$
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  IF EXISTS (SELECT 1 FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND instance_id=p_target_id AND (cancelled_at IS NOT NULL OR completed_at IS NOT NULL)) THEN RETURN 'Cancelled'; END IF;
  IF alerting.maintenance_active_unscoped(p_target_id,clock_timestamp()) THEN RETURN 'Maintenance'; END IF;
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox d JOIN alerting.rule r ON r.rule_id=d.rule_id AND r.instance_id=d.instance_id WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND r.enabled) THEN RETURN 'RuleDisabled'; END IF;
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox d JOIN alerting.destination z ON z.destination_id=d.destination_id AND z.instance_id=d.instance_id WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision) THEN RETURN 'DestinationNotApproved'; END IF;
  RETURN 'Active';
END $$;
CREATE OR REPLACE FUNCTION alerting.renew_delivery_with_outcome(p_delivery_id uuid, p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE readiness text; changed boolean;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock(hashtextextended(p_target_id::text,0));
  -- Serialize readiness, maintenance transition, and lease mutation on the
  -- claimed outbox row.  A revoked/disabled row therefore cannot be renewed
  -- after this function has decided to keep it active.
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND instance_id=p_target_id FOR UPDATE) THEN RETURN 'LostFence'; END IF;
  readiness := alerting.recheck_delivery(p_delivery_id,p_target_id);
  IF readiness='Maintenance' THEN changed := alerting.defer_delivery(p_delivery_id,p_target_id,'maintenance',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'DeferMaintenance'; END IF;
  IF readiness='RuleDisabled' THEN changed := alerting.cancel_delivery(p_delivery_id,p_target_id,'disabled',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'CancelDisabled'; END IF;
  IF readiness='DestinationNotApproved' THEN changed := alerting.cancel_delivery(p_delivery_id,p_target_id,'unapproved',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'CancelUnapproved'; END IF;
  IF readiness='Cancelled' THEN RETURN 'CancelDisabled'; END IF;
  IF NOT alerting.renew_delivery(p_delivery_id,p_target_id,p_work_key,p_owner_execution_id,p_fencing) THEN RETURN 'LostFence'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN 'Active';
EXCEPTION
  -- A lease can be released between the worker's claim and its renewal.
  -- Convert the assertion failure into the typed outcome and clear only the
  -- stale claim owned by this exact fence; never touch a newer claimant.
  WHEN SQLSTATE '55000' THEN
    UPDATE alerting.delivery_outbox d
       SET leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
     WHERE d.delivery_id=p_delivery_id
       AND d.instance_id=p_target_id
       AND d.lease_work_key=p_work_key
       AND d.lease_owner_execution_id=p_owner_execution_id
       AND d.lease_fencing=p_fencing
       AND NOT EXISTS (
         SELECT 1 FROM control.worker_lease l
          WHERE l.work_key=p_work_key
            AND l.owner_execution_id=p_owner_execution_id
            AND l.fencing_token=p_fencing
            AND l.released_at IS NULL
            AND l.expires_at>clock_timestamp());
    RETURN 'LostFence';
END $$;
GRANT EXECUTE ON FUNCTION alerting.claim_due_evaluations(uuid,text,uuid,bigint,integer), alerting.claim_due_deliveries(uuid,text,uuid,bigint,integer), alerting.recover_delivery_claims(uuid,text,uuid,bigint), alerting.complete_delivery(uuid,uuid,boolean,boolean,text,integer,integer,text,uuid,bigint), alerting.renew_delivery(uuid,uuid,text,uuid,bigint), alerting.renew_delivery_with_outcome(uuid,uuid,text,uuid,bigint), alerting.cancel_delivery(uuid,uuid,text,text,uuid,bigint), alerting.defer_delivery(uuid,uuid,text,text,uuid,bigint), alerting.recheck_delivery(uuid,uuid) TO sqlobserver_collector;

-- Approval metadata is server-owned.  Keep the legacy ten-argument function
-- private and expose a thirteen-argument wrapper that accepts only the
-- resolver-derived revision/scope/digest.  The wrapper validates the exact
-- canonical binding before delegating to the existing audited mutation.
REVOKE ALL ON FUNCTION alerting.upsert_destination(uuid,text,text,boolean,text,text,uuid,bigint,text,boolean,text) FROM PUBLIC,sqlobserver_server;
CREATE OR REPLACE FUNCTION alerting.upsert_destination(p_destination_id uuid, p_kind text, p_configuration_reference text, p_enabled boolean, p_idempotency_key text, p_actor_sid text, p_correlation_id uuid, p_expected_revision bigint, p_request_digest text, p_approve boolean, p_approval_revision bigint, p_approval_scope uuid, p_approval_digest text, p_action text)
RETURNS TABLE(audit_id uuid, recorded_at timestamptz) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, audit AS $$
DECLARE target uuid := current_setting('sqlobserver.target_scope', true)::uuid; expected bytea; a uuid := gen_random_uuid(); t timestamptz := clock_timestamp(); existing_digest bytea; updated integer;
BEGIN
  PERFORM alerting.require_canonical_operation_uuid(p_idempotency_key);
  IF target IS NULL THEN RAISE EXCEPTION 'destination target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended(target::text,0));
  IF p_action NOT IN ('alert.destination.configure','alert.destination.update','alert.destination.approve','alert.destination.retire') THEN RAISE EXCEPTION 'destination mutation action is invalid' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND (p_approve OR p_expected_revision IS NOT NULL) THEN RAISE EXCEPTION 'destination configure requires create semantics' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.approve' AND (NOT p_approve OR p_expected_revision IS NULL OR p_expected_revision<=0) THEN RAISE EXCEPTION 'destination approve requires an existing row and positive revision' USING ERRCODE='22023'; END IF;
  IF p_action IN ('alert.destination.update','alert.destination.retire') AND (p_approve OR p_expected_revision IS NULL OR p_expected_revision<=0) THEN RAISE EXCEPTION 'destination update requires a positive expected revision' USING ERRCODE='22023'; END IF;
  IF p_action='alert.destination.configure' AND EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=target) THEN RAISE EXCEPTION 'destination already exists; create cannot update' USING ERRCODE='23505'; END IF;
  IF p_action<>'alert.destination.configure' AND NOT EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=target) THEN RAISE EXCEPTION 'destination does not exist' USING ERRCODE='P0002'; END IF;
  IF p_approve THEN
    IF p_approval_scope IS DISTINCT FROM target OR p_approval_revision IS NULL OR p_approval_revision <= 0 OR p_approval_digest IS NULL OR p_approval_digest !~ '^[0-9a-fA-F]{64}$' THEN RAISE EXCEPTION 'destination approval metadata is invalid' USING ERRCODE='22023'; END IF;
    IF p_request_digest IS NULL OR p_request_digest !~ '^[0-9a-fA-F]{64}$' THEN RAISE EXCEPTION 'destination configuration digest is invalid' USING ERRCODE='22023'; END IF;
    expected := sha256(convert_to(target::text || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || p_approval_revision::text,'UTF8'));
    IF decode(lower(p_approval_digest),'hex') IS DISTINCT FROM expected THEN RAISE EXCEPTION 'destination approval digest does not match server preflight' USING ERRCODE='22023'; END IF;
    IF p_kind NOT IN ('https-webhook','windows-event-log') OR position('://' in p_configuration_reference) > 0 THEN RAISE EXCEPTION 'destination catalog binding is invalid' USING ERRCODE='22023'; END IF;
    SELECT i.audit_id,i.recorded_at,i.request_digest INTO a,t,existing_digest FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key;
    IF a IS NOT NULL THEN
      IF (SELECT i.target_scope FROM alerting.admin_idempotency i WHERE i.action_name='upsert_destination' AND i.idempotency_key=p_idempotency_key) IS DISTINCT FROM target OR existing_digest IS DISTINCT FROM sha256(convert_to(target::text || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || p_enabled::text || '|' || p_approve::text || '|' || p_action || '|' || coalesce(p_expected_revision::text,'') || '|' || coalesce(p_request_digest,'') || '|' || p_approval_revision::text || '|' || p_approval_scope::text || '|' || p_approval_digest,'UTF8')) THEN RAISE EXCEPTION 'idempotency key payload conflict' USING ERRCODE='40001'; END IF;
      RETURN QUERY SELECT a,t; RETURN;
    END IF;
    IF p_expected_revision IS NOT NULL AND p_expected_revision <= 0 THEN RAISE EXCEPTION 'destination revision is invalid' USING ERRCODE='22023'; END IF;
    IF p_expected_revision IS NOT NULL AND EXISTS (SELECT 1 FROM alerting.destination d WHERE d.destination_id=p_destination_id AND d.instance_id=target AND d.revision<>p_expected_revision) THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
    INSERT INTO alerting.destination(destination_id,instance_id,kind,configuration_reference,enabled,approved,approved_kind,approved_configuration_reference,approved_revision,approval_digest,approval_scope,configuration_digest)
      VALUES(p_destination_id,target,p_kind,p_configuration_reference,p_enabled,true,p_kind,p_configuration_reference,p_approval_revision,lower(p_approval_digest),target,lower(p_request_digest))
      ON CONFLICT(destination_id) DO UPDATE SET kind=EXCLUDED.kind,configuration_reference=EXCLUDED.configuration_reference,enabled=EXCLUDED.enabled,revision=alerting.destination.revision+1,approved=true,approved_kind=EXCLUDED.approved_kind,approved_configuration_reference=EXCLUDED.approved_configuration_reference,approved_revision=EXCLUDED.approved_revision,approval_digest=EXCLUDED.approval_digest,approval_scope=EXCLUDED.approval_scope,configuration_digest=EXCLUDED.configuration_digest WHERE p_expected_revision IS NULL OR alerting.destination.revision=p_expected_revision;
    GET DIAGNOSTICS updated=ROW_COUNT; IF updated=0 THEN RAISE EXCEPTION 'destination revision conflict' USING ERRCODE='40001'; END IF;
    UPDATE alerting.delivery_outbox SET cancelled_at=t,cancel_reason=CASE WHEN NOT p_enabled THEN 'destination_disabled' ELSE 'destination_revision_changed' END,completed_at=t,leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL WHERE destination_id=p_destination_id AND instance_id=target AND p_action<>'alert.destination.configure' AND completed_at IS NULL AND cancelled_at IS NULL;
    INSERT INTO alerting.admin_idempotency(action_name,idempotency_key,request_digest,target_scope,operation_id,expected_revision,audit_id,recorded_at) VALUES('upsert_destination',p_idempotency_key,sha256(convert_to(target::text || '|' || p_destination_id::text || '|' || p_kind || '|' || p_configuration_reference || '|' || p_enabled::text || '|' || p_approve::text || '|' || p_action || '|' || coalesce(p_expected_revision::text,'') || '|' || coalesce(p_request_digest,'') || '|' || p_approval_revision::text || '|' || p_approval_scope::text || '|' || p_approval_digest,'UTF8')),target,CASE WHEN p_idempotency_key ~ '^[0-9a-fA-F-]{36}$' THEN p_idempotency_key::uuid END,p_expected_revision,a,t);
    INSERT INTO audit.activity(occurred_at,activity_id,actor_kind,actor_identifier,action_name,authorization_result,outcome,correlation_id,safe_details) VALUES(t,a,'user',p_actor_sid,p_action,'allowed','succeeded',p_correlation_id,jsonb_build_object('configurationReference',p_configuration_reference,'revision',p_approval_revision,'scope',target));
    RETURN QUERY SELECT a,t;
  ELSE
    IF p_approval_revision IS NOT NULL OR p_approval_scope IS NOT NULL OR p_approval_digest IS NOT NULL THEN RAISE EXCEPTION 'approval metadata is only valid for approval' USING ERRCODE='22023'; END IF;
    RETURN QUERY SELECT * FROM alerting.upsert_destination(p_destination_id,p_kind,p_configuration_reference,p_enabled,p_idempotency_key,p_actor_sid,p_correlation_id,p_expected_revision,p_request_digest,false,p_action);
  END IF;
END $$;
GRANT EXECUTE ON FUNCTION alerting.upsert_destination(uuid,text,text,boolean,text,text,uuid,bigint,text,boolean,bigint,uuid,text,text) TO sqlobserver_server;

-- Pass 18 coordinator: discover only targets with eligible work, bounded by
-- the caller's shared global budget and ordered by the oldest durable work.
DROP FUNCTION IF EXISTS alerting.list_targets_with_due_alert_work(text,integer);
CREATE OR REPLACE FUNCTION alerting.list_targets_with_due_alert_work(p_work_kind text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS TABLE(target_id uuid) LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
BEGIN
  IF p_work_kind NOT IN ('evaluation','delivery') OR p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing<=0 OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'alert work discovery arguments are invalid' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  IF p_work_kind='evaluation' THEN
    -- Reconcile the oldest canonical evidence before paging targets. This
    -- makes target discovery consume durable queue identities rather than
    -- repeatedly rediscovering already queued source rows.
    PERFORM alerting.reconcile_due_evidence_internal(p_max_results);
  END IF;
  RETURN QUERY SELECT t.instance_id
  FROM control.observation_target t
    WHERE t.retired_at IS NULL AND
    CASE WHEN p_work_kind='evaluation' THEN EXISTS (SELECT 1 FROM alerting.evaluation_queue q JOIN alerting.rule r ON r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.enabled WHERE q.instance_id=t.instance_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())) ELSE EXISTS (SELECT 1 FROM alerting.delivery_outbox d JOIN alerting.rule r ON r.instance_id=d.instance_id AND r.rule_id=d.rule_id AND r.enabled JOIN alerting.destination z ON z.destination_id=d.destination_id AND z.instance_id=d.instance_id AND z.enabled AND z.approved AND z.approval_scope=d.destination_approval_scope AND z.approved_kind=d.destination_kind AND z.approved_configuration_reference=d.destination_configuration_reference AND z.approved_revision=d.destination_approval_revision AND z.approval_digest=d.destination_approval_digest AND z.configuration_digest=d.destination_configuration_digest AND z.revision=d.destination_revision WHERE d.instance_id=t.instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.attempt BETWEEN 0 AND 7 AND d.due_at<=clock_timestamp() AND (d.leased_until IS NULL OR d.leased_until<clock_timestamp()) AND NOT alerting.maintenance_active_unscoped(t.instance_id,clock_timestamp())) END
  ORDER BY CASE WHEN p_work_kind='evaluation' THEN (SELECT min(q.observed_at) FROM alerting.evaluation_queue q JOIN alerting.rule r ON r.instance_id=q.instance_id AND r.rule_id=q.rule_id AND r.enabled WHERE q.instance_id=t.instance_id AND q.completed_at IS NULL AND q.due_at<=clock_timestamp() AND (q.claimed_until IS NULL OR q.claimed_until<clock_timestamp())) ELSE (SELECT min(d.due_at) FROM alerting.delivery_outbox d WHERE d.instance_id=t.instance_id AND d.completed_at IS NULL AND d.cancelled_at IS NULL AND d.due_at<=clock_timestamp()) END NULLS LAST,t.instance_id LIMIT p_max_results;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
END $$;

DROP FUNCTION IF EXISTS alerting.reconcile_due_evaluations(text,uuid,bigint,integer);
DROP FUNCTION IF EXISTS alerting.claim_due_evaluations(text,uuid,bigint,integer);
DROP FUNCTION IF EXISTS alerting.claim_due_deliveries(bigint,integer);
DROP FUNCTION IF EXISTS alerting.claim_due_deliveries(text,uuid,bigint,integer);
DROP FUNCTION IF EXISTS alerting.evaluate_and_enqueue(jsonb,jsonb,text,uuid,bigint);
DROP FUNCTION IF EXISTS alerting.list_due_evaluation_targets(integer);
DROP FUNCTION IF EXISTS alerting.list_due_delivery_targets(integer);
CREATE OR REPLACE FUNCTION alerting.reconcile_due_evaluations(p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint, p_max_results integer)
RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, telemetry, reporting, control AS $$
DECLARE discovered integer;
BEGIN
  IF p_target_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'evaluation target scope is required' USING ERRCODE='42501'; END IF;
  IF p_work_key IS NULL OR p_owner_execution_id IS NULL OR p_fencing IS NULL OR p_fencing<=0 OR p_max_results IS NULL OR p_max_results NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'evaluation reconciliation arguments are invalid' USING ERRCODE='22023'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  SELECT count(*) INTO discovered FROM alerting.reconcile_due_evidence_internal(p_max_results);
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN discovered;
END $$;
CREATE OR REPLACE FUNCTION alerting.evaluate_and_enqueue(p_instance_id uuid, p_observations jsonb, p_decisions jsonb, p_work_key text, p_owner_execution_id uuid, p_lease_fencing bigint)
RETURNS TABLE(evaluated_count integer, changed_count integer, suppressed_count integer, completed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control, audit AS $$
BEGIN
  IF p_instance_id IS NULL OR current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_instance_id::text THEN RAISE EXCEPTION 'evaluation target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_lease_fencing);
  PERFORM alerting.assert_evaluation_claims(p_instance_id,p_observations,p_work_key,p_owner_execution_id,p_lease_fencing);
  RETURN QUERY SELECT * FROM alerting.evaluate_and_enqueue_internal(p_observations,p_decisions,p_work_key,p_owner_execution_id,p_lease_fencing);
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_lease_fencing);
END $$;
GRANT EXECUTE ON FUNCTION alerting.evaluate_and_enqueue(uuid,jsonb,jsonb,text,uuid,bigint) TO sqlobserver_collector;
REVOKE ALL ON FUNCTION alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer), alerting.reconcile_due_evaluations(uuid,text,uuid,bigint,integer) FROM PUBLIC,sqlobserver_server;
GRANT EXECUTE ON FUNCTION alerting.list_targets_with_due_alert_work(text,text,uuid,bigint,integer), alerting.reconcile_due_evaluations(uuid,text,uuid,bigint,integer) TO sqlobserver_collector;
