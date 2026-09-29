-- sqlobserver:nontransactional-index=telemetry.ix_collection_run_v2_backfill_order
CREATE INDEX CONCURRENTLY ix_collection_run_v2_backfill_order
    ON telemetry.collection_run (instance_id, collector_id, started_at, run_id);
