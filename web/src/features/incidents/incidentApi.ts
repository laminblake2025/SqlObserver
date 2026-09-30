import { apiFetch } from "../../apiClient.ts";
import { parseSavedIncident, validIncidentId, verifySavedIncident } from "./incidentModel.ts";
import type { SavedIncident, SaveIncidentCommand } from "./incidentModel.ts";

export class IncidentRequestError extends Error {
  readonly retryable: boolean;
  constructor(message: string, retryable: boolean) { super(message); this.name = "IncidentRequestError"; this.retryable=retryable; }
}

async function request(targetId: string, threadId: string | undefined, command: SaveIncidentCommand | undefined,
  signal: AbortSignal): Promise<SavedIncident> {
  if (!validIncidentId(targetId) || threadId !== undefined && !validIncidentId(threadId)) throw new IncidentRequestError("Select an authorized server and incident.",false);
  try {
    const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/incidents/saved${threadId ? `/${encodeURIComponent(threadId)}` : ""}`, {
      method: command ? "POST" : "GET", cache: "no-store", signal,
      headers: command ? { "Content-Type": "application/json" } : undefined,
      body: command ? JSON.stringify(command) : undefined,
    });
    if (!response.ok) throw new IncidentRequestError(response.status === 403
      ? command ? "Operator or Target Administrator access is required to save this incident." : "Viewer access is required to read this incident."
      : response.status === 404 ? "This saved investigation was not found. Automatically correlated incidents remain available in Analytics."
      : response.status === 409 ? "The server revision or save operation changed. Refresh the server and start a new investigation."
      : response.status === 400 || response.status === 413 ? "The incident window, title or notes exceeded the save limits."
      : response.status === 429 ? "Too many save requests. Wait briefly and retry the same save."
      : "The incident service is temporarily unavailable. Retry the same save to check its outcome.",
      response.status >= 500 || response.status === 429);
    const result = parseSavedIncident(await response.json(),targetId,threadId);
    if (command) verifySavedIncident(result,command);
    return result;
  } catch (error) {
    if (signal.aborted) throw error;
    if (error instanceof IncidentRequestError) throw error;
    throw new IncidentRequestError(command
      ? "The save outcome could not be verified. Retry the same save to check its outcome."
      : "The saved investigation could not be loaded or validated. Retry to load it again.",true);
  }
}

export const saveIncident = (target: string, command: SaveIncidentCommand, signal: AbortSignal) => request(target,undefined,command,signal);
export const getSavedIncident = (target: string, thread: string, signal: AbortSignal) => request(target,thread,undefined,signal);
