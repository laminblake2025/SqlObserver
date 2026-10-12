export type QueryPerformanceMetric = "cpu" | "duration" | "executions" | "logical_reads" | "writes" | "rows";
export type QueryStoreWaitRankedPlan = { readonly databaseId: number; readonly queryFingerprint: string;
  readonly planFingerprint: string; readonly waitMilliseconds: string; readonly comparableRows: number;
  readonly sourceRows: number; readonly lastObservedAtUtc: string };
export type QueryStoreWaitRanking = { readonly fromUtc: string; readonly toUtc: string;
  readonly category: number | null;
  readonly coverage: "no_comparable_groups" | "observed_groups_only";
  readonly items: readonly QueryStoreWaitRankedPlan[] };
export type QueryStorePlanRegression = { readonly databaseId: number; readonly queryFingerprint: string;
  readonly previousPlanFingerprint: string; readonly currentPlanFingerprint: string;
  readonly previousAverageMilliseconds: string; readonly currentAverageMilliseconds: string;
  readonly slowdownRatio: string; readonly previousExecutions: string; readonly currentExecutions: string;
  readonly previousComparableRows: number; readonly previousSourceRows: number;
  readonly currentComparableRows: number; readonly currentSourceRows: number;
  readonly lastObservedAtUtc: string };
export type QueryStorePlanRegressionList = { readonly fromUtc: string; readonly toUtc: string;
  readonly comparisonFromUtc: string;
  readonly coverage: "no_qualifying_changes" | "observed_groups_only";
  readonly items: readonly QueryStorePlanRegression[] };
export type QueryPerformanceDatabaseCatalog = { readonly databaseId: number; readonly databaseName: string };
export type QueryPerformanceDatabaseStatus = { readonly databaseId: number; readonly status: string; readonly sourceState: string; readonly reason: string; readonly fallbackAttempted: boolean; readonly truncated: boolean; readonly lossKind: string; readonly sourceRowsRead: number; readonly responseBytes: number; readonly minimumLostItems: number; readonly lossCountIsExact: boolean; readonly minimumLostBytes: number };
export type QueryPerformanceStatus = { readonly targetId: string; readonly snapshotUtc: string; readonly source?: "query_store" | "plan_cache" | "mixed" | "unavailable"; readonly sourceState: string; readonly coverage: string; readonly fresh: boolean; readonly truncated: boolean; readonly reason?: string; readonly targetStatus?: string; readonly targetReason?: string; readonly databaseStatuses: readonly QueryPerformanceDatabaseStatus[]; readonly databaseCatalog: readonly QueryPerformanceDatabaseCatalog[] };
export type QueryPerformanceItem = { readonly query: { readonly databaseId: number; readonly queryFingerprint: string }; readonly plan?: { readonly planFingerprint: string }; readonly source: "query_store" | "plan_cache" | "mixed"; readonly sourceState: string; readonly semantics: string; readonly metrics: Record<string, number | null>; readonly intervalStartUtc: string; readonly intervalEndUtc: string; readonly coverage: string; readonly fresh: boolean; readonly truncated: boolean; readonly contentAvailable: false; readonly collectionRunId: string; readonly observationKey: string };
export type QueryPerformancePage = { readonly repositoryTimeUtc: string; readonly items: readonly QueryPerformanceItem[]; readonly nextCursor?: string; readonly cursorsByMetric?: Partial<Record<QueryPerformanceMetric, string>>; readonly fromUtc?: string; readonly toUtc?: string };
export type QueryPerformanceHistoryItem = QueryPerformanceItem;
export type QueryPerformanceHistoryPage = { readonly repositoryTimeUtc: string; readonly items: readonly QueryPerformanceHistoryItem[]; readonly nextCursor?: string };
export type QueryPerformancePlan = { readonly targetId: string; readonly databaseId: number; readonly queryFingerprint: string; readonly planFingerprint: string; readonly source: "query_store" | "plan_cache" | "mixed"; readonly observedAtUtc: string; readonly coverage: string; readonly contentAvailable: boolean };
export type QueryPerformanceText = { readonly collectionRunId: string; readonly status: "available" | "unavailable"; readonly text: string | null };
export type QueryPerformancePlanContent = { readonly collectionRunId: string; readonly planFingerprint: string; readonly status: "available" | "unavailable"; readonly xml: string | null };
export type QueryPerformancePlanWaits = { readonly collectionRunId: string; readonly planFingerprint: string; readonly status: "available" | "unavailable"; readonly capturedAtUtc: string | null; readonly categories: readonly { readonly category: number; readonly waitMilliseconds: number }[] | null };
export type QueryStoreGroupTrendPoint = {
  readonly bucketStartUtc: string; readonly sourceRows: number;
  readonly runtimeComparableRows: number; readonly runtimeBaselineRows: number;
  readonly runtimeResetRows: number; readonly runtimeAmbiguousRows: number;
  readonly cpuMilliseconds: string | null; readonly durationMilliseconds: string | null;
  readonly executions: string | null; readonly logicalReads: string | null;
  readonly writes: string | null; readonly rows: string | null;
  readonly waitComparableRows: number; readonly waitBaselineRows: number;
  readonly waitResetRows: number; readonly waitUnavailableRows: number;
  readonly waitStaleRows: number; readonly waitCategories: Readonly<Record<string, string>>;
};
export type QueryStoreGroupTrend = {
  readonly fromUtc: string; readonly toUtc: string;
  readonly points: readonly QueryStoreGroupTrendPoint[];
};

