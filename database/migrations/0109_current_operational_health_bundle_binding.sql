-- Bind the already pinned M9 backup/AG source assets to the repository
-- catalog. The previous asset-file update changed the embedded bundle hash
-- without advancing the database contract; historical runs remain intact.
SET LOCAL ROLE sqlobserver_migrator;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('689ae4dc9b8c0f2c15ce1064c0a823e47b79fec62d21d11c48a7804b4e4c1919','hex')
 WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
   AND collector_version=1
   AND asset_bundle_sha256=decode('8fa22b58b193640b94e1290fc48820f3d68afdfda9076809f4e9b4fc3b2fa593','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN
  RAISE EXCEPTION 'Unexpected prior M9 operational health bundle registry' USING ERRCODE='55000';
 END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
