-- Forward correction of the reviewed passive SQL bundles; output contracts and limits are unchanged.
-- Deploy the matching collector with this migration. Earlier binaries fail catalog reconciliation.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '5min';
SET LOCAL TIME ZONE 'UTC';
SET LOCAL ROLE sqlobserver_migrator;
-- This transactional seed-data repair is restricted to two exact prior hashes.
-- The original pins remain in migrations 10/11 and historical run digests remain untouched.
-- Runtime roles retain no registry write access; restore the append-only trigger before commit.
ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
BEGIN
 UPDATE control.collector_contract SET asset_bundle_sha256=decode('de178d916eb80f75611f3982879dc53ab2270589a22b3b28e10cd24e756b745a','hex')
 WHERE collector_id='deadlocks.system-health' AND collector_version=1
   AND asset_bundle_sha256=decode('72570fba287327e1dec64a56d6211b9c24a7d597b35010c9b9ac765615d2963f','hex');
 IF NOT FOUND THEN RAISE EXCEPTION 'Unexpected prior bundle for deadlocks.system-health' USING ERRCODE='55000'; END IF;
 UPDATE control.collector_contract SET asset_bundle_sha256=decode('ba28508f8b9e2c3074b3605856de963d1663a884fce8356e1f2485040aa6c78f','hex')
 WHERE collector_id='queries.performance' AND collector_version=1
   AND asset_bundle_sha256=decode('da915ffb11e60bc0f1f019cb3e3e81ccafabc2c3ff94b26520378574562855eb','hex');
 IF NOT FOUND THEN RAISE EXCEPTION 'Unexpected prior bundle for queries.performance' USING ERRCODE='55000'; END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
