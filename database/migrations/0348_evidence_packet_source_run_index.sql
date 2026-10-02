-- sqlobserver:partitioned-concurrent-index=analytics.ix_evidence_packet_source_run
CREATE INDEX ix_evidence_packet_source_run
    ON ONLY analytics.evidence_packet_v2 (source_run_id);
