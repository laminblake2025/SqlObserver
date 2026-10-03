import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { PlanCacheContent, PlanCacheContentKind, PlanCacheContentReason } from "./queryPerformanceTypes";
import { readBoundedBody } from "../activity/activityParser.mjs";
import type { QueryPerformanceDatabaseCatalog, QueryPerformanceDatabaseStatus, QueryPerformanceHistoryPage, QueryPerformanceItem, QueryPerformanceMetric, QueryPerformancePage, QueryPerformancePlan, QueryPerformancePlanContent, QueryPerformancePlanWaits, QueryPerformanceStatus, QueryPerformanceText, QueryStoreGroupTrend, QueryStoreGroupTrendPoint, QueryStoreWaitRanking, QueryStorePlanRegressionList, PlanCacheIntervalRanking, PlanCacheHistorySelection, PlanCacheIntervalHistory } from "./queryPerformanceTypes";

const maximumResponseBytes = 8 * 1024 * 1024;
const states = new Set(["read_write", "read_only", "disabled", "unsupported", "permission_denied", "read_failure", "timed_out", "mixed", "unavailable"]);
const semantics = new Set(["query_store_interval", "plan_cache_cumulative", "plan_cache_delta", "plan_cache_baseline", "reset"]);
const coverage = new Set(["complete", "truncated", "unavailable", "no_activity"]);
const sources = new Set(["query_store", "plan_cache", "mixed", "unavailable"]);
const databaseStatuses = new Set(["query_store_empty", "query_store_rows", "query_store_disabled", "query_store_unsupported", "query_store_permission_denied", "query_store_read_failure", "query_store_timed_out", "plan_cache_rows", "plan_cache_empty", "plan_cache_permission_denied", "plan_cache_read_failure", "plan_cache_timed_out", "output_capped"]);
const lossKinds = new Set(["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "duplicate_overlap"]);
const statusReasons = new Set(["query_store_empty", "query_store_read", "query_store_disabled", "query_store_unsupported", "query_store_probe_missing", "query_store_permission_denied", "query_store_read_failure", "query_store_timeout", "plan_cache_fallback", "plan_cache_empty", "plan_cache_permission_denied", "plan_cache_read_failure", "plan_cache_timeout", "output_capped"]);
const metrics = new Set(["cpu", "duration", "executions", "logical_reads", "writes", "rows"]);
function statusReasonCompatible(status: string, reason: string): boolean {
  return status === "query_store_empty" && reason === "query_store_empty" || status === "query_store_rows" && (reason === "query_store_read" || reason === "query_store_empty") || status === "query_store_disabled" && reason === "query_store_disabled" || status === "query_store_unsupported" && (reason === "query_store_unsupported" || reason === "query_store_probe_missing") || status === "query_store_permission_denied" && reason === "query_store_permission_denied" || status === "query_store_read_failure" && reason === "query_store_read_failure" || status === "query_store_timed_out" && reason === "query_store_timeout" || status === "plan_cache_rows" && reason === "plan_cache_fallback" || status === "plan_cache_empty" && reason === "plan_cache_empty" || status === "plan_cache_permission_denied" && reason === "plan_cache_permission_denied" || status === "plan_cache_read_failure" && reason === "plan_cache_read_failure" || status === "plan_cache_timed_out" && reason === "plan_cache_timeout" || status === "output_capped" && reason === "output_capped";
}
const digest = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{64}$/iu.test(value);
const optionalDigest = (value: unknown): value is string | null | undefined => value === null || value === undefined || digest(value);
const nativeTicks = (value: string): bigint => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$)/u, "$1"));
const timestamp = (value: unknown): value is string => {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/u.test(value)) return false;
  try { nativeTicks(value); return true; } catch { return false; }
};
const validWindow = (fromUtc: unknown, toUtc: unknown): boolean => timestamp(fromUtc) && timestamp(toUtc)
  && nativeTicks(toUtc) > nativeTicks(fromUtc) && nativeTicks(toUtc) - nativeTicks(fromUtc) <= 7n * 86_400_000_000n;
const canonicalInstant = (value: string): string => value.replace(/(?:\.(\d{1,7}))?Z$/u, (_match, fraction: string | undefined) => `.${(fraction ?? "").padEnd(7, "0")}Z`);
const metric = (value: unknown): value is number | null | undefined => value === undefined || value === null || typeof value === "number" && Number.isSafeInteger(value) && value >= 0;

