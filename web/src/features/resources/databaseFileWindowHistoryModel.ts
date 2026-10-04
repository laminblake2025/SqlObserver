import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";

export interface FileSnapshotSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface FileHistoryBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface FileHistoryCoverage {
  readonly runCount: number; readonly completeRunCount: number; readonly gapRunCount: number; readonly failedRunCount: number;
  readonly lostRunCount: number; readonly missingPayloadRunCount: number; readonly emptyRunCount: number;
  readonly retainedFileCount: number; readonly retentionDays: number | null; readonly hasVisibilityGap: boolean;
}
export interface FileHistoryReceipt extends FileSnapshotSelection {
  readonly completedAtUtc: string; readonly outcome: string; readonly lossKind: string | null; readonly minimumLostItems: number | null;
  readonly expectedFileCount: number | null; readonly storedFileCount: number; readonly firstObservedAtUtc: string | null; readonly lastObservedAtUtc: string | null;
  readonly firstCollectedAtUtc: string | null; readonly lastCollectedAtUtc: string | null; readonly state: string; readonly reason: string;
}
export interface HistoricalDatabaseFile {
  readonly databaseId: number; readonly fileId: number; readonly logicalName: string; readonly fileType: string; readonly fileState: string;
  readonly sizeBytes: string; readonly maximumSizeBytes: string | null; readonly growthBytes: string; readonly growthPercent: number;
  readonly growthMode: string; readonly observedAtUtc: string; readonly collectedAtUtc: string;
  readonly readCount: string; readonly writeCount: string; readonly bytesRead: string; readonly bytesWritten: string; readonly ioStallMilliseconds: string;
  readonly readStallMilliseconds: string | null; readonly writeStallMilliseconds: string | null;
}
export interface FileHistoryHeader extends FileHistoryBinding {
  readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string;
  readonly nextCursor: string | null;
}
export interface FileReceiptPage extends FileHistoryHeader { readonly coverage: FileHistoryCoverage; readonly items: readonly FileHistoryReceipt[] }
export interface FileSnapshotPage extends FileHistoryHeader, FileSnapshotSelection { readonly receipt: FileHistoryReceipt | null; readonly items: readonly HistoricalDatabaseFile[] }
export const invalidFileHistory = "Database file history returned invalid or mismatched evidence. Retry this exact read or refresh the received source.";
const invalid = (): never => { throw new Error(invalidFileHistory); };
const integer = (v: unknown, minimum: number, maximum: number): v is number => typeof v === "number" && Number.isSafeInteger(v) && v >= minimum && v <= maximum;
const equalTime = (a: string, b: string) => utcMicroseconds(a) === utcMicroseconds(b);
export function fileHistoryUtc(value: unknown): string {
  if (typeof value !== "string") return invalid();
  try { return canonicalUtcTimestamp(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); } catch { return invalid(); }
}
export function fileHistoryWindow(fromUtc: string, toUtc: string) {
  const from = fileHistoryUtc(fromUtc), to = fileHistoryUtc(toUtc), duration = utcMicroseconds(to) - utcMicroseconds(from);
  if (duration <= 0n || duration > 7n * 86400n * 1000000n) throw new Error("Database file history supports a shared UTC window of up to seven days. Choose a shorter window.");
  return { fromUtc: from, toUtc: to };
}
export function validFileSnapshotSelection(value: unknown): value is FileSnapshotSelection {
  if (!value || typeof value !== "object") return false;
  const row = value as FileSnapshotSelection;
  return notificationId(row.runId) && integer(row.sampleTargetRevision, 1, Number.MAX_SAFE_INTEGER);
}
export function fileSnapshotIdentity(value: FileSnapshotSelection | null | undefined): string {
  return value === null ? "invalid" : value ? JSON.stringify([value.runId.toLowerCase(), value.sampleTargetRevision]) : "default";
}
export function fileHistoryContextIdentity(binding: FileHistoryBinding | undefined): string {
  if (!binding) return "unavailable";
  try { const window = fileHistoryWindow(binding.fromUtc, binding.toUtc); return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, utcMicroseconds(window.fromUtc).toString(), utcMicroseconds(window.toUtc).toString()]); }
  catch { return JSON.stringify([binding.targetId.toLowerCase(), binding.targetRevision, binding.fromUtc, binding.toUtc]); }
}
export function fileHistoryCursor(v: unknown): v is string {
  if (typeof v !== "string" || !v.length || v.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(v)) return false;
  try { return btoa(atob(v)) === v; } catch { return false; }
}
function exact(v: unknown, keys: string): Record<string, unknown> {
  if (!v || typeof v !== "object" || Array.isArray(v) || Object.keys(v).sort().join("|") !== keys.split("|").sort().join("|")) return invalid();
  return v as Record<string, unknown>;
}
export function fileHistoryBytes(v: unknown): string {
  if (typeof v !== "string" || !/^(?:0|[1-9]\d{0,18})$/u.test(v) || BigInt(v) > 9223372036854775807n) return invalid();
  return v;
}
const states = ["available", "partial", "unavailable"];
const outcomes = ["succeeded", "partial", "timed_out", "transient_failure", "permanent_failure", "permission_denied", "unsupported", "output_invalid", "lease_lost", "circuit_open"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection"];
const receiptReasons = ["observed_files", "observed_empty", "collection_failed", "collection_loss", "collection_gap", "source_schema_unsupported", "payload_missing", "stored_count_mismatch", "source_clock_skew"];
function parseCoverage(value: unknown): FileHistoryCoverage {
  const r = exact(value, "runCount|completeRunCount|gapRunCount|failedRunCount|lostRunCount|missingPayloadRunCount|emptyRunCount|retainedFileCount|retentionDays|hasVisibilityGap");
  for (const key of ["runCount", "completeRunCount", "gapRunCount", "failedRunCount", "lostRunCount", "missingPayloadRunCount", "emptyRunCount"]) if (!integer(r[key], 0, 10000)) return invalid();
  if (!integer(r.retainedFileCount, 0, 250000) || r.retentionDays !== null && !integer(r.retentionDays, 1, Number.MAX_SAFE_INTEGER) || typeof r.hasVisibilityGap !== "boolean") return invalid();
  const coverage = r as unknown as FileHistoryCoverage;
  if (coverage.completeRunCount + coverage.gapRunCount !== coverage.runCount || coverage.failedRunCount > coverage.gapRunCount || coverage.lostRunCount > coverage.gapRunCount || coverage.missingPayloadRunCount > coverage.gapRunCount || coverage.emptyRunCount > coverage.completeRunCount || coverage.gapRunCount > 0 && !coverage.hasVisibilityGap) return invalid();
  return coverage;
}
function parseReceipt(value: unknown, header: FileHistoryHeader): FileHistoryReceipt {
  const r = exact(value, "runId|sampleTargetRevision|completedAtUtc|outcome|lossKind|minimumLostItems|expectedFileCount|storedFileCount|firstObservedAtUtc|lastObservedAtUtc|firstCollectedAtUtc|lastCollectedAtUtc|state|reason");
  if (!validFileSnapshotSelection(r) || (r.sampleTargetRevision as number) > header.targetRevision || !outcomes.includes(String(r.outcome)) || r.lossKind !== null && !losses.includes(String(r.lossKind)) || r.minimumLostItems !== null && !integer(r.minimumLostItems, 0, 2147483647) || r.expectedFileCount !== null && !integer(r.expectedFileCount, 0, 10000) || !integer(r.storedFileCount, 0, 10000) || !states.includes(String(r.state)) || !receiptReasons.includes(String(r.reason))) return invalid();
  const completedAtUtc = fileHistoryUtc(r.completedAtUtc), times = [r.firstObservedAtUtc,r.lastObservedAtUtc,r.firstCollectedAtUtc,r.lastCollectedAtUtc].map(v => v === null ? null : fileHistoryUtc(v));
  if (utcMicroseconds(completedAtUtc) < utcMicroseconds(header.fromUtc) || utcMicroseconds(completedAtUtc) >= utcMicroseconds(header.toUtc) || utcMicroseconds(completedAtUtc) > utcMicroseconds(header.cutoffUtc) || times.some(v => v === null) && times.some(v => v !== null) || r.storedFileCount === 0 && times.some(v => v !== null) || r.storedFileCount > 0 && times.some(v => v === null)) return invalid();
  if (times[0] && times[1] && utcMicroseconds(times[0]) > utcMicroseconds(times[1]) || times[2] && times[3] && (utcMicroseconds(times[2]) > utcMicroseconds(times[3]) || utcMicroseconds(times[3]) > utcMicroseconds(header.cutoffUtc))) return invalid();
  const skew=times[1]!==null&&(utcMicroseconds(times[1]!)>utcMicroseconds(header.cutoffUtc)||[0,1].some(i=>{const delta=utcMicroseconds(times[i]!)-utcMicroseconds(times[i+2]!);return delta>300000000n||delta< -300000000n;}));
  if (r.state === "available" && (r.outcome !== "succeeded" || r.lossKind !== "none" || r.minimumLostItems !== 0 || r.expectedFileCount !== r.storedFileCount || skew || r.reason !== (r.storedFileCount>0?"observed_files":"observed_empty")) || ["observed_files","observed_empty"].includes(String(r.reason))&&r.state!=="available" || r.reason==="source_clock_skew"&&(r.storedFileCount===0||r.state==="available")) return invalid();
  return { ...r, runId: String(r.runId).toLowerCase(), completedAtUtc, firstObservedAtUtc: times[0]!, lastObservedAtUtc: times[1]!, firstCollectedAtUtc: times[2]!, lastCollectedAtUtc: times[3]! } as unknown as FileHistoryReceipt;
}
function header(value: unknown, binding: FileHistoryBinding, extra: string, prior: readonly FileHistoryHeader[], cursor?: string,limit=extra==="coverage"?50:100): {r: Record<string, unknown>; common: FileHistoryHeader} {
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision, 1, Number.MAX_SAFE_INTEGER)) return invalid();
  const window = fileHistoryWindow(binding.fromUtc,binding.toUtc), r = exact(value, "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason|items|nextCursor|" + extra);
  const fromUtc = fileHistoryUtc(r.fromUtc), toUtc = fileHistoryUtc(r.toUtc), cutoffUtc = fileHistoryUtc(r.cutoffUtc), repositoryTimeUtc = fileHistoryUtc(r.repositoryTimeUtc);
  if (!integer(limit,1,100)||!notificationId(r.targetId) || r.targetId.toLowerCase() !== binding.targetId.toLowerCase() || r.targetRevision !== binding.targetRevision || !equalTime(fromUtc,window.fromUtc) || !equalTime(toUtc,window.toUtc) || typeof r.readEpoch !== "string" || !/^[a-f0-9]{64}$/u.test(r.readEpoch) || utcMicroseconds(cutoffUtc) > utcMicroseconds(repositoryTimeUtc) || !states.includes(String(r.state)) || !Array.isArray(r.items) || r.items.length > limit || r.nextCursor !== null && (!fileHistoryCursor(r.nextCursor) || prior.some(p => p.nextCursor === r.nextCursor)) || Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!fileHistoryCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || r.nextCursor === cursor)) return invalid();
  const common: FileHistoryHeader = {targetId:r.targetId,targetRevision:binding.targetRevision,fromUtc,toUtc,cutoffUtc,repositoryTimeUtc,readEpoch:r.readEpoch,state:String(r.state),reason:String(r.reason),nextCursor:r.nextCursor as string|null};
  if (prior.length && (!equalTime(prior[0]!.cutoffUtc,cutoffUtc) || prior[0]!.readEpoch !== common.readEpoch || prior[0]!.state !== common.state || prior[0]!.reason !== common.reason)) return invalid();
  return {r,common};
}
export function parseFileReceiptPage(value: unknown, binding: FileHistoryBinding, prior: readonly FileReceiptPage[] = [], cursor?: string): FileReceiptPage {
  const {r,common} = header(value,binding,"coverage",prior,cursor), coverage = parseCoverage(r.coverage);
  if (!['retained_window','visibility_gaps','no_completed_runs','revision_boundary'].includes(common.reason) || common.state !== (coverage.runCount === 0 ? "unavailable" : coverage.hasVisibilityGap ? "partial" : "available") || (common.reason === "revision_boundary" ? !coverage.hasVisibilityGap : common.reason !== (coverage.runCount === 0 ? "no_completed_runs" : coverage.hasVisibilityGap ? "visibility_gaps" : "retained_window")) || prior.length && JSON.stringify(prior[0]!.coverage) !== JSON.stringify(coverage)) return invalid();
  const seen = new Set(prior.flatMap(p => p.items.map(item => item.runId))), rows = (r.items as unknown[]).map(v => parseReceipt(v,common));
  let tail = prior.at(-1)?.items.at(-1);
  for (const row of rows) { if (seen.has(row.runId) || tail && (utcMicroseconds(row.completedAtUtc) > utcMicroseconds(tail.completedAtUtc) || equalTime(row.completedAtUtc,tail.completedAtUtc) && row.runId >= tail.runId)) return invalid(); seen.add(row.runId); tail = row; }
  const all = [...prior.flatMap(p => p.items),...rows];
  const counts={runCount:all.length,completeRunCount:all.filter(row=>row.state==="available").length,gapRunCount:all.filter(row=>row.state!=="available").length,
    failedRunCount:all.filter(row=>!['succeeded','partial'].includes(row.outcome)).length,lostRunCount:all.filter(row=>row.lossKind!==null&&row.lossKind!=="none").length,
    missingPayloadRunCount:all.filter(row=>row.expectedFileCount!==null&&row.expectedFileCount>row.storedFileCount).length,emptyRunCount:all.filter(row=>row.reason==="observed_empty").length,
    retainedFileCount:all.reduce((sum,row)=>sum+row.storedFileCount,0)};
  if (Object.entries(counts).some(([key,count])=>count>coverage[key as keyof typeof counts]||common.nextCursor===null&&count!==coverage[key as keyof typeof counts]) || common.nextCursor !== null && (rows.length !== 50 || all.length >= coverage.runCount)) return invalid();
  return {...common,coverage,items:rows};
}
function parseFile(value: unknown, page: FileHistoryHeader, receipt: FileHistoryReceipt): HistoricalDatabaseFile {
  const r = exact(value,"databaseId|fileId|logicalName|fileType|fileState|sizeBytes|maximumSizeBytes|growthBytes|growthPercent|growthMode|observedAtUtc|collectedAtUtc|readCount|writeCount|bytesRead|bytesWritten|ioStallMilliseconds|readStallMilliseconds|writeStallMilliseconds");
  if (!integer(r.databaseId,1,32767) || !integer(r.fileId,1,65535) || typeof r.logicalName !== "string" || r.logicalName.length===0 || new TextEncoder().encode(r.logicalName).length > 1024 || /[\uD800-\uDFFF]/u.test(r.logicalName) || !["rows","log","filestream","fulltext","other"].includes(String(r.fileType)) || !["online","restoring","recovering","recovery_pending","suspect","emergency","offline","defunct","other"].includes(String(r.fileState)) || !integer(r.growthPercent,0,100)) return invalid();
  const sizeBytes=fileHistoryBytes(r.sizeBytes), maximumSizeBytes=r.maximumSizeBytes===null?null:fileHistoryBytes(r.maximumSizeBytes),growthBytes=fileHistoryBytes(r.growthBytes),observedAtUtc=fileHistoryUtc(r.observedAtUtc),collectedAtUtc=fileHistoryUtc(r.collectedAtUtc);
  const clockDelta=utcMicroseconds(observedAtUtc)-utcMicroseconds(collectedAtUtc);
  if(receipt.state==="available"&&(utcMicroseconds(observedAtUtc)>utcMicroseconds(page.cutoffUtc)||clockDelta>300000000n||clockDelta< -300000000n))return invalid();
  if (maximumSizeBytes!==null && BigInt(maximumSizeBytes)<BigInt(sizeBytes) || utcMicroseconds(collectedAtUtc)>utcMicroseconds(page.cutoffUtc) || receipt.firstObservedAtUtc===null || receipt.lastObservedAtUtc===null || receipt.firstCollectedAtUtc===null || receipt.lastCollectedAtUtc===null || utcMicroseconds(observedAtUtc)<utcMicroseconds(receipt.firstObservedAtUtc) || utcMicroseconds(observedAtUtc)>utcMicroseconds(receipt.lastObservedAtUtc) || utcMicroseconds(collectedAtUtc)<utcMicroseconds(receipt.firstCollectedAtUtc) || utcMicroseconds(collectedAtUtc)>utcMicroseconds(receipt.lastCollectedAtUtc)) return invalid();
  const mode=BigInt(growthBytes)>0n?r.growthPercent>0?"ambiguous":"fixed_bytes":r.growthPercent>0?"percent":"disabled";
  const total=fileHistoryBytes(r.ioStallMilliseconds),read=r.readStallMilliseconds===null?null:fileHistoryBytes(r.readStallMilliseconds),write=r.writeStallMilliseconds===null?null:fileHistoryBytes(r.writeStallMilliseconds);
  if (r.growthMode!==mode||(read===null)!==(write===null)||read!==null&&BigInt(read)+BigInt(write!)!==BigInt(total))return invalid();
  return {...r,sizeBytes,maximumSizeBytes,growthBytes,observedAtUtc,collectedAtUtc,readCount:fileHistoryBytes(r.readCount),writeCount:fileHistoryBytes(r.writeCount),bytesRead:fileHistoryBytes(r.bytesRead),bytesWritten:fileHistoryBytes(r.bytesWritten),ioStallMilliseconds:total,readStallMilliseconds:read,writeStallMilliseconds:write} as unknown as HistoricalDatabaseFile;
}
export function parseFileSnapshotPage(value: unknown, binding: FileHistoryBinding, selection: FileSnapshotSelection, prior: readonly FileSnapshotPage[] = [], cursor?: string,limit=100): FileSnapshotPage {
  if (!validFileSnapshotSelection(selection)) return invalid();
  const {r,common}=header(value,binding,"runId|sampleTargetRevision|receipt",prior,cursor,limit);
  if (r.runId !== selection.runId.toLowerCase() || r.sampleTargetRevision !== selection.sampleTargetRevision || !receiptReasons.includes(common.reason) && !["snapshot_not_retained","snapshot_outside_window"].includes(common.reason)) return invalid();
  const receipt=r.receipt===null?null:parseReceipt(r.receipt,common);
  if (receipt && (receipt.runId !== selection.runId.toLowerCase() || receipt.sampleTargetRevision !== selection.sampleTargetRevision || receipt.state !== common.state || receipt.reason !== common.reason) || !receipt && (common.state !== "unavailable" || !["snapshot_not_retained","snapshot_outside_window"].includes(common.reason) || (r.items as unknown[]).length!==0 || common.nextCursor!==null) || prior.length && JSON.stringify(prior[0]!.receipt)!==JSON.stringify(receipt)) return invalid();
  const rows=(r.items as unknown[]).map(v=>receipt?parseFile(v,common,receipt):invalid()),seen=new Set(prior.flatMap(p=>p.items.map(f=>`${f.databaseId}:${f.fileId}`)));
  let tail=prior.at(-1)?.items.at(-1);for(const file of rows){const identity=`${file.databaseId}:${file.fileId}`;if(seen.has(identity)||tail&&(file.databaseId<tail.databaseId||file.databaseId===tail.databaseId&&file.fileId<=tail.fileId))return invalid();seen.add(identity);tail=file;}
  const count=prior.reduce((sum,p)=>sum+p.items.length,0)+rows.length;
  if (receipt && (count>receipt.storedFileCount || common.nextCursor!==null&&(rows.length!==limit||count>=receipt.storedFileCount) || common.nextCursor===null&&count!==receipt.storedFileCount))return invalid();
  return {...common,runId:selection.runId.toLowerCase(),sampleTargetRevision:selection.sampleTargetRevision,receipt,items:rows};
}
