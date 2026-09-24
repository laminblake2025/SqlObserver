-- Bind the corrected backup permission probe to the existing four-collector
-- operational bundle. Historical runs, schedules and manifest semantics stay
-- unchanged; a stale or partial prior registry rolls the migration back.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('5ab54f5ac93eb67a2626d0c005fc15cda9289d7fc0e3b083c52e7fc58a1a0987','hex')
 WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
   AND collector_version=1
   AND asset_bundle_sha256=decode('29e5a566ecc9b3b7fedecbd72574371d1e31195726482a6b83099f08202d26a0','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN
  RAISE EXCEPTION 'Unexpected prior M9 operational health bundle registry' USING ERRCODE='55000';
 END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
