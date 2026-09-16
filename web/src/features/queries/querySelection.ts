import type { QueryPerformanceHistoryPage, QueryPerformanceItem, QueryPerformancePage, QueryPerformanceStatus } from "./queryPerformanceTypes";

export function queryPerformanceDatabaseIds(page: QueryPerformancePage | undefined, status: QueryPerformanceStatus | undefined) {
  return [...new Set([
    ...(status?.databaseStatuses ?? []).map(item => item.databaseId),
    ...(status?.databaseCatalog ?? []).map(item => item.databaseId),
    ...(page?.items ?? []).map(item => item.query.databaseId),
  ])].sort((left, right) => left - right);
}

export function queryPerformanceDatabaseOptions(page: QueryPerformancePage | undefined, status: QueryPerformanceStatus | undefined) {
  const names = new Map((status?.databaseCatalog ?? []).map(item => [item.databaseId, item.databaseName]));
  return queryPerformanceDatabaseIds(page, status).map(databaseId => ({ databaseId, databaseName: names.get(databaseId) }));
}

export function metricsForObservation(item: QueryPerformanceItem | undefined, history: QueryPerformanceHistoryPage | undefined) {
  if (!item) return undefined;
  const match = history?.items.find(row => row.collectionRunId === item.collectionRunId && row.observationKey === item.observationKey && row.query.databaseId === item.query.databaseId && row.query.queryFingerprint === item.query.queryFingerprint && row.source === item.source && row.semantics === item.semantics);
  return match?.metrics ?? item.metrics;
}
