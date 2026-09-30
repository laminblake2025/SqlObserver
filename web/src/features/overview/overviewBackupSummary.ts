import { availabilityUtcKey } from '../operations/availabilityEvidence.ts';
import type { OverviewBackupChecks, OverviewBackupSummary } from './overviewTypes';

const unknownReasons = ['not_collected', 'backup_not_observed', 'source_incomplete', 'source_truncated',
  'permission_denied', 'collection_failed', 'unsupported', 'stale_observation', 'future_source_time', 'topology_unknown',
  'other_replica_evidence_required', 'recovery_model_unknown', 'log_threshold_not_configured', 'finish_time_unknown',
  'backup_metadata_unknown', 'checksum_unknown', 'policy_not_configured', 'database_identity_unknown', 'timestamp_qualification_unavailable'];
const invalid = (): never => { throw new Error('Overview returned invalid backup policy evidence.'); };
function object(value: unknown, fields: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const item = value as Record<string, unknown>, keys = fields.split(' ');
  if (Object.keys(item).length !== keys.length || keys.some(key => !Object.hasOwn(item, key))) return invalid();
  return item;
}
function count(value: unknown, minimum = 0): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= 512;
}
function micros(value: unknown): bigint {
  const key = availabilityUtcKey(value);
  return BigInt(Date.parse(key.slice(0, 19) + 'Z')) * 1000n + BigInt(key.slice(20, 26));
}
function checks(value: unknown, expected: number): OverviewBackupChecks {
  const row = object(value, 'withinThreshold policyViolated indeterminate notRequired disabled unknownReasons');
  const values = [row.withinThreshold, row.policyViolated, row.indeterminate, row.notRequired, row.disabled];
  if (!values.every(v => count(v)) || (values as number[]).reduce((sum, v) => sum + v, 0) !== expected ||
    !Array.isArray(row.unknownReasons) || row.unknownReasons.length > unknownReasons.length) return invalid();
  let sum = 0, previous = -1;
  for (const entry of row.unknownReasons) {
    const reason = object(entry, 'reason count');
    const index = unknownReasons.indexOf(String(reason.reason));
    if (typeof reason.reason !== 'string' || index <= previous || !count(reason.count, 1)) return invalid();
    previous = index; sum += reason.count;
  }
  if (sum !== row.indeterminate) return invalid();
  return row as unknown as OverviewBackupChecks;
}

export function parseOverviewBackupSummary(value: unknown, targetId: string, cutoffUtc: string): OverviewBackupSummary | null {
  // Older responses and targets without a cache row have no verdict.
  if (value === null || value === undefined) return null;
  const row = object(value, 'targetId assessedAtUtc validUntilUtc status evidenceStatus coverage databaseCount collectorEnabled full log observedFaultDatabases');
  // Overview refresh time can carry .NET's seventh digit; backup instants are whole microseconds.
  const assessed = micros(row.assessedAtUtc), expiry = micros(row.validUntilUtc), cutoff = micros(cutoffUtc.replace(/(\.\d{6})\dZ$/, '$1Z'));
  if (row.targetId !== targetId || !count(row.databaseCount) || typeof row.collectorEnabled !== 'boolean' ||
    !['not_collected', 'collection_failed', 'evidence_not_retained', 'withheld_by_limit', 'no_data', 'retained'].includes(String(row.evidenceStatus)) ||
    !['not_collected', 'complete', 'partial', 'truncated', 'permission_denied', 'failed', 'unsupported'].includes(String(row.coverage)) ||
    assessed > cutoff || expiry <= cutoff || expiry <= assessed || expiry - assessed > 120000000n) return invalid();
  const retained = row.evidenceStatus === 'retained', expected = retained ? row.databaseCount : 0;
  const full = checks(row.full, expected), log = checks(row.log, expected);
  if (!count(row.observedFaultDatabases) || row.observedFaultDatabases > expected ||
    retained && (expected === 0 || !['complete', 'partial'].includes(String(row.coverage))) ||
    row.evidenceStatus === 'not_collected' && (row.coverage !== 'not_collected' || row.databaseCount !== 0) ||
    row.evidenceStatus === 'collection_failed' && (!['failed', 'permission_denied', 'unsupported'].includes(String(row.coverage)) || row.databaseCount !== 0) ||
    row.evidenceStatus === 'evidence_not_retained' && row.coverage !== 'failed' ||
    row.evidenceStatus === 'withheld_by_limit' && (row.coverage !== 'truncated' || row.databaseCount !== 0) ||
    row.evidenceStatus === 'no_data' && (row.coverage !== 'partial' || row.databaseCount !== 0)) return invalid();
  const status = !retained ? 'indeterminate' : full.policyViolated + log.policyViolated > 0 ? 'policy_violated'
    : !row.collectorEnabled || row.coverage !== 'complete' || full.indeterminate + log.indeterminate > 0 ? 'indeterminate'
    : full.disabled + log.disabled === expected * 2 ? 'disabled'
    : full.withinThreshold + log.withinThreshold > 0 ? 'within_policy' : 'indeterminate';
  if (row.status !== status) return invalid();
  return row as unknown as OverviewBackupSummary;
}

export function backupSummaryExpired(summary: OverviewBackupSummary, now: number): boolean {
  return !Number.isFinite(now) || micros(summary.validUntilUtc) <= BigInt(Math.trunc(now)) * 1000n;
}
