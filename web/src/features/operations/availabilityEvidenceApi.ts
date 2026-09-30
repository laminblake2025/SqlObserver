import { apiFetch } from "../../apiClient.ts";
import { readBoundedBody } from "./operationsParser.mjs";
import { parseAvailabilityPage, type AvailabilityPage, type AvailabilityStream } from "./availabilityEvidence.ts";

export class AvailabilityEvidenceError extends Error {
  readonly restart: boolean;
  readonly retryable: boolean;
  constructor(message: string, restart = false, retryable = true) {
    super(message);
    this.name = "AvailabilityEvidenceError";
    this.restart = restart;
    this.retryable = retryable;
  }
}

export async function getAvailabilityEvidence(targetId: string, stream: AvailabilityStream, atUtc: string,
  signal: AbortSignal, cursor?: string): Promise<AvailabilityPage> {
  const query = new URLSearchParams({ atUtc, limit: "50" });
  if (cursor !== undefined) query.set("cursor", cursor);
  let response: Response;
  try {
    response = await apiFetch(`/api/v2/observation-targets/${encodeURIComponent(targetId)}/availability-groups/${stream}?${query}`,
      { credentials: "same-origin", headers: { Accept: "application/json" }, cache: "no-store", signal });
  } catch (error) {
    if (signal.aborted) throw error;
    throw new AvailabilityEvidenceError("AG evidence is temporarily unavailable. Retry this page.");
  }
  if (!response.ok) {
    if ((response.status === 400 && cursor !== undefined) || response.status === 409)
      throw new AvailabilityEvidenceError("The AG snapshot changed or its cursor expired. Restart both streams from the first page.", true);
    if (response.status === 401 || response.status === 403)
      throw new AvailabilityEvidenceError("A Viewer, Operator or TargetAdministrator role scoped to this server is required.", false, false);
    if (response.status === 400)
      throw new AvailabilityEvidenceError("Choose a valid UTC cutoff for AG evidence.", false, false);
    throw new AvailabilityEvidenceError(response.status === 504 ? "AG evidence timed out. Retry this page."
      : "AG evidence is temporarily unavailable. Retry this page.");
  }
  try {
    return parseAvailabilityPage(JSON.parse(await readBoundedBody(response, signal)), stream, targetId, atUtc);
  } catch (error) {
    if (signal.aborted) throw error;
    throw new AvailabilityEvidenceError("AG evidence could not be verified. Restart from the first page.", true);
  }
}
