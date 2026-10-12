-- Change the M5 wait source to omit wait types with no recorded tasks.
-- All four M5 contracts share one pinned bundle digest. Keep historical
-- snapshots and collector schedules unchanged while rebinding that bundle.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('d233698a8b350ebdf805cbb65b085b0a93b64fc66f57f8f21d354c3445ee00c8','hex')
 WHERE collector_id IN ('activity.sessions','activity.requests','waits.server','blocking.current')
   AND collector_version=1
   AND asset_bundle_sha256=decode('56bef6e01c8d826a120c1e5edd81db6fccf448fd686400322d69240618ae9191','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN
  RAISE EXCEPTION 'Unexpected prior M5 activity bundle registry' USING ERRCODE='55000';
 END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
