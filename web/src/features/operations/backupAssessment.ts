import { availabilityUtcKey } from "./availabilityEvidence.ts";
import { overviewWindow } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";

export type BackupStatus = "indeterminate" | "within_threshold" | "policy_violated" | "not_required" | "disabled";
export interface BackupCheck {
  readonly status: BackupStatus; readonly reason: string; readonly maximumAgeSeconds: number | null;
  readonly observedAgeSeconds: number | null; readonly selectedBackup: BackupSelected | null; readonly action: string | null;
}
interface BackupSelected {
  readonly kind: string; readonly copyOnly: boolean; readonly finishUtc: string | null; readonly sourceLocalFinish: string | null;
  readonly hasChecksum: boolean | null; readonly isDamaged: boolean | null; readonly metadataComplete: boolean | null;
}
export interface BackupSample {
  readonly kind: string; readonly copyOnly: boolean; readonly recordedFinishUtc: string | null;
  readonly sourceLocalFinish: string | null; readonly sourceLocalStart: string | null; readonly recordedStartOffsetMinutes: number | null;
  readonly sizeBytes: string | null; readonly hasChecksum: boolean | null; readonly isDamaged: boolean | null;
  readonly metadataComplete: boolean | null; readonly firstRecoveryForkFingerprint: string | null; readonly lastRecoveryForkFingerprint: string;
  readonly timeState: string; readonly qualificationRevision: number | null; readonly qualificationTrusted: boolean;
}
export interface BackupPolicy {
  readonly enabled: boolean; readonly maximumFullAgeSeconds: number; readonly maximumLogAgeSeconds: number | null;
  readonly maximumObservationAgeSeconds: number; readonly fullRequirement: "full_image" | "differential_base"; readonly requireChecksums: boolean;
}
export interface BackupDatabase {
  readonly databaseOrdinal: number; readonly databaseFingerprint: string | null; readonly familyFingerprint: string | null;
  readonly recoveryForkFingerprint: string | null; readonly groupFingerprint: string | null; readonly agDatabaseFingerprint: string | null;
  readonly localReplicaFingerprint: string | null; readonly recoveryModel: string; readonly catalogDatabaseState: string | null;
  readonly scope: string; readonly localReplicaRole: string | null; readonly localReplicaPreferred: boolean | null;
  readonly unclassifiedBackupSeen: boolean; readonly coverage: string; readonly policyRevision: number | null;
  readonly policy: BackupPolicy | null; readonly full: BackupCheck; readonly log: BackupCheck;
  readonly observedIssues: readonly { readonly kind: string; readonly copyOnly: boolean; readonly reason: string }[];
  readonly samples: readonly BackupSample[];
}
export interface BackupPage {
  readonly contractVersion: 2; readonly targetId: string; readonly atUtc: string; readonly targetRevision: number; readonly runId: string | null;
  readonly sourceObservedAtUtc: string | null; readonly completedAtUtc: string | null; readonly receivedAtUtc: string | null;
  readonly sourceZoneId: string | null; readonly historyLowerBoundLocal: string | null; readonly coverage: string; readonly evidenceStatus: string;
  readonly sourceRows: number; readonly outputItems: number; readonly retainedItems: number; readonly databaseCount: number;
  readonly collectorEnabled: boolean; readonly action: string | null; readonly items: readonly BackupDatabase[];
  readonly hasMore: boolean; readonly nextCursor: string | null;
}
const reasons = ["not_collected", "recent_backup", "age_exceeded", "backup_not_observed", "source_incomplete", "source_truncated",
  "permission_denied", "collection_failed", "unsupported", "stale_observation", "future_source_time", "topology_unknown",
  "other_replica_evidence_required", "recovery_model_unknown", "log_threshold_not_configured", "finish_time_unknown",
  "backup_metadata_unknown", "backup_damaged", "checksum_missing", "checksum_unknown", "simple_recovery", "policy_disabled",
  "policy_not_configured", "database_identity_unknown", "timestamp_qualification_unavailable"];
