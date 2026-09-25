-- Advance the pinned M7 collector bundle for bounded Query Store plan XML.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
    UPDATE control.collector_contract
    SET manifest_sha256=decode('2531bb91e153f9e0f7fcca10b0e6920457918800b3cd29f298a46c89e97bb71e','hex'),
        asset_bundle_sha256=decode('2ece191aeac2e2de19e19dcb71672bb1d94653176b2203fc0c9831307c84db28','hex')
    WHERE collector_id='queries.performance' AND collector_version=1
      AND manifest_sha256=decode('308bf667ca3148b96d164b4f3d0ab89ad1c99826495428b1a8589cdbf366aedb','hex')
      AND asset_bundle_sha256=decode('9bd8432a58a849630c3404af4d3e14a7b4eabdecc3bc0507bad467b9de7b9677','hex');
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed<>1 THEN
        RAISE EXCEPTION 'Unexpected prior M7 plan source bundle registry' USING ERRCODE='55000';
    END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
