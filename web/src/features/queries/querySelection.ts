import type { QueryPerformanceItem, QueryPerformanceHistoryPage } from "./queryPerformanceTypes";

export function metricsForObservation(item: QueryPerformanceItem | undefined, history: QueryPerformanceHistoryPage | undefined) {
  if (!item) return undefined;
  const match = history?.items.find(row => row.collectionRunId === item.collectionRunId && row.observationKey === item.observationKey && row.query.databaseId === item.query.databaseId && row.query.queryFingerprint === item.query.queryFingerprint && row.source === item.source && row.semantics === item.semantics);
  return match?.metrics ?? item.metrics;
}
