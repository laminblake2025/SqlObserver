import { apiFetch } from "../../apiClient.ts";
import { parseDeadlockDetail, parseDeadlockPage } from "./deadlockParser.mjs";
import { readBoundedBody } from "../activity/activityParser.mjs";
import type { DeadlockDetail, DeadlockPage } from "./deadlockTypes";
import { deadlockPageLimit, validateDeadlockReceipt, validateDeadlockRequest, type DeadlockWindow } from "./deadlockPaging.ts";
export class DeadlockRequestError extends Error {
  readonly status?: number;
  constructor(message: string, status?: number) { super(message); this.status = status; }
}
export function deadlockAccessDenied(error: unknown): boolean {
  return error instanceof DeadlockRequestError && (error.status === 401 || error.status === 403);
}
function boundedSignal(signal?: AbortSignal): AbortSignal { return signal ?? new AbortController().signal; }
export async function getDeadlocks(instanceId: string, signal?: AbortSignal, cursor?: string, window?: DeadlockWindow): Promise<DeadlockPage> {
  if (window) validateDeadlockRequest(instanceId, window, cursor);
  const query = new URLSearchParams({ limit: String(deadlockPageLimit) });
  if (window) { query.set("fromUtc", window.fromUtc); query.set("toUtc", window.toUtc); }
  if (cursor !== undefined) query.set("cursor", cursor);
  try {
    const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/deadlocks?${query}`, { signal, headers: { Accept: "application/json" } });
    if (!response.ok) throw new DeadlockRequestError("Deadlock evidence is unavailable.", response.status);
    const page = parseDeadlockPage(await readBoundedBody(response, boundedSignal(signal)), instanceId);
    if (page.items.length > deadlockPageLimit) throw new Error("Invalid page limit.");
    if (window) validateDeadlockReceipt(page, instanceId, window, [], cursor);
    return page;
  } catch (error) {
    if (signal?.aborted || error instanceof DeadlockRequestError) throw error;
    throw new DeadlockRequestError("Deadlock evidence returned an invalid or unavailable response.");
  }
}
export async function getDeadlock(instanceId: string, eventId: string, signal?: AbortSignal): Promise<DeadlockDetail> {
  try {
    const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/deadlocks/${encodeURIComponent(eventId)}`, { signal, headers: { Accept: "application/json" } });
    if (!response.ok) throw new DeadlockRequestError("Deadlock detail is unavailable.", response.status);
    return parseDeadlockDetail(await readBoundedBody(response, boundedSignal(signal)), instanceId, eventId);
  } catch (error) {
    if (signal?.aborted || error instanceof DeadlockRequestError) throw error;
    throw new DeadlockRequestError("Deadlock detail returned an invalid or unavailable response.");
  }
}
