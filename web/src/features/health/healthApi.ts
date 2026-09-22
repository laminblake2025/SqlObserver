import { responseFailure } from "../../api/responseFailure.ts";
import type {
  DatabaseFileHealthPage,
  DatabaseHealthPage,
  TargetHealthSnapshot,
} from "./healthTypes";

const firstPageLimit = 25;

export class HealthRequestError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "HealthRequestError";
  }
}

export async function getTargetHealth(
  instanceId: string,
  signal: AbortSignal,
): Promise<TargetHealthSnapshot> {
  return getJson(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health`,
    signal,
  );
}

export async function getDatabaseHealth(
  instanceId: string,
  signal: AbortSignal,
): Promise<DatabaseHealthPage> {
  return getJson(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/databases?limit=${String(firstPageLimit)}`,
    signal,
  );
}

export async function getDatabaseFileHealth(
  instanceId: string,
  signal: AbortSignal,
): Promise<DatabaseFileHealthPage> {
  return getJson(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/files?limit=${String(firstPageLimit)}`,
    signal,
  );
}

async function getJson<T>(path: string, signal: AbortSignal): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      credentials: "same-origin",
      headers: { Accept: "application/json" },
      signal,
    });
  } catch (error: unknown) {
    if (signal.aborted) {
      throw error;
    }

    throw new HealthRequestError("The health service is temporarily unavailable.");
  }

  if (!response.ok) {
    throw new HealthRequestError(responseFailure(response, getSafeHealthFailure(response.status)).message);
  }

  return (await response.json()) as T;
}

function getSafeHealthFailure(status: number): string {
  switch (status) {
    case 403:
      return "You are not authorized to view health for this target.";
    case 404:
      return "No health snapshot is available for this target.";
    case 429:
      return "Too many health requests. Wait briefly and retry.";
    case 504:
      return "The health request exceeded its execution limit.";
    default:
      return "The health request failed safely.";
  }
}
