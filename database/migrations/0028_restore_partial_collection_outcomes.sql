-- Preserve M5/M6/M7 partial evidence alongside the M9 outcome extensions.
ALTER TABLE telemetry.collection_run_outcome DROP CONSTRAINT ck_collection_outcome_reason_matrix;
ALTER TABLE telemetry.collection_run_outcome ADD CONSTRAINT ck_collection_outcome_reason_matrix CHECK
 ((outcome='succeeded' AND reason_code='completed' AND loss_kind='none') OR
  (outcome='partial' AND ((reason_code='output_validation_failed' AND loss_kind='output_validation_failure') OR (reason_code='blocking_graph_limit' AND loss_kind='blocking_graph_limit') OR (reason_code='duplicate_overlap' AND loss_kind='duplicate_overlap') OR (reason_code='source_row_limit' AND loss_kind='source_row_limit') OR (reason_code='response_byte_limit' AND loss_kind='response_byte_limit') OR (reason_code IN ('degraded','visibility_incomplete') AND loss_kind='visibility_incomplete'))) OR
  (outcome='timed_out' AND reason_code='deadline_exceeded' AND loss_kind='none') OR
  (outcome='transient_failure' AND reason_code='transient_target_failure' AND loss_kind='none') OR
  (outcome='permanent_failure' AND reason_code IN ('permanent_target_failure','target_revision_changed') AND loss_kind='none') OR
  (outcome='permission_denied' AND reason_code='required_permission_missing' AND loss_kind='none') OR
  (outcome='unsupported' AND reason_code IN ('target_unsupported','capability_profile_missing','capability_profile_stale','capability_missing','target_version_unsupported','target_platform_unsupported','target_edition_unsupported') AND loss_kind='none') OR
  (outcome='output_invalid' AND reason_code='output_validation_failed' AND loss_kind='output_validation_failure') OR
  (outcome='lease_lost' AND reason_code='lease_ownership_lost' AND loss_kind='none') OR
  (outcome='circuit_open' AND reason_code='circuit_currently_open' AND loss_kind='none') OR
  (outcome='degraded' AND reason_code='degraded' AND loss_kind='none'));
ALTER TABLE telemetry.collection_run_outcome DROP CONSTRAINT ck_collection_outcome_loss;
ALTER TABLE telemetry.collection_run_outcome ADD CONSTRAINT ck_collection_outcome_loss CHECK
 (loss_kind IN ('blocking_graph_limit','duplicate_overlap','none','source_row_limit','response_byte_limit','output_validation_failure','ingestion_rejection','visibility_incomplete')
  AND ((loss_kind='none') = (NOT loss_detected))
  AND lost_row_count >= 0 AND lost_byte_count >= 0
  AND ((loss_detected AND (lost_row_count > 0 OR lost_byte_count > 0)) OR (NOT loss_detected AND lost_row_count=0 AND lost_byte_count=0 AND loss_count_exact)));
