-- Embedded development fixture, never a production migration. The guarded CLI runs
-- this in one transaction after verifying the dedicated database and sample identities.
-- Fixed identities and timestamps preserve append-only history and bound repeated runs.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL TIME ZONE 'UTC';
SET LOCAL lock_timeout = '5s';

INSERT INTO control.observation_target
    (instance_id,instance_key,display_name,host_name,tcp_port,connect_timeout,authentication_mode,
     transport_security_mode,lifecycle_state,revision,created_at,updated_at,discovery_requested_at)
VALUES
    ('00000000-0000-4000-8000-000000000001','development.sample.001','[Sample] Orders SQL','sample-sql-01.invalid',1433,
     interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,
     statement_timestamp(),statement_timestamp(),statement_timestamp()),
    ('00000000-0000-4000-8000-000000000002','development.sample.002','[Sample] Reporting SQL','sample-sql-02.invalid',1433,
     interval '5 seconds','windows_integrated_service_identity','mandatory_validated','active',1,
     statement_timestamp(),statement_timestamp(),statement_timestamp())
ON CONFLICT (instance_id) DO NOTHING;

-- Only the bootstrap login creates the temporary staging relation; application
-- and migrator database privileges remain exactly those defined by migrations.
SET LOCAL ROLE NONE;
CREATE TEMP TABLE development_seed_runs ON COMMIT DROP AS
SELECT target.instance_id,target.instance_key,target.created_at AS seed_to,
    point.number AS sample_number,contract.collector_id,
    target.created_at - interval '1 hour' + point.number * interval '5 minutes' AS observed_at,
    md5('sqlobserver-development-v1:' || target.instance_id || ':' || contract.collector_id || ':' || point.number)::uuid AS run_id,
    CASE contract.collector_id WHEN 'engine.core' THEN 9 WHEN 'activity.sessions' THEN 2
        WHEN 'activity.requests' THEN 1 WHEN 'waits.server' THEN 2
        WHEN 'blocking.current' THEN CASE WHEN target.instance_key='development.sample.001' THEN 1 ELSE 0 END END AS items
FROM control.observation_target AS target
CROSS JOIN generate_series(0,12) AS point(number)
CROSS JOIN (VALUES ('engine.core'),('activity.sessions'),('activity.requests'),('waits.server'),('blocking.current')) AS contract(collector_id);
GRANT SELECT ON TABLE pg_temp.development_seed_runs TO sqlobserver_migrator;
SET LOCAL ROLE sqlobserver_migrator;

DO $development$
DECLARE day date; month date;
BEGIN
    FOR day IN SELECT DISTINCT observed_at::date FROM development_seed_runs LOOP
        PERFORM control.ensure_daily_metric_partition(day);
        PERFORM control.ensure_activity_daily_partitions(day);
    END LOOP;
    FOR month IN SELECT DISTINCT date_trunc('month',observed_at)::date FROM development_seed_runs LOOP
        PERFORM control.ensure_blocking_monthly_partition(month);
    END LOOP;
END
$development$;

INSERT INTO telemetry.collection_run
    (run_id,instance_id,collector_id,collector_version,output_schema_version,target_revision,schedule_revision,
     work_key,owner_execution_id,fencing_token,request_digest,scheduled_for,started_at)
SELECT run_id,instance_id,collector_id,1,1,1,1,'development/sample/v1/' || run_id,
    '00000000-0000-4000-8000-000000000900',1,decode(repeat('ab',32),'hex'),observed_at,observed_at
FROM development_seed_runs
ON CONFLICT (run_id) DO NOTHING;

INSERT INTO telemetry.collection_run_outcome
    (run_id,outcome,reason_code,attempt_count,retry_count,duration_ms,source_row_count,output_item_count,
     inserted_item_count,duplicate_item_count,rejected_item_count,response_bytes,output_bytes,persisted_bytes,
     truncated,loss_detected,loss_kind,loss_count_exact,lost_row_count,lost_byte_count,completion_digest,completed_at)
SELECT run_id,'succeeded','completed',1,0,10,items,items,items,0,0,items*256,items*256,items*256,
    false,false,'none',true,0,0,decode(repeat('cd',32),'hex'),observed_at
FROM development_seed_runs
ON CONFLICT (run_id) DO NOTHING;

INSERT INTO telemetry.raw_metric_sample
    (observed_at,sample_id,instance_id,metric_key,metric_value,dimensions,collected_at,collection_run_id)
