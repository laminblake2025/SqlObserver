-- Match the bounded deadlock file-reader deadline to the deployed collector.
-- Preserve installed migration history, schedules, run digests and runtime grants.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
BEGIN
 UPDATE control.collector_contract SET manifest_sha256=decode('f7d21c58bbdf50706e756dc98052cdbf42f1f68883a45a3eca7551742e40dab3','hex'),asset_bundle_sha256=decode('0136e76abae5aa1719e8c2ff0a1a67bf4d31d3304e8e7d86e04545ca2c779659','hex'),execution_timeout=interval '10 seconds'
 WHERE collector_id='deadlocks.system-health' AND collector_version=1
 AND manifest_sha256=decode('5f3b0a9fef5a7d06f37cea5063e39a8b1b7e20dbec7c8234f84f521d41ca0a60','hex')
 AND asset_bundle_sha256=decode('de178d916eb80f75611f3982879dc53ab2270589a22b3b28e10cd24e756b745a','hex') AND execution_timeout=interval '5 seconds';
 IF NOT FOUND THEN RAISE EXCEPTION 'Unexpected prior deadlock collector contract' USING ERRCODE='55000'; END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
