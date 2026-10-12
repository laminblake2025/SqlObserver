-- Deploy with the matching M9 assets. This forward repair changes only the
-- four shared bundle bindings; historical runs and schedules remain intact.
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL ROLE sqlobserver_migrator;

ALTER TABLE control.collector_contract DISABLE TRIGGER collector_contract_append_only;
DO $migration$
DECLARE changed integer;
BEGIN
 UPDATE control.collector_contract
 SET asset_bundle_sha256=decode('5cead2f81a535b7726c5eb6c6b4abac35cbc8cd85301b60e1867ba694e4a3e8e','hex')
 WHERE collector_id IN ('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')
 AND collector_version=1
 AND asset_bundle_sha256=decode('5697aaf35aee3f30f339de5fd973041978b6a0d767759e30cd223eb829e74484','hex');
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed<>4 THEN RAISE EXCEPTION 'Unexpected prior operational health bundle registry' USING ERRCODE='55000'; END IF;
END
$migration$;
ALTER TABLE control.collector_contract ENABLE TRIGGER collector_contract_append_only;
