-- Advance the pinned M7 source bundle after adding a bounded Query Store
-- text lookup. Existing schedules and historical runs remain unchanged.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
    UPDATE control.collector_contract
    SET manifest_sha256=decode('308bf667ca3148b96d164b4f3d0ab89ad1c99826495428b1a8589cdbf366aedb','hex'),
        asset_bundle_sha256=decode('1f1afa0fe152ff21f01782192bfbab5585da4e408cb2122df9975feb30bdec11','hex')
    WHERE collector_id='queries.performance' AND collector_version=1
      AND manifest_sha256=decode('d3504950a8fc6b10b2da9f786cc7881098e2f360200330e71ee0e353561d3e69','hex')
      AND asset_bundle_sha256=decode('ba28508f8b9e2c3074b3605856de963d1663a884fce8356e1f2485040aa6c78f','hex');
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed<>1 THEN
        RAISE EXCEPTION 'Unexpected prior M7 source bundle registry' USING ERRCODE='55000';
    END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
