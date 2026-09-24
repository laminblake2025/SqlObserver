export type TargetHealthState =
  | "pending"
  | "current"
  | "degraded"
  | "unavailable"
  | "unsupported"
  | "stale"
  | "disabled";

export type CollectorHealthReason =
  | "none"
  | "capability_profile_missing"
  | "capability_profile_stale"
  | "capability_missing"
  | "permission_denied"
  | "version_unsupported"
  | "platform_unsupported"
  | "edition_unsupported"
  | "never_collected"
  | "timed_out"
  | "collection_failed"
  | "output_invalid"
  | "sample_loss"
  | "circuit_open"
  | "evidence_stale";

export type CollectorExecutionOutcome =
  | "succeeded"
  | "partial"
  | "transient_failure"
  | "permanent_failure"
  | "permission_denied"
  | "unsupported"
  | "timed_out"
  | "output_invalid"
  | "lease_lost"
  | "circuit_open";

export type CollectorLossKind =
  | "source_row_limit"
  | "response_byte_limit"
  | "output_validation_failure"
  | "ingestion_rejection";

export type CollectorCircuitState = "closed" | "open" | "half_open";

export interface CollectorHealthSummary {
  readonly collectorId: string;
  readonly manifestVersion: number;
  readonly outputSchemaVersion: number;
  readonly state: TargetHealthState;
  readonly reason: CollectorHealthReason;
  readonly lastExecutionOutcome: CollectorExecutionOutcome | null;
  readonly circuitState: CollectorCircuitState;
  readonly durationMilliseconds: number | null;
  readonly retryCount: number;
  readonly sourceRows: number;
  readonly outputRows: number;
  readonly insertedRows: number;
  readonly duplicateRows: number;
  readonly rejectedRows: number;
  readonly responseBytes: number;
  readonly persistedBytes: number;
  readonly sampleLossKind: CollectorLossKind | null;
  readonly minimumLostItems: number | null;
  readonly lossCountIsExact: boolean | null;
  readonly minimumLostBytes: number | null;
  readonly hasVisibilityGap: boolean;
  readonly scheduledAtUtc: string;
  readonly lastAttemptAtUtc: string | null;
  readonly lastSuccessAtUtc: string | null;
  readonly nextDueAtUtc: string;
}

export interface TargetHealthSnapshot {
  readonly instanceId: string;
  readonly state: TargetHealthState;
  readonly repositoryTimeUtc: string;
  readonly collectors: readonly CollectorHealthSummary[];
  readonly coreMetrics: readonly CoreMetricSummary[];
}

export interface CoreMetricSummary {
  readonly sampleId: string;
  readonly metricId: string;
  readonly observedAtUtc: string;
  readonly value: number;
  readonly dimensions: readonly MetricDimensionSummary[];
}

export interface MetricDimensionSummary {
  readonly key: string;
  readonly value: string;
}

export type DatabaseOperationalState =
  | "online"
  | "restoring"
  | "recovering"
  | "recovery_pending"
  | "suspect"
  | "emergency"
  | "offline"
  | "copying"
  | "offline_secondary"
  | "other";

export type DatabaseRecoveryModel = "full" | "bulk_logged" | "simple" | "other";

export type DatabaseUserAccess =
  | "multi_user"
  | "restricted_user"
  | "single_user"
  | "other";

export interface DatabaseHealthSummary {
  readonly databaseId: number;
  readonly name: string;
  readonly state: DatabaseOperationalState;
  readonly recoveryModel: DatabaseRecoveryModel;
  readonly userAccess: DatabaseUserAccess;
  readonly isReadOnly: boolean;
  readonly compatibilityLevel: number;
  readonly observedAtUtc: string;
  readonly collector: CollectorHealthSummary;
}

export interface DatabaseHealthPage {
  readonly instanceId: string;
  readonly repositoryTimeUtc: string;
  readonly collector: CollectorHealthSummary;
  readonly items: readonly DatabaseHealthSummary[];
  readonly nextCursor: string | null;
}

export type DatabaseFileType = "rows" | "log" | "filestream" | "full_text" | "other";

export type DatabaseFileState =
  | "online"
  | "restoring"
  | "recovering"
  | "recovery_pending"
  | "suspect"
  | "emergency"
  | "offline"
  | "defunct"
  | "other";

export interface DatabaseFileHealthSummary {
  readonly databaseId: number;
  readonly fileId: number;
  readonly logicalName: string;
  readonly fileType: DatabaseFileType;
  readonly state: DatabaseFileState;
  readonly sizeBytes: string;
  readonly maximumSizeBytes: string | null;
  readonly growthBytes: string;
  readonly growthPercent: number;
  readonly readCount: string;
  readonly writeCount: string;
  readonly bytesRead: string;
  readonly bytesWritten: string;
  readonly ioStallMilliseconds: string;
  readonly readStallMilliseconds: string | null;
  readonly writeStallMilliseconds: string | null;
  readonly observedAtUtc: string;
  readonly collector: CollectorHealthSummary;
}

export interface DatabaseFileHealthPage {
  readonly instanceId: string;
  readonly repositoryTimeUtc: string;
  readonly collector: CollectorHealthSummary;
  readonly items: readonly DatabaseFileHealthSummary[];
  readonly nextCursor: string | null;
}

export interface TargetHealthEvidence {
  readonly target: TargetHealthSnapshot;
  readonly databases: DatabaseHealthPage;
  readonly files: DatabaseFileHealthPage;
}
