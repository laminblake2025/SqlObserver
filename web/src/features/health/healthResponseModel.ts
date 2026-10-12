import { utcMicroseconds } from "../../utcTimestamp.ts";
import type {
  CollectorHealthSummary, CoreMetricSummary, DatabaseHealthPage, DatabaseHealthSummary,
  DatabaseFileHealthPage, DatabaseFileHealthSummary, DatabaseOptionChangeResult,
  ServerConfigurationChangeResult, ServerConfigurationSnapshotResult, TargetHealthSnapshot,
} from "./healthTypes.ts";

export const healthResponseMaximumBytes = 256 * 1024;
const states = ["pending", "current", "degraded", "unavailable", "unsupported", "stale", "disabled"] as const;
const reasons = ["none", "capability_profile_missing", "capability_profile_stale", "capability_missing", "permission_denied", "version_unsupported", "platform_unsupported", "edition_unsupported", "never_collected", "timed_out", "collection_failed", "output_invalid", "sample_loss", "circuit_open", "evidence_stale"] as const;
const outcomes = ["succeeded", "partial", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "timed_out", "output_invalid", "lease_lost", "circuit_open"] as const;
const lossKinds = ["source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "duplicate_overlap", "visibility_incomplete"] as const;
const longMax = 9223372036854775807n;
const invalid = (): never => { throw new Error("Invalid health response."); };
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
  return value as Record<string, unknown>;
}
function text(value: unknown, maximum: number, safe = true): string {
  if (typeof value !== "string" || value.length === 0 || new TextEncoder().encode(value).length > maximum ||
      /\p{Surrogate}/u.test(value) || safe && (!value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value))) return invalid();
  return value;
}
function token(value: unknown, maximum: number, pattern = /^[a-z][a-z0-9._-]*$/iu): string {
  const result = text(value, maximum); if (!pattern.test(result)) return invalid(); return result;
}
function guid(value: unknown): string {
  const result = text(value, 36);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(result) || result === "00000000-0000-0000-0000-000000000000") return invalid();
  return result;
}
function integer(value: unknown, minimum = 0, maximum = Number.MAX_SAFE_INTEGER): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < minimum || value > maximum) return invalid(); return value;
}
function finite(value: unknown, minimum = -Infinity): number {
  if (typeof value !== "number" || !Number.isFinite(value) || value < minimum) return invalid(); return value;
}
function boolean(value: unknown): boolean { if (typeof value !== "boolean") return invalid(); return value; }
function oneOf<T extends string>(value: unknown, choices: readonly T[]): T {
  if (typeof value !== "string" || !choices.includes(value as T)) return invalid(); return value as T;
}
function nullable<T>(value: unknown, parse: (value: unknown) => T): T | null { return value === null ? null : parse(value); }
function timestamp(value: unknown): string {
  if (typeof value !== "string") return invalid();
  utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); return value;
}
const clock = (value: unknown) => utcMicroseconds(timestamp(value).replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
function target(value: unknown, expected: string): string {
  const result = guid(value); if (result.toLowerCase() !== guid(expected).toLowerCase()) return invalid(); return result;
}
function rows(value: unknown, maximum: number): readonly unknown[] {
  if (!Array.isArray(value) || value.length > maximum) return invalid(); return value;
}
function cursor(value: unknown, maximum = 512): string | null { return nullable(value, item => text(item, maximum)); }
function counter(value: unknown): string {
  const result = text(value, 19);
  if (!/^(?:0|[1-9]\d*)$/u.test(result) || BigInt(result) > longMax) return invalid(); return result;
}
function unique<T>(items: readonly T[], key: (item: T) => string | number): readonly T[] {
  if (new Set(items.map(key)).size !== items.length) return invalid(); return items;
}
function windowBounds(row: Record<string, unknown>, expected: { readonly fromUtc: string; readonly toUtc: string }) {
  const fromUtc = timestamp(row.fromUtc), toUtc = timestamp(row.toUtc), from = clock(fromUtc), to = clock(toUtc);
  if (from !== clock(expected.fromUtc) || to !== clock(expected.toUtc) || to <= from || to - from > 7n * 86400n * 1000000n) return invalid();
  return { fromUtc, toUtc, from, to };
}
function detected(value: unknown, from: bigint, to: bigint): string {
  const result = timestamp(value), at = clock(result); if (at < from || at >= to) return invalid(); return result;
}
function collector(value: unknown): CollectorHealthSummary {
  const row = object(value);
  return {
    collectorId: token(row.collectorId, 128), manifestVersion: integer(row.manifestVersion, 1, 2147483647), outputSchemaVersion: integer(row.outputSchemaVersion, 1, 2147483647),
    state: oneOf(row.state, states), reason: oneOf(row.reason, reasons), lastExecutionOutcome: nullable(row.lastExecutionOutcome, item => oneOf(item, outcomes)),
    circuitState: oneOf(row.circuitState, ["closed", "open", "half_open"] as const), durationMilliseconds: nullable(row.durationMilliseconds, item => finite(item, 0)),
    retryCount: integer(row.retryCount, 0, 2147483647), sourceRows: integer(row.sourceRows), outputRows: integer(row.outputRows),
    insertedRows: integer(row.insertedRows), duplicateRows: integer(row.duplicateRows), rejectedRows: integer(row.rejectedRows),
    responseBytes: integer(row.responseBytes), persistedBytes: integer(row.persistedBytes), sampleLossKind: nullable(row.sampleLossKind, item => oneOf(item, lossKinds)),
    minimumLostItems: nullable(row.minimumLostItems, item => integer(item, 0, 2147483647)), lossCountIsExact: nullable(row.lossCountIsExact, boolean),
    minimumLostBytes: nullable(row.minimumLostBytes, item => integer(item, 0, 2147483647)), hasVisibilityGap: boolean(row.hasVisibilityGap),
    scheduledAtUtc: timestamp(row.scheduledAtUtc), lastAttemptAtUtc: nullable(row.lastAttemptAtUtc, timestamp),
    lastSuccessAtUtc: nullable(row.lastSuccessAtUtc, timestamp), nextDueAtUtc: timestamp(row.nextDueAtUtc),
  };
}
function metric(value: unknown): CoreMetricSummary {
  const row = object(value);
  const dimensions = unique(rows(row.dimensions, 16).map(item => { const dimension = object(item); return {
    key: token(dimension.key, 64, /^[a-z][a-z0-9_-]*$/iu), value: text(dimension.value, 256),
  }; }), item => item.key);
  return { sampleId: guid(row.sampleId), metricId: token(row.metricId, 128), observedAtUtc: timestamp(row.observedAtUtc), value: finite(row.value), dimensions };
}
export function parseTargetHealthResponse(value: unknown, expectedTarget: string): TargetHealthSnapshot {
  const row = object(value);
  return { instanceId: target(row.instanceId, expectedTarget), state: oneOf(row.state, states), repositoryTimeUtc: timestamp(row.repositoryTimeUtc),
    collectors: unique(rows(row.collectors, 64).map(collector), item => item.collectorId),
    coreMetrics: unique(rows(row.coreMetrics, 1000).map(metric), item => item.sampleId.toLowerCase()) };
}
function database(value: unknown): DatabaseHealthSummary {
  const row = object(value);
  return { databaseId: integer(row.databaseId, 1, 32767), name: text(row.name, 1024, false),
    state: oneOf(row.state, ["online", "restoring", "recovering", "recovery_pending", "suspect", "emergency", "offline", "copying", "offline_secondary", "other"] as const),
    recoveryModel: oneOf(row.recoveryModel, ["full", "bulk_logged", "simple", "other"] as const),
    userAccess: oneOf(row.userAccess, ["multi_user", "restricted_user", "single_user", "other"] as const),
    isReadOnly: boolean(row.isReadOnly), compatibilityLevel: integer(row.compatibilityLevel, 0, 999),
    // Older inventory APIs omit this optional observation; absence is not evidence of no log reuse wait.
    logReuseWaitDescription: row.logReuseWaitDescription === undefined ? null
      : nullable(row.logReuseWaitDescription, item => token(item, 60, /^[a-z][a-z0-9_]*$/iu)),
    observedAtUtc: timestamp(row.observedAtUtc), collector: collector(row.collector) };
}
export function parseDatabaseHealthResponse(value: unknown, expectedTarget: string): DatabaseHealthPage {
  const row = object(value);
  return { instanceId: target(row.instanceId, expectedTarget), repositoryTimeUtc: timestamp(row.repositoryTimeUtc), collector: collector(row.collector),
    items: unique(rows(row.items, 25).map(database), item => item.databaseId), nextCursor: cursor(row.nextCursor, 256) };
}
function file(value: unknown): DatabaseFileHealthSummary {
  const row = object(value);
  const sizeBytes = counter(row.sizeBytes), maximumSizeBytes = nullable(row.maximumSizeBytes, counter), ioStallMilliseconds = counter(row.ioStallMilliseconds);
  const readStallMilliseconds = nullable(row.readStallMilliseconds, counter), writeStallMilliseconds = nullable(row.writeStallMilliseconds, counter);
  if (maximumSizeBytes !== null && BigInt(maximumSizeBytes) < BigInt(sizeBytes) || (readStallMilliseconds === null) !== (writeStallMilliseconds === null) ||
      readStallMilliseconds !== null && BigInt(readStallMilliseconds) + BigInt(writeStallMilliseconds!) !== BigInt(ioStallMilliseconds)) return invalid();
  return { databaseId: integer(row.databaseId, 1, 32767), fileId: integer(row.fileId, 1, 65535), logicalName: text(row.logicalName, 1024, false),
    fileType: oneOf(row.fileType, ["rows", "log", "filestream", "full_text", "other"] as const),
    state: oneOf(row.state, ["online", "restoring", "recovering", "recovery_pending", "suspect", "emergency", "offline", "defunct", "other"] as const),
    sizeBytes, maximumSizeBytes, growthBytes: counter(row.growthBytes), growthPercent: integer(row.growthPercent, 0, 100),
    readCount: counter(row.readCount), writeCount: counter(row.writeCount), bytesRead: counter(row.bytesRead), bytesWritten: counter(row.bytesWritten),
    ioStallMilliseconds, readStallMilliseconds, writeStallMilliseconds, observedAtUtc: timestamp(row.observedAtUtc), collector: collector(row.collector) };
}
export function parseDatabaseFileHealthResponse(value: unknown, expectedTarget: string): DatabaseFileHealthPage {
  const row = object(value);
  return { instanceId: target(row.instanceId, expectedTarget), repositoryTimeUtc: timestamp(row.repositoryTimeUtc), collector: collector(row.collector),
    items: unique(rows(row.items, 25).map(file), item => `${item.databaseId}:${item.fileId}`), nextCursor: cursor(row.nextCursor, 256) };
}
export function parseDatabaseOptionChangesResponse(value: unknown, expectedTarget: string,
    expectedWindow: { readonly fromUtc: string; readonly toUtc: string }): DatabaseOptionChangeResult {
  const row = object(value), bounds = windowBounds(row, expectedWindow);
  return { targetId: target(row.targetId, expectedTarget), fromUtc: bounds.fromUtc, toUtc: bounds.toUtc,
    semantics: oneOf(row.semantics, ["detected_between_consecutive_complete_inventory_runs"] as const),
    items: rows(row.items, 100).map(item => { const change = object(item); return {
      databaseId: integer(change.databaseId, 5, 32767), databaseName: text(change.databaseName, 1024, false),
      optionKey: oneOf(change.optionKey, ["recovery_model", "user_access", "is_read_only", "compatibility_level"] as const),
      previousValue: text(change.previousValue, 128), currentValue: text(change.currentValue, 128),
      previousObservedAtUtc: timestamp(change.previousObservedAtUtc), observedAtUtc: timestamp(change.observedAtUtc),
      detectedAtUtc: detected(change.detectedAtUtc, bounds.from, bounds.to), collectionRunId: guid(change.collectionRunId),
    }; }) };
}
const sourceTypes = ["bit", "tinyint", "smallint", "int", "bigint"] as const;
function configured(value: unknown, sourceType: string): string {
  const result = text(value, 128);
  if (!/^(?:0|-?[1-9]\d*)$/u.test(result)) return invalid();
  const number = BigInt(result);
  const bounds: Readonly<Record<string, readonly [bigint, bigint]>> = { bit: [0n, 1n], tinyint: [0n, 255n], smallint: [-32768n, 32767n], int: [-2147483648n, 2147483647n], bigint: [-9223372036854775808n, longMax] };
  if (number < bounds[sourceType]![0] || number > bounds[sourceType]![1]) return invalid(); return result;
}
function configuration(value: unknown) {
  const row = object(value), sourceType = oneOf(row.sourceType, sourceTypes);
  return { row, configurationId: integer(row.configurationId, 1, 1000000), configurationName: text(row.configurationName, 256), sourceType,
    configuredValue: configured(row.configuredValue, sourceType), inUseValue: configured(row.inUseValue, sourceType) };
}
export function parseServerConfigurationChangesResponse(value: unknown, expectedTarget: string,
    expectedWindow: { readonly fromUtc: string; readonly toUtc: string }): ServerConfigurationChangeResult {
  const row = object(value), bounds = windowBounds(row, expectedWindow), publicationGeneration = integer(row.publicationGeneration);
  const coverage = oneOf(row.coverage, ["never_collected", "observed_changes_only"] as const);
  if (coverage !== (publicationGeneration === 0 ? "never_collected" : "observed_changes_only")) return invalid();
  const items = rows(row.items, 100).map(item => {
    const { row: change, ...setting } = configuration(item);
    const previousConfiguredValue = configured(change.previousConfiguredValue, setting.sourceType), previousInUseValue = configured(change.previousInUseValue, setting.sourceType);
    const configuredChanged = boolean(change.configuredChanged), inUseChanged = boolean(change.inUseChanged);
    if (configuredChanged !== (previousConfiguredValue !== setting.configuredValue) || inUseChanged !== (previousInUseValue !== setting.inUseValue) || !configuredChanged && !inUseChanged) return invalid();
    return { ...setting, previousConfiguredValue, previousInUseValue, configuredChanged, inUseChanged,
      previousObservedAtUtc: timestamp(change.previousObservedAtUtc), observedAtUtc: timestamp(change.observedAtUtc),
      detectedAtUtc: detected(change.detectedAtUtc, bounds.from, bounds.to), runId: guid(change.runId) };
  });
  return { targetId: target(row.targetId, expectedTarget), targetRevision: integer(row.targetRevision, 1), publicationGeneration,
    fromUtc: bounds.fromUtc, toUtc: bounds.toUtc, coverage, items: unique(items, item => `${item.runId.toLowerCase()}:${item.configurationId}`), nextCursor: cursor(row.nextCursor) };
}
export function parseServerConfigurationSnapshotResponse(value: unknown, expectedTarget: string): ServerConfigurationSnapshotResult {
  const row = object(value), runId = nullable(row.runId, guid), observedAtUtc = nullable(row.observedAtUtc, timestamp);
  const coverage = oneOf(row.coverage, ["never_collected", "latest_complete_scan"] as const);
  const items = unique(rows(row.items, 100).map(item => {
    const { row: setting, ...result } = configuration(item); return { ...result, isDynamic: boolean(setting.isDynamic), isAdvanced: boolean(setting.isAdvanced) };
  }), item => item.configurationId);
  const nextCursor = cursor(row.nextCursor);
  if ((runId === null) !== (observedAtUtc === null) || coverage !== (runId === null ? "never_collected" : "latest_complete_scan") ||
      runId === null && items.length !== 0 || nextCursor !== null && items.length === 0) return invalid();
  return { targetId: target(row.targetId, expectedTarget), targetRevision: integer(row.targetRevision, 1), runId, observedAtUtc,
    serverMajorVersion: nullable(row.serverMajorVersion, item => integer(item, 1, 32767)), collectorEnabled: boolean(row.collectorEnabled),
    lastOutcome: nullable(row.lastOutcome, item => oneOf(item, outcomes)), coverage, items, nextCursor };
}
