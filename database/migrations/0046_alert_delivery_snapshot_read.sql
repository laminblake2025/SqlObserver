-- Delivery claims return payloads through the fenced function. The adapter then
-- reads only the immutable approval snapshot in that same target transaction.
-- Keep payloads and mutations behind the existing functions and forced RLS.
SET LOCAL ROLE sqlobserver_migrator;
GRANT SELECT (delivery_id, instance_id, destination_approval_revision, destination_configuration_digest)
ON alerting.delivery_outbox TO sqlobserver_collector;

-- The server owns the active-alert API; this overload must not inherit PUBLIC execution.
REVOKE ALL ON FUNCTION reporting.list_active_alerts(uuid,integer,timestamptz,uuid,timestamptz) FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
-- Administrative entry points are callable only by the authenticated server role.
REVOKE ALL ON FUNCTION alerting.upsert_rule(jsonb,text,text,uuid),
 alerting.upsert_maintenance(jsonb,text,text,uuid),
 alerting.acknowledge(uuid,uuid,text,text,uuid,bigint,uuid,text),
 alerting.upsert_destination(uuid,text,text,boolean,text,text,uuid,bigint,text,boolean,text),
 alerting.upsert_destination(uuid,text,text,boolean,text,text,uuid,bigint,text,boolean,bigint,uuid,text,text),
 alerting.cancel_delivery_admin(uuid,uuid,text,text,uuid,text,text),
 alerting.cancel_maintenance(uuid,uuid,text,text,uuid,bigint,text)
FROM PUBLIC,sqlobserver_collector,sqlobserver_auditor;