async function readJson(url: string, expectedTargetId: string, signal?: AbortSignal, maxBytes = maximumResponseBytes, sensitive = false): Promise<unknown> {
  const response = await apiFetch(url, { signal, cache: sensitive ? "no-store" : "default", headers: { Accept: "application/json" } }, { maximumResponseBytes: maxBytes });
  if (!response.ok) throw new ApiRequestError(response.status === 404 ? "Query performance evidence is unavailable." : "Query performance request failed.", response.status);
  if (!/^application\/json(?:;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Query performance response content type was invalid.");
  const length = Number(response.headers.get("content-length") ?? "0");
  if (!Number.isFinite(length) || length > maxBytes) throw new Error("Query performance response exceeded its bound.");
  let body: string; try { body = await readBoundedBody(response, signal ?? new AbortController().signal, maxBytes); } catch { throw new Error("Query performance response exceeded its bound."); }
  let value: unknown; try { value = JSON.parse(body); } catch { throw new Error("Query performance response was invalid."); }
  if (typeof value !== "object" || value === null || (value as { targetId?: unknown }).targetId !== expectedTargetId) throw new Error("Query performance response was outside its target contract.");
  return value;
}

export async function getPlanCacheContent(selection: PlanCacheHistorySelection, collectionRunId: string,
    cacheEntryFingerprint: string, kind: PlanCacheContentKind, signal: AbortSignal): Promise<PlanCacheContent> {
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
  if (!guid.test(selection.targetId) || !guid.test(collectionRunId) || !Number.isInteger(selection.databaseId)
      || selection.databaseId < 5 || selection.databaseId > 32766 || kind !== "text" && kind !== "plan"
      || ![selection.databaseIncarnation, selection.queryFingerprint, selection.planFingerprint, cacheEntryFingerprint]
        .every(value => /^[0-9a-f]{64}$/u.test(value))) throw new Error("Cache content identity was invalid.");
  const url = `/api/v1/observation-targets/${selection.targetId}/query-performance/cache/databases/${selection.databaseId}`
    + `/history/${selection.queryFingerprint}/plans/${selection.planFingerprint}/runs/${collectionRunId}`
    + `/entries/${cacheEntryFingerprint}/content/${kind}?databaseIncarnation=${selection.databaseIncarnation}`;
  // A JSON-escaped one-MiB UTF-8 plan can use six MiB on the wire.
  const value = await readJson(url, selection.targetId, signal, kind === "text" ? 128 * 1024 : 8 * 1024 * 1024, true) as Record<string, unknown>;
  const reasons = new Set(["available", "evidence_unavailable", "capture_missing", "capture_disabled", "not_selected",
    "source_unavailable", "oversized", "response_byte_limit", "protection_unavailable", "protection_failed"]);
  const content = kind === "text" ? value.text : value.xml;
  if (value.databaseId !== selection.databaseId || value.databaseIncarnation !== selection.databaseIncarnation
      || value.queryFingerprint !== selection.queryFingerprint || value.planFingerprint !== selection.planFingerprint
      || typeof value.collectionRunId !== "string" || value.collectionRunId.toLowerCase() !== collectionRunId.toLowerCase()
      || value.cacheEntryFingerprint !== cacheEntryFingerprint || value.payloadKind !== kind
      || value.status !== "available" && value.status !== "unavailable"
      || typeof value.reason !== "string" || !reasons.has(value.reason)
      || value.status === "available" && (value.reason !== "available" || typeof content !== "string" || !content.length
        || new TextEncoder().encode(content).length > (kind === "text" ? 16 * 1024 : 1024 * 1024) || !timestamp(value.observedAtUtc))
      || value.status === "unavailable" && (value.reason === "available" || content !== null)
      || value.observedAtUtc !== null && !timestamp(value.observedAtUtc)
      || (kind === "text" ? value.xml : value.text) !== null)
    throw new Error("Cache content response did not match its exact observation or bounds.");
  return { status: value.status, reason: value.reason as PlanCacheContentReason,
    observedAtUtc: value.observedAtUtc as string | null, content: content as string | null };
}

export async function getQueryPerformanceText(instanceId: string, databaseId: number, queryFingerprint: string, collectionRunId: string, signal: AbortSignal): Promise<QueryPerformanceText> {
  if (!Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767 || !digest(queryFingerprint)
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(collectionRunId))
    throw new Error("Query text identity was invalid.");
  const url = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/databases/${databaseId}/history/${queryFingerprint}/runs/${collectionRunId}/text`;
  const value = await readJson(url, instanceId, signal, 128 * 1024, true) as Record<string, unknown>;
  if (value.databaseId !== databaseId || value.queryFingerprint !== queryFingerprint
      || typeof value.collectionRunId !== "string" || value.collectionRunId.toLowerCase() !== collectionRunId.toLowerCase()
      || value.status !== "available" && value.status !== "unavailable"
      || value.status === "available" && (typeof value.text !== "string" || value.text.length === 0 || new TextEncoder().encode(value.text).length > 16 * 1024)
      || value.status === "unavailable" && value.text !== null)
    throw new Error("Query text response was outside its bounds.");
  return { collectionRunId, status: value.status, text: value.text as string | null };
}
export async function getQueryPerformancePlanContent(instanceId: string, databaseId: number,
    queryFingerprint: string, planFingerprint: string, collectionRunId: string,
    signal: AbortSignal): Promise<QueryPerformancePlanContent> {
  if (!Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767
      || !digest(queryFingerprint) || !digest(planFingerprint)
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(collectionRunId))
    throw new Error("Query plan identity was invalid.");
  const url = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/databases/${databaseId}/history/${queryFingerprint}/runs/${collectionRunId}/plans/${planFingerprint}/content`;
  const value = await readJson(url, instanceId, signal, 2 * 1024 * 1024, true) as Record<string, unknown>;
  if (value.databaseId !== databaseId || value.queryFingerprint !== queryFingerprint
      || value.planFingerprint !== planFingerprint
      || typeof value.collectionRunId !== "string"
      || value.collectionRunId.toLowerCase() !== collectionRunId.toLowerCase()
      || value.status !== "available" && value.status !== "unavailable"
      || value.status === "available" && (typeof value.xml !== "string" || value.xml.length === 0 || new TextEncoder().encode(value.xml).length > 1024 * 1024)
      || value.status === "unavailable" && value.xml !== null)
    throw new Error("Query plan response was outside its bounds.");
  return { collectionRunId, planFingerprint, status: value.status, xml: value.xml as string | null };
}
export async function getQueryPerformancePlanWaits(instanceId: string, databaseId: number,
    queryFingerprint: string, planFingerprint: string, collectionRunId: string,
    signal: AbortSignal): Promise<QueryPerformancePlanWaits> {
  if (!Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767
      || !digest(queryFingerprint) || !digest(planFingerprint)
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(collectionRunId))
    throw new Error("Query wait identity was invalid.");
  const url = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/databases/${databaseId}/history/${queryFingerprint}/runs/${collectionRunId}/plans/${planFingerprint}/waits`;
  const value = await readJson(url, instanceId, signal, 16 * 1024, true) as Record<string, unknown>;
  const categories = value.categories;
  if (value.databaseId !== databaseId || value.queryFingerprint !== queryFingerprint
      || value.planFingerprint !== planFingerprint
      || typeof value.collectionRunId !== "string"
      || value.collectionRunId.toLowerCase() !== collectionRunId.toLowerCase()
      || value.semantics !== "query_store_interval_total"
      || value.status !== "available" && value.status !== "unavailable"
      || value.status === "available" && (!timestamp(value.capturedAtUtc)
        || !Array.isArray(categories) || categories.length > 32)
      || value.status === "unavailable" && (categories !== null || value.capturedAtUtc !== null))
    throw new Error("Query wait response was outside its bounds.");
  const rows = categories as unknown[] | null;
  const seen = new Set<number>();
  if (rows?.some(row => {
    if (typeof row !== "object" || row === null) return true;
    const item = row as Record<string, unknown>;
    if (!Number.isInteger(item.category) || (item.category as number) < 0 || (item.category as number) > 31
        || !Number.isSafeInteger(item.waitMilliseconds) || (item.waitMilliseconds as number) <= 0
        || seen.has(item.category as number)) return true;
    seen.add(item.category as number);
    return false;
  })) throw new Error("Query wait categories were invalid.");
  return { collectionRunId, planFingerprint, status: value.status,
    capturedAtUtc: value.capturedAtUtc as string | null,
    categories: rows as QueryPerformancePlanWaits["categories"] };
}
export async function getQueryStoreGroupPlanTrend(instanceId: string, databaseId: number,
    queryFingerprint: string, planFingerprint: string, fromUtc: string, toUtc: string,
    signal: AbortSignal): Promise<QueryStoreGroupTrend> {
  const ticks = (utc: unknown): bigint => {
    if (!timestamp(utc)) throw new Error("Query Store group trend timestamp was invalid.");
    return nativeTicks(utc) * 10n;
  };
  const from = ticks(fromUtc), to = ticks(toUtc), hour = 36_000_000_000n;
  const firstBucket = from - (from % hour + hour) % hour;
  if (!Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767
      || !digest(queryFingerprint) || !digest(planFingerprint)
      || to <= from || to - from > 7n * 24n * hour)
    throw new Error("Query Store group trend request was invalid.");
  const url = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance`
    + `/databases/${databaseId}/history/${queryFingerprint}/plans/${planFingerprint}/trend`
    + `?fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}`;
  const value = await readJson(url, instanceId, signal, 512 * 1024, true) as Record<string, unknown>;
  if (value.databaseId !== databaseId || value.queryFingerprint !== queryFingerprint
      || value.planFingerprint !== planFingerprint
      || value.semantics !== "query_store_group_delta"
      || value.revisionScope !== "current" || value.coverage !== "observed_groups_only"
      || value.bucketSeconds !== 3600 || !timestamp(value.fromUtc) || !timestamp(value.toUtc)
      || canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc)
      || canonicalInstant(value.toUtc) !== canonicalInstant(toUtc)
      || !Array.isArray(value.points) || value.points.length > 169)
    throw new Error("Query Store group trend response was outside its bounds.");
  let previousBucket: bigint | undefined;
  const points = value.points.map(raw => {
    if (typeof raw !== "object" || raw === null)
      throw new Error("Query Store group trend point was invalid.");
    const point = raw as Record<string, unknown>;
    if (!timestamp(point.bucketStartUtc))
      throw new Error("Query Store group trend bucket was invalid.");
    const bucket = ticks(point.bucketStartUtc);
    if (bucket % hour !== 0n || previousBucket !== undefined && bucket <= previousBucket
        || bucket < firstBucket || bucket >= to)
      throw new Error("Query Store group trend bucket was outside its window.");
    previousBucket = bucket;
    const counts = [point.sourceRows, point.runtimeComparableRows,
      point.runtimeBaselineRows, point.runtimeResetRows, point.runtimeAmbiguousRows,
      point.waitComparableRows, point.waitBaselineRows, point.waitResetRows,
      point.waitUnavailableRows, point.waitStaleRows];
    if (counts.some(count => !Number.isSafeInteger(count) || (count as number) < 0)
        || (point.sourceRows as number) === 0
        || (point.runtimeComparableRows as number) + (point.runtimeBaselineRows as number)
          + (point.runtimeResetRows as number) + (point.runtimeAmbiguousRows as number) !== point.sourceRows
        || (point.waitComparableRows as number) + (point.waitBaselineRows as number)
          + (point.waitResetRows as number) + (point.waitUnavailableRows as number)
          + (point.waitStaleRows as number) !== point.sourceRows)
      throw new Error("Query Store group trend coverage was inconsistent.");
    const metricKeys = ["cpuMilliseconds", "durationMilliseconds", "executions",
      "logicalReads", "writes", "rows"] as const;
    if (metricKeys.some(key => (point.runtimeComparableRows as number) === 0
      ? point[key] !== null : !wholeCount(point[key])))
      throw new Error("Query Store group trend metrics were inconsistent.");
    const categories = point.waitCategories;
    if (typeof categories !== "object" || categories === null || Array.isArray(categories)
        || Object.keys(categories).length > 32
        || (point.waitComparableRows as number) === 0 && Object.keys(categories).length !== 0
        || Object.entries(categories).some(([key, count]) =>
          !/^(?:0|[1-9]|[12][0-9]|3[01])$/u.test(key) || !wholeCount(count)))
      throw new Error("Query Store group trend waits were invalid.");
    return point as QueryStoreGroupTrendPoint;
  });
  return { fromUtc, toUtc, points };
}
const wholeCount = (value: unknown): value is string =>
  typeof value === "string" && /^(?:0|[1-9][0-9]{0,28})$/u.test(value);
function item(value: unknown, metric: QueryPerformanceMetric = "cpu"): QueryPerformanceItem {
  if (typeof value !== "object" || value === null) throw new Error("Query performance item was invalid."); const i = value as Record<string, unknown>; const v = i.value;
  const source = String(i.source); const sourceState = String(i.sourceState); const semantic = String(i.semantics); const sourceMatrix = source === "query_store" && semantic === "query_store_interval" && (sourceState === "read_write" || sourceState === "read_only") || source === "plan_cache" && semantic !== "query_store_interval" && (sourceState === "disabled" || sourceState === "read_failure" || sourceState === "permission_denied" || sourceState === "timed_out" || sourceState === "unsupported");
  if (typeof i.databaseId !== "number" || !Number.isInteger(i.databaseId) || i.databaseId <= 0 || i.databaseId > 32767 || !digest(i.queryFingerprint) || !optionalDigest(i.planFingerprint) || !sources.has(source) || source === "mixed" || source === "unavailable" || sourceState === "mixed" || sourceState === "unavailable" || !states.has(sourceState) || typeof i.metric !== "string" || i.metric !== metric || !semantics.has(semantic) || !sourceMatrix || typeof v !== "number" && v !== null || typeof v === "number" && (!Number.isSafeInteger(v) || v < 0) || !timestamp(i.intervalStartUtc) || !timestamp(i.intervalEndUtc) || nativeTicks(i.intervalEndUtc) <= nativeTicks(i.intervalStartUtc) || !coverage.has(String(i.coverage)) || typeof i.fresh !== "boolean" || typeof i.truncated !== "boolean" || i.contentAvailable !== false || typeof i.collectionRunId !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(i.collectionRunId) || typeof i.observationKey !== "string" || !/^[0-9a-f]{32}$/iu.test(i.observationKey)) throw new Error("Query performance item was outside its bounds.");
  const metricKey = { cpu: "cpuMilliseconds", duration: "durationMilliseconds", executions: "executions", logical_reads: "logicalReads", writes: "writes", rows: "rows" }[metric];
  return { query: { databaseId: i.databaseId, queryFingerprint: i.queryFingerprint }, plan: typeof i.planFingerprint === "string" ? { planFingerprint: i.planFingerprint } : undefined, source: i.source as QueryPerformanceItem["source"], sourceState: String(i.sourceState), semantics: String(i.semantics), metrics: { [metricKey]: v as number | null }, intervalStartUtc: i.intervalStartUtc, intervalEndUtc: i.intervalEndUtc, coverage: String(i.coverage), fresh: i.fresh, truncated: i.truncated, contentAvailable: false, collectionRunId: i.collectionRunId, observationKey: i.observationKey };
}
export async function getQueryPerformanceTop(instanceId: string, metric: QueryPerformanceMetric, cursor?: string, fromUtc?: string, toUtc?: string, signal?: AbortSignal): Promise<QueryPerformancePage> {
  if (!metrics.has(metric) || fromUtc !== undefined && (toUtc === undefined || !validWindow(fromUtc, toUtc))) throw new Error("Query performance top request bounds were invalid.");
  if (cursor !== undefined && (cursor.length === 0 || cursor.length > 1024)) throw new Error("Query performance cursor exceeded its bound.");
  if (cursor !== undefined && (fromUtc === undefined || toUtc === undefined)) throw new Error("Query performance cursor requires its bound window.");
  const windowQuery = fromUtc === undefined || toUtc === undefined ? "" : `&fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}`;
  const cursorQuery = cursor === undefined ? "" : `&cursor=${encodeURIComponent(cursor)}`;
  const value = await readJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/top?metric=${metric}&limit=200${windowQuery}${cursorQuery}`, instanceId, signal) as { metric?: unknown; items?: unknown; snapshotUtc?: unknown; nextCursor?: unknown; fromUtc?: unknown; toUtc?: unknown };
  if (value.metric !== metric || !timestamp(value.snapshotUtc) || !Array.isArray(value.items) || value.items.length > 200 || value.nextCursor !== undefined && value.nextCursor !== null && (typeof value.nextCursor !== "string" || value.nextCursor.length === 0 || value.nextCursor.length > 1024) || typeof value.fromUtc !== "string" || typeof value.toUtc !== "string" || !timestamp(value.fromUtc) || !timestamp(value.toUtc) || fromUtc !== undefined && canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc) || toUtc !== undefined && canonicalInstant(value.toUtc) !== canonicalInstant(toUtc)) throw new Error("Query performance top response was outside its bounds.");
  const effectiveFrom = value.fromUtc; const effectiveTo = value.toUtc;
  const items = value.items.map(x => { const parsed = item(x, metric); if (effectiveFrom !== undefined && effectiveTo !== undefined && (nativeTicks(parsed.intervalStartUtc) < nativeTicks(effectiveFrom) || nativeTicks(parsed.intervalEndUtc) > nativeTicks(effectiveTo))) throw new Error("Query performance top item was outside its bound window."); return parsed; }); return { repositoryTimeUtc: value.snapshotUtc, items, nextCursor: typeof value.nextCursor === "string" ? value.nextCursor : undefined, fromUtc: effectiveFrom, toUtc: effectiveTo };
}
export async function getQueryPerformance(instanceId: string, signal?: AbortSignal): Promise<QueryPerformancePage> {
  const metrics: QueryPerformanceMetric[] = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"]; const to = new Date().toISOString(); const from = new Date(Date.parse(to) - 24 * 60 * 60 * 1000).toISOString(); const pages = await Promise.all(metrics.map(metric => getQueryPerformanceTop(instanceId, metric, undefined, from, to, signal))); const merged = new Map<string, QueryPerformanceItem>();
  const firstPage = pages[0]; if (firstPage === undefined) throw new Error("Query performance returned no metric pages.");
  if (pages.some(page => page.repositoryTimeUtc !== firstPage.repositoryTimeUtc || page.fromUtc !== firstPage.fromUtc || page.toUtc !== firstPage.toUtc)) throw new Error("Query performance metric pages did not share one repository snapshot and window.");
  pages.forEach(page => page.items.forEach(x => { const key = `${x.collectionRunId}:${x.observationKey}`; const prior = merged.get(key); merged.set(key, prior === undefined ? x : { ...prior, metrics: { ...prior.metrics, ...x.metrics } }); }));
  const cursorsByMetric: Partial<Record<QueryPerformanceMetric, string>> = {}; pages.forEach((page, index) => { const metricName = metrics[index]; if (metricName !== undefined && page.nextCursor !== undefined) cursorsByMetric[metricName] = page.nextCursor; });
  return { repositoryTimeUtc: firstPage.repositoryTimeUtc, items: [...merged.values()], nextCursor: firstPage.nextCursor, cursorsByMetric, fromUtc: firstPage.fromUtc, toUtc: firstPage.toUtc };
}
export async function getQueryPerformanceStatus(instanceId: string, fromUtc?: string, toUtc?: string, signal?: AbortSignal): Promise<QueryPerformanceStatus> {
  if (fromUtc !== undefined && (toUtc === undefined || !validWindow(fromUtc, toUtc))) throw new Error("Query performance status window was invalid.");
  const query = fromUtc === undefined || toUtc === undefined ? "" : `?fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}`;
  const value = await readJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/status${query}`, instanceId, signal) as Record<string, unknown>; const raw = value.databaseStatuses; const rawCatalog = value.databaseCatalog;
  const targetStatuses = new Set(["inventory_failure", "connection_failure", "deadline_exceeded", "circuit_open", "unsupported", "output_invalid", "lease_lost"]);
  const targetReasons = new Set(["inventory_read_failure", "transient_target_failure", "permanent_target_failure", "required_permission_missing", "deadline_exceeded", "circuit_currently_open", "target_unsupported", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "capability_missing", "capability_profile_missing", "capability_profile_stale", "output_validation_failed", "lease_ownership_lost"]);
  if (!timestamp(value.snapshotUtc) || typeof value.source !== "string" || !sources.has(value.source) || typeof value.sourceState !== "string" || !states.has(value.sourceState) || !coverage.has(String(value.coverage)) || typeof value.fresh !== "boolean" || typeof value.truncated !== "boolean" || value.contentAvailable !== false || !Array.isArray(raw) || raw.length > 256 || rawCatalog !== undefined && (!Array.isArray(rawCatalog) || rawCatalog.length > 256) || value.reason !== undefined && value.reason !== null && (typeof value.reason !== "string" || value.reason.length > 128) || value.targetStatus !== undefined && value.targetStatus !== null && (typeof value.targetStatus !== "string" || !targetStatuses.has(value.targetStatus)) || value.targetReason !== undefined && value.targetReason !== null && (typeof value.targetReason !== "string" || !targetReasons.has(value.targetReason))) throw new Error("Query performance status response was outside its bounds.");
  const targetFailure = value.targetStatus !== undefined && value.targetStatus !== null;
  const targetReasonByStatus: Record<string, string[]> = { inventory_failure: ["inventory_read_failure"], connection_failure: ["transient_target_failure", "permanent_target_failure", "required_permission_missing"], deadline_exceeded: ["deadline_exceeded"], circuit_open: ["circuit_currently_open"], unsupported: ["target_unsupported", "target_version_unsupported", "target_platform_unsupported", "target_edition_unsupported", "capability_missing", "capability_profile_missing", "capability_profile_stale"], output_invalid: ["output_validation_failed"], lease_lost: ["lease_ownership_lost"] };
  const hasTargetReason = value.targetReason !== undefined && value.targetReason !== null;
  if (targetFailure !== hasTargetReason || targetFailure && (value.source !== "unavailable" || value.sourceState !== "unavailable" || value.coverage !== "unavailable" || value.fresh !== false || typeof value.targetReason !== "string" || !targetReasonByStatus[String(value.targetStatus)]?.includes(value.targetReason))) throw new Error("Target failure evidence was inconsistent.");
  const seen = new Set<number>();
  const parsed = raw.map(x => { if (typeof x !== "object" || x === null) throw new Error("Database status was invalid."); const s = x as Record<string, unknown>; const databaseId = s.databaseId; const status = s.status; const sourceState = s.sourceState; const reason = s.reason; const fallbackAttempted = s.fallbackAttempted; const truncated = s.truncated; const lossKind = s.lossKind; const sourceRowsRead = s.sourceRowsRead; const responseBytes = s.responseBytes; const queryStoreState = typeof sourceState === "string" && states.has(sourceState) && ["read_write", "read_only", "disabled", "unsupported", "permission_denied", "read_failure", "timed_out"].includes(sourceState); const queryStoreStatus = typeof status === "string" && status.startsWith("query_store_"); const planCacheStatus = status === "plan_cache_rows" || status === "plan_cache_empty" || status === "plan_cache_permission_denied" || status === "plan_cache_read_failure" || status === "plan_cache_timed_out"; const compatible = queryStoreStatus ? ((status === "query_store_rows" || status === "query_store_empty") ? (sourceState === "read_write" || sourceState === "read_only") : status.endsWith("disabled") ? sourceState === "disabled" : status.endsWith("unsupported") ? sourceState === "unsupported" : status.endsWith("permission_denied") ? sourceState === "permission_denied" : status.endsWith("read_failure") ? sourceState === "read_failure" : status.endsWith("timed_out") ? sourceState === "timed_out" : false) : planCacheStatus ? fallbackAttempted === true && queryStoreState : status === "output_capped" && truncated === true && queryStoreState;
    const lossConsistent = typeof lossKind === "string" && (lossKind === "none" ? truncated === false : truncated === true);
    const minimumLostItems = s.minimumLostItems; const lossCountIsExact = s.lossCountIsExact; const minimumLostBytes = s.minimumLostBytes;
    if (typeof databaseId !== "number" || !Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767 || seen.has(databaseId) || typeof status !== "string" || !databaseStatuses.has(status) || typeof sourceState !== "string" || !states.has(sourceState) || sourceState === "mixed" || sourceState === "unavailable" || typeof reason !== "string" || !statusReasons.has(reason) || !statusReasonCompatible(status, reason) || typeof fallbackAttempted !== "boolean" || typeof truncated !== "boolean" || typeof lossKind !== "string" || !lossKinds.has(lossKind) || !lossConsistent || typeof sourceRowsRead !== "number" || !Number.isInteger(sourceRowsRead) || sourceRowsRead < 0 || sourceRowsRead > 2001 || typeof responseBytes !== "number" || !Number.isInteger(responseBytes) || responseBytes < 0 || responseBytes > maximumResponseBytes || typeof minimumLostItems !== "number" || !Number.isInteger(minimumLostItems) || minimumLostItems < 0 || minimumLostItems > 20000 || typeof lossCountIsExact !== "boolean" || typeof minimumLostBytes !== "number" || !Number.isInteger(minimumLostBytes) || minimumLostBytes < 0 || minimumLostBytes > maximumResponseBytes || lossKind === "none" && (minimumLostItems !== 0 || minimumLostBytes !== 0 || !lossCountIsExact) || !compatible) throw new Error("Database status was outside its bounds."); seen.add(databaseId); return { databaseId, status, sourceState, reason, fallbackAttempted, truncated, lossKind, sourceRowsRead, responseBytes, minimumLostItems, lossCountIsExact, minimumLostBytes } satisfies QueryPerformanceDatabaseStatus; });
  const catalogSeen = new Set<number>();
  const catalogInput = rawCatalog === undefined ? [] : rawCatalog as unknown[];
  const catalog: QueryPerformanceDatabaseCatalog[] = catalogInput.map(x => { if (typeof x !== "object" || x === null) throw new Error("Database catalog entry was invalid."); const entry = x as Record<string, unknown>; const databaseId = entry.databaseId; const databaseName = entry.databaseName; if (typeof databaseId !== "number" || !Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767 || catalogSeen.has(databaseId) || typeof databaseName !== "string" || databaseName.length === 0 || databaseName.length > 1024) throw new Error("Database catalog entry was outside its bounds."); catalogSeen.add(databaseId); return { databaseId, databaseName } satisfies QueryPerformanceDatabaseCatalog; });
  return { targetId: instanceId, snapshotUtc: value.snapshotUtc as string, source: value.source as QueryPerformanceStatus["source"], sourceState: value.sourceState, coverage: String(value.coverage), fresh: value.fresh, truncated: value.truncated, reason: typeof value.reason === "string" ? value.reason : undefined, targetStatus: typeof value.targetStatus === "string" ? value.targetStatus : undefined, targetReason: typeof value.targetReason === "string" ? value.targetReason : undefined, databaseStatuses: parsed, databaseCatalog: catalog };
}
export async function getQueryPerformanceHistory(instanceId: string, databaseId: number, queryFingerprint: string, fromUtc: string, toUtc: string, signal?: AbortSignal, cursor?: string): Promise<QueryPerformanceHistoryPage> {
  if (!Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767 || !digest(queryFingerprint)) throw new Error("Query identity was invalid.");
  if (!validWindow(fromUtc, toUtc)) throw new Error("Query performance history window was invalid.");
  if (cursor !== undefined && (cursor.length === 0 || cursor.length > 1024)) throw new Error("Query performance history cursor was invalid.");
  const cursorQuery = cursor === undefined ? "" : `&cursor=${encodeURIComponent(cursor)}`;
  const value = await readJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/databases/${databaseId}/history/${queryFingerprint}?fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}&limit=200${cursorQuery}`, instanceId, signal) as { databaseId?: unknown; queryFingerprint?: unknown; items?: unknown; snapshotUtc?: unknown; fromUtc?: unknown; toUtc?: unknown; nextCursor?: unknown };
  if (value.databaseId !== databaseId || value.queryFingerprint !== queryFingerprint || !timestamp(value.snapshotUtc) || typeof value.fromUtc !== "string" || typeof value.toUtc !== "string" || canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc) || canonicalInstant(value.toUtc) !== canonicalInstant(toUtc) || !timestamp(value.fromUtc) || !timestamp(value.toUtc) || !Array.isArray(value.items) || value.items.length > 200 || value.nextCursor !== undefined && value.nextCursor !== null && (typeof value.nextCursor !== "string" || value.nextCursor.length === 0 || value.nextCursor.length > 1024)) throw new Error("Query performance history response was outside its bounds.");
  const items = value.items.map(raw => { const x = raw as Record<string, unknown>; if (x.databaseId !== databaseId || x.queryFingerprint !== queryFingerprint || !metric(x.cpuMilliseconds) || !metric(x.durationMilliseconds) || !metric(x.executions) || !metric(x.logicalReads) || !metric(x.writes) || !metric(x.rows)) throw new Error("Query performance history metrics were invalid."); const base = item({ ...x, metric: "cpu", value: x.cpuMilliseconds ?? null }, "cpu"); if (nativeTicks(base.intervalStartUtc) < nativeTicks(fromUtc) || nativeTicks(base.intervalEndUtc) > nativeTicks(toUtc)) throw new Error("Query performance history item was outside its bound window."); return { ...base, metrics: { cpuMilliseconds: x.cpuMilliseconds ?? null, durationMilliseconds: x.durationMilliseconds ?? null, executions: x.executions ?? null, logicalReads: x.logicalReads ?? null, writes: x.writes ?? null, rows: x.rows ?? null } }; });
  return { repositoryTimeUtc: value.snapshotUtc, items, nextCursor: typeof value.nextCursor === "string" ? value.nextCursor : undefined };
}
export async function getQueryPerformancePlan(instanceId: string, databaseId: number, queryFingerprint: string, planFingerprint: string, signal?: AbortSignal): Promise<QueryPerformancePlan> {
  if (!Number.isInteger(databaseId) || databaseId <= 0 || databaseId > 32767 || !digest(queryFingerprint) || !digest(planFingerprint)) throw new Error("Plan identity was invalid.");
  const value = await readJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/databases/${databaseId}/plans/${planFingerprint}?queryFingerprint=${queryFingerprint}`, instanceId, signal) as Record<string, unknown>;
  if (value.databaseId !== databaseId || value.queryFingerprint !== queryFingerprint || value.planFingerprint !== planFingerprint || !sources.has(String(value.source)) || value.source === "mixed" || value.source === "unavailable" || !timestamp(value.observedAtUtc) || !coverage.has(String(value.coverage)) || typeof value.contentAvailable !== "boolean") throw new Error("Query performance plan response was outside its bounds.");
  return { targetId: instanceId, databaseId, queryFingerprint, planFingerprint, source: value.source as QueryPerformancePlan["source"], observedAtUtc: value.observedAtUtc, coverage: String(value.coverage), contentAvailable: value.contentAvailable };
}

