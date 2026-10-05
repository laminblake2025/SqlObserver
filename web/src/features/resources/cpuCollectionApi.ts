import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";
export interface CpuCollectionStatus { readonly targetId: string; readonly targetRevision: number; readonly collectorEnabled: boolean; readonly scheduleRevision: number }
export function parseCpuCollectionStatus(value: unknown, targetId: string, targetRevision: number): CpuCollectionStatus {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).sort().join("|") !== "collectorEnabled|scheduleRevision|targetId|targetRevision") throw new Error("CPU collection status is invalid.");
  const row = value as CpuCollectionStatus;
  if (!notificationId(row.targetId) || row.targetId.toLowerCase() !== targetId.toLowerCase() || row.targetRevision !== targetRevision || typeof row.collectorEnabled !== "boolean" || !Number.isSafeInteger(row.scheduleRevision) || row.scheduleRevision < 1) throw new Error("CPU collection status does not match this server revision.");
  return row;
}
export class CpuCollectionWriteError extends Error {
  readonly outcomeUnknown: boolean;
  readonly status?: number;
  constructor(message: string, outcomeUnknown: boolean, status?: number) { super(message); this.outcomeUnknown = outcomeUnknown; this.status = status; }
}
const unknownOutcome = "The CPU collection change outcome is unknown. Reload current state before another change; the write is not retried automatically.";
async function request(targetId: string, targetRevision: number, signal: AbortSignal, change?: { expectedEnabled: boolean; enabled: boolean }) {
  if (!notificationId(targetId) || !Number.isSafeInteger(targetRevision) || targetRevision < 1 || change && (typeof change.expectedEnabled !== "boolean" || typeof change.enabled !== "boolean")) throw new Error("Choose a valid server revision and collection state.");
  signal.throwIfAborted();
  let response: Response;
  try {
    response = await apiFetch(`/api/v1/observation-targets/${targetId.toLowerCase()}/resources/cpu/collection${change ? "" : `?targetRevision=${targetRevision}`}`, {
      method: change ? "PUT" : "GET", credentials: "same-origin", cache: "no-store", signal,
      headers: { Accept: "application/json", ...(change ? { "Content-Type": "application/json" } : {}) },
      ...(change ? { body: JSON.stringify({ expectedTargetRevision: targetRevision, ...change }) } : {}),
    }, { maximumResponseBytes: 16384 });
  } catch { if (change) throw new CpuCollectionWriteError(unknownOutcome, true); throw new Error("CPU collection status could not be read. Reload current state."); }
  if (!response.ok) {
    const message = [401,403].includes(response.status) ? "CPU collection access was denied. Reload current state after access is restored."
      : response.status === 409 ? "CPU collection state or target revision changed. Reload current state before another change."
      : response.status === 404 ? "CPU collection status is unavailable for this server."
      : response.status === 429 ? "Too many collection changes. Wait, then reload current state."
      : "CPU collection request was refused. Reload current state.";
    if (change) throw new CpuCollectionWriteError([400,401,403,404,409,429].includes(response.status) ? message : unknownOutcome, ![400,401,403,404,409,429].includes(response.status), response.status);
    throw new ApiRequestError(message, response.status);
  }
  try {
    if (response.status !== 200 || !/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error();
    const result = parseCpuCollectionStatus(await readJsonBounded(response, 16384), targetId, targetRevision);
    signal.throwIfAborted();
    if (change && result.collectorEnabled !== change.enabled) throw new Error();
    return result;
  } catch { if (change) throw new CpuCollectionWriteError(unknownOutcome, true, response.status); throw new Error("CPU collection status is invalid. Reload current state."); }
}
export const getCpuCollectionStatus = (targetId: string, revision: number, signal: AbortSignal) => request(targetId, revision, signal);
export const setCpuCollectionStatus = (targetId: string, revision: number, expectedEnabled: boolean, enabled: boolean, signal: AbortSignal) => request(targetId, revision, signal, { expectedEnabled, enabled });