const timeStates = ["resolved", "finish_missing", "start_missing", "qualification_missing", "recorded_offset_unknown",
  "source_zone_mismatch", "invalid_local_time", "ambiguous_local_time", "recorded_offset_mismatch", "outside_qualified_window", "finish_before_start"];
const headerFields = "contractVersion targetId atUtc targetRevision runId sourceObservedAtUtc completedAtUtc receivedAtUtc sourceZoneId historyLowerBoundLocal coverage evidenceStatus sourceRows outputItems retainedItems databaseCount collectorEnabled action items hasMore nextCursor";
const databaseFields = "databaseOrdinal databaseFingerprint familyFingerprint recoveryForkFingerprint groupFingerprint agDatabaseFingerprint localReplicaFingerprint recoveryModel catalogDatabaseState scope localReplicaRole localReplicaPreferred unclassifiedBackupSeen coverage policyRevision policy full log observedIssues samples";
const selectedFields = "kind copyOnly finishUtc sourceLocalFinish hasChecksum isDamaged metadataComplete";
const sampleFields = "kind copyOnly recordedFinishUtc sourceLocalFinish sourceLocalStart recordedStartOffsetMinutes sizeBytes hasChecksum isDamaged metadataComplete firstRecoveryForkFingerprint lastRecoveryForkFingerprint timeState qualificationRevision qualificationTrusted";
const fingerprint = /^[a-f0-9]{64}$/;
const guid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const fail = (): never => { throw new Error("Backup evidence could not be verified."); };
function object(value: unknown, keys: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return fail();
  const result = value as Record<string, unknown>;
  if (Object.keys(result).sort().join(" ") !== keys.split(" ").sort().join(" ")) fail();
  return result;
}
function number(value: unknown, min = 0, max = Number.MAX_SAFE_INTEGER): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max;
}
function nullable(value: unknown, predicate: (v: unknown) => boolean): boolean { return value === null || predicate(value); }
const bool = (v: unknown) => typeof v === "boolean";
function token(value: unknown, choices: readonly string[]): boolean { return typeof value === "string" && choices.includes(value); }
function utc(value: unknown): value is string { try { return typeof value === "string" && !!availabilityUtcKey(value); } catch { return false; } }
function local(value: unknown): boolean {
  return typeof value === "string" && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?$/.test(value)
    && Number(value.slice(0, 4)) >= 1753 && utc(`${value}Z`);
}
const text = (v: unknown) => typeof v === "string" && v.length > 0 && v.length <= 512;
const hash = (v: unknown) => typeof v === "string" && fingerprint.test(v);
const age = (v: unknown) => typeof v === "number" && Number.isFinite(v) && v >= 0;
function micros(value: string): bigint {
  const key = availabilityUtcKey(value);
  return BigInt(Date.parse(`${key.slice(0, 19)}Z`)) * 1000n + BigInt(key.slice(20, 26));
}
export function backupCutoff(scope: OverviewScope, now: number): string {
  const window = overviewWindow(scope, now);
  const at = scope.range === "custom" ? scope.to! : window.toUtc;
  if (scope.range === "custom") availabilityUtcKey(scope.from ?? "");
  if (micros(at) > BigInt(now) * 1000n) throw new Error("Backup cutoff cannot be in the future.");
  return at;
}

