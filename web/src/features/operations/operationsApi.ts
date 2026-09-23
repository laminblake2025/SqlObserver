import { responseFailure } from "../../api/responseFailure.ts";
import { readBoundedJson } from "./operationsParser.mjs";
import type { OperationalKind, OperationalPage, OperationalState } from "./operationsTypes";

const paths: Record<OperationalKind, string> = { backups: "backups", agent: "sql-agent/failures", "tempdb-summary": "tempdb", "tempdb-files": "tempdb/files", "ag-replicas": "availability-groups/replicas", "ag-databases": "availability-groups/databases" };
export class OperationalApiError extends Error { readonly status: number; readonly retryable: boolean; constructor(status: number, message: string, retryable = false) { super(message); this.name = "OperationalApiError"; this.status = status; this.retryable = retryable; } }
const record = (value: unknown): value is Record<string, unknown> => typeof value === "object" && value !== null && !Array.isArray(value);
const isState = (value: string): value is OperationalState => ["Complete", "Partial", "Degraded", "Unsupported", "PermissionDenied", "NoData"].includes(value);
function asPage(value: Readonly<Record<string, unknown>>): OperationalPage {
  const targetId = value.targetId, targetRevision = value.targetRevision, runId = value.runId, evidence = value.evidence, observedAtUtc = value.observedAtUtc, state = value.state, items = value.items, hasMore = value.hasMore, nextCursor = value.nextCursor, truncated = value.truncated;
  if (typeof targetId !== "string" || typeof targetRevision !== "number" || (runId !== null && typeof runId !== "string") || typeof observedAtUtc !== "string" || typeof state !== "string" || !isState(state) || !Array.isArray(items) || !items.every(record) || typeof hasMore !== "boolean" || (nextCursor !== null && typeof nextCursor !== "string") || typeof truncated !== "boolean") throw new OperationalApiError(502, "Operational health returned an invalid shape.");
  return { targetId, targetRevision, runId, evidence: record(evidence) ? evidence : null, observedAtUtc, state, items, hasMore, nextCursor, truncated, coverageFromUtc: typeof value.coverageFromUtc === "string" ? value.coverageFromUtc : null, coverageToUtc: typeof value.coverageToUtc === "string" ? value.coverageToUtc : null, totalBytes: typeof value.totalBytes === "number" ? value.totalBytes : null, usedBytes: typeof value.usedBytes === "number" ? value.usedBytes : null, logTotalBytes: typeof value.logTotalBytes === "number" ? value.logTotalBytes : null, logUsedBytes: typeof value.logUsedBytes === "number" ? value.logUsedBytes : null, visibilityScope: typeof value.visibilityScope === "string" ? value.visibilityScope : null };
}
function message(status: number): [string, boolean] { if (status === 400) return ["The operational-health request is invalid.", false]; if (status === 403) return ["You do not have permission to view this target.", false]; if (status === 404) return ["This target has no operational-health evidence.", false]; if (status === 409) return ["Operational-health evidence changed; reload the target.", true]; if (status === 413) return ["Operational-health response exceeded its safety bound.", true]; if (status === 504) return ["Operational-health request timed out.", true]; return ["Operational health is temporarily unavailable.", true]; }
export interface OperationalQuery { readonly cursor?: string | null; readonly limit?: number; readonly fromUtc?: string; readonly toUtc?: string; readonly signal?: AbortSignal; }
export async function getOperationalPage(instanceId: string, kind: OperationalKind, query: OperationalQuery = {}): Promise<OperationalPage> {
  const params = new URLSearchParams(); if (query.limit !== undefined) params.set("limit", String(query.limit)); if (query.cursor) params.set("cursor", query.cursor); if (kind === "agent") { if (query.fromUtc) params.set("fromUtc", query.fromUtc); if (query.toUtc) params.set("toUtc", query.toUtc); }
  const suffix = params.toString(); const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/${paths[kind]}${suffix ? `?${suffix}` : ""}`, { credentials: "same-origin", headers: { Accept: "application/json" }, signal: query.signal });
  if (!response.ok) { const [text, retryable] = message(response.status); throw new OperationalApiError(response.status, responseFailure(response, text).message, retryable); }
  try { return asPage(await readBoundedJson(response, kind, instanceId, query.signal)); } catch (error) { if (error instanceof DOMException && error.name === "AbortError") throw error; if (error instanceof OperationalApiError) throw error; throw new OperationalApiError(502, "Operational health returned invalid data."); }
}
export const getBackups = (id: string, query?: OperationalQuery) => getOperationalPage(id, "backups", query);
export const getAgentFailures = (id: string, query?: OperationalQuery) => getOperationalPage(id, "agent", query);
export const getTempDbSummary = (id: string, query?: OperationalQuery) => getOperationalPage(id, "tempdb-summary", query);
export const getTempDbFiles = (id: string, query?: OperationalQuery) => getOperationalPage(id, "tempdb-files", query);
export const getAvailabilityReplicas = (id: string, query?: OperationalQuery) => getOperationalPage(id, "ag-replicas", query);
export const getAvailabilityDatabases = (id: string, query?: OperationalQuery) => getOperationalPage(id, "ag-databases", query);
export const getTempDb = getTempDbSummary;
export const getAvailabilityGroupReplicas = getAvailabilityReplicas;
export const getAvailabilityGroupDatabases = getAvailabilityDatabases;
export const getOperationalSnapshot = (id: string, path: string, signal: AbortSignal) => getOperationalPage(id, path === "sql-agent/failures" ? "agent" : path === "tempdb/files" ? "tempdb-files" : path === "tempdb" ? "tempdb-summary" : path === "availability-groups/replicas" ? "ag-replicas" : path === "availability-groups/databases" ? "ag-databases" : "backups", { signal });
