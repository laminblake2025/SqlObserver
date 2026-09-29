-- Prioritize databases without a recent full backup and older regular fulls
-- before differential/log rows when the bounded M9 backup query is truncated.
-- Only the pinned source bundle changes; schedules and historical runs remain.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('ce5cf58bf6442b561426b7e72b7825c3e08093e80e5277f2ec6a93ddece6ba87','hex')
 WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
   AND collector_version=1
   AND asset_bundle_sha256=decode('5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN
  RAISE EXCEPTION 'Unexpected prior M9 operational health bundle registry' USING ERRCODE='55000';
 END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