export function parseBackupPage(value: unknown, targetId: string, atUtc: string): BackupPage {
  const root = object(value, headerFields);
  if (root.contractVersion !== 2 || typeof root.targetId !== "string" || !guid.test(root.targetId) || root.targetId.toLowerCase() !== targetId.toLowerCase()
    || !utc(root.atUtc) || availabilityUtcKey(root.atUtc) !== availabilityUtcKey(atUtc) || !number(root.targetRevision, 1)
    || !nullable(root.runId, v => typeof v === "string" && guid.test(v) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(v))
    || !nullable(root.sourceObservedAtUtc, utc) || !nullable(root.completedAtUtc, utc) || !nullable(root.receivedAtUtc, utc)
    || (root.runId === null) !== (root.completedAtUtc === null) || !nullable(root.historyLowerBoundLocal, local)
    || !nullable(root.sourceZoneId, v => typeof v === "string" && v.length > 0 && v.length <= 128 && !/[\x00-\x1f\x7f]/.test(v))
    || !token(root.coverage, ["not_collected", "complete", "partial", "truncated", "permission_denied", "failed", "unsupported"])
    || !token(root.evidenceStatus, ["not_collected", "collection_failed", "evidence_not_retained", "withheld_by_limit", "no_data", "retained"])
    || !number(root.sourceRows, 0, 3073) || !number(root.outputItems, 0, 3072) || root.outputItems > root.sourceRows
    || !number(root.retainedItems, 0, root.outputItems as number) || !number(root.databaseCount, 0, 512)
    || !bool(root.collectorEnabled) || !nullable(root.action, text) || !bool(root.hasMore) || !Array.isArray(root.items) || root.items.length > 50
    || (root.hasMore ? !text(root.nextCursor) : root.nextCursor !== null)) fail();
  for (const name of ["completedAtUtc", "receivedAtUtc"])
    if (root[name] !== null && availabilityUtcKey(root[name] as string) > availabilityUtcKey(atUtc)) fail();
  const items = root.items as unknown[];
  if (root.evidenceStatus !== "retained") {
    if (items.length || root.hasMore) fail();
    if (root.evidenceStatus === "not_collected" && (root.runId !== null || root.sourceRows !== 0 || root.outputItems !== 0 || root.coverage !== "not_collected")
      || root.evidenceStatus === "collection_failed" && (root.runId === null || !token(root.coverage, ["permission_denied", "failed", "unsupported"]) || root.retainedItems !== 0)
      || root.evidenceStatus === "evidence_not_retained" && (root.runId === null || root.coverage !== "failed")
      || root.evidenceStatus === "withheld_by_limit" && (root.coverage !== "truncated" || root.sourceRows === 0 || root.outputItems !== 0)
      || root.evidenceStatus === "no_data" && (root.runId === null || root.coverage !== "partial" || root.sourceRows !== 0 || root.outputItems !== 0)) fail();
  } else if (root.runId === null || root.sourceObservedAtUtc === null || root.historyLowerBoundLocal === null
    || root.receivedAtUtc === null || root.retainedItems !== root.outputItems || root.outputItems !== root.sourceRows
    || !token(root.coverage, ["complete", "partial"]) || root.databaseCount === 0 || items.length === 0) fail();
  let ordinal = 0;
  const identities = new Set<string>();
  for (const raw of items) {
    const item = object(raw, databaseFields);
    if (!number(item.databaseOrdinal, ordinal + 1, root.databaseCount as number) || ordinal !== 0 && item.databaseOrdinal !== ordinal + 1) fail();
    ordinal = item.databaseOrdinal as number;
    for (const name of ["databaseFingerprint", "familyFingerprint", "recoveryForkFingerprint", "groupFingerprint", "agDatabaseFingerprint", "localReplicaFingerprint"])
      if (!nullable(item[name], hash)) fail();
    if (typeof item.databaseFingerprint === "string") {
      if (identities.has(item.databaseFingerprint)) fail(); identities.add(item.databaseFingerprint);
    }
    if (!token(item.recoveryModel, ["unknown", "simple", "full", "bulk_logged"])
      || !nullable(item.catalogDatabaseState, v => token(v, ["ONLINE", "RESTORING", "RECOVERING", "RECOVERY_PENDING", "SUSPECT", "EMERGENCY", "OFFLINE"]))
      || !token(item.scope, ["unknown", "standalone", "availability_group_local_only"])
      || !nullable(item.localReplicaRole, v => token(v, ["PRIMARY", "SECONDARY", "RESOLVING"]))
      || !nullable(item.localReplicaPreferred, bool) || !bool(item.unclassifiedBackupSeen) || !token(item.coverage, ["complete", "partial"])
      || !nullable(item.policyRevision, v => number(v, 1)) || (item.policy === null) !== (item.policyRevision === null)
      || !Array.isArray(item.samples) || item.samples.length > 5 || !Array.isArray(item.observedIssues) || item.observedIssues.length > 10) fail();
    let policy: Record<string, unknown> | null = null;
    if (item.policy !== null) {
      policy = object(item.policy, "enabled maximumFullAgeSeconds maximumLogAgeSeconds maximumObservationAgeSeconds fullRequirement requireChecksums");
      if (item.databaseFingerprint === null || !bool(policy.enabled) || !number(policy.maximumFullAgeSeconds, 1, 2592000)
        || !nullable(policy.maximumLogAgeSeconds, v => number(v, 1, 2592000)) || !number(policy.maximumObservationAgeSeconds, 1, 3600)
        || !token(policy.fullRequirement, ["full_image", "differential_base"]) || !bool(policy.requireChecksums)) fail();
    }
    const slots = new Set<string>();
    const samples = item.samples as unknown[];
    for (const rawSample of samples) {
      const sample = object(rawSample, sampleFields); const slot = `${sample.kind}:${sample.copyOnly}`;
      if (slots.has(slot) || item.databaseFingerprint === null || !token(sample.kind, ["full", "differential", "log"]) || !bool(sample.copyOnly)
        || sample.kind === "differential" && sample.copyOnly || !nullable(sample.recordedFinishUtc, utc)
        || !nullable(sample.sourceLocalStart, local) || !nullable(sample.sourceLocalFinish, local)
        || !nullable(sample.recordedStartOffsetMinutes, v => number(v, -720, 720) && (v as number) % 15 === 0)
        || !nullable(sample.sizeBytes, v => typeof v === "string" && /^(0|[1-9][0-9]{0,18})$/.test(v) && BigInt(v) <= 9223372036854775807n)
        || !nullable(sample.hasChecksum, bool) || !nullable(sample.isDamaged, bool) || !nullable(sample.metadataComplete, bool)
        || !nullable(sample.firstRecoveryForkFingerprint, hash) || !hash(sample.lastRecoveryForkFingerprint)
        || sample.lastRecoveryForkFingerprint !== item.recoveryForkFingerprint || !token(sample.timeState, timeStates)
        || !nullable(sample.qualificationRevision, v => number(v, 1)) || !bool(sample.qualificationTrusted)
        || sample.qualificationTrusted && sample.qualificationRevision === null || (sample.timeState === "resolved") !== (sample.recordedFinishUtc !== null)) fail();
      slots.add(slot);
    }
    for (const issue of item.observedIssues as unknown[]) {
      const i = object(issue, "kind copyOnly reason");
      if (!token(i.kind, ["full", "differential", "log"]) || !bool(i.copyOnly) || !token(i.reason, ["backup_damaged", "checksum_missing"])) fail();
    }
    for (const kind of ["full", "log"]) {
      const c = object(item[kind], "status reason maximumAgeSeconds observedAgeSeconds selectedBackup action");
      if (!token(c.status, ["indeterminate", "within_threshold", "policy_violated", "not_required", "disabled"])
        || !token(c.reason, reasons) || !nullable(c.maximumAgeSeconds, v => number(v, 1, 2592000))
        || !nullable(c.observedAgeSeconds, age) || !nullable(c.action, text)) fail();
      if (c.selectedBackup !== null) {
        const selected = object(c.selectedBackup, selectedFields);
        if (!token(selected.kind, ["full", "differential", "log"]) || !bool(selected.copyOnly) || !nullable(selected.finishUtc, utc)
          || !nullable(selected.sourceLocalFinish, local) || !nullable(selected.hasChecksum, bool) || !nullable(selected.isDamaged, bool)
          || !nullable(selected.metadataComplete, bool)) fail();
        const found = samples.find(v => {
          const s = v as BackupSample;
          return s.kind === selected.kind && s.copyOnly === selected.copyOnly && s.recordedFinishUtc === selected.finishUtc
            && s.qualificationTrusted && s.sourceLocalFinish === selected.sourceLocalFinish && s.hasChecksum === selected.hasChecksum
            && s.isDamaged === selected.isDamaged && s.metadataComplete === selected.metadataComplete;
        });
        if (!found) fail();
      }
      if (policy === null && (c.status !== "indeterminate" || c.reason !== (item.databaseFingerprint === null ? "database_identity_unknown" : "policy_not_configured")
        || c.maximumAgeSeconds !== null || c.observedAgeSeconds !== null || c.selectedBackup !== null)) fail();
      if (c.status === "within_threshold" && (c.reason !== "recent_backup" || c.selectedBackup === null || policy?.enabled !== true
        || item.coverage !== "complete" || item.scope !== "standalone" || c.observedAgeSeconds === null || c.maximumAgeSeconds === null
        || (c.observedAgeSeconds as number) > (c.maximumAgeSeconds as number))) fail();
      if (c.status === "within_threshold") {
        const selected = c.selectedBackup as BackupSelected;
        const limit = kind === "full" ? policy!.maximumFullAgeSeconds : policy!.maximumLogAgeSeconds;
        if (selected.kind !== kind || selected.isDamaged !== false || selected.metadataComplete !== true || selected.finishUtc === null
          || policy!.requireChecksums && selected.hasChecksum !== true || kind === "log" && selected.copyOnly
          || kind === "full" && policy!.fullRequirement === "differential_base" && selected.copyOnly
          || c.maximumAgeSeconds !== limit || root.sourceObservedAtUtc === null
          || availabilityUtcKey(selected.finishUtc) > availabilityUtcKey(root.sourceObservedAtUtc as string)
          || availabilityUtcKey(root.sourceObservedAtUtc as string) > availabilityUtcKey(root.atUtc as string)
          || micros(root.atUtc as string) - micros(root.sourceObservedAtUtc as string) > BigInt(policy!.maximumObservationAgeSeconds as number) * 1000000n
          || Math.abs(Number(micros(root.atUtc as string) - micros(selected.finishUtc)) / 1000000 - (c.observedAgeSeconds as number)) > 0.000001) fail();
      }
      if (c.status === "disabled" && (policy?.enabled !== false || c.reason !== "policy_disabled" || c.observedAgeSeconds !== null)) fail();
      if (c.status === "not_required" && (kind !== "log" || item.recoveryModel !== "simple" || c.reason !== "simple_recovery" || policy?.enabled !== true)) fail();
    }
  }
  if (root.hasMore && items.length !== 50 || root.hasMore && ordinal >= (root.databaseCount as number)
    || root.evidenceStatus === "retained" && !root.hasMore && ordinal !== root.databaseCount) fail();
  return root as unknown as BackupPage;
}

