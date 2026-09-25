import type {
  DatabaseFileHealthPage,
  DatabaseHealthPage,
  TargetHealthEvidence,
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
  cursor?: string,
): Promise<DatabaseHealthPage> {
  const parameters = new URLSearchParams({ limit: String(firstPageLimit) });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  return getJson(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/databases?${parameters}`,
    signal,
  );
}

export async function getDatabaseFileHealth(
  instanceId: string,
  signal: AbortSignal,
  cursor?: string,
): Promise<DatabaseFileHealthPage> {
  const parameters = new URLSearchParams({ limit: String(firstPageLimit) });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  return getJson(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/files?${parameters}`,
    signal,
  );
}

export async function getTargetHealthEvidence(
  instanceId: string,
  signal: AbortSignal,
): Promise<TargetHealthEvidence> {
  const [target, databases, files] = await Promise.allSettled([
    getTargetHealth(instanceId, signal),
    getDatabaseHealth(instanceId, signal),
    getDatabaseFileHealth(instanceId, signal),
  ]);
  if (target.status === "rejected") {
    throw target.reason;
  }

  if (databases.status === "rejected") {
    throw databases.reason;
  }

  if (files.status === "rejected") {
    throw files.reason;
  }

  return { target: target.value, databases: databases.value, files: files.value };
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
    throw new HealthRequestError(getSafeHealthFailure(response.status));
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
