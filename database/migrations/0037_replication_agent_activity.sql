-- Deploy with the matching passive replication collector. Historical run digests remain unchanged.
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $upgrade$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract SET asset_bundle_sha256=decode('8fa9d8d4c8f3a8fdfb17ffe675f7642220ada826719136d5b7338372866c2b9a','hex')
 WHERE collector_id='replication.health' AND collector_version=1 AND asset_bundle_sha256=decode('7e06e0e3d1c71dd3c9e5a2e2acd14412984e761009921a63bf1141a5b34d18aa','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>1 THEN RAISE EXCEPTION 'Unexpected prior replication bundle registry' USING ERRCODE='55000'; END IF;
END $upgrade$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
