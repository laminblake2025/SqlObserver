import { apiFetch } from "../../apiClient.ts";
import { readBoundedBody } from "./operationsParser.mjs";
import { parseBackupQualification, type BackupQualification, type QualificationCommand } from "./backupQualification.ts";

export class BackupQualificationError extends Error {
  readonly retryable: boolean;
  constructor(message: string, retryable = false) { super(message); this.name = "BackupQualificationError"; this.retryable = retryable; }
}
const uncertain = "The qualification result is uncertain. Retry the same command or reload the current qualification.";
function path(targetId: string): string {
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(targetId)) throw new BackupQualificationError("The server identity could not be verified.");
  return `/api/v1/observation-targets/${targetId}/health/backup-zone-qualification/`;
}
async function request(url: string, signal: AbortSignal, command?: QualificationCommand): Promise<Response> {
  try { return await apiFetch(url, { method: command ? "PUT" : "GET", credentials: "same-origin", cache: "no-store", signal,
    headers: command ? { Accept: "application/json", "Content-Type": "application/json" } : { Accept: "application/json" },
    ...(command ? { body: JSON.stringify(command) } : {}) }); }
  catch (error) { if (signal.aborted) throw error; throw new BackupQualificationError(command ? uncertain : "Current qualification is temporarily unavailable. Reload it.", true); }
}
function failure(status: number, saving: boolean): never {
  if (status === 401 || status === 403) throw new BackupQualificationError("A TargetAdministrator scoped to this server is required to administer historical time rules.");
  if (status === 409) throw new BackupQualificationError("The target or qualification changed. Reload the current qualification before submitting again.");
  if (status === 400) throw new BackupQualificationError("The qualification was rejected. Check the verified serialized rules, source zone ID, UTC interval and evidence digest.");
  if (status === 413) throw new BackupQualificationError("The qualification command exceeds the request byte limit. Reload and check the serialized rules.");
  if (status === 404) throw new BackupQualificationError("The server revision is no longer available. Refresh the server evidence.");
  throw new BackupQualificationError(saving ? uncertain : "Current qualification is temporarily unavailable. Reload it.", true);
}
export async function getBackupQualification(targetId: string, revision: number, signal: AbortSignal): Promise<BackupQualification> {
  const response = await request(`${path(targetId)}?targetRevision=${revision}`, signal);
  if (!response.ok) return failure(response.status, false);
  try { return parseBackupQualification(JSON.parse(await readBoundedBody(response, signal, 16384)), targetId, revision); }
  catch (error) { if (signal.aborted) throw error; throw new BackupQualificationError("Current qualification could not be verified. Reload it."); }
}
export async function setBackupQualification(targetId: string, command: QualificationCommand, signal: AbortSignal): Promise<number> {
  const response = await request(path(targetId), signal, command);
  if (!response.ok) return failure(response.status, true);
  try {
    const result = JSON.parse(await readBoundedBody(response, signal, 16384));
    if (!result || typeof result !== "object" || Array.isArray(result) || Object.keys(result).sort().join(" ") !== "enabled qualificationRevision targetId targetRevision"
      || typeof result.targetId !== "string" || result.targetId.toLowerCase() !== targetId.toLowerCase()
      || result.targetRevision !== command.expectedTargetRevision || result.qualificationRevision !== command.expectedQualificationRevision + 1
      || result.enabled !== command.enabled) throw new Error();
    return result.qualificationRevision;
  } catch (error) { if (signal.aborted) throw error; throw new BackupQualificationError("The qualification result could not be verified. Retry the same command or reload the current qualification.", true); }
}
