export const MAX_RESPONSE_BYTES = 1024 * 1024;
export const MAX_PAGE_SIZE = 200;
export const maximumResponseBytes = MAX_RESPONSE_BYTES;
export const pageLimit = MAX_PAGE_SIZE;
const STATES = new Set(["Complete", "Partial", "Degraded", "Unsupported", "PermissionDenied", "NoData"]);
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const HEX = /^[0-9a-f]{64}$/i;
const AGENT_ITEM_KEYS = new Set([
  "jobId", "historyInstanceId", "stepId", "runStatus", "failureKind", "messageId", "severity", "retryAttempt", "durationSeconds", "firstObservedAtUtc", "contentAvailable", "failureFingerprint",
  "JobId", "HistoryInstanceId", "StepId", "RunStatus", "FailureKind", "MessageId", "Severity", "RetryAttempt", "DurationSeconds", "FirstObservedAtUtc", "ContentAvailable", "FailureFingerprint",
]);
const utc = value => typeof value === "string" && /Z$/.test(value) && Number.isFinite(Date.parse(value));
const field = (object, lower, upper) => object[lower] ?? object[upper];
const fail = message => { throw new TypeError(`Invalid operational-health response: ${message}`); };
const integer = (value, name, min = 0) => { if (!Number.isSafeInteger(value) || value < min) fail(name); return value; };
const string = (value, name, max = 1024) => { if (typeof value !== "string" || value.length === 0 || value.length > max) fail(name); return value; };

export function parseOperationalPage(value, kind = "backups", expectedTargetId = undefined) {
  // Keep the test/runtime API forgiving about argument order while retaining a strict payload contract.
  if (typeof kind === "string" && GUID.test(kind) && (expectedTargetId === undefined || !GUID.test(expectedTargetId))) [kind, expectedTargetId] = [expectedTargetId ?? "backups", kind];
  if (!value || typeof value !== "object" || Array.isArray(value)) fail("root");
  const targetId = string(field(value, "targetId", "TargetId"), "targetId", 64);
  if (!GUID.test(targetId)) fail("targetId");
  if (expectedTargetId !== undefined && targetId.toLowerCase() !== expectedTargetId.toLowerCase()) fail("target binding");
  const targetRevision = integer(field(value, "targetRevision", "TargetRevision"), "targetRevision", 1);
  const observedAtUtc = string(field(value, "observedAtUtc", "ObservedAtUtc"), "observedAtUtc", 64);
  if (!utc(observedAtUtc)) fail("observedAtUtc");
  const state = string(field(value, "state", "State"), "state", 32);
  if (!STATES.has(state)) fail("state");
  const runId = field(value, "runId", "RunId");
  if (runId !== null && runId !== undefined && !GUID.test(String(runId))) fail("runId");
  const evidence = field(value, "evidence", "Evidence");
  if (state === "NoData" && (runId != null || evidence != null)) fail("NoData evidence");
  if (evidence != null && (!evidence || typeof evidence !== "object" || Array.isArray(evidence))) fail("evidence");
  const items = field(value, "items", "Items");
  if (!Array.isArray(items) || items.length > MAX_PAGE_SIZE) fail("items");
  if (state === "NoData" && items.length !== 0) fail("NoData items");
  const hasMore = field(value, "hasMore", "HasMore");
  if (typeof hasMore !== "boolean") fail("hasMore");
  const nextCursor = field(value, "nextCursor", "NextCursor");
  if (hasMore) string(nextCursor, "nextCursor", 1024); else if (nextCursor !== null && nextCursor !== undefined) fail("cursor without hasMore");
  if (state === "NoData" && hasMore) fail("NoData cursor");
  const truncated = field(value, "truncated", "Truncated");
  if (typeof truncated !== "boolean") fail("truncated");
  const output = { targetId, targetRevision, runId: runId ?? null, evidence: evidence ?? null, observedAtUtc, state, items, hasMore, nextCursor: nextCursor ?? null, truncated };
  if (kind === "backups") items.forEach(validateBackup);
  else if (kind === "agent") { validateCoverage(value, output); items.forEach(validateAgent); }
  else if (kind === "tempdb-summary" || kind === "tempdb") Object.assign(output, validateTempDbSummary(value, items));
  else if (kind === "tempdb-files" || kind === "tempdb/files") items.forEach(validateTempDbFile);
  else if (kind === "ag-replicas" || kind === "availability-groups/replicas") { output.visibilityScope = validateVisibility(value); items.forEach(validateReplica); }
  else if (kind === "ag-databases" || kind === "availability-groups/databases") { output.visibilityScope = validateVisibility(value); items.forEach(validateDatabase); }
  else fail("kind");
  return Object.freeze(output);
}

