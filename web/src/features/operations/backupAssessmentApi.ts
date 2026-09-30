import { apiFetch } from "../../apiClient.ts";
import { readBoundedBody } from "./operationsParser.mjs";
import { parseBackupPage, type BackupPage } from "./backupAssessment.ts";

export class BackupAssessmentError extends Error {
  readonly restart: boolean;
  readonly retryable: boolean;
  constructor(message: string, restart = false, retryable = true) {
    super(message); this.name = "BackupAssessmentError"; this.restart = restart; this.retryable = retryable;
  }
}
export async function getBackupAssessment(targetId: string, atUtc: string, signal: AbortSignal, cursor?: string): Promise<BackupPage> {
  const query = new URLSearchParams({ atUtc, limit: "50" });
  if (cursor !== undefined) query.set("cursor", cursor);
  let response: Response;
  try {
    response = await apiFetch(`/api/v2/observation-targets/${encodeURIComponent(targetId)}/health/backups?${query}`,
      { credentials: "same-origin", headers: { Accept: "application/json" }, cache: "no-store", signal });
  } catch (error) {
    if (signal.aborted) throw error;
    throw new BackupAssessmentError("Backup assessment is temporarily unavailable. Retry this page.");
  }
  if (!response.ok) {
    if (response.status === 409 || response.status === 400 && cursor !== undefined)
      throw new BackupAssessmentError("The backup snapshot changed or expired. Restart from the first page.", true);
    if (response.status === 401 || response.status === 403)
      throw new BackupAssessmentError("A Viewer, Operator or TargetAdministrator role scoped to this server is required.", false, false);
    if (response.status === 400) throw new BackupAssessmentError("Choose a valid UTC cutoff for backup assessment.", false, false);
    throw new BackupAssessmentError(response.status === 504 ? "Backup assessment timed out. Retry this page." : "Backup assessment is temporarily unavailable. Retry this page.");
  }
  try { return parseBackupPage(JSON.parse(await readBoundedBody(response, signal)), targetId, atUtc); }
  catch (error) {
    if (signal.aborted) throw error;
    throw new BackupAssessmentError("Backup evidence could not be verified. Restart from the first page.", true);
  }
}
