-- Run after the repository migration runner has applied 0001--0014.
-- These assertions are intentionally server-side so Docker and an operator
-- supplied PostgreSQL connection exercise the same catalog/RLS behavior.
DO $$
DECLARE n integer;
BEGIN
 IF to_regclass('telemetry.host_metric_snapshot_v2') IS NULL OR to_regclass('telemetry.replication_snapshot_v2') IS NULL THEN RAISE EXCEPTION 'M10 parents missing'; END IF;
 IF to_regclass('analytics.metric_rollup_v2') IS NULL OR to_regclass('analytics.evidence_packet_v2') IS NULL THEN RAISE EXCEPTION 'M10 analytics parents missing'; END IF;
 SELECT count(*) INTO n FROM pg_class WHERE relname LIKE 'host_metric_snapshot_v2_p%' AND relkind='r';
 IF n < 9 THEN RAISE EXCEPTION 'daily D-1..D+7 partition set incomplete: %',n; END IF;
 SELECT count(*) INTO n FROM pg_class WHERE relname LIKE 'evidence_packet_v2_p%' AND relkind='r';
 IF n < 3 THEN RAISE EXCEPTION 'monthly current+2 partition set incomplete: %',n; END IF;
 IF NOT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='telemetry.host_metric_snapshot_v2'::regclass) THEN RAISE EXCEPTION 'host parent is not FORCE RLS'; END IF;
 IF NOT (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='analytics.evidence_packet_v2'::regclass) THEN RAISE EXCEPTION 'evidence parent is not FORCE RLS'; END IF;
 IF EXISTS (SELECT 1 FROM system.retention_policy WHERE data_class LIKE 'm10_%' AND (enabled OR retain_for IS NOT NULL)) THEN RAISE EXCEPTION 'M10 retention is enabled by default'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='analytics.metric_rollup_v2'::regclass AND contype='p' AND pg_get_constraintdef(oid) LIKE '%target_revision%') THEN RAISE EXCEPTION 'rollup key is not target-revision fenced'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='analytics.metric_rollup_v2'::regclass AND contype='p' AND pg_get_constraintdef(oid) LIKE '%rollup_interval%' AND pg_get_constraintdef(oid) LIKE '%dimension_hash%' AND pg_get_constraintdef(oid) LIKE '%generation%') THEN RAISE EXCEPTION 'rollup key is not interval/dimension/generation fenced'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='analytics.metric_rollup_v2'::regclass AND contype='f' AND pg_get_constraintdef(oid) LIKE '%target_revision%') THEN RAISE EXCEPTION 'rollup target revision FK missing'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='control.analytics_job'::regclass AND contype='c' AND conname='ck_m10_rollup_job_target') THEN RAISE EXCEPTION 'rollup job target check missing'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='analytics.metric_baseline'::regclass AND contype='p' AND pg_get_constraintdef(oid) LIKE '%target_revision%') THEN RAISE EXCEPTION 'baseline key is not target-revision fenced'; END IF;
 IF has_table_privilege('sqlobserver_server','telemetry.host_metric_snapshot','SELECT') OR has_table_privilege('sqlobserver_server','analytics.metric_rollup','SELECT') THEN RAISE EXCEPTION 'server retains direct M10 compatibility-view SELECT'; END IF;
 IF to_regprocedure('reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,timestamptz)') IS NOT NULL THEN RAISE EXCEPTION 'legacy timestamp-only diagnostics overload remains'; END IF;
 IF to_regprocedure('reporting.search_m10_diagnostics(uuid,bigint,timestamptz,timestamptz,integer,timestamptz,uuid,timestamptz)') IS NULL THEN RAISE EXCEPTION 'composite diagnostics cursor function missing'; END IF;
 IF position('digest(' IN pg_get_functiondef('telemetry.commit_m9_health(uuid,text,integer,integer,bigint,uuid,text,uuid,text,jsonb,bytea)'::regprocedure)) > 0 THEN RAISE EXCEPTION 'M9 pgcrypto helper remains'; END IF;
 IF pg_get_functiondef('telemetry.commit_m9_health(uuid,text,integer,integer,bigint,uuid,text,uuid,text,jsonb,bytea)'::regprocedure) !~ 'sha256' THEN RAISE EXCEPTION 'M9 core sha256 repair missing'; END IF;
 IF to_regprocedure('control.schedule_m10_rollup_jobs(uuid,bigint)') IS NULL OR to_regprocedure('control.claim_m10_rollup_jobs(text,uuid,bigint,integer)') IS NULL OR to_regprocedure('control.complete_m10_rollup_job(uuid,text,text,uuid,bigint)') IS NULL THEN RAISE EXCEPTION 'live rollup control functions missing'; END IF;
 IF to_regprocedure('reporting.read_m10_rollup_derivation_inputs(uuid,bigint,timestamptz,timestamptz,text,integer,timestamptz,text,bytea)') IS NULL OR to_regprocedure('reporting.get_m10_forecast_scoped(uuid,bigint,text,jsonb,interval,timestamptz)') IS NULL OR to_regprocedure('reporting.get_m10_forecast_capacity_scoped(uuid,bigint,text,jsonb,timestamptz)') IS NULL OR to_regprocedure('reporting.get_m10_forecast_capacity(uuid,bigint,text,bytea,timestamptz)') IS NULL THEN RAISE EXCEPTION 'dimension-scoped analytics functions missing'; END IF;
 IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid='analytics.metric_forecast'::regclass AND attname='dimension_hash' AND attnotnull) THEN RAISE EXCEPTION 'forecast dimension identity missing'; END IF;
END $$;
SELECT 'M10 PostgreSQL assertions: PASS' AS result;
