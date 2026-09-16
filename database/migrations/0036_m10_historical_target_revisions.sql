-- M10 history must reference an immutable revision identity, not the mutable
-- current target row. Otherwise the first host sample prevents rediscovery.
CREATE TABLE control.observation_target_revision_identity
(
 instance_id uuid NOT NULL REFERENCES control.observation_target(instance_id),
 target_revision bigint NOT NULL CHECK(target_revision>0),
 PRIMARY KEY(instance_id,target_revision)
);
INSERT INTO control.observation_target_revision_identity SELECT instance_id,revision FROM control.observation_target;
REVOKE ALL ON control.observation_target_revision_identity FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE FUNCTION control.capture_observation_target_revision_identity() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,control AS $$
BEGIN
 INSERT INTO control.observation_target_revision_identity VALUES(NEW.instance_id,NEW.revision) ON CONFLICT DO NOTHING;
 RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION control.capture_observation_target_revision_identity() FROM PUBLIC,sqlobserver_server,sqlobserver_collector,sqlobserver_auditor;
CREATE TRIGGER capture_observation_target_revision_identity AFTER INSERT OR UPDATE OF revision ON control.observation_target
FOR EACH ROW EXECUTE FUNCTION control.capture_observation_target_revision_identity();
CREATE TRIGGER observation_target_revision_identity_append_only BEFORE UPDATE OR DELETE ON control.observation_target_revision_identity
FOR EACH STATEMENT EXECUTE FUNCTION control.reject_collector_history_mutation();
DO $repair$
DECLARE reference record; repaired integer:=0;
BEGIN
 FOR reference IN
  SELECT c.conrelid::regclass AS relation,c.conname
  FROM pg_constraint c
  WHERE c.contype='f' AND c.confrelid='control.observation_target'::regclass
   AND c.conparentid=0 AND cardinality(c.confkey)=2
   AND c.conname LIKE 'fk_m10_%'
 LOOP
  EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I',reference.relation,reference.conname);
  EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I FOREIGN KEY(instance_id,target_revision) REFERENCES control.observation_target_revision_identity(instance_id,target_revision)',reference.relation,reference.conname);
  repaired:=repaired+1;
 END LOOP;
 IF repaired=0 THEN RAISE EXCEPTION 'Expected M10 historical revision foreign keys'; END IF;
END $repair$;
