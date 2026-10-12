import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";

export interface CpuCaptureSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface CpuCaptureBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface CpuCaptureCoverage {
  readonly runCount: number; readonly retainedReceiptCount: number; readonly completeRunCount: number; readonly gapRunCount: number;
  readonly failedRunCount: number; readonly lostRunCount: number; readonly missingPayloadRunCount: number; readonly hasVisibilityGap: boolean;
}
export interface CpuCaptureReceipt extends CpuCaptureSelection {
  readonly completedAtUtc: string; readonly outcome: string; readonly lossKind: string; readonly minimumLostItems: number;
  readonly capturePresent: boolean; readonly sourceFrames: number | null; readonly state: string; readonly reason: string;
}
interface Admission {
  readonly nativeQualification: "absent"; readonly targetComparisonAdmitted: false; readonly hardwareIdentityVerified: false;
  readonly sqlProcessPercent: null; readonly operatingSystemPercent: null;
}
export interface CpuCaptureHeader extends CpuCaptureBinding, Admission {
  readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string;
}
export interface CpuCaptureReceiptPage extends CpuCaptureHeader { readonly coverage: CpuCaptureCoverage; readonly items: readonly CpuCaptureReceipt[]; readonly nextCursor: string | null }
export interface CpuCaptureCounter {
  readonly counterType: number; readonly cStatus: number; readonly firstValue: string; readonly secondValue: string;
  readonly collectedLocalFileTime100ns: string; readonly multiCount: number; readonly apiStatus: number; readonly timeBase: string;
}
export interface CpuCaptureProcess { readonly creationBefore100ns: string | null; readonly creationAfter100ns: string | null; readonly exitAfter100ns: string | null; readonly apiStatus: number; readonly waitResultAfter: number | null }
export interface CpuCaptureSql {
  readonly sourceObservedAtUtc: string; readonly ordinal: number; readonly sourceSchemaVersion: number; readonly processId: number | null;
  readonly sourceStartLocal: string | null; readonly startMsTicks: string | null; readonly currentMsTicks: string | null;
  readonly processUserTimeMs: string | null; readonly processKernelTimeMs: string | null; readonly sqlReportedLogicalProcessors: number | null;
}
export interface CpuCaptureTcp {
  readonly observedAtUtc: string; readonly monotonic100ns: string; readonly af4Status: number; readonly af6Status: number;
  readonly rowCount4: number; readonly rowCount6: number; readonly matchingRowCount: number;
  readonly sqlProcess: CpuCaptureProcess; readonly collectorProcess: CpuCaptureProcess;
}
export interface CpuCaptureCpu {
  readonly observedAtUtc: string; readonly monotonicTicks: string; readonly monotonicFrequency: string;
  readonly sqlProcessCreationFileTime100ns: string | null; readonly logicalProcessorCount: number | null;
  readonly processCounter: CpuCaptureCounter | null; readonly osCounter: CpuCaptureCounter | null;
}
export interface CpuCaptureFrame {
  readonly processCreationBefore100ns: string | null; readonly processCreationAfter100ns: string | null; readonly processExitAfter100ns: string | null;
  readonly logicalProcessorsBefore: number; readonly logicalProcessorsAfter: number; readonly processInstances: number;
  readonly processMappingAmbiguous: boolean; readonly processWaitResultAfter: number | null;
}
export interface CpuCaptureEligibility {
  readonly sqlProcessEligible: boolean; readonly operatingSystemEligible: boolean; readonly sameNativeInterval: boolean;
  readonly sqlProcessReason: string; readonly operatingSystemReason: string;
  readonly sqlFromNative100ns: string | null; readonly sqlToNative100ns: string | null; readonly osFromNative100ns: string | null; readonly osToNative100ns: string | null;
}
export interface CpuCaptureDiagnostics extends Admission {
  readonly sourceSchemaVersion: number; readonly state: string; readonly reason: string; readonly candidateReason: string;
  readonly candidateLocalAssociation: boolean; readonly sourceFrames: number;
  readonly sqlBefore: CpuCaptureSql | null; readonly sqlAfter: CpuCaptureSql | null;
  readonly tcpBefore: CpuCaptureTcp | null; readonly tcpAfter: CpuCaptureTcp | null;
  readonly cpuBefore: CpuCaptureCpu | null; readonly cpuAfter: CpuCaptureCpu | null;
  readonly idProcessBefore: CpuCaptureCounter | null; readonly idProcessAfter: CpuCaptureCounter | null;
  readonly frameBefore: CpuCaptureFrame | null; readonly frameAfter: CpuCaptureFrame | null;
  readonly processStatusBefore: number; readonly processStatusAfter: number; readonly osStatusBefore: number; readonly osStatusAfter: number;
  readonly tcpBeforeReadReason: string | null; readonly cpuPairReadReason: string | null; readonly tcpAfterReadReason: string | null; readonly processReadReason: string | null;
  readonly numericalEligibility: CpuCaptureEligibility | null;
  readonly bytes: { readonly tcpBeforeHelperBytes: number | null; readonly cpuPairHelperBytes: number | null; readonly tcpAfterHelperBytes: number | null; readonly sqlLogicalResponseUpperBoundBytes: string };
}
export interface CpuCaptureDetailPage extends CpuCaptureHeader, CpuCaptureSelection { readonly receipt: CpuCaptureReceipt | null; readonly capture: CpuCaptureDiagnostics | null }
export const invalidCpuCaptureHistory = "CPU capture history returned invalid or mismatched evidence. Retry this exact read or refresh its received source.";
const invalid = (): never => { throw new Error(invalidCpuCaptureHistory); };
const integer = (value: unknown, min: number, max: number): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max;
const token = (value: unknown, allowed: readonly string[]): value is string => typeof value === "string" && allowed.includes(value);
function exact(value: unknown, keys: string): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value) || Object.keys(value).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return value as Record<string, unknown>;
}
export function cpuCaptureUtc(value: unknown): string {
  if (typeof value !== "string") return invalid();
  try { return canonicalUtcTimestamp(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); }
}
export function cpuCaptureSourceUtc(value: unknown): string {
  if (typeof value !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$/u.test(value)) return invalid();
  try { utcMicroseconds(value.replace(/(\.\d{6})\dZ$/u, "$1Z")); } catch { return invalid(); }
  return value;
}
export function cpuCaptureInt64(value: unknown, signed = false): string {
  if (typeof value !== "string" || !/^(?:0|[1-9]\d{0,18}|-[1-9]\d{0,18})$/u.test(value)) return invalid();
  const number = BigInt(value);
  if (number > 9223372036854775807n || number < (signed ? -9223372036854775808n : 0n)) return invalid();
  return value;
}
const optionalLong = (value: unknown, signed = false) => value === null ? null : cpuCaptureInt64(value, signed);
const uint = (value: unknown): number => integer(value, 0, 4294967295) ? value : invalid();
const boolean = (value: unknown): boolean => typeof value === "boolean" ? value : invalid();
const nullableInteger = (value: unknown, min: number, max: number) => value === null ? null : integer(value, min, max) ? value : invalid();
export function cpuCaptureWindow(fromUtc: string, toUtc: string) {
  const from = cpuCaptureUtc(fromUtc), to = cpuCaptureUtc(toUtc), duration = utcMicroseconds(to) - utcMicroseconds(from);
  if (duration <= 0n || duration > 31n * 86400n * 1000000n) throw new Error("CPU capture history supports a shared UTC window of up to 31 days. Choose a shorter window.");
  return { fromUtc: from, toUtc: to };
}
export function validCpuCaptureSelection(value: unknown): value is CpuCaptureSelection {
  if (!value || typeof value !== "object") return false;
  const row = value as CpuCaptureSelection;
  return notificationId(row.runId) && integer(row.sampleTargetRevision, 1, Number.MAX_SAFE_INTEGER);
}
export const cpuCaptureSelectionIdentity = (value: CpuCaptureSelection | null | undefined) => value === null ? "invalid" : value ? JSON.stringify([value.runId.toLowerCase(), value.sampleTargetRevision]) : "default";
export function cpuCaptureContextIdentity(binding: CpuCaptureBinding | undefined) {
  if (!binding) return "unavailable";
  try { const window = cpuCaptureWindow(binding.fromUtc, binding.toUtc); return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, utcMicroseconds(window.fromUtc).toString(), utcMicroseconds(window.toUtc).toString()]); }
  catch { return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, binding.fromUtc, binding.toUtc]); }
}
export function cpuCaptureCursor(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) return false;
  try { return btoa(atob(value)) === value; } catch { return false; }
}
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "visibility_incomplete"];
const captureReasons = ["source_qualification_absent", "host_binding_unavailable", "host_binding_denied", "host_binding_boundary", "host_cpu_capability_unavailable", "native_capture_incomplete", "candidate_association_rejected", "numerical_evidence_incomplete"];
const readReasons = ["source_qualification_absent", "timed_out", "host_binding_unavailable", "cpu_capability_unavailable", "permission_denied", "unsupported", "invalid_source_output", "provider_failure", "host_identity_unavailable", "wrong_host", "sql_source_schema_unqualified", "tcp_source_unavailable", "unsupported_address", "sql_process_unavailable", "native_table_unavailable", "native_process_denied", "process_unavailable", "process_exited", "process_epoch_unavailable", "process_epoch_changed", "process_lifetime_unknown", "socket_not_observed", "process_mapping_ambiguous", "process_race", "process_missing", "process_counter_unavailable"];
const candidateReasons = ["None", "TargetBoundary", "RevisionBoundary", "RunBoundary", "ConnectionBoundary", "SqlSourceIncomplete", "SqlEngineBoundary", "SourceSchemaBoundary", "UnsupportedTransport", "SocketWitnessIncomplete", "UnsupportedAddress", "TupleChanged", "CollectorProcessChanged", "QueryBoundary", "HostWitnessIncomplete", "HostBindingMismatch", "MachineBoundary", "BootBoundary", "NativeClockBoundary", "CaptureDoesNotBracketCpu", "CaptureWindowExceeded", "NativeTableUnavailable", "ProcessUnavailable", "ProcessExited", "ProcessLifetimeUnknown", "ProcessRestart", "ServerSocketMissing", "ReverseSocketMissing", "SocketNotEstablished", "SocketOwnerMismatch", "SocketAmbiguous", "NativeCaptureIncomplete"];
const numericalReasons = ["None", "Baseline", "TargetBoundary", "RevisionBoundary", "RunBoundary", "QueryBoundary", "SourceQualificationAbsent", "HostBindingMismatch", "MachineBoundary", "BootBoundary", "ProcessRestart", "ProcessorTopologyChanged", "ClockBoundary", "ClockSkew", "SourceUnavailable", "CounterStatusInvalid", "CounterTypeMismatch", "CounterTimeBaseMismatch", "CounterReset", "DenominatorInvalid", "IntervalMismatch", "CounterRangeInvalid"];
function admission(row: Record<string, unknown>): Admission {
  if (row.nativeQualification !== "absent" || row.targetComparisonAdmitted !== false || row.hardwareIdentityVerified !== false || row.sqlProcessPercent !== null || row.operatingSystemPercent !== null) return invalid();
  return { nativeQualification: "absent", targetComparisonAdmitted: false, hardwareIdentityVerified: false, sqlProcessPercent: null, operatingSystemPercent: null };
}
function coverage(value: unknown): CpuCaptureCoverage {
  const row = exact(value, "runCount|retainedReceiptCount|completeRunCount|gapRunCount|failedRunCount|lostRunCount|missingPayloadRunCount|hasVisibilityGap");
  for (const key of ["runCount", "retainedReceiptCount", "completeRunCount", "gapRunCount", "failedRunCount", "lostRunCount", "missingPayloadRunCount"]) if (!integer(row[key], 0, 10000)) return invalid();
  boolean(row.hasVisibilityGap);
  const result = row as unknown as CpuCaptureCoverage;
  if (result.retainedReceiptCount > result.runCount || result.completeRunCount > result.retainedReceiptCount || result.completeRunCount + result.gapRunCount !== result.runCount || result.failedRunCount > result.gapRunCount || result.lostRunCount > result.runCount || result.missingPayloadRunCount > result.gapRunCount || result.gapRunCount > 0 && !result.hasVisibilityGap) return invalid();
  return result;
}
function receipt(value: unknown, header: CpuCaptureHeader): CpuCaptureReceipt {
  const row = exact(value, "runId|sampleTargetRevision|completedAtUtc|outcome|lossKind|minimumLostItems|capturePresent|sourceFrames|state|reason");
  if (!validCpuCaptureSelection(row) || row.sampleTargetRevision > header.targetRevision || !token(row.outcome, outcomes) || !token(row.lossKind, losses) || !integer(row.minimumLostItems, 0, 6) || row.lossKind === "none" && row.minimumLostItems !== 0) return invalid();
  const present = boolean(row.capturePresent), frames = nullableInteger(row.sourceFrames, 0, 6), completedAtUtc = cpuCaptureUtc(row.completedAtUtc);
  if (present !== (frames !== null) || present && (row.outcome !== "succeeded" || row.lossKind !== "none" || !token(row.reason, captureReasons) || row.state !== (frames === 6 ? "partial" : "unavailable")) || !present && (row.state !== "unavailable" || row.reason !== (row.outcome === "succeeded" ? "payload_missing" : "collection_failed")) || utcMicroseconds(completedAtUtc) < utcMicroseconds(header.fromUtc) || utcMicroseconds(completedAtUtc) >= utcMicroseconds(header.toUtc) || utcMicroseconds(completedAtUtc) > utcMicroseconds(header.cutoffUtc)) return invalid();
  return { ...row, runId: row.runId.toLowerCase(), completedAtUtc } as unknown as CpuCaptureReceipt;
}
const admissionKeys = "nativeQualification|targetComparisonAdmitted|hardwareIdentityVerified|sqlProcessPercent|operatingSystemPercent";
function header(value: unknown, binding: CpuCaptureBinding, extra: string): { row: Record<string, unknown>; common: CpuCaptureHeader } {
  const row = exact(value, "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|" + admissionKeys + "|" + extra);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER) || !notificationId(row.targetId) || row.targetId.toLowerCase() !== binding.targetId.toLowerCase() || row.targetRevision !== binding.targetRevision || typeof row.readEpoch !== "string" || !/^[a-f0-9]{64}$/u.test(row.readEpoch) || !token(row.state, ["available", "partial", "unavailable"])) return invalid();
  const window = cpuCaptureWindow(binding.fromUtc, binding.toUtc), fromUtc = cpuCaptureUtc(row.fromUtc), toUtc = cpuCaptureUtc(row.toUtc), cutoffUtc = cpuCaptureUtc(row.cutoffUtc), repositoryTimeUtc = cpuCaptureUtc(row.repositoryTimeUtc);
  if (utcMicroseconds(fromUtc) !== utcMicroseconds(window.fromUtc) || utcMicroseconds(toUtc) !== utcMicroseconds(window.toUtc) || utcMicroseconds(cutoffUtc) > utcMicroseconds(repositoryTimeUtc)) return invalid();
  return { row, common: { targetId: row.targetId.toLowerCase(), targetRevision: binding.targetRevision, fromUtc, toUtc, cutoffUtc, repositoryTimeUtc, readEpoch: row.readEpoch, state: String(row.state), reason: String(row.reason), ...admission(row) } };
}
export function parseCpuCaptureReceiptPage(value: unknown, binding: CpuCaptureBinding, prior: readonly CpuCaptureReceiptPage[] = [], cursor?: string): CpuCaptureReceiptPage {
  const { row, common } = header(value, binding, "coverage|items|nextCursor"), cov = coverage(row.coverage);
  if (!Array.isArray(row.items) || row.items.length > 100 || row.nextCursor !== null && !cpuCaptureCursor(row.nextCursor) || Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!cpuCaptureCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || row.nextCursor === cursor) || prior.some(page => page.nextCursor !== null && page.nextCursor === row.nextCursor)) return invalid();
  const state = cov.runCount === 0 ? "unavailable" : cov.hasVisibilityGap ? "partial" : "available";
  const reason = cov.runCount === 0 ? "no_completed_runs" : cov.hasVisibilityGap ? "visibility_gaps" : "retained_captures";
  if (common.state !== state || (common.reason === "revision_boundary" ? !cov.hasVisibilityGap : common.reason !== reason)) return invalid();
  if (prior.length && (prior[0]!.readEpoch !== common.readEpoch || utcMicroseconds(prior[0]!.cutoffUtc) !== utcMicroseconds(common.cutoffUtc) || prior[0]!.state !== common.state || prior[0]!.reason !== common.reason || JSON.stringify(prior[0]!.coverage) !== JSON.stringify(cov))) return invalid();
  const items = row.items.map(value => receipt(value, common)), all = [...prior.flatMap(page => page.items), ...items];
  let tail = prior.at(-1)?.items.at(-1); const seen = new Set(prior.flatMap(page => page.items.map(item => item.runId)));
  for (const item of items) {
    if (seen.has(item.runId) || tail && (utcMicroseconds(item.completedAtUtc) > utcMicroseconds(tail.completedAtUtc) || utcMicroseconds(item.completedAtUtc) === utcMicroseconds(tail.completedAtUtc) && item.runId >= tail.runId)) return invalid();
    seen.add(item.runId); tail = item;
  }
  const counts = { runCount: all.length, completeRunCount: all.filter(item => item.outcome === "succeeded" && item.lossKind === "none" && item.sourceFrames === 6).length,
    failedRunCount: all.filter(item => item.outcome !== "succeeded").length, lostRunCount: all.filter(item => item.lossKind !== "none").length,
    missingPayloadRunCount: all.filter(item => item.outcome === "succeeded" && !item.capturePresent).length };
  if (all.length - counts.completeRunCount > cov.gapRunCount || all.filter(item => item.capturePresent).length > cov.retainedReceiptCount) return invalid();
  if (Object.entries(counts).some(([key, count]) => count > cov[key as keyof typeof counts] || row.nextCursor === null && count !== cov[key as keyof typeof counts]) || row.nextCursor !== null && (items.length !== 100 || all.length >= cov.runCount)) return invalid();
  return { ...common, coverage: cov, items, nextCursor: row.nextCursor as string | null };
}

