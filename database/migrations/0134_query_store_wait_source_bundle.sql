-- Advance the pinned M7 bundle for bounded per-plan Query Store wait reads.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
    UPDATE control.collector_contract
    SET manifest_sha256=decode('bb6b9a0deba84a50a5a86c996569f01260edf0d99502876914ff5bda1bfe3e33','hex'),
        asset_bundle_sha256=decode('5b9289da31b8051dfca4a9bfb7a0b16755253b1122897a2464e797c40a465c96','hex')
    WHERE collector_id='queries.performance' AND collector_version=1
      AND manifest_sha256=decode('2531bb91e153f9e0f7fcca10b0e6920457918800b3cd29f298a46c89e97bb71e','hex')
      AND asset_bundle_sha256=decode('2ece191aeac2e2de19e19dcb71672bb1d94653176b2203fc0c9831307c84db28','hex');
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed<>1 THEN
        RAISE EXCEPTION 'Unexpected prior M7 wait source bundle registry' USING ERRCODE='55000';
    END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
