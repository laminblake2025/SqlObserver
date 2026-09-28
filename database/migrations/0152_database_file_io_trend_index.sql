-- sqlobserver:nontransactional-index=telemetry.ix_database_file_trend_target_window
CREATE INDEX CONCURRENTLY ix_database_file_trend_target_window
    ON telemetry.database_file_snapshot (instance_id, target_revision, database_id, file_id, observed_at, snapshot_id);