function validateBackup(item) {
  if (!item || typeof item !== "object" || !HEX.test(field(item, "databaseFingerprint", "DatabaseFingerprint")) || !["Full", "Differential", "Log"].includes(field(item, "kind", "Kind")) || !["Complete", "NotSeenWithin35Days", "Truncated", "Unknown"].includes(field(item, "coverage", "Coverage"))) fail("backup evidence");
  const size = field(item, "sizeBytes", "SizeBytes"); if (size != null) integer(size, "backup size");
  const finish = field(item, "lastFinishUtc", "LastFinishUtc"); if (finish != null && !utc(finish)) fail("backup UTC");
  const setId = field(item, "backupSetId", "BackupSetId"); if (setId != null) integer(setId, "backup set");
}
function validateAgent(item) {
  if (!item || typeof item !== "object" || !GUID.test(String(field(item, "jobId", "JobId"))) || !HEX.test(field(item, "failureFingerprint", "FailureFingerprint"))) fail("agent identity");
  for (const key of Object.keys(item)) if (!AGENT_ITEM_KEYS.has(key)) fail("agent unknown field");
  integer(field(item, "historyInstanceId", "HistoryInstanceId"), "agent history"); integer(field(item, "stepId", "StepId"), "agent step"); integer(field(item, "retryAttempt", "RetryAttempt"), "agent retry"); integer(field(item, "durationSeconds", "DurationSeconds"), "agent duration");
  if (field(item, "contentAvailable", "ContentAvailable") !== false) fail("agent sensitive content");
  if (!utc(field(item, "firstObservedAtUtc", "FirstObservedAtUtc"))) fail("agent first observed");
}
function optionalInteger(value, lower, upper, name) {
  const candidate = field(value, lower, upper);
  return candidate == null ? null : integer(candidate, name);
}
function validateTempDbSummary(value, items) {
  if (items.length !== 0) fail("summary files");
  const totalBytes = optionalInteger(value, "totalBytes", "TotalBytes", "totalBytes");
  const usedBytes = optionalInteger(value, "usedBytes", "UsedBytes", "usedBytes");
  const logTotalBytes = optionalInteger(value, "logTotalBytes", "LogTotalBytes", "logTotalBytes");
  const logUsedBytes = optionalInteger(value, "logUsedBytes", "LogUsedBytes", "logUsedBytes");
  if ((totalBytes != null && usedBytes != null && usedBytes > totalBytes) || (logTotalBytes != null && logUsedBytes != null && logUsedBytes > logTotalBytes)) fail("summary consistency");
  return { totalBytes, usedBytes, logTotalBytes, logUsedBytes };
}
function validateTempDbFile(item) { if (!item || typeof item !== "object") fail("tempdb file"); integer(field(item, "fileId", "FileId"), "file id"); const values = ["sizeBytes", "usedBytes", "freeBytes"].map(name => field(item, name, name[0].toUpperCase() + name.slice(1))); values.forEach((v, i) => integer(v, ["sizeBytes", "usedBytes", "freeBytes"][i])); if (values[1] + values[2] > values[0]) fail("tempdb consistency"); }
function validateVisibility(value) { const visibility = field(value, "visibilityScope", "VisibilityScope"); if (typeof visibility !== "string" || !["PrimaryAllKnown", "SecondaryLocalOnly", "ResolvingLocalOnly"].includes(visibility)) fail("AG visibility"); return visibility; }
function token(value) { return typeof value === "string" && value.length > 0 && value.length <= 64 && /^[A-Za-z0-9 _-]+$/.test(value); }
function validateReplica(item) { if (!item || !HEX.test(field(item, "groupFingerprint", "GroupFingerprint")) || !HEX.test(field(item, "replicaFingerprint", "ReplicaFingerprint")) || !token(field(item, "role", "Role")) || !token(field(item, "operationalState", "OperationalState")) || !token(field(item, "connectedState", "ConnectedState")) || !["PrimaryAllKnown", "SecondaryLocalOnly", "ResolvingLocalOnly"].includes(field(item, "visibilityScope", "VisibilityScope")) || typeof field(item, "stateAvailable", "StateAvailable") !== "boolean") fail("AG replica evidence"); }
function validateDatabase(item) { if (!item || !HEX.test(field(item, "groupFingerprint", "GroupFingerprint")) || !HEX.test(field(item, "databaseFingerprint", "DatabaseFingerprint")) || !token(field(item, "synchronizationState", "SynchronizationState")) || !token(field(item, "databaseState", "DatabaseState")) || !["PrimaryAllKnown", "SecondaryLocalOnly", "ResolvingLocalOnly"].includes(field(item, "visibilityScope", "VisibilityScope")) || typeof field(item, "stateAvailable", "StateAvailable") !== "boolean") fail("AG database evidence"); }
function validateCoverage(value, output) { const from = field(value, "coverageFromUtc", "CoverageFromUtc"), to = field(value, "coverageToUtc", "CoverageToUtc"); if ((from != null && !utc(from)) || (to != null && !utc(to))) fail("Agent coverage"); if (from != null && to != null && Date.parse(from) >= Date.parse(to)) fail("Agent coverage order"); output.coverageFromUtc = from ?? null; output.coverageToUtc = to ?? null; }

