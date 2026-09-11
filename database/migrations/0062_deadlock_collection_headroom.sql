-- A parameterized file read takes roughly eight seconds in the populated lab.
-- Give connection, bounded XML reads and parsing room within the same non-overlap
-- contract. Keep the thirty-second schedule and every file/row/payload bound.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
BEGIN
 UPDATE control.collector_contract SET manifest_sha256=decode('59cfabc2a63efa7236e61170f6ad1e3afee79e5dd79e4367ed90e49fb18214b8','hex'),asset_bundle_sha256=decode('57fa05f859d8f1e355786b84cc0ea6c05810ace0088fe176120b6ba019a654de','hex'),execution_timeout=interval '20 seconds'
 WHERE collector_id='deadlocks.system-health' AND collector_version=1
 AND manifest_sha256=decode('f7d21c58bbdf50706e756dc98052cdbf42f1f68883a45a3eca7551742e40dab3','hex')
 AND asset_bundle_sha256=decode('0136e76abae5aa1719e8c2ff0a1a67bf4d31d3304e8e7d86e04545ca2c779659','hex') AND execution_timeout=interval '10 seconds';
 IF NOT FOUND THEN RAISE EXCEPTION 'Unexpected prior deadlock collector contract' USING ERRCODE='55000'; END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