export async function getQueryStoreTopWaits(instanceId: string, fromUtc: string,
    toUtc: string, signal: AbortSignal, category?: number): Promise<QueryStoreWaitRanking> {
  if (!validWindow(fromUtc, toUtc)
      || category !== undefined && (!Number.isInteger(category) || category < 0 || category > 20))
    throw new Error("Query wait ranking window was invalid.");
  const path = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/waits/top`
    + `?fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}&limit=25`
    + (category === undefined ? "" : `&category=${category}`);
  const value = await readJson(path, instanceId, signal, 64 * 1024) as Record<string, unknown>;
  if (!timestamp(value.fromUtc) || !timestamp(value.toUtc)
      || canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc)
      || canonicalInstant(value.toUtc) !== canonicalInstant(toUtc)
      || value.category !== (category ?? null)
      || value.semantics !== "query_store_group_wait_delta"
      || value.coverage !== "observed_groups_only" && value.coverage !== "no_comparable_groups"
      || !Array.isArray(value.items) || value.items.length > 25)
    throw new Error("Query wait ranking response was outside its bounds.");
  const seen = new Set<string>();
  let prior: bigint | undefined;
  const items = value.items.map((raw: unknown) => {
    if (typeof raw !== "object" || raw === null) throw new Error("Query wait ranking row was invalid.");
    const row = raw as Record<string, unknown>;
    if (!Number.isSafeInteger(row.databaseId) || (row.databaseId as number) < 1 ||
        (row.databaseId as number) > 32767 || !digest(row.queryFingerprint) ||
        !digest(row.planFingerprint) || typeof row.waitMilliseconds !== "string" ||
        !/^(?:0|[1-9]\d{0,38})$/u.test(row.waitMilliseconds) ||
        !Number.isSafeInteger(row.comparableRows) || (row.comparableRows as number) < 1 ||
        !Number.isSafeInteger(row.sourceRows) || (row.sourceRows as number) < (row.comparableRows as number) ||
        !timestamp(row.lastObservedAtUtc) || nativeTicks(row.lastObservedAtUtc) < nativeTicks(fromUtc) ||
        nativeTicks(row.lastObservedAtUtc) >= nativeTicks(toUtc))
      throw new Error("Query wait ranking row was invalid.");
    const identity = `${row.databaseId}:${row.queryFingerprint}:${row.planFingerprint}`;
    const wait = BigInt(row.waitMilliseconds);
    if (seen.has(identity) || prior !== undefined && wait > prior)
      throw new Error("Query wait ranking order was invalid.");
    seen.add(identity);
    prior = wait;
    return row as QueryStoreWaitRanking["items"][number];
  });
  if (value.coverage === "no_comparable_groups" && items.length > 0)
    throw new Error("Query wait ranking coverage was invalid.");
  return { fromUtc, toUtc, category: category ?? null,
    coverage: value.coverage as QueryStoreWaitRanking["coverage"], items };
}

export async function getQueryStorePlanRegressions(instanceId: string, fromUtc: string,
    toUtc: string, signal: AbortSignal): Promise<QueryStorePlanRegressionList> {
  if (!validWindow(fromUtc, toUtc))
    throw new Error("Plan regression window was invalid.");
  const path = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/regressions`
    + `?fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}&limit=25`;
  const value = await readJson(path, instanceId, signal, 64 * 1024) as Record<string, unknown>;
  if (!timestamp(value.fromUtc) || !timestamp(value.toUtc) || !timestamp(value.comparisonFromUtc)
      || canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc)
      || canonicalInstant(value.toUtc) !== canonicalInstant(toUtc)
      || nativeTicks(value.comparisonFromUtc) !== nativeTicks(fromUtc) - (nativeTicks(toUtc) - nativeTicks(fromUtc))
      || value.semantics !== "query_store_group_plan_comparison"
      || value.coverage !== "observed_groups_only" && value.coverage !== "no_qualifying_changes"
      || !Array.isArray(value.items) || value.items.length > 25)
    throw new Error("Plan regression response was outside its bounds.");
  const positiveDecimal = (input: unknown): input is string =>
    typeof input === "string" && /^(?:0|[1-9]\d{0,38})(?:\.\d{1,26})?$/u.test(input);
  const seen = new Set<string>();
  let priorRatio: number | undefined;
  const items = value.items.map((raw: unknown) => {
    if (typeof raw !== "object" || raw === null) throw new Error("Plan regression row was invalid.");
    const row = raw as Record<string, unknown>;
    const ratio = Number(row.slowdownRatio);
    if (!Number.isSafeInteger(row.databaseId) || (row.databaseId as number) < 1 ||
        (row.databaseId as number) > 32767 || !digest(row.queryFingerprint) ||
        !digest(row.previousPlanFingerprint) || !digest(row.currentPlanFingerprint) ||
        row.previousPlanFingerprint === row.currentPlanFingerprint ||
        !positiveDecimal(row.previousAverageMilliseconds) ||
        !positiveDecimal(row.currentAverageMilliseconds) ||
        !positiveDecimal(row.slowdownRatio) || !Number.isFinite(ratio) || ratio < 2 ||
        !positiveDecimal(row.previousExecutions) || !positiveDecimal(row.currentExecutions) ||
        !Number.isSafeInteger(row.previousComparableRows) || (row.previousComparableRows as number) < 2 ||
        !Number.isSafeInteger(row.currentComparableRows) || (row.currentComparableRows as number) < 2 ||
        !Number.isSafeInteger(row.previousSourceRows) ||
        (row.previousSourceRows as number) < (row.previousComparableRows as number) ||
        !Number.isSafeInteger(row.currentSourceRows) ||
        (row.currentSourceRows as number) < (row.currentComparableRows as number) ||
        !timestamp(row.lastObservedAtUtc) || nativeTicks(row.lastObservedAtUtc) < nativeTicks(fromUtc) ||
        nativeTicks(row.lastObservedAtUtc) >= nativeTicks(toUtc) ||
        priorRatio !== undefined && ratio > priorRatio)
      throw new Error("Plan regression row was invalid.");
    const identity = `${row.databaseId}:${row.queryFingerprint}`;
    if (seen.has(identity)) throw new Error("Plan regression identity was duplicated.");
    seen.add(identity);
    priorRatio = ratio;
    return row as QueryStorePlanRegressionList["items"][number];
  });
  if (value.coverage === "no_qualifying_changes" && items.length > 0)
    throw new Error("Plan regression coverage was invalid.");
  return { fromUtc, toUtc, comparisonFromUtc: value.comparisonFromUtc,
    coverage: value.coverage as QueryStorePlanRegressionList["coverage"], items };
}