export async function readBoundedJson(response, kind = "backups", expectedTargetId = undefined, signal = undefined) {
  const text = await readBoundedBody(response, signal);
  return parseOperationalPage(JSON.parse(text), kind, expectedTargetId);
}
export async function readBoundedBody(response, signal = undefined, maximumBytes = MAX_RESPONSE_BYTES) {
  if (!response || !response.body || typeof response.body.getReader !== "function") { const text = await response.text(); if (new TextEncoder().encode(text).byteLength > maximumBytes) fail("response size"); return text; }
  const length = response.headers?.get?.("content-length"); if (length && (!/^\d+$/.test(length) || Number(length) > maximumBytes)) fail("response size");
  const reader = response.body.getReader(); const decoder = new TextDecoder("utf-8", { fatal: true }); let bytes = 0; let text = "";
  try { for (;;) { if (signal?.aborted) throw new DOMException("The operation was aborted.", "AbortError"); const part = await reader.read(); if (part.done) break; bytes += part.value.byteLength; if (bytes > maximumBytes) fail("response size"); text += decoder.decode(part.value, { stream: true }); } text += decoder.decode(); return text; } finally { await reader.cancel().catch(() => {}); }
}
export const parseOperationsPage = parseOperationalPage;
export const validateOperationalPage = parseOperationalPage;
export const readBoundedResponse = readBoundedJson;
export const parsePage = parseOperationalPage;
export class OperationalRequestError extends Error {}
export function safeStatusMessage(status) { if (status === 403) return "You are not authorized to view this target."; if (status === 404) return "No operational-health evidence is available."; if (status === 409) return "Operational-health evidence changed; reload the target."; if (status === 504) return "Operational-health execution exceeded its time limit."; return "Operational health is temporarily unavailable."; }
export async function getPage(url, kind, expectedTargetId, signal) { let response; try { response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" }, signal }); } catch (error) { if (signal?.aborted) throw error; throw new OperationalRequestError("Operational health is temporarily unavailable."); } if (!response.ok) throw new OperationalRequestError(safeStatusMessage(response.status)); return parseOperationalPage(JSON.parse(await readBoundedBody(response, signal)), kind, expectedTargetId); }
