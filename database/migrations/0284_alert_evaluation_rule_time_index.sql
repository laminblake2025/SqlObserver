-- sqlobserver:nontransactional-index=alerting.alert_evaluation_rule_time
CREATE INDEX CONCURRENTLY alert_evaluation_rule_time
    ON alerting.evaluation_queue (instance_id, rule_id, observed_at);
