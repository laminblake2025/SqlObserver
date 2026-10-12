import type { BackupPolicy } from "./backupAssessment.ts";

export interface CurrentBackupPolicy extends BackupPolicy {
  readonly targetId: string; readonly targetRevision: number; readonly databaseFingerprint: string; readonly policyRevision: number;
}
export interface BackupPolicyDraft {
  readonly enabled: string; readonly fullSeconds: string; readonly logSeconds: string;
  readonly observationSeconds: string; readonly fullRequirement: string; readonly checksums: string;
}
export interface BackupPolicyCommand extends BackupPolicy {
  readonly expectedTargetRevision: number; readonly expectedPolicyRevision: number; readonly operationId: string;
}
export function policyDraft(policy: CurrentBackupPolicy | null): BackupPolicyDraft {
  return policy ? { enabled: String(policy.enabled), fullSeconds: String(policy.maximumFullAgeSeconds),
    logSeconds: policy.maximumLogAgeSeconds === null ? "" : String(policy.maximumLogAgeSeconds),
    observationSeconds: String(policy.maximumObservationAgeSeconds), fullRequirement: policy.fullRequirement,
    checksums: String(policy.requireChecksums) }
    : { enabled: "", fullSeconds: "", logSeconds: "", observationSeconds: "", fullRequirement: "", checksums: "" };
}
export function policyCommand(draft: BackupPolicyDraft, targetRevision: number, policyRevision: number,
  operationId: string, recoveryModel: string): BackupPolicyCommand {
  const seconds = (value: string, maximum: number, name: string): number => {
    if (!/^[1-9][0-9]*$/.test(value) || !Number.isSafeInteger(Number(value)) || Number(value) > maximum)
      throw new Error(`${name} must be whole seconds between 1 and ${maximum}.`);
    return Number(value);
  };
  const boolean = (value: string, name: string): boolean => {
    if (value !== "true" && value !== "false") throw new Error(`Choose ${name}.`);
    return value === "true";
  };
  if (!Number.isSafeInteger(targetRevision) || targetRevision < 1 || !Number.isSafeInteger(policyRevision)
    || policyRevision < 0 || policyRevision >= Number.MAX_SAFE_INTEGER
    || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(operationId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(operationId))
    throw new Error("Reload the current policy before saving.");
  const enabled = boolean(draft.enabled, "whether this policy is enabled");
  if (draft.fullRequirement !== "full_image" && draft.fullRequirement !== "differential_base") throw new Error("Choose a full backup requirement.");
  const maximumLogAgeSeconds = draft.logSeconds === "" ? null : seconds(draft.logSeconds, 2592000, "Log age limit");
  if (enabled && (recoveryModel === "full" || recoveryModel === "bulk_logged") && maximumLogAgeSeconds === null)
    throw new Error("Full and Bulk-logged recovery require an explicit log age limit.");
  return { expectedTargetRevision: targetRevision, expectedPolicyRevision: policyRevision, operationId, enabled,
    maximumFullAgeSeconds: seconds(draft.fullSeconds, 2592000, "Full age limit"), maximumLogAgeSeconds,
    maximumObservationAgeSeconds: seconds(draft.observationSeconds, 3600, "Observation age limit"),
    fullRequirement: draft.fullRequirement, requireChecksums: boolean(draft.checksums, "whether checksums are required") };
}
export function parseCurrentBackupPolicy(value: unknown, targetId: string, targetRevision: number, databaseFingerprint: string): CurrentBackupPolicy {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new Error("Current backup policy could not be verified.");
  const p = value as Record<string, unknown>;
  const fields = "targetId targetRevision databaseFingerprint policyRevision enabled maximumFullAgeSeconds maximumLogAgeSeconds maximumObservationAgeSeconds fullRequirement requireChecksums";
  const integer = (v: unknown, max: number) => typeof v === "number" && Number.isSafeInteger(v) && v >= 1 && v <= max;
  if (Object.keys(p).sort().join(" ") !== fields.split(" ").sort().join(" ") || typeof p.targetId !== "string"
    || p.targetId.toLowerCase() !== targetId.toLowerCase() || p.targetRevision !== targetRevision || p.databaseFingerprint !== databaseFingerprint
    || !integer(p.policyRevision, Number.MAX_SAFE_INTEGER - 1) || typeof p.enabled !== "boolean" || typeof p.requireChecksums !== "boolean"
    || !integer(p.maximumFullAgeSeconds, 2592000) || !(p.maximumLogAgeSeconds === null || integer(p.maximumLogAgeSeconds, 2592000))
    || !integer(p.maximumObservationAgeSeconds, 3600) || !["full_image", "differential_base"].includes(p.fullRequirement as string))
    throw new Error("Current backup policy could not be verified.");
  return p as unknown as CurrentBackupPolicy;
}
