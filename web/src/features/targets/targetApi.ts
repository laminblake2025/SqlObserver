import { apiFetch } from "../../apiClient.ts";
import { invalidTargetResponse, maximumTargetResponseBytes, parseObservationTarget, parseObservationTargetGrouping,
  parseObservationTargetPage } from "./targetResponseParser.ts";
import { targetManagementIdentity, targetManagementReceipt, targetManagementUpdate, type TargetManagementAction } from "./targetManagementModel.ts";
import type {
  ObservationTargetPage,
  ObservationTargetSummary,
  RegisterObservationTargetRequest,
  UpdateObservationTargetRequest,
} from "./targetTypes";
export type { UpdateObservationTargetRequest } from "./targetTypes";

const targetsPath = "/api/v1/observation-targets";

export async function listObservationTargets(
  signal: AbortSignal,
  cursor?: string,
  limit = 50,
): Promise<ObservationTargetPage> {
  if (!Number.isInteger(limit) || limit < 1 || limit > 100) throw new Error("Invalid target page limit.");
  return getJson(`${targetsPath}?limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`, {
    credentials: "same-origin",
    headers: { Accept: "application/json" },
    signal,
  }, signal, value => parseObservationTargetPage(value, limit));
}

export async function registerObservationTarget(
  request: RegisterObservationTargetRequest,
  signal: AbortSignal,
): Promise<ObservationTargetSummary> {
  return getJson(targetsPath, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(request),
    signal,
  }, signal, value => parseObservationTarget(value, request.instanceId));
}

async function getJson<T>(path: string, init: RequestInit, signal: AbortSignal, parse: (value: unknown) => T): Promise<T> {
  let response: Response;
  try { response = await apiFetch(path, init, { maximumResponseBytes: maximumTargetResponseBytes }); }
  catch (error) {
    if (signal.aborted) throw signal.reason ?? error;
    if (error instanceof Error && /API response is too large|API response.*invalid length/u.test(error.message)) throw invalidTargetResponse();
    throw new Error("The target service is temporarily unavailable. Retry the request.");
  }
  if (signal.aborted) throw signal.reason;
  if (!response.ok) {
    if (response.status === 429) {
      throw new Error("Too many administrative requests. Wait briefly and retry.");
    }

    throw new Error(`SqlObserver request failed with status ${response.status}.`);
  }

  try {
    return parse(await readTargetResponse(response, signal));
  } catch (error) {
    if (signal.aborted) throw signal.reason ?? error;
    throw invalidTargetResponse();
  }
}

async function readTargetResponse(response: Response, signal: AbortSignal): Promise<unknown> {
  if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw invalidTargetResponse();
  const bytes = await response.arrayBuffer();
  if (signal.aborted) throw signal.reason;
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

export async function getObservationTarget(instanceId: string, signal: AbortSignal): Promise<ObservationTargetSummary> {
  return getJson(`${targetsPath}/${encodeURIComponent(instanceId)}`, { credentials: "same-origin", headers: { Accept: "application/json" }, signal }, signal,
    value => parseObservationTarget(value, instanceId));
}

export async function updateObservationTargetGrouping(
  instanceId: string, expectedRevision: number, environment: string | null,
  tags: readonly string[], signal: AbortSignal,
): Promise<{ instanceId: string; environment: string | null; tags: readonly string[]; groupingRevision: number }> {
  let normalizedEnvironment: string | null, normalizedTags: string[];
  try {
    if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(instanceId) ||
      instanceId === "00000000-0000-0000-0000-000000000000" ||
      !Number.isSafeInteger(expectedRevision) || expectedRevision < 0 || expectedRevision >= Number.MAX_SAFE_INTEGER ||
      !Array.isArray(tags) || tags.length > 16) throw new Error();
    const normalize = (value: string | null): string | null => {
      if (value === null) return null;
      if (typeof value !== "string") throw new Error();
      const label = value.trim().toLowerCase();
      if (!label) return null;
      if (label.length > 64 || !/^[a-z0-9][a-z0-9._:\-]*$/u.test(label)) throw new Error();
      return label;
    };
    normalizedEnvironment = normalize(environment);
    normalizedTags = tags.map(tag => { const label = normalize(tag); if (!label) throw new Error(); return label; }).sort();
    if (new Set(normalizedTags).size !== normalizedTags.length) throw new Error();
  } catch {
    throw new TargetManagementError("Use a valid server revision and up to 16 unique labels of 1–64 letters, digits, dots, underscores, colons, or hyphens.", 400, false);
  }
  if (signal.aborted) throw signal.reason;
  let response: Response;
  try {
    response = await apiFetch(`${targetsPath}/${encodeURIComponent(instanceId)}/grouping`, {
      method: "PUT", credentials: "same-origin",
      headers: { Accept: "application/json", "Content-Type": "application/json" },
      body: JSON.stringify({ expectedRevision, environment, tags }), signal,
    }, { maximumResponseBytes: maximumTargetResponseBytes });
  } catch {
    throw new TargetManagementError(unknownGroupingOutcome, undefined, true);
  }
  if (!response.ok) {
    const message = response.status === 409 ? "These labels changed elsewhere. Reload server labels before saving again."
      : response.status === 403 ? "Editing server labels requires the Target Administrator role on this server."
      : response.status === 400 ? "The server labels were refused. Check the labels and revision."
      : response.status === 429 ? "Too many administrative requests. Wait briefly and retry."
      : response.status === 401 ? "Your access could not be verified. Reload server labels."
      : response.status === 404 ? "The selected server is unavailable. Reload server labels."
      : unknownGroupingOutcome;
    throw new TargetManagementError(message, response.status, ![400, 401, 403, 404, 409, 429].includes(response.status));
  }
  try {
    if (response.status !== 200) throw invalidTargetResponse();
    const receipt = parseObservationTargetGrouping(await readTargetResponse(response, signal), instanceId);
    if (receipt.groupingRevision !== expectedRevision + 1 || receipt.environment !== normalizedEnvironment ||
      receipt.tags.length !== normalizedTags.length || receipt.tags.some((tag, index) => tag !== normalizedTags[index]))
      throw invalidTargetResponse();
    return receipt;
  } catch {
    throw new TargetManagementError(unknownGroupingOutcome, response.status, true);
  }
}

