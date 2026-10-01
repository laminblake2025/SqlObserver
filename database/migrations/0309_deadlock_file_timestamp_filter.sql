-- Preserve bounded passive collection while avoiding native XE timestamp
-- predicate pushdown that reports retained events as an empty successful scan.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
BEGIN
    UPDATE control.collector_contract
    SET asset_bundle_sha256=decode('21c5911e826f945833b74b128ae499464f494cf5f0be4a59ba042d451e1ebd8d','hex')
    WHERE collector_id='deadlocks.system-health' AND collector_version=1
      AND manifest_sha256=decode('59cfabc2a63efa7236e61170f6ad1e3afee79e5dd79e4367ed90e49fb18214b8','hex')
      AND asset_bundle_sha256=decode('57fa05f859d8f1e355786b84cc0ea6c05810ace0088fe176120b6ba019a654de','hex')
      AND execution_timeout=interval '20 seconds';
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Unexpected prior deadlock collector contract' USING ERRCODE='55000';
    END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
