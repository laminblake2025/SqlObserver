-- Keep reduced replication visibility as an explicit gap reason.
SET LOCAL ROLE sqlobserver_migrator;
ALTER TABLE telemetry.visibility_gap DROP CONSTRAINT ck_visibility_gap_reason;
ALTER TABLE telemetry.visibility_gap ADD CONSTRAINT ck_visibility_gap_reason CHECK
 (reason_code IN ('source_row_limit','response_byte_limit','deadline_exceeded',
 'transient_target_failure','permanent_target_failure','required_permission_missing',
 'target_unsupported','output_validation_failed','lease_ownership_lost',
 'circuit_currently_open','capability_profile_missing','capability_profile_stale',
 'capability_missing','target_version_unsupported','target_platform_unsupported',
 'target_edition_unsupported','target_revision_changed','ingestion_rejection',
 'blocking_graph_limit','visibility_incomplete'));