export type PlanCacheIntervalQuery = {
  readonly databaseId: number; readonly databaseIncarnation: string;
  readonly queryFingerprint: string; readonly planFingerprint: string;
  readonly cpuMilliseconds: string; readonly durationMilliseconds: string;
  readonly executions: string; readonly logicalReads: string; readonly writes: string; readonly rows: string;
  readonly comparableRows: number; readonly firstObservedAtUtc: string; readonly lastObservedAtUtc: string;
};
export type PlanCacheIntervalRanking = {
  readonly targetId: string; readonly fromUtc: string; readonly toUtc: string;
  readonly metric: QueryPerformanceMetric; readonly semantics: "plan_cache_delta";
  readonly coverage: "observed_entries_only" | "no_comparable_rows";
  readonly sourceFrames: number; readonly incompleteFrames: number; readonly comparableRows: number;
  readonly unavailableRows: number; readonly boundaryRows: number; readonly hasMore: boolean;
  readonly items: readonly PlanCacheIntervalQuery[];
};

export type PlanCacheHistorySelection = {
  readonly targetId: string; readonly databaseId: number; readonly databaseIncarnation: string;
  readonly queryFingerprint: string; readonly planFingerprint: string;
};
export type PlanCacheContentKind = "text" | "plan";
export type PlanCacheContentReason = "available" | "evidence_unavailable" | "capture_missing" | "capture_disabled"
  | "not_selected" | "source_unavailable" | "oversized" | "response_byte_limit" | "protection_unavailable" | "protection_failed";
export type PlanCacheContent = {
  readonly status: "available" | "unavailable"; readonly reason: PlanCacheContentReason;
  readonly observedAtUtc: string | null; readonly content: string | null;
};
export type PlanCacheIntervalHistoryItem = {
  readonly collectionRunId: string; readonly cacheEntryFingerprint: string; readonly sourceEpoch: string;
  readonly observedAtUtc: string; readonly committedAtUtc: string; readonly captureComplete: boolean;
  readonly transition: "comparable" | "window_boundary" | "baseline_unavailable" | "reset" | "stale" | "comparison_window_expired";
  readonly intervalFromUtc: string | null; readonly intervalToUtc: string | null;
  readonly cpuMilliseconds: string | null; readonly durationMilliseconds: string | null;
  readonly executions: string | null; readonly logicalReads: string | null; readonly writes: string | null; readonly rows: string | null;
};
export type PlanCacheIntervalHistory = PlanCacheHistorySelection & {
  readonly fromUtc: string; readonly toUtc: string; readonly snapshotUtc: string; readonly revision: number;
  readonly totalRows: number; readonly nextCursor: string | null; readonly items: readonly PlanCacheIntervalHistoryItem[];
};
