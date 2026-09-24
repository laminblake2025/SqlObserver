-- sqlobserver:nontransactional-index=alerting.ix_alert_state_fleet_inbox
CREATE INDEX CONCURRENTLY ix_alert_state_fleet_inbox
    ON alerting.rule_state (state, fired_at, instance_id, alert_id);