export function assertBackupSnapshot(first: BackupPage, next: BackupPage): void {
  if (first.targetId !== next.targetId || first.targetRevision !== next.targetRevision || first.runId !== next.runId
    || availabilityUtcKey(first.atUtc) !== availabilityUtcKey(next.atUtc) || first.sourceObservedAtUtc !== next.sourceObservedAtUtc
    || first.databaseCount !== next.databaseCount || first.outputItems !== next.outputItems || first.evidenceStatus !== next.evidenceStatus)
    throw new Error("The backup snapshot changed. Restart from the first page.");
}
export function assertBackupPagePosition(page: BackupPage, index: number): void {
  if (page.evidenceStatus !== "retained") { if (index !== 0) fail(); return; }
  const after = index * 50;
  if (page.items.length !== Math.min(50, page.databaseCount - after) || page.items[0]?.databaseOrdinal !== after + 1)
    throw new Error("The backup page has missing or overlapping database rows. Restart from the first page.");
}

export function backupDuration(seconds: number | null): string {
  if (seconds === null) return "Unknown";
  if (seconds < 60) return `${seconds.toFixed(0)} s`;
  if (seconds < 3600) return `${(seconds / 60).toFixed(1)} min`;
  if (seconds < 86400) return `${(seconds / 3600).toFixed(1)} h`;
  return `${(seconds / 86400).toFixed(1)} days`;
}
export function backupLabel(token: string): string { return token.replaceAll("_", " "); }
