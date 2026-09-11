-- Delivery permits and their separate renewal connections share a target lock.
-- Administrative changes retain exclusive locks, preventing changes during send.
-- Row locks and exact lease fencing still serialize individual outbox mutations.
SET LOCAL ROLE sqlobserver_migrator;
CREATE OR REPLACE FUNCTION alerting.defer_delivery(p_delivery_id uuid, p_target_id uuid, p_reason text, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE changed integer; active_window_id uuid; active_window_ends timestamptz;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock_shared(hashtextextended(p_target_id::text,0));
  -- Overlapping windows are deterministic: the earliest start, then UUID,
  -- owns this defer intent. Cancellation can therefore release only work
  -- attributed to that exact active window.
  SELECT m.window_id,m.ends_at INTO active_window_id,active_window_ends
    FROM alerting.maintenance_window m
    WHERE m.instance_id=p_target_id AND m.cancelled_at IS NULL AND clock_timestamp()>=m.starts_at AND clock_timestamp()<m.ends_at
    ORDER BY m.starts_at,m.window_id LIMIT 1 FOR UPDATE;
  IF active_window_id IS NULL THEN
    PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
    RETURN false;
  END IF;
  UPDATE alerting.delivery_outbox d
    SET due_at=active_window_ends,
        leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL,last_error_code=left(coalesce(p_reason,'maintenance'),64)
    WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND d.lease_work_key=p_work_key AND d.lease_owner_execution_id=p_owner_execution_id AND d.lease_fencing=p_fencing AND d.leased_until>clock_timestamp() AND d.completed_at IS NULL AND d.cancelled_at IS NULL;
  GET DIAGNOSTICS changed=ROW_COUNT;
  INSERT INTO alerting.delivery_suppression_intent(delivery_id,instance_id,operation_id,reason)
    SELECT d.delivery_id,d.instance_id,d.operation_id,'maintenance_started:' || active_window_id::text FROM alerting.delivery_outbox d WHERE d.delivery_id=p_delivery_id AND d.instance_id=p_target_id AND changed=1;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN changed=1;
END $$;
CREATE OR REPLACE FUNCTION alerting.renew_delivery_with_outcome(p_delivery_id uuid, p_target_id uuid, p_work_key text, p_owner_execution_id uuid, p_fencing bigint)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public, alerting, control AS $$
DECLARE readiness text; changed boolean;
BEGIN
  IF current_setting('sqlobserver.target_scope',true) IS DISTINCT FROM p_target_id::text THEN RAISE EXCEPTION 'delivery target scope is required' USING ERRCODE='42501'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  PERFORM pg_advisory_xact_lock_shared(hashtextextended(p_target_id::text,0));
  -- Serialize readiness, maintenance transition, and lease mutation on the
  -- claimed outbox row.  A revoked/disabled row therefore cannot be renewed
  -- after this function has decided to keep it active.
  IF NOT EXISTS (SELECT 1 FROM alerting.delivery_outbox WHERE delivery_id=p_delivery_id AND instance_id=p_target_id FOR UPDATE) THEN RETURN 'LostFence'; END IF;
  readiness := alerting.recheck_delivery(p_delivery_id,p_target_id);
  IF readiness='Maintenance' THEN changed := alerting.defer_delivery(p_delivery_id,p_target_id,'maintenance',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'DeferMaintenance'; END IF;
  IF readiness='RuleDisabled' THEN changed := alerting.cancel_delivery(p_delivery_id,p_target_id,'disabled',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'CancelDisabled'; END IF;
  IF readiness='DestinationNotApproved' THEN changed := alerting.cancel_delivery(p_delivery_id,p_target_id,'unapproved',p_work_key,p_owner_execution_id,p_fencing); IF NOT changed THEN RETURN 'LostFence'; END IF; RETURN 'CancelUnapproved'; END IF;
  IF readiness='Cancelled' THEN RETURN 'CancelDisabled'; END IF;
  IF NOT alerting.renew_delivery(p_delivery_id,p_target_id,p_work_key,p_owner_execution_id,p_fencing) THEN RETURN 'LostFence'; END IF;
  PERFORM control.assert_worker_lease(p_work_key,p_owner_execution_id,p_fencing);
  RETURN 'Active';
EXCEPTION
  -- A lease can be released between the worker's claim and its renewal.
  -- Convert the assertion failure into the typed outcome and clear only the
  -- stale claim owned by this exact fence; never touch a newer claimant.
  WHEN SQLSTATE '55000' THEN
    UPDATE alerting.delivery_outbox d
       SET leased_until=NULL,lease_work_key=NULL,lease_owner_execution_id=NULL,lease_fencing=NULL
     WHERE d.delivery_id=p_delivery_id
       AND d.instance_id=p_target_id
       AND d.lease_work_key=p_work_key
       AND d.lease_owner_execution_id=p_owner_execution_id
       AND d.lease_fencing=p_fencing
       AND NOT EXISTS (
         SELECT 1 FROM control.worker_lease l
          WHERE l.work_key=p_work_key
            AND l.owner_execution_id=p_owner_execution_id
            AND l.fencing_token=p_fencing
            AND l.released_at IS NULL
            AND l.expires_at>clock_timestamp());
    RETURN 'LostFence';
END $$;
