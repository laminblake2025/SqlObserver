-- sqlobserver:partitioned-concurrent-index=telemetry.ix_run_ref_raw_metric_sample
CREATE INDEX ix_run_ref_raw_metric_sample
    ON ONLY telemetry.raw_metric_sample (collection_run_id);
