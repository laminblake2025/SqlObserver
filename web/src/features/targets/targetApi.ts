import { apiFetch } from "../../apiClient.ts";
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
  const response = await apiFetch(`${targetsPath}?limit=${limit}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ""}`, {
    credentials: "same-origin",
    headers: { Accept: "application/json" },
    signal,
  });

  return readJson<ObservationTargetPage>(response);
}

export async function registerObservationTarget(
  request: RegisterObservationTargetRequest,
  signal: AbortSignal,
): Promise<ObservationTargetSummary> {
  const response = await apiFetch(targetsPath, {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(request),
    signal,
  });

  return readJson<ObservationTargetSummary>(response);
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    if (response.status === 429) {
      throw new Error("Too many administrative requests. Wait briefly and retry.");
    }

    throw new Error(`SqlObserver request failed with status ${response.status}.`);
  }

  return (await response.json()) as T;
}

export async function getObservationTarget(instanceId: string, signal: AbortSignal): Promise<ObservationTargetSummary> {
  const response = await apiFetch(`${targetsPath}/${encodeURIComponent(instanceId)}`, { credentials: "same-origin", headers: { Accept: "application/json" }, signal });
  return readJson<ObservationTargetSummary>(response);
}

export async function updateObservationTargetGrouping(
  instanceId: string, expectedRevision: number, environment: string | null,
  tags: readonly string[], signal: AbortSignal,
): Promise<{ instanceId: string; environment: string | null; tags: readonly string[]; groupingRevision: number }> {
  const response = await apiFetch(`${targetsPath}/${encodeURIComponent(instanceId)}/grouping`, {
    method: "PUT", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ expectedRevision, environment, tags }), signal,
  });
  return readJson(response);
}
