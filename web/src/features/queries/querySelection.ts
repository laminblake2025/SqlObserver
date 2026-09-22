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

export function sameQuerySeries(left: QueryPerformanceItem, right: QueryPerformanceItem) {
  return left.query.databaseId === right.query.databaseId && left.query.queryFingerprint === right.query.queryFingerprint &&
    left.plan?.planFingerprint === right.plan?.planFingerprint && left.source === right.source && left.semantics === right.semantics;
}

export function sameQueryObservation(left: QueryPerformanceItem, right: QueryPerformanceItem) {
  return left.collectionRunId === right.collectionRunId && left.observationKey === right.observationKey && sameQuerySeries(left, right);
}

export function historyForObservation(item: QueryPerformanceItem | undefined, history: QueryPerformanceHistoryPage | undefined) {
  return item === undefined ? [] : (history?.items ?? []).filter(row => sameQuerySeries(row, item));
}

export function metricsForObservation(item: QueryPerformanceItem | undefined, history: QueryPerformanceHistoryPage | undefined) {
  if (!item) return undefined;
  const match = history?.items.find(row => sameQueryObservation(row, item));
  return match?.metrics ?? item.metrics;
}