SELECT run.observed_at,md5(run.run_id || ':' || metric.key)::uuid,run.instance_id,metric.key,metric.value,
    '{}'::jsonb,run.observed_at,run.run_id
FROM development_seed_runs AS run
CROSS JOIN LATERAL (VALUES
    ('engine.user_connections', CASE WHEN run.instance_key='development.sample.001' THEN 40.0 ELSE 12.0 END + run.sample_number % 4 * 5),
    ('engine.batch_requests_total',100000.0 + run.sample_number*30000),
    ('engine.sql_compilations_total',1000.0 + run.sample_number*30),
    ('engine.sql_recompilations_total',100.0 + run.sample_number),
    ('engine.page_life_expectancy_seconds',8000.0 + run.sample_number*60),
    ('engine.process_physical_memory_bytes',8589934592.0 + run.sample_number*1048576),
    ('engine.committed_memory_bytes',10737418240.0),
    ('engine.target_memory_bytes',12884901888.0),
    ('engine.start_time_key',extract(epoch FROM run.seed_to - timestamptz '2000-01-01 00:00:00+00') - 86400)
) AS metric(key,value)
WHERE run.collector_id='engine.core'
ON CONFLICT (observed_at,sample_id) DO NOTHING;

INSERT INTO telemetry.activity_session_snapshot
    (observed_at,collection_run_id,instance_id,target_revision,session_id,status_code,is_user_process,database_id,
     open_transaction_count,cpu_ms,memory_usage_pages,reads,writes,logical_reads,total_elapsed_ms,collected_at)
SELECT run.observed_at,run.run_id,run.instance_id,1,session.id,'running',true,5,
    CASE WHEN session.id=51 THEN 1 ELSE 0 END,100+run.sample_number*10,128,500,20,1000,60000,run.observed_at
FROM development_seed_runs AS run CROSS JOIN (VALUES (51),(52)) AS session(id)
WHERE run.collector_id='activity.sessions'
ON CONFLICT (observed_at,collection_run_id,session_id) DO NOTHING;

INSERT INTO telemetry.activity_request_snapshot
    (observed_at,collection_run_id,instance_id,target_revision,session_id,request_id,status_code,command_code,database_id,
     cpu_ms,total_elapsed_ms,reads,writes,logical_reads,row_count,percent_complete,collected_at)
SELECT observed_at,run_id,instance_id,1,52,0,
    CASE WHEN instance_key='development.sample.001' THEN 'suspended' ELSE 'running' END,
    'select',5,100,60000,500,0,1000,100,0,observed_at
FROM development_seed_runs WHERE collector_id='activity.requests'
ON CONFLICT (observed_at,collection_run_id,session_id,request_id) DO NOTHING;

INSERT INTO telemetry.server_wait_snapshot
    (observed_at,collection_run_id,instance_id,target_revision,wait_type,waiting_tasks_count,wait_time_ms,
     maximum_wait_time_ms,signal_wait_time_ms,collected_at)
SELECT run.observed_at,run.run_id,run.instance_id,1,wait.type,100+run.sample_number*5,
    10000+run.sample_number*wait.increment,1000,100+run.sample_number*10,run.observed_at
FROM development_seed_runs AS run
CROSS JOIN (VALUES ('LCK_M_X',2000),('PAGEIOLATCH_SH',600)) AS wait(type,increment)
WHERE run.collector_id='waits.server'
ON CONFLICT (observed_at,collection_run_id,wait_type) DO NOTHING;

INSERT INTO events.blocking_edge
    (observed_at,edge_id,collection_run_id,instance_id,target_revision,blocked_session_id,blocker_kind,blocker_session_id,
     wait_type,waiting_task_count,wait_duration_ms,root_blocker_session_id,chain_depth,chain_state,collected_at)
SELECT observed_at,md5(run_id || ':blocking-edge')::uuid,run_id,instance_id,1,52,'session',51,
    'LCK_M_X',1,60000+sample_number*5000,51,1,'resolved',observed_at
FROM development_seed_runs WHERE collector_id='blocking.current' AND items=1
ON CONFLICT (observed_at,edge_id) DO NOTHING;

UPDATE control.collector_schedule AS schedule
SET last_started_at=run.observed_at,last_completed_at=run.observed_at,last_succeeded_at=run.observed_at,
    last_outcome='succeeded',next_due_at=run.observed_at+schedule.collection_interval
FROM development_seed_runs AS run
WHERE run.sample_number=12 AND schedule.instance_id=run.instance_id AND schedule.collector_id=run.collector_id
    AND schedule.last_completed_at IS NULL;