function counter(value: unknown): CpuCaptureCounter | null {
  if (value === null) return null;
  const row = exact(value, "counterType|cStatus|firstValue|secondValue|collectedLocalFileTime100ns|multiCount|apiStatus|timeBase");
  return { counterType: uint(row.counterType), cStatus: uint(row.cStatus), firstValue: cpuCaptureInt64(row.firstValue, true), secondValue: cpuCaptureInt64(row.secondValue, true), collectedLocalFileTime100ns: cpuCaptureInt64(row.collectedLocalFileTime100ns, true), multiCount: uint(row.multiCount), apiStatus: uint(row.apiStatus), timeBase: cpuCaptureInt64(row.timeBase, true) };
}
function process(value: unknown): CpuCaptureProcess {
  const row = exact(value, "creationBefore100ns|creationAfter100ns|exitAfter100ns|apiStatus|waitResultAfter");
  return { creationBefore100ns: optionalLong(row.creationBefore100ns), creationAfter100ns: optionalLong(row.creationAfter100ns), exitAfter100ns: optionalLong(row.exitAfter100ns, true), apiStatus: uint(row.apiStatus), waitResultAfter: row.waitResultAfter === null ? null : uint(row.waitResultAfter) };
}
function sql(value: unknown, ordinal: number): CpuCaptureSql | null {
  if (value === null) return null;
  const row = exact(value, "sourceObservedAtUtc|ordinal|sourceSchemaVersion|processId|sourceStartLocal|startMsTicks|currentMsTicks|processUserTimeMs|processKernelTimeMs|sqlReportedLogicalProcessors");
  if (row.ordinal !== ordinal || !integer(row.sourceSchemaVersion, 1, 2)) return invalid();
  if (row.sourceStartLocal !== null) {
    if (typeof row.sourceStartLocal !== "string" || !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}$/u.test(row.sourceStartLocal)) return invalid();
    cpuCaptureSourceUtc(row.sourceStartLocal + "Z");
  }
  return { sourceObservedAtUtc: cpuCaptureSourceUtc(row.sourceObservedAtUtc), ordinal, sourceSchemaVersion: Number(row.sourceSchemaVersion), processId: nullableInteger(row.processId, 1, 2147483647), sourceStartLocal: row.sourceStartLocal as string | null, startMsTicks: optionalLong(row.startMsTicks), currentMsTicks: optionalLong(row.currentMsTicks), processUserTimeMs: optionalLong(row.processUserTimeMs), processKernelTimeMs: optionalLong(row.processKernelTimeMs), sqlReportedLogicalProcessors: nullableInteger(row.sqlReportedLogicalProcessors, 1, 4096) };
}
function tcp(value: unknown): CpuCaptureTcp | null {
  if (value === null) return null;
  const row = exact(value, "observedAtUtc|monotonic100ns|af4Status|af6Status|rowCount4|rowCount6|matchingRowCount|sqlProcess|collectorProcess");
  if (!integer(row.rowCount4, 0, 4096) || !integer(row.rowCount6, 0, 4096) || row.rowCount4 + row.rowCount6 > 4096 || !integer(row.matchingRowCount, 0, 16) || row.matchingRowCount > row.rowCount4 + row.rowCount6) return invalid();
  return { ...row, observedAtUtc: cpuCaptureSourceUtc(row.observedAtUtc), monotonic100ns: cpuCaptureInt64(row.monotonic100ns), af4Status: uint(row.af4Status), af6Status: uint(row.af6Status), sqlProcess: process(row.sqlProcess), collectorProcess: process(row.collectorProcess) } as CpuCaptureTcp;
}
function cpu(value: unknown): CpuCaptureCpu | null {
  if (value === null) return null;
  const row = exact(value, "observedAtUtc|monotonicTicks|monotonicFrequency|sqlProcessCreationFileTime100ns|logicalProcessorCount|processCounter|osCounter");
  const frequency = cpuCaptureInt64(row.monotonicFrequency); if (BigInt(frequency) <= 0n) return invalid();
  return { observedAtUtc: cpuCaptureSourceUtc(row.observedAtUtc), monotonicTicks: cpuCaptureInt64(row.monotonicTicks), monotonicFrequency: frequency, sqlProcessCreationFileTime100ns: optionalLong(row.sqlProcessCreationFileTime100ns), logicalProcessorCount: nullableInteger(row.logicalProcessorCount, 1, 4294967295), processCounter: counter(row.processCounter), osCounter: counter(row.osCounter) };
}
function frame(value: unknown): CpuCaptureFrame | null {
  if (value === null) return null;
  const row = exact(value, "processCreationBefore100ns|processCreationAfter100ns|processExitAfter100ns|logicalProcessorsBefore|logicalProcessorsAfter|processInstances|processMappingAmbiguous|processWaitResultAfter");
  if (!integer(row.processInstances, 0, 1024)) return invalid();
  return { processCreationBefore100ns: optionalLong(row.processCreationBefore100ns), processCreationAfter100ns: optionalLong(row.processCreationAfter100ns), processExitAfter100ns: optionalLong(row.processExitAfter100ns, true), logicalProcessorsBefore: uint(row.logicalProcessorsBefore), logicalProcessorsAfter: uint(row.logicalProcessorsAfter), processInstances: row.processInstances, processMappingAmbiguous: boolean(row.processMappingAmbiguous), processWaitResultAfter: row.processWaitResultAfter === null ? null : uint(row.processWaitResultAfter) };
}
function eligibility(value: unknown): CpuCaptureEligibility | null {
  if (value === null) return null;
  const row = exact(value, "sqlProcessEligible|operatingSystemEligible|sameNativeInterval|sqlProcessReason|operatingSystemReason|sqlFromNative100ns|sqlToNative100ns|osFromNative100ns|osToNative100ns");
  const sqlEligible = boolean(row.sqlProcessEligible), osEligible = boolean(row.operatingSystemEligible), same = boolean(row.sameNativeInterval);
  const sf = optionalLong(row.sqlFromNative100ns), st = optionalLong(row.sqlToNative100ns), of = optionalLong(row.osFromNative100ns), ot = optionalLong(row.osToNative100ns);
  for (const [eligible, reason, from, to] of [[sqlEligible, row.sqlProcessReason, sf, st], [osEligible, row.operatingSystemReason, of, ot]] as const)
    if (!token(reason, numericalReasons) || (eligible ? reason !== "None" || from === null || to === null || BigInt(from) <= 0n || BigInt(to) <= BigInt(from) : reason === "None" || from !== null || to !== null)) return invalid();
  if (same !== (sqlEligible && osEligible && sf === of && st === ot)) return invalid();
  return { sqlProcessEligible: sqlEligible, operatingSystemEligible: osEligible, sameNativeInterval: same, sqlProcessReason: String(row.sqlProcessReason), operatingSystemReason: String(row.operatingSystemReason), sqlFromNative100ns: sf, sqlToNative100ns: st, osFromNative100ns: of, osToNative100ns: ot };
}
function capture(value: unknown): CpuCaptureDiagnostics {
  const row = exact(value, "sourceSchemaVersion|state|reason|candidateReason|candidateLocalAssociation|sourceFrames|sqlBefore|sqlAfter|tcpBefore|tcpAfter|cpuBefore|cpuAfter|idProcessBefore|idProcessAfter|frameBefore|frameAfter|processStatusBefore|processStatusAfter|osStatusBefore|osStatusAfter|tcpBeforeReadReason|cpuPairReadReason|tcpAfterReadReason|processReadReason|numericalEligibility|bytes|" + admissionKeys);
  const result = { ...row, ...admission(row), sqlBefore: sql(row.sqlBefore, 1), sqlAfter: sql(row.sqlAfter, 2), tcpBefore: tcp(row.tcpBefore), tcpAfter: tcp(row.tcpAfter), cpuBefore: cpu(row.cpuBefore), cpuAfter: cpu(row.cpuAfter), idProcessBefore: counter(row.idProcessBefore), idProcessAfter: counter(row.idProcessAfter), frameBefore: frame(row.frameBefore), frameAfter: frame(row.frameAfter), numericalEligibility: eligibility(row.numericalEligibility) } as unknown as CpuCaptureDiagnostics;
  const frames = [result.sqlBefore, result.sqlAfter, result.tcpBefore, result.tcpAfter, result.cpuBefore, result.cpuAfter].filter(value => value !== null).length;
  if (row.sourceSchemaVersion !== 1 || row.sourceFrames !== frames || row.state !== (frames === 6 ? "partial" : "unavailable") || !token(row.reason, captureReasons) || !token(row.candidateReason, candidateReasons) || boolean(row.candidateLocalAssociation) !== (row.candidateReason === "None") || row.candidateReason === "None" && frames !== 6 || result.numericalEligibility !== null && row.candidateLocalAssociation !== true) return invalid();
  for (const key of ["processStatusBefore", "processStatusAfter", "osStatusBefore", "osStatusAfter"]) uint(row[key]);
  for (const key of ["tcpBeforeReadReason", "cpuPairReadReason", "tcpAfterReadReason", "processReadReason"]) if (row[key] !== null && !token(row[key], readReasons)) return invalid();
  const bytes = exact(row.bytes, "tcpBeforeHelperBytes|cpuPairHelperBytes|tcpAfterHelperBytes|sqlLogicalResponseUpperBoundBytes");
  for (const key of ["tcpBeforeHelperBytes", "cpuPairHelperBytes", "tcpAfterHelperBytes"]) nullableInteger(bytes[key], 0, 65536);
  if (BigInt(cpuCaptureInt64(bytes.sqlLogicalResponseUpperBoundBytes)) > 16384n) return invalid();
  return result;
}
export function parseCpuCaptureDetailPage(value: unknown, binding: CpuCaptureBinding, selected: CpuCaptureSelection): CpuCaptureDetailPage {
  if (!validCpuCaptureSelection(selected)) return invalid();
  const { row, common } = header(value, binding, "runId|sampleTargetRevision|receipt|capture");
  if (row.runId !== selected.runId.toLowerCase() || row.sampleTargetRevision !== selected.sampleTargetRevision || selected.sampleTargetRevision > binding.targetRevision) return invalid();
  const received = row.receipt === null ? null : receipt(row.receipt, common), diagnostics = row.capture === null ? null : capture(row.capture);
  if (received && (received.runId !== row.runId || received.sampleTargetRevision !== row.sampleTargetRevision || received.state !== common.state || received.reason !== common.reason || received.capturePresent !== (diagnostics !== null) || diagnostics !== null && (diagnostics.sourceFrames !== received.sourceFrames || diagnostics.state !== received.state || diagnostics.reason !== received.reason)) || !received && (diagnostics !== null || common.state !== "unavailable" || !["capture_not_retained", "capture_outside_window"].includes(common.reason))) return invalid();
  return { ...common, runId: selected.runId.toLowerCase(), sampleTargetRevision: selected.sampleTargetRevision, receipt: received, capture: diagnostics };
}