export async function getPlanCacheIntervalRanking(instanceId: string, fromUtc: string, toUtc: string,
    rankingMetric: QueryPerformanceMetric, signal: AbortSignal): Promise<PlanCacheIntervalRanking> {
  if (!validWindow(fromUtc, toUtc) || !metrics.has(rankingMetric))
    throw new Error("Cache ranking window or metric was invalid.");
  const path = `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/query-performance/cache/top`
    + `?fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}&metric=${rankingMetric}&limit=25`;
  const value = await readJson(path, instanceId, signal, 256 * 1024) as Record<string, unknown>;
  const count = (input: unknown): input is number => typeof input === "number" && Number.isSafeInteger(input) && input >= 0;
  if (!timestamp(value.fromUtc) || !timestamp(value.toUtc) || canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc)
      || canonicalInstant(value.toUtc) !== canonicalInstant(toUtc) || value.metric !== rankingMetric || value.semantics !== "plan_cache_delta"
      || !count(value.sourceFrames) || !count(value.incompleteFrames) || value.incompleteFrames > value.sourceFrames
      || !count(value.comparableRows) || !count(value.unavailableRows) || !count(value.boundaryRows)
      || typeof value.hasMore !== "boolean" || !Array.isArray(value.items) || value.items.length > 25
      || value.hasMore && value.items.length !== 25
      || value.coverage !== (value.comparableRows === 0 ? "no_comparable_rows" : "observed_entries_only")
      || (value.comparableRows === 0) !== (value.items.length === 0)
      || BigInt(value.comparableRows) + BigInt(value.unavailableRows) + BigInt(value.boundaryRows) > BigInt(value.sourceFrames) * 2000n)
    throw new Error("Cache ranking response was outside its scope or coverage bounds.");
  const exact = (input: unknown, integer = false): bigint => {
    if (typeof input !== "string" || !/^(?:0|[1-9]\d{0,28})(?:\.\d{1,24})?$/u.test(input))
      throw new Error("Cache ranking metric was invalid.");
    const [whole, fraction = ""] = input.split(".");
    if (integer && /[1-9]/u.test(fraction)) throw new Error("Cache ranking count was fractional.");
    return BigInt(whole + fraction.padEnd(24, "0"));
  };
  const fields = ["cpuMilliseconds", "durationMilliseconds", "executions", "logicalReads", "writes", "rows"];
  const rankField = ["cpu", "duration", "executions", "logical_reads", "writes", "rows"].indexOf(rankingMetric);
  const seen = new Set<string>(); let previous: bigint | undefined; let included = 0n;
  const items = value.items.map((input: unknown) => {
    if (typeof input !== "object" || input === null) throw new Error("Cache ranking row was invalid.");
    const row = input as Record<string, unknown>;
    if (!count(row.databaseId) || row.databaseId < 5 || row.databaseId > 32766 || !digest(row.databaseIncarnation)
        || !digest(row.queryFingerprint) || !digest(row.planFingerprint) || !count(row.comparableRows) || row.comparableRows === 0
        || !timestamp(row.firstObservedAtUtc) || !timestamp(row.lastObservedAtUtc)
        || canonicalInstant(row.firstObservedAtUtc) < canonicalInstant(fromUtc)
        || canonicalInstant(row.lastObservedAtUtc) >= canonicalInstant(toUtc)
        || canonicalInstant(row.lastObservedAtUtc) <= canonicalInstant(row.firstObservedAtUtc))
      throw new Error("Cache ranking row identity or window was invalid.");
    const identity = `${row.databaseId}:${row.databaseIncarnation}:${row.queryFingerprint}:${row.planFingerprint}`;
    const values = fields.map((field, index) => exact(row[field], index >= 2));
    const score = values[rankField];
    if (score === undefined) throw new Error("Cache ranking metric was invalid.");
    if (seen.has(identity) || previous !== undefined && score > previous) throw new Error("Cache ranking order was invalid.");
    seen.add(identity); previous = score; included += BigInt(row.comparableRows);
    return row as PlanCacheIntervalRanking["items"][number];
  });
  if (included > BigInt(value.comparableRows)) throw new Error("Cache ranking coverage was invalid.");
  return { targetId: instanceId, fromUtc, toUtc, metric: rankingMetric, semantics: "plan_cache_delta",
    coverage: value.coverage as PlanCacheIntervalRanking["coverage"], sourceFrames: value.sourceFrames,
    incompleteFrames: value.incompleteFrames, comparableRows: value.comparableRows,
    unavailableRows: value.unavailableRows, boundaryRows: value.boundaryRows, hasMore: value.hasMore, items };
}

