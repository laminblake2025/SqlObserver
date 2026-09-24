-- Deploy with the matching passive activity and replication collector bundles.
-- The runner applies this file and its ledger entry in one transaction; guarded
-- updates preserve historical run identities, schedules and every other contract field.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('56bef6e01c8d826a120c1e5edd81db6fccf448fd686400322d69240618ae9191','hex')
 WHERE collector_id IN ('activity.sessions','activity.requests','waits.server','blocking.current')
 AND collector_version=1
 AND asset_bundle_sha256=decode('86b049c90409e157c06612ebd48c36435213122636c9a84637e1d79029cc959e','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN RAISE EXCEPTION 'Unexpected prior activity bundle registry' USING ERRCODE='55000'; END IF;

 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('e9d52f49d1c728ed6968867a1caee11d6a5f288c5326da585b68a9bed0060f36','hex')
 WHERE collector_id='replication.health' AND collector_version=1
 AND asset_bundle_sha256=decode('8fa9d8d4c8f3a8fdfb17ffe675f7642220ada826719136d5b7338372866c2b9a','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>1 THEN RAISE EXCEPTION 'Unexpected prior replication bundle registry' USING ERRCODE='55000'; END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
