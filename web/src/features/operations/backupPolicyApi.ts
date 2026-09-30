import { apiFetch } from "../../apiClient.ts";
import { readBoundedBody } from "./operationsParser.mjs";
import { parseCurrentBackupPolicy, type BackupPolicyCommand, type CurrentBackupPolicy } from "./backupPolicy.ts";

export class BackupPolicyError extends Error {
  readonly retryable: boolean;
  constructor(message: string, retryable = false) { super(message); this.name = "BackupPolicyError"; this.retryable = retryable; }
}
function path(targetId: string, fingerprint: string): string {
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(targetId) || !/^[a-f0-9]{64}$/.test(fingerprint))
    throw new BackupPolicyError("The database identity could not be verified.");
  return `/api/v1/observation-targets/${targetId}/health/backups/databases/${fingerprint}/policy/`;
}
async function request(url: string, signal: AbortSignal, command?: BackupPolicyCommand): Promise<Response> {
  try { return await apiFetch(url, { method: command ? "PUT" : "GET", credentials: "same-origin", cache: "no-store", signal,
    headers: command ? { Accept: "application/json", "Content-Type": "application/json" } : { Accept: "application/json" },
    ...(command ? { body: JSON.stringify(command) } : {}) }); }
  catch (error) { if (signal.aborted) throw error;
    throw new BackupPolicyError(command ? "The save result is uncertain. Retry the same save or reload the current policy." : "Current policy is temporarily unavailable. Reload it.", true); }
}
async function failure(response: Response, signal: AbortSignal, saving: boolean): Promise<never> {
  let code: unknown;
  try { code = JSON.parse(await readBoundedBody(response, signal, 16384)).code; } catch { /* Never reflect arbitrary response text. */ }
  if (response.status === 401 || response.status === 403) throw new BackupPolicyError("A TargetAdministrator scoped to this server is required to save policies.");
  if (response.status === 409) throw new BackupPolicyError(code === "backup_inventory_unavailable"
    ? "Collect recent complete database identity and recovery-model evidence before enabling this policy."
    : "The target or policy changed. Reload the current policy before saving again.");
  if (response.status === 404) throw new BackupPolicyError("This database identity is no longer available. Refresh the server evidence.");
  if (response.status === 400) throw new BackupPolicyError(code === "log_threshold_required"
    ? "Full and Bulk-logged recovery require an explicit log age limit." : "The policy settings were rejected. Reload and check the explicit limits.");
  if (response.status === 429) throw new BackupPolicyError("Policy administration is busy. Retry the same save later.", true);
  throw new BackupPolicyError(saving ? "The save result is uncertain. Retry the same save or reload the current policy." : "Current policy is temporarily unavailable. Reload it.", true);
}
export async function getCurrentBackupPolicy(targetId: string, revision: number, fingerprint: string, signal: AbortSignal): Promise<CurrentBackupPolicy | null> {
  const response = await request(`${path(targetId, fingerprint)}?targetRevision=${revision}`, signal);
  if (response.status === 404) return null;
  if (!response.ok) return failure(response, signal, false);
  try { return parseCurrentBackupPolicy(JSON.parse(await readBoundedBody(response, signal, 16384)), targetId, revision, fingerprint); }
  catch (error) { if (signal.aborted) throw error; throw new BackupPolicyError("Current backup policy could not be verified. Reload it."); }
}
export async function saveBackupPolicy(targetId: string, fingerprint: string, command: BackupPolicyCommand, signal: AbortSignal): Promise<CurrentBackupPolicy> {
  const response = await request(path(targetId, fingerprint), signal, command);
  if (!response.ok) return failure(response, signal, true);
  try {
    const policy = parseCurrentBackupPolicy(JSON.parse(await readBoundedBody(response, signal, 16384)), targetId, command.expectedTargetRevision, fingerprint);
    if (policy.policyRevision !== command.expectedPolicyRevision + 1 || ["enabled", "maximumFullAgeSeconds", "maximumLogAgeSeconds", "maximumObservationAgeSeconds", "fullRequirement", "requireChecksums"]
      .some(key => policy[key as keyof CurrentBackupPolicy] !== command[key as keyof BackupPolicyCommand])) throw new Error();
    return policy;
  } catch (error) { if (signal.aborted) throw error; throw new BackupPolicyError("The save result could not be verified. Retry the same save or reload the current policy.", true); }
}
