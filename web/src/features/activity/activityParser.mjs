export const pageLimit = 25;
export const maximumResponseBytes = 256 * 1024;

export class ActivityRequestError extends Error {}

export async function getPage(url, parseItem, expectedInstanceId, signal) {
  let response;
  try { response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" }, signal }); }
  catch (error) { if (signal.aborted) throw error; throw new ActivityRequestError("Activity evidence is temporarily unavailable."); }
  if (!response.ok) throw new ActivityRequestError(safeStatusMessage(response.status));
  const declaredLength = response.headers.get("content-length");
  if (declaredLength !== null && (!/^\d+$/u.test(declaredLength) || Number(declaredLength) > maximumResponseBytes)) throw invalidResponse();
  let value;
  try { value = JSON.parse(await readBoundedBody(response, signal)); }
  catch (error) { if (error instanceof ActivityRequestError) throw error; throw invalidResponse(); }
  return parsePage(value, parseItem, expectedInstanceId);
}

export async function readBoundedBody(response, signal) {
  if (response.body === null) throw invalidResponse();
  const reader = response.body.getReader();
  const chunks = []; let total = 0;
  try {
    while (true) {
      if (signal.aborted) throw signal.reason;
      const next = await reader.read();
      if (next.done) break;
      total += next.value.byteLength;
      if (total > maximumResponseBytes) throw invalidResponse();
      chunks.push(next.value);
    }
  } finally { await reader.cancel().catch(() => {}); }
  const bytes = new Uint8Array(total); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
}

export function parsePage(value, parseItem, expectedInstanceId) {
  const record = object(value);
  const instanceId = safeGuid(record.instanceId);
  if (expectedInstanceId !== undefined && instanceId.toLowerCase() !== expectedInstanceId.toLowerCase()) throw invalidResponse();
  const repositoryTimeUtc = safeTimestamp(record.repositoryTimeUtc);
  if (!Array.isArray(record.items) || record.items.length > pageLimit) throw invalidResponse();
  const evidence = record.evidence === undefined || record.evidence === null ? undefined : parseEvidence(record.evidence);
  const fromUtc = record.fromUtc === undefined ? undefined : safeTimestamp(record.fromUtc);
  const toUtc = record.toUtc === undefined ? undefined : safeTimestamp(record.toUtc);
  if ((fromUtc === undefined) !== (toUtc === undefined)) throw invalidResponse();
  const nextCursor = record.nextCursor === null || record.nextCursor === undefined ? undefined : safeCursor(record.nextCursor);
  const baselineRunId = record.baselineRunId === null || record.baselineRunId === undefined ? undefined : safeGuid(record.baselineRunId);
  const maximumChainDepth = record.maximumChainDepth === undefined ? undefined : safeInteger(record.maximumChainDepth);
  const maximumGraphNodes = record.maximumGraphNodes === undefined ? undefined : safeInteger(record.maximumGraphNodes);
  return { instanceId, repositoryTimeUtc, items: record.items.map(parseItem), ...(evidence ? { evidence } : {}), ...(fromUtc ? { fromUtc, toUtc } : {}), ...(nextCursor ? { nextCursor } : {}), ...(baselineRunId ? { baselineRunId } : {}), ...(maximumChainDepth === undefined ? {} : { maximumChainDepth }), ...(maximumGraphNodes === undefined ? {} : { maximumGraphNodes }) };
}

export function parseEvidence(value) { const r = object(value); return { runId: safeGuid(r.runId), targetRevision: safeText(r.targetRevision), collectorId: safeText(r.collectorId), freshness: safeText(r.freshness), outcome: safeText(r.outcome), reason: safeText(r.reason), isPartial: safeBoolean(r.isPartial), completedAtUtc: safeTimestamp(r.completedAtUtc), ...(r.loss == null ? {} : { loss: parseLoss(r.loss) }) }; }
function parseLoss(value) { const r = object(value); return { kind: safeText(r.kind), minimumLostItems: safeInteger(r.minimumLostItems), countIsExact: safeBoolean(r.countIsExact), minimumLostBytes: safeInteger(r.minimumLostBytes) }; }
export function parseSession(value) { const r = object(value); return { sessionId: safeInteger(r.sessionId), status: safeText(r.status), isUserProcess: safeBoolean(r.isUserProcess), ...(r.databaseId == null ? {} : { databaseId: safeInteger(r.databaseId) }), openTransactionCount: safeInteger(r.openTransactionCount), cpuMilliseconds: safeCounter(r.cpuMilliseconds), memoryUsagePages: safeCounter(r.memoryUsagePages), reads: safeCounter(r.reads), writes: safeCounter(r.writes), logicalReads: safeCounter(r.logicalReads), totalElapsedMilliseconds: safeCounter(r.totalElapsedMilliseconds), observedAtUtc: safeTimestamp(r.observedAtUtc) }; }
export function parseRequest(value) { const r = object(value); return { sessionId: safeInteger(r.sessionId), requestId: safeInteger(r.requestId), status: safeText(r.status), command: safeText(r.command), ...(r.databaseId == null ? {} : { databaseId: safeInteger(r.databaseId) }), cpuMilliseconds: safeCounter(r.cpuMilliseconds), totalElapsedMilliseconds: safeCounter(r.totalElapsedMilliseconds), reads: safeCounter(r.reads), writes: safeCounter(r.writes), logicalReads: safeCounter(r.logicalReads), rowCount: safeCounter(r.rowCount), percentComplete: safePercent(r.percentComplete), observedAtUtc: safeTimestamp(r.observedAtUtc) }; }
export function parseWait(value) { const r = object(value); return { waitType: safeText(r.waitType), waitingTasksCount: safeCounter(r.waitingTasksCount), waitTimeMilliseconds: safeCounter(r.waitTimeMilliseconds), maximumWaitTimeMilliseconds: safeCounter(r.maximumWaitTimeMilliseconds), signalWaitTimeMilliseconds: safeCounter(r.signalWaitTimeMilliseconds), baselineAvailable: safeBoolean(r.baselineAvailable), resetDetected: safeBoolean(r.resetDetected), ...(r.waitingTasksDelta == null ? {} : { waitingTasksDelta: safeCounter(r.waitingTasksDelta) }), ...(r.waitTimeMillisecondsDelta == null ? {} : { waitTimeMillisecondsDelta: safeCounter(r.waitTimeMillisecondsDelta) }), ...(r.signalWaitTimeMillisecondsDelta == null ? {} : { signalWaitTimeMillisecondsDelta: safeCounter(r.signalWaitTimeMillisecondsDelta) }), observedAtUtc: safeTimestamp(r.observedAtUtc) }; }
export function parseEdge(value) { const r = object(value); return { blockedSessionId: safeInteger(r.blockedSessionId), blockerKind: safeText(r.blockerKind), ...(r.blockerSessionId == null ? {} : { blockerSessionId: safeInteger(r.blockerSessionId) }), waitType: safeText(r.waitType), waitingTaskCount: safeCounter(r.waitingTaskCount), waitDurationMilliseconds: safeCounter(r.waitDurationMilliseconds), ...(r.rootBlockerSessionId == null ? {} : { rootBlockerSessionId: safeInteger(r.rootBlockerSessionId) }), chainDepth: safeInteger(r.chainDepth), chainState: safeText(r.chainState), observedAtUtc: safeTimestamp(r.observedAtUtc) }; }
export function parseHistory(value) { const r = object(value); return { evidence: parseEvidence(r.evidence), edge: parseEdge(r.edge) }; }
function object(value) { if (typeof value !== "object" || value === null || Array.isArray(value)) throw invalidResponse(); return value; }
export function safeText(value) { if (typeof value !== "string" || value.length === 0 || value.length > 160 || /[\u0000-\u001f\u007f]/u.test(value)) throw invalidResponse(); return value; }
export function safeCursor(value) { if (typeof value !== "string" || value.length === 0 || value.length > 512 || /[\u0000-\u001f\u007f]/u.test(value)) throw invalidResponse(); return value; }
function safeCounter(value) { if (typeof value !== "string" || !/^\d{1,24}$/u.test(value)) throw invalidResponse(); return value; }
function safeTimestamp(value) { if (typeof value !== "string" || value.length > 64 || Number.isNaN(Date.parse(value))) throw invalidResponse(); return value; }
function safeGuid(value) { if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu.test(value)) throw invalidResponse(); return value; }
function safeInteger(value) { if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 0) throw invalidResponse(); return value; }
function safePercent(value) { if (typeof value !== "number" || !Number.isFinite(value) || value < 0 || value > 100) throw invalidResponse(); return value; }
function safeBoolean(value) { if (typeof value !== "boolean") throw invalidResponse(); return value; }
export function invalidResponse() { return new ActivityRequestError("Activity evidence returned an invalid response."); }
export function safeStatusMessage(status) { if (status === 403) return "You are not authorized to view activity for this target."; if (status === 404) return "No activity snapshot is available for this target."; if (status === 504) return "The activity request exceeded its execution limit."; return "The activity request failed safely."; }
