export interface OverviewTarget { targetId: string; displayName: string; lifecycle: string; environment: string | null; tags: readonly string[]; groupingRevision: number; canManageGrouping: boolean; }
export interface OverviewValue { value: number | null; state: string; observedAtUtc: string | null; }
export interface OverviewPoint { timeUtc: string; value: number | null; samples: number; }
export interface OverviewSeries { targetId: string; label: string; metric: string; unit: string; state: string; dimension: string | null; points: readonly OverviewPoint[]; }
export interface OverviewIssue { targetId: string; server: string; title: string; detail: string; destination: string; priority: number; observedAtUtc: string | null; }
export interface OverviewResource { targetId: string; server: string; label: string; value: number | null; unit: string; state: string; observedAtUtc: string | null; }
export interface OverviewBackupChecks { withinThreshold: number; policyViolated: number; indeterminate: number; notRequired: number; disabled: number; unknownReasons: readonly { reason: string; count: number }[]; }
export interface OverviewBackupSummary { targetId: string; assessedAtUtc: string; validUntilUtc: string; status: 'indeterminate' | 'within_policy' | 'policy_violated' | 'disabled'; evidenceStatus: string; coverage: string; databaseCount: number; collectorEnabled: boolean; full: OverviewBackupChecks; log: OverviewBackupChecks; observedFaultDatabases: number; }
export interface OverviewEvidence { targetId: string; displayName: string; collectionState: string; lastObservedUtc: string | null; activeAlerts: OverviewValue; blockedSessions: OverviewValue; deadlocks: OverviewValue; issues: readonly OverviewIssue[]; resources: readonly OverviewResource[]; series: readonly OverviewSeries[]; gaps: readonly string[]; backupSummary?: OverviewBackupSummary | null; }
export interface OverviewSnapshot { refreshedAtUtc: string; fromUtc: string; toUtc: string; targetId: string | null; targets: readonly OverviewTarget[]; excludedTargets: number; evidence: readonly OverviewEvidence[]; }
export type OverviewRange = '1h' | '6h' | '24h' | '7d' | 'custom';
export interface OverviewScope { target: string; range: OverviewRange; from?: string; to?: string; compare: boolean; }
