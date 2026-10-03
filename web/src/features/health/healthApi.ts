import { apiFetch } from "../../apiClient.ts";
import {
  healthResponseMaximumBytes, parseTargetHealthResponse, parseDatabaseHealthResponse,
  parseDatabaseFileHealthResponse, parseDatabaseOptionChangesResponse,
  parseServerConfigurationChangesResponse, parseServerConfigurationSnapshotResponse,
} from "./healthResponseModel.ts";
import type {
  DatabaseFileHealthPage,
  DatabaseHealthPage,
  DatabaseOptionChangeResult,
  ServerConfigurationChangeResult,
  ServerConfigurationSnapshotResult,
  TargetHealthEvidence,
  TargetHealthSnapshot,
} from "./healthTypes";

const firstPageLimit = 25;

export async function getDatabaseOptionChanges(instanceId: string,
  window: { readonly fromUtc: string; readonly toUtc: string },
  signal: AbortSignal): Promise<DatabaseOptionChangeResult> {
  const parameters = new URLSearchParams({ fromUtc: window.fromUtc,
    toUtc: window.toUtc, limit: "100" });
  return getJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/database-option-changes?${parameters}`, signal,
    value => parseDatabaseOptionChangesResponse(value, instanceId, window));
}

export async function getServerConfigurationChanges(instanceId: string,
  window: { readonly fromUtc: string; readonly toUtc: string },
  signal: AbortSignal, cursor?: string): Promise<ServerConfigurationChangeResult> {
  const parameters = new URLSearchParams({ fromUtc: window.fromUtc,
    toUtc: window.toUtc, limit: "100" });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  return getJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/server-configuration-changes?${parameters}`, signal,
    value => parseServerConfigurationChangesResponse(value, instanceId, window));
}

export async function getServerConfigurationSnapshot(instanceId: string,
  signal: AbortSignal, cursor?: string): Promise<ServerConfigurationSnapshotResult> {
  const parameters = new URLSearchParams({ limit: "100" });
  if (cursor !== undefined) parameters.set("cursor", cursor);
  return getJson(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/server-configuration-snapshot?${parameters}`, signal,
    value => parseServerConfigurationSnapshotResponse(value, instanceId));
}

export async function setServerConfigurationCollection(instanceId: string,
  expectedTargetRevision: number, expectedEnabled: boolean, enabled: boolean,
  signal: AbortSignal): Promise<void> {
  if (typeof instanceId !== 'string' || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(instanceId)
    || instanceId === '00000000-0000-0000-0000-000000000000' || !Number.isSafeInteger(expectedTargetRevision)
    || expectedTargetRevision < 1 || typeof expectedEnabled !== 'boolean' || typeof enabled !== 'boolean')
    throw new ConfigurationCollectionError('Choose a valid server, revision and collection state.', 400, false);
  if (signal.aborted) throw signal.reason;
  let response: Response;
  try {
    response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health/server-configuration-collection`, {
      method: "PUT", credentials: "same-origin",
      headers: { Accept: "application/json", "Content-Type": "application/json" },
      body: JSON.stringify({ expectedTargetRevision, expectedEnabled, enabled }), signal,
    }, { maximumResponseBytes: 16_384 });
  } catch {
    throw new ConfigurationCollectionError(unknownConfigurationOutcome, undefined, true);
  }
  if (!response.ok) {
    const message = response.status === 409 ? 'Collection state changed. Reload before trying again.'
      : response.status === 403 ? 'Target Administrator access on this server is required.'
      : response.status === 401 ? 'Your access could not be verified. Reload current state.'
      : response.status === 404 ? 'This server is unavailable. Reload current state.'
      : response.status === 400 ? 'The collection request was refused. Reload the current server revision.'
      : response.status === 429 ? 'Too many administrative requests. Wait briefly and retry.'
      : unknownConfigurationOutcome;
    throw new ConfigurationCollectionError(message, response.status, ![400, 401, 403, 404, 409, 429].includes(response.status));
  }
  try {
    if (signal.aborted || response.status !== 200 || !/^application\/json(?:\s*;|$)/iu.test(response.headers.get('content-type') ?? '')) throw new Error();
    const value: unknown = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(await response.arrayBuffer()));
    if (signal.aborted || !value || typeof value !== 'object' || Array.isArray(value)) throw new Error();
    const receipt = value as Record<string, unknown>;
    if (typeof receipt.targetId !== 'string' || receipt.targetId.toLowerCase() !== instanceId.toLowerCase()
      || receipt.targetRevision !== expectedTargetRevision || receipt.collectorEnabled !== enabled
      || !Number.isSafeInteger(receipt.scheduleRevision) || (receipt.scheduleRevision as number) < 1) throw new Error();
  } catch {
    throw new ConfigurationCollectionError(unknownConfigurationOutcome, response.status, true);
  }
}

