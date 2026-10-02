import type { SqlVolumeItem, SqlVolumePage, SqlVolumeEvidenceState, SqlVolumeTrendState, SqlVolumeLossKind } from "./sqlVolumeApi.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

export const sqlVolumeMaximumResponseBytes = 64 * 1024;
export const invalidSqlVolumeResponseMessage = "Volume evidence returned an invalid or mismatched response. Load the latest snapshot to retry.";
export interface SqlVolumeResponseBinding {
  readonly targetRevision: number;
  readonly snapshotRunId?: string | null;
  readonly afterVolumeKey?: string;
  readonly beforeVolumeKey?: string;
}
const invalid = (): never => { throw new Error(invalidSqlVolumeResponseMessage); };
const guid = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const key = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{64}$/u.test(value);
const integer = (value: unknown, minimum: number, maximum: number): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= minimum && value <= maximum;
function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
  return value as Record<string, unknown>;
}
function clock(value: unknown): bigint {
  if (typeof value !== "string") return invalid();
  try { return utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); }
}
function timestamp(value: unknown): string { clock(value); return value as string; }
function capacity(value: unknown): string | null {
  if (value === null) return null;
  if (typeof value !== "string" || !/^(?:0|[1-9]\d{0,18})$/u.test(value) || BigInt(value) > 9223372036854775807n) return invalid();
  return value;
}
export function isSqlVolumeCursor(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 2048 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) return false;
  try { return btoa(atob(value)) === value; } catch { return false; }
}
const states: readonly SqlVolumeEvidenceState[] = ["current", "stale", "partial", "superseded", "unavailable"];
const trends: readonly SqlVolumeTrendState[] = ["capacity_unknown", "full", "capacity_changed", "insufficient_history", "not_depleting", "unstable_trend", "beyond_horizon", "estimated", "evidence_unavailable"];
const losses: readonly SqlVolumeLossKind[] = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "blocking_graph_limit", "duplicate_overlap", "visibility_incomplete"];
export function parseSqlVolumeResponse(value: unknown, instanceId: string, binding: SqlVolumeResponseBinding, requestCursor?: string): SqlVolumePage {
  const page = record(value);
  if (!guid(instanceId) || !binding || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER) ||
      !guid(page.instanceId) || page.instanceId.toLowerCase() !== instanceId.toLowerCase() || page.targetRevision !== binding.targetRevision ||
      typeof page.state !== "string" || !states.includes(page.state as SqlVolumeEvidenceState) ||
      typeof page.reason !== "string" || !/^[a-z0-9_]{1,128}$/u.test(page.reason) ||
      page.snapshotRunId !== null && !guid(page.snapshotRunId) ||
      page.lossKind !== null && (typeof page.lossKind !== "string" || !losses.includes(page.lossKind as SqlVolumeLossKind)) ||
      page.minimumLostItems !== null && !integer(page.minimumLostItems, 0, 1001) ||
      typeof page.hasVisibilityGap !== "boolean" || !Array.isArray(page.items) || page.items.length > 25 ||
      page.nextCursor !== null && !isSqlVolumeCursor(page.nextCursor) ||
      requestCursor !== undefined && (!isSqlVolumeCursor(requestCursor) || page.nextCursor === requestCursor) ||
      binding.afterVolumeKey !== undefined && !key(binding.afterVolumeKey) || binding.beforeVolumeKey !== undefined && !key(binding.beforeVolumeKey)) return invalid();
  if (binding.snapshotRunId !== undefined && (page.snapshotRunId === null ? binding.snapshotRunId !== null :
      !guid(binding.snapshotRunId) || page.snapshotRunId.toLowerCase() !== binding.snapshotRunId.toLowerCase())) return invalid();
  const repositoryTimeUtc = timestamp(page.repositoryTimeUtc);
  const completedAtUtc = page.completedAtUtc === null ? null : timestamp(page.completedAtUtc);
  const state = page.state as SqlVolumeEvidenceState, lossKind = page.lossKind as SqlVolumeLossKind | null;
  if (page.hasVisibilityGap !== (state !== "current" || lossKind !== null && lossKind !== "none") ||
      page.snapshotRunId === null && (completedAtUtc !== null || page.items.length > 0 || page.nextCursor !== null) ||
      state !== "unavailable" && (page.snapshotRunId === null || completedAtUtc === null) ||
      page.nextCursor !== null && page.items.length === 0 || state === "unavailable" && (page.items.length > 0 || page.nextCursor !== null)) return invalid();
  let lastKey: string | undefined;
  const items = page.items.map(value => {
    const item = record(value);
    if (!key(item.volumeKey) || lastKey !== undefined && item.volumeKey <= lastKey ||
        binding.afterVolumeKey !== undefined && item.volumeKey <= binding.afterVolumeKey ||
        binding.beforeVolumeKey !== undefined && item.volumeKey >= binding.beforeVolumeKey ||
        typeof item.identityKind !== "string" || !["volume_id", "mount_point", "file_scoped_unknown"].includes(item.identityKind) ||
        !integer(item.mappedFileCount, 1, 1000) || typeof item.trendState !== "string" || !trends.includes(item.trendState as SqlVolumeTrendState) ||
        !integer(item.trendSampleDays, 0, 15) || item.daysUntilFull !== null &&
        (typeof item.daysUntilFull !== "number" || !Number.isFinite(item.daysUntilFull) || item.daysUntilFull < 0 || item.daysUntilFull > 3650)) return invalid();
    lastKey = item.volumeKey;
    const totalBytes = capacity(item.totalBytes), availableBytes = capacity(item.availableBytes);
    const observedAtUtc = timestamp(item.observedAtUtc);
    if ((totalBytes === null) !== (availableBytes === null) || totalBytes !== null &&
        (BigInt(totalBytes) < 1n || BigInt(availableBytes!) > BigInt(totalBytes)) || completedAtUtc === null ||
        clock(observedAtUtc) > clock(completedAtUtc) + 300000000n) return invalid();
    const trendState = item.trendState as SqlVolumeTrendState;
    if (trendState === "evidence_unavailable" ? item.trendSampleDays !== 0 || item.daysUntilFull !== null :
        state !== "current" || page.hasVisibilityGap ||
        (trendState === "capacity_unknown" ? totalBytes !== null || item.trendSampleDays !== 0 || item.daysUntilFull !== null :
          totalBytes === null || (trendState === "full" ? availableBytes !== "0" || item.trendSampleDays !== 0 || item.daysUntilFull !== 0 :
            availableBytes === "0" || (trendState === "estimated" ? item.daysUntilFull === null : item.daysUntilFull !== null) ||
            ["estimated", "beyond_horizon", "unstable_trend", "not_depleting"].includes(trendState) && item.trendSampleDays < 5))) return invalid();
    return { volumeKey: item.volumeKey, identityKind: item.identityKind as SqlVolumeItem["identityKind"], mappedFileCount: item.mappedFileCount,
      totalBytes, availableBytes, observedAtUtc, trendState, trendSampleDays: item.trendSampleDays, daysUntilFull: item.daysUntilFull as number | null };
  });
  return { instanceId: page.instanceId, targetRevision: binding.targetRevision, snapshotRunId: page.snapshotRunId as string | null,
    state, reason: page.reason, completedAtUtc, lossKind, minimumLostItems: page.minimumLostItems as number | null,
    hasVisibilityGap: page.hasVisibilityGap, repositoryTimeUtc, items, nextCursor: page.nextCursor as string | null };
}
