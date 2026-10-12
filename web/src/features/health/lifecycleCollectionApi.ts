import { apiFetch, ApiRequestError } from "../../apiClient.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import { readJsonBounded } from "../alerts/alertRuntimeParser.ts";

export interface LifecycleCollectionStatus {
  readonly targetId: string;
  readonly targetRevision: number;
  readonly scheduleRevision: number;
  readonly collectorEnabled: boolean;
}
const positiveRevision = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) > 0;
export function parseLifecycleCollectionStatus(value: unknown, targetId: string, targetRevision: number): LifecycleCollectionStatus {
  if (!notificationId(targetId) || !positiveRevision(targetRevision) || !value || typeof value !== "object" || Array.isArray(value)
      || Object.keys(value).sort().join("|") !== "collectorEnabled|scheduleRevision|targetId|targetRevision") throw new Error("Lifecycle collection status is invalid.");
  const row = value as LifecycleCollectionStatus;
  if (!notificationId(row.targetId) || row.targetId.toLowerCase() !== targetId.toLowerCase() || row.targetRevision !== targetRevision
      || !positiveRevision(row.scheduleRevision) || typeof row.collectorEnabled !== "boolean") throw new Error("Lifecycle collection status does not match this server revision.");
  return row;
}
export class LifecycleCollectionWriteError extends Error {
  readonly outcomeUnknown: boolean;
  readonly status?: number;
  constructor(message: string, outcomeUnknown: boolean, status?: number) { super(message); this.outcomeUnknown = outcomeUnknown; this.status = status; }
}
const unknownOutcome = "The lifecycle collection change outcome is unknown. Read current state before another change; this write will not be retried automatically.";
async function request(targetId: string, targetRevision: number, signal: AbortSignal,
  change?: { readonly expectedScheduleRevision: number; readonly expectedEnabled: boolean; readonly enabled: boolean }): Promise<LifecycleCollectionStatus> {
  if (!notificationId(targetId) || !positiveRevision(targetRevision) || change && (!positiveRevision(change.expectedScheduleRevision)
      || typeof change.expectedEnabled !== "boolean" || typeof change.enabled !== "boolean")) throw new Error("Choose a valid server revision and collection state.");
  signal.throwIfAborted();
  let response: Response;
  try {
    response = await apiFetch(`/api/v1/observation-targets/${targetId.toLowerCase()}/lifecycle/collection${change ? "" : `?targetRevision=${targetRevision}`}`, {
      method: change ? "PUT" : "GET", signal, credentials: "same-origin", cache: "no-store",
      headers: { Accept: "application/json", ...(change ? { "Content-Type": "application/json" } : {}) },
      ...(change ? { body: JSON.stringify({ expectedTargetRevision: targetRevision, ...change }) } : {}),
    }, { maximumResponseBytes: 16384 });
  } catch {
    if (change) throw new LifecycleCollectionWriteError(unknownOutcome, true);
    throw new Error("Lifecycle collection state could not be read. Try reading current state again.");
  }
  if (!response.ok) {
    const message = [401, 403].includes(response.status) ? "Lifecycle collection access was denied. Restore access before reading current state again."
      : response.status === 409 ? "The target or collection schedule changed. Read current state before another change."
      : response.status === 404 ? "Lifecycle collection is unavailable for this server."
      : response.status === 429 ? "Too many collection requests. Wait, then read current state."
      : "Lifecycle collection request was refused. Read current state again.";
    if (change) {
      const refused = [400, 401, 403, 404, 409, 429].includes(response.status);
      throw new LifecycleCollectionWriteError(refused ? message : unknownOutcome, !refused, response.status);
    }
    throw new ApiRequestError(message, response.status);
  }
  try {
    if (response.status !== 200 || !/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error();
    const value = parseLifecycleCollectionStatus(await readJsonBounded(response, 16384), targetId, targetRevision);
    signal.throwIfAborted();
    if (change && (value.collectorEnabled !== change.enabled || value.scheduleRevision !== change.expectedScheduleRevision + (change.expectedEnabled === change.enabled ? 0 : 1))) throw new Error();
    return value;
  } catch {
    if (change) throw new LifecycleCollectionWriteError(unknownOutcome, true, response.status);
    throw new Error("Lifecycle collection status is invalid. Read current state again.");
  }
}
export const getLifecycleCollectionStatus = (targetId: string, revision: number, signal: AbortSignal) => request(targetId, revision, signal);
export const setLifecycleCollectionStatus = (expected: LifecycleCollectionStatus, enabled: boolean, signal: AbortSignal) => request(expected.targetId, expected.targetRevision, signal, {
  expectedScheduleRevision: expected.scheduleRevision, expectedEnabled: expected.collectorEnabled, enabled,
});
