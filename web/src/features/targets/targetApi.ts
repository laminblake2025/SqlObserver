import { apiFetch } from "../../apiClient.ts";
import { invalidTargetResponse, maximumTargetResponseBytes, parseObservationTarget, parseObservationTargetGrouping,
  parseObservationTargetPage } from "./targetResponseParser.ts";
import type {
  ObservationTargetPage,
  ObservationTargetSummary,
  RegisterObservationTargetRequest,
} from "./targetTypes";

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
    if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw invalidTargetResponse();
    const bytes = await response.arrayBuffer();
    if (signal.aborted) throw signal.reason;
    return parse(JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)));
  } catch (error) {
    if (signal.aborted) throw signal.reason ?? error;
    throw invalidTargetResponse();
  }
}

export async function getObservationTarget(instanceId: string, signal: AbortSignal): Promise<ObservationTargetSummary> {
  return getJson(`${targetsPath}/${encodeURIComponent(instanceId)}`, { credentials: "same-origin", headers: { Accept: "application/json" }, signal }, signal,
    value => parseObservationTarget(value, instanceId));
}

export async function updateObservationTargetGrouping(
  instanceId: string, expectedRevision: number, environment: string | null,
  tags: readonly string[], signal: AbortSignal,
): Promise<{ instanceId: string; environment: string | null; tags: readonly string[]; groupingRevision: number }> {
  return getJson(`${targetsPath}/${encodeURIComponent(instanceId)}/grouping`, {
    method: "PUT", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, environment, tags }), signal,
  }, signal, value => parseObservationTargetGrouping(value, instanceId));
}