const unknownConfigurationOutcome = 'The configuration collection outcome is unknown. Reload current state before another change.';
export class ConfigurationCollectionError extends Error {
  readonly status: number | undefined;
  readonly outcomeUnknown: boolean;
  constructor(message: string, status: number | undefined, outcomeUnknown: boolean) {
    super(message); this.name = 'ConfigurationCollectionError'; this.status = status; this.outcomeUnknown = outcomeUnknown;
  }
}

export class HealthRequestError extends Error {
  readonly status: number | undefined;
  public constructor(message: string, status?: number) {
    super(message);
    this.name = "HealthRequestError";
    this.status = status;
  }
}

export async function getTargetHealth(
  instanceId: string,
  signal: AbortSignal,
): Promise<TargetHealthSnapshot> {
  return getJson(
    `/api/v1/observation-targets/${encodeURIComponent(instanceId)}/health`,
    signal,
    value => parseTargetHealthResponse(value, instanceId),
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
    value => parseDatabaseHealthResponse(value, instanceId),
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
    value => parseDatabaseFileHealthResponse(value, instanceId),
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

async function getJson<T>(path: string, signal: AbortSignal, parse: (value: unknown) => T): Promise<T> {
  let response: Response;
  try {
    response = await apiFetch(path, {
      credentials: "same-origin",
      headers: { Accept: "application/json" },
      signal,
    }, { maximumResponseBytes: healthResponseMaximumBytes });
  } catch (error: unknown) {
    if (signal.aborted) {
      throw error;
    }

    throw new HealthRequestError(error instanceof Error && /response is too large|invalid length/u.test(error.message)
      ? "Health evidence returned an invalid or mismatched response. Refresh to retry."
      : "The health service is temporarily unavailable.");
  }

  if (signal.aborted) throw signal.reason;
  if (!response.ok) {
    throw new HealthRequestError(getSafeHealthFailure(response.status), response.status);
  }

  try {
    if (signal.aborted) throw signal.reason;
    if (!/^application\/json(?:\s*;|$)/iu.test(response.headers.get("content-type") ?? "")) throw new Error("Invalid content type.");
    const body = new TextDecoder("utf-8", { fatal: true }).decode(await response.arrayBuffer());
    if (signal.aborted) throw signal.reason;
    return parse(JSON.parse(body));
  } catch (error) {
    if (signal.aborted) throw signal.reason ?? error;
    throw new HealthRequestError("Health evidence returned an invalid or mismatched response. Refresh to retry.");
  }
}

function getSafeHealthFailure(status: number): string {
  switch (status) {
    case 403:
      return "You are not authorized to view health for this target.";
    case 404:
      return "No health snapshot is available for this target.";
    case 409:
      return "Health evidence changed. Restart from the first page.";
    case 429:
      return "Too many health requests. Wait briefly and retry.";
    case 504:
      return "The health request exceeded its execution limit.";
    default:
      return "The health request failed safely.";
  }
}
