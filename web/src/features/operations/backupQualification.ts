import { availabilityUtcKey } from "./availabilityEvidence.ts";

export interface BackupQualification {
  readonly targetId: string; readonly targetRevision: number; readonly qualificationRevision: number; readonly enabled: boolean;
  readonly sourceZoneId: string | null; readonly fromUtc: string | null; readonly toUtc: string | null;
  readonly evidenceSha256: string | null; readonly rulesSha256: string | null;
}
export interface QualificationDraft {
  readonly action: string; readonly sourceZoneId: string; readonly fromUtc: string; readonly toUtc: string;
  readonly serializedZoneRules: string; readonly evidenceSha256: string; readonly confirmed: boolean; readonly revokeConfirmed: boolean;
}
export interface QualificationCommand {
  readonly expectedTargetRevision: number; readonly expectedQualificationRevision: number; readonly operationId: string;
  readonly enabled: boolean; readonly historicalConfigurationConfirmed: boolean;
  readonly serializedZoneRules: string | null; readonly sourceZoneId: string | null;
  readonly fromUtc: string | null; readonly toUtc: string | null; readonly evidenceSha256: string | null;
}
export function emptyQualificationDraft(): QualificationDraft {
  return { action: "", sourceZoneId: "", fromUtc: "", toUtc: "", serializedZoneRules: "", evidenceSha256: "", confirmed: false, revokeConfirmed: false };
}
export function editQualificationDraft<K extends keyof QualificationDraft>(draft: QualificationDraft, field: K, value: QualificationDraft[K]): QualificationDraft {
  return { ...draft, [field]: value, ...(field !== "confirmed" && field !== "revokeConfirmed" ? { confirmed: false } : {}),
    ...(field === "action" ? { revokeConfirmed: false } : {}) };
}
export function qualificationCommand(draft: QualificationDraft, current: BackupQualification, operationId: string): QualificationCommand {
  if (!Number.isSafeInteger(current.targetRevision) || current.targetRevision < 1 || !Number.isSafeInteger(current.qualificationRevision)
    || current.qualificationRevision < 0 || current.qualificationRevision >= Number.MAX_SAFE_INTEGER
    || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(operationId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(operationId))
    throw new Error("Reload the current qualification before submitting.");
  const base = { expectedTargetRevision: current.targetRevision, expectedQualificationRevision: current.qualificationRevision, operationId };
  if (draft.action === "revoke") {
    if (!current.enabled) throw new Error("There is no active qualification to revoke.");
    if (!draft.revokeConfirmed) throw new Error("Confirm revocation before submitting.");
    return { ...base, enabled: false, historicalConfigurationConfirmed: false, serializedZoneRules: null,
      sourceZoneId: null, fromUtc: null, toUtc: null, evidenceSha256: null };
  }
  if (draft.action !== "qualify") throw new Error("Choose whether to qualify or revoke historical time rules.");
  if (!draft.sourceZoneId.trim() || draft.sourceZoneId.length > 128 || /[\x00-\x1f\x7f-\x9f]/.test(draft.sourceZoneId))
    throw new Error("Enter the verified SQL Server source zone ID (at most 128 characters).");
  let from: string, to: string;
  try { from = availabilityUtcKey(draft.fromUtc); } catch { throw new Error("From (UTC) must end in Z and have at most six fractional digits."); }
  try { to = availabilityUtcKey(draft.toUtc); } catch { throw new Error("To (UTC) must end in Z and have at most six fractional digits."); }
  if (to <= from) throw new Error("To (UTC) must be after From (UTC); the end is exclusive.");
  if (!draft.serializedZoneRules || draft.serializedZoneRules.includes('\0') || new TextEncoder().encode(draft.serializedZoneRules).length > 65536)
    throw new Error("Paste the verified .NET serialized zone rules, bounded to 65536 UTF-8 bytes.");
  if (!/^[a-f0-9]{64}$/.test(draft.evidenceSha256)) throw new Error("Enter the 64-character lowercase SHA-256 digest of historical configuration evidence.");
  if (!draft.confirmed) throw new Error("Confirm that the historical source configuration and rules were verified for the entire interval.");
  const command: QualificationCommand = { ...base, enabled: true, historicalConfigurationConfirmed: true,
    serializedZoneRules: draft.serializedZoneRules, sourceZoneId: draft.sourceZoneId,
    fromUtc: draft.fromUtc, toUtc: draft.toUtc, evidenceSha256: draft.evidenceSha256 };
  if (new TextEncoder().encode(JSON.stringify(command)).length > 65536)
    throw new Error("The serialized rules and command exceed the 65536-byte request limit.");
  return command;
}
export function parseBackupQualification(value: unknown, targetId: string, targetRevision: number): BackupQualification {
  const fail = (): never => { throw new Error("Historical time qualification could not be verified."); };
  if (!value || typeof value !== "object" || Array.isArray(value)) return fail();
  const p = value as Record<string, unknown>;
  const fields = "targetId targetRevision qualificationRevision enabled sourceZoneId fromUtc toUtc evidenceSha256 rulesSha256";
  if (Object.keys(p).sort().join(" ") !== fields.split(" ").sort().join(" ") || typeof p.targetId !== "string"
    || p.targetId.toLowerCase() !== targetId.toLowerCase() || p.targetRevision !== targetRevision || typeof p.enabled !== "boolean"
    || typeof p.qualificationRevision !== "number" || !Number.isSafeInteger(p.qualificationRevision)
    || p.qualificationRevision < 0 || p.qualificationRevision >= Number.MAX_SAFE_INTEGER) return fail();
  if (p.enabled) {
    if (p.qualificationRevision === 0 || typeof p.sourceZoneId !== "string" || !p.sourceZoneId.trim() || p.sourceZoneId.length > 128
      || /[\x00-\x1f\x7f-\x9f]/.test(p.sourceZoneId) || typeof p.fromUtc !== "string" || typeof p.toUtc !== "string"
      || typeof p.evidenceSha256 !== "string" || !/^[a-f0-9]{64}$/.test(p.evidenceSha256)
      || typeof p.rulesSha256 !== "string" || !/^[a-f0-9]{64}$/.test(p.rulesSha256)) return fail();
    try { if (availabilityUtcKey(p.toUtc) <= availabilityUtcKey(p.fromUtc)) return fail(); } catch { return fail(); }
  } else if (["sourceZoneId", "fromUtc", "toUtc", "evidenceSha256", "rulesSha256"].some(field => p[field] !== null)) return fail();
  return p as unknown as BackupQualification;
}
