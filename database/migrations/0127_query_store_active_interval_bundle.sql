-- Advance the M7 bundle after including active Query Store intervals in the
-- bounded read. The completed portion ends at the collection timestamp.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
    UPDATE control.collector_contract
    SET asset_bundle_sha256=decode('9bd8432a58a849630c3404af4d3e14a7b4eabdecc3bc0507bad467b9de7b9677','hex')
    WHERE collector_id='queries.performance' AND collector_version=1
      AND manifest_sha256=decode('308bf667ca3148b96d164b4f3d0ab89ad1c99826495428b1a8589cdbf366aedb','hex')
      AND asset_bundle_sha256=decode('1f1afa0fe152ff21f01782192bfbab5585da4e408cb2122df9975feb30bdec11','hex');
    GET DIAGNOSTICS changed=ROW_COUNT;
    IF changed<>1 THEN
        RAISE EXCEPTION 'Unexpected prior M7 source bundle registry' USING ERRCODE='55000';
    END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
