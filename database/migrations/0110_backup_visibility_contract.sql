-- The backup query requires VIEW ANY DATABASE to include databases without
-- backup history. Advance its manifest and the shared M9 asset bundle pins.
-- Preserve collector schedules and historical runs.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET manifest_sha256=decode('7b33dfe41e9e5dbdd8dec1afe5504e34add138f56181f039d838b3e0f854e6e9','hex')
 WHERE collector_id='backups.status' AND collector_version=1
   AND manifest_sha256=decode('065e9f16747d10316ce420feeb350b097e8e86e9faa2d6f5b1d9c33ae1e29cff','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>1 THEN
  RAISE EXCEPTION 'Unexpected prior backup manifest registry' USING ERRCODE='55000';
 END IF;

 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('29e5a566ecc9b3b7fedecbd72574371d1e31195726482a6b83099f08202d26a0','hex')
 WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
   AND collector_version=1
   AND asset_bundle_sha256=decode('689ae4dc9b8c0f2c15ce1064c0a823e47b79fec62d21d11c48a7804b4e4c1919','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN
  RAISE EXCEPTION 'Unexpected prior M9 operational health bundle registry' USING ERRCODE='55000';
 END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