const unknownGroupingOutcome = "The server labels outcome is unknown. Reload server labels before saving again.";

export class TargetManagementError extends Error {
  readonly status: number | undefined;
  readonly outcomeUnknown: boolean;

  constructor(message: string, status: number | undefined, outcomeUnknown: boolean) {
    super(message); this.name = "TargetManagementError";
    this.status = status; this.outcomeUnknown = outcomeUnknown;
  }
}

const unknownManagementOutcome = "The target management outcome is unknown. Reload current state before trying another action.";

function managementStatusError(status: number): TargetManagementError {
  const message = status === 409 ? "The server configuration changed. Reload current state before trying another action."
    : status === 429 ? "Too many administrative requests. Wait briefly and retry."
    : status === 403 ? "Target management requires the Target Administrator role on this server."
    : status === 401 ? "Your access could not be verified. Reload current state before trying another action."
    : status === 404 ? "The selected server is unavailable. Reload current state."
    : status === 400 ? "The server configuration request was refused. Check the connection fields and revision."
    : unknownManagementOutcome;
  return new TargetManagementError(message, status, ![400, 401, 403, 404, 409, 429].includes(status));
}

async function manageObservationTarget(instanceId: string, expectedRevision: number, action: TargetManagementAction,
  signal: AbortSignal, update?: UpdateObservationTargetRequest): Promise<ObservationTargetSummary> {
  let identity: string, body: UpdateObservationTargetRequest | { expectedRevision: number };
  try {
    identity = targetManagementIdentity(instanceId, expectedRevision);
    body = action === "update" ? targetManagementUpdate(identity, update!) : { expectedRevision };
  } catch {
    throw new TargetManagementError("Choose a valid server identity, revision and connection configuration.", undefined, false);
  }
  if (signal.aborted) throw signal.reason;
  let response: Response;
  try {
    response = await apiFetch(`${targetsPath}/${identity}${action === "update" ? "" : `/${action}`}`, {
      method: action === "update" ? "PUT" : "POST", credentials: "same-origin",
      headers: { Accept: "application/json", "Content-Type": "application/json" }, body: JSON.stringify(body), signal,
    }, { maximumResponseBytes: maximumTargetResponseBytes });
  } catch {
    // These routes have no replay identity. Cancellation after dispatch can also
    // lose a committed response; the next action must follow authoritative readback.
    throw new TargetManagementError(unknownManagementOutcome, undefined, true);
  }
  if (!response.ok) throw managementStatusError(response.status);
  try {
    if (response.status !== 200) throw invalidTargetResponse();
    return targetManagementReceipt(await readTargetResponse(response, signal), identity, expectedRevision, action,
      action === "update" ? body as UpdateObservationTargetRequest : undefined);
  } catch {
    throw new TargetManagementError(unknownManagementOutcome, response.status, true);
  }
}

export function updateObservationTarget(instanceId: string, request: UpdateObservationTargetRequest,
  signal: AbortSignal): Promise<ObservationTargetSummary> {
  return manageObservationTarget(instanceId, request?.expectedRevision, "update", signal, request);
}

export function requestObservationTargetRediscovery(instanceId: string, expectedRevision: number,
  signal: AbortSignal): Promise<ObservationTargetSummary> {
  return manageObservationTarget(instanceId, expectedRevision, "rediscovery", signal);
}

export function retireObservationTarget(instanceId: string, expectedRevision: number,
  signal: AbortSignal): Promise<ObservationTargetSummary> {
  return manageObservationTarget(instanceId, expectedRevision, "retire", signal);
}