export async function getPlanCacheIntervalHistory(selection: PlanCacheHistorySelection, fromUtc: string, toUtc: string,
    signal: AbortSignal, cursor?: string): Promise<PlanCacheIntervalHistory> {
  const count = (value: unknown): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
  const utc = (value: unknown): value is string => timestamp(value) && canonicalInstant(value).endsWith("0Z");
  const ticks = (value: string): bigint => nativeTicks(value) * 10n;
  if (!count(selection.databaseId) || selection.databaseId < 5 || selection.databaseId > 32766
      || ![selection.databaseIncarnation, selection.queryFingerprint, selection.planFingerprint].every(value => /^[0-9a-f]{64}$/u.test(value))
      || !utc(fromUtc) || !utc(toUtc) || ticks(toUtc) <= ticks(fromUtc) || ticks(toUtc) - ticks(fromUtc) > 6048000000000n
      || cursor !== undefined && (cursor.length > 728 || !/^[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+$/u.test(cursor)))
    throw new Error("Cache history scope or window was invalid.");
  const parameters = new URLSearchParams({ databaseIncarnation: selection.databaseIncarnation, fromUtc, toUtc, limit: "100" });
  if (cursor) parameters.set("cursor", cursor);
  const value = await readJson(`/api/v1/observation-targets/${encodeURIComponent(selection.targetId)}/query-performance/cache/databases/${selection.databaseId}/history/${selection.queryFingerprint}/plans/${selection.planFingerprint}?${parameters}`,
    selection.targetId, signal, 262144, true) as Record<string, unknown>;
  if (value.databaseId !== selection.databaseId || value.databaseIncarnation !== selection.databaseIncarnation
      || value.queryFingerprint !== selection.queryFingerprint || value.planFingerprint !== selection.planFingerprint
      || !utc(value.fromUtc) || canonicalInstant(value.fromUtc) !== canonicalInstant(fromUtc)
      || !utc(value.toUtc) || canonicalInstant(value.toUtc) !== canonicalInstant(toUtc)
      || value.semantics !== "plan_cache_delta" || value.coverage !== "observed_entries_only"
      || !utc(value.snapshotUtc) || !count(value.revision) || value.revision < 1 || !count(value.totalRows)
      || !Array.isArray(value.items) || value.items.length > 100 || value.items.length > value.totalRows
      || value.nextCursor !== null && (typeof value.nextCursor !== "string" || value.nextCursor.length > 728
        || !/^[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+$/u.test(value.nextCursor))
      || value.nextCursor !== null && value.items.length !== 100
      || cursor === undefined && (value.items.length !== Math.min(100, value.totalRows) || (value.nextCursor !== null) !== (value.totalRows > 100)))
    throw new Error("Cache history response exceeded its snapshot bounds.");
  const fields = ["cpuMilliseconds", "durationMilliseconds", "executions", "logicalReads", "writes", "rows"];
  const seen = new Set<string>(); let previous: string | undefined;
  const items = value.items.map((input: unknown) => {
    if (!input || typeof input !== "object") throw new Error("Cache history row was invalid.");
    const row = input as Record<string, unknown>;
    if (typeof row.collectionRunId !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(row.collectionRunId)
        || row.collectionRunId === "00000000-0000-0000-0000-000000000000" || !digest(row.cacheEntryFingerprint) || !digest(row.sourceEpoch)
        || !utc(row.observedAtUtc) || !utc(row.committedAtUtc) || ticks(row.observedAtUtc) < ticks(fromUtc) || ticks(row.observedAtUtc) >= ticks(toUtc)
        || ticks(row.committedAtUtc) < ticks(fromUtc) - 3000000000n || ticks(row.committedAtUtc) >= ticks(toUtc) + 3000000000n
        || typeof row.captureComplete !== "boolean") throw new Error("Cache history row identity was invalid.");
    const key = `${canonicalInstant(row.observedAtUtc)}:${canonicalInstant(row.committedAtUtc)}:${row.collectionRunId}:${row.cacheEntryFingerprint}`;
    const identity = `${row.collectionRunId}:${row.cacheEntryFingerprint}`;
    if (seen.has(identity) || previous !== undefined && key >= previous) throw new Error("Cache history row order was invalid.");
    seen.add(identity); previous = key;
    const comparable = row.transition === "comparable", boundary = row.transition === "window_boundary";
    if (comparable || boundary) {
      if (!utc(row.intervalFromUtc) || !utc(row.intervalToUtc) || canonicalInstant(row.intervalToUtc) !== canonicalInstant(row.observedAtUtc)
          || ticks(row.intervalToUtc) <= ticks(row.intervalFromUtc) || ticks(row.intervalToUtc) - ticks(row.intervalFromUtc) > 6048000000000n
          || comparable && ticks(row.intervalFromUtc) < ticks(fromUtc) || boundary && ticks(row.intervalFromUtc) >= ticks(fromUtc))
        throw new Error("Cache history comparison interval was invalid.");
    } else if (!["baseline_unavailable", "reset", "stale", "comparison_window_expired"].includes(String(row.transition))
        || row.intervalFromUtc !== null || row.intervalToUtc !== null) throw new Error("Cache history state was invalid.");
    fields.forEach((field, index) => {
      const number = row[field];
      if (!comparable) { if (number !== null) throw new Error("Unavailable comparison included counters."); return; }
      if (typeof number !== "string" || !/^(?:0|[1-9]\d{0,18})(?:\.\d{1,24})?$/u.test(number) || index >= 2 && number.includes("."))
        throw new Error("Cache history counter was invalid.");
      const [whole = "0", fraction = ""] = number.split(".");
      if (index < 2 && /[1-9]/u.test(fraction.slice(3))) throw new Error("Cache history counter precision was invalid.");
      const native = index < 2 ? BigInt(whole) * 1000n + BigInt(fraction.slice(0, 3).padEnd(3, "0")) : BigInt(whole);
      if (native > 9223372036854775807n) throw new Error("Cache history counter exceeded its native bound.");
    });
    return row as PlanCacheIntervalHistory["items"][number];
  });
  return { ...selection, fromUtc, toUtc, snapshotUtc: value.snapshotUtc, revision: value.revision, totalRows: value.totalRows,
    nextCursor: value.nextCursor as string | null, items };
}
