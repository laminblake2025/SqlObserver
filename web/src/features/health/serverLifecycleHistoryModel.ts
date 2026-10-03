import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import type { TimelineEvent } from "../../components/dashboardTimelineModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";
import { dashboardTimelineSelectionHref } from "../../components/dashboardTimelineSelectionModel.ts";

export interface ServerLifecycleBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface ServerLifecycleSelection { readonly eventId: string; readonly sampleTargetRevision: number; readonly afterRunId: string; readonly detectedAtUtc: string }
export interface ServerLifecycleEvent extends ServerLifecycleSelection {
  readonly targetId: string; readonly beforeRunId: string; readonly kind: "restart_detected" | "responding_node_changed";
  readonly affectedIdentityFingerprint: string; readonly beforeObservedAtUtc: string; readonly afterObservedAtUtc: string;
  readonly occurredAtUtc: null; readonly sourceSchemaVersion: 1;
}
export interface ServerLifecycleEngineProof { readonly physicalNodeFingerprint: string; readonly logicalInstanceFingerprint: string;
  readonly startupIncarnationFingerprint: string; readonly processId: number; readonly sourceStartLocal: string;
  readonly startMsTicks: string; readonly currentMsTicks: string; readonly coverage: "complete" }
export interface ServerLifecycleCoverage { readonly runCount: number; readonly retainedSnapshotCount: number; readonly completeRunCount: number;
  readonly gapRunCount: number; readonly failedRunCount: number; readonly lostRunCount: number; readonly retiredRunCount: number;
  readonly comparisonBoundaryCount: number; readonly eventCount: number; readonly captureBoundaryUtc: string | null;
  readonly hasVisibilityGap: boolean; readonly availabilityQualification: "unqualified"; readonly sourceConsistencyVerified: false }
export interface ServerLifecycleHeader extends ServerLifecycleBinding { readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string;
  readonly state: "available" | "partial" | "unavailable"; readonly reason: string }
export interface ServerLifecyclePage extends ServerLifecycleHeader { readonly coverage: ServerLifecycleCoverage; readonly items: readonly ServerLifecycleEvent[]; readonly nextCursor: string | null }
export interface ServerLifecycleDetail extends ServerLifecycleHeader { readonly event: ServerLifecycleEvent | null;
  readonly beforeEngine: ServerLifecycleEngineProof | null; readonly afterEngine: ServerLifecycleEngineProof | null }
export const invalidServerLifecycle = "Server lifecycle evidence returned an invalid or mismatched receipt. Refresh this exact read to recover.";
const fail = (): never => { throw new Error(invalidServerLifecycle); };
const integer = (v: unknown, min = 1, max = Number.MAX_SAFE_INTEGER): v is number => typeof v === "number" && Number.isSafeInteger(v) && v >= min && v <= max;
const digest = (v: unknown): v is string => typeof v === "string" && /^[0-9a-f]{64}$/u.test(v);
const at = utcMicroseconds;
function exact(v: unknown, fields: string): Record<string,unknown> {
  if (!v || typeof v !== "object" || Array.isArray(v) || Object.keys(v).sort().join("|") !== fields.split("|").sort().join("|")) return fail();
  return v as Record<string,unknown>;
}
function id(v: unknown): string { if (!notificationId(v)) return fail(); return v.toLowerCase(); }
export function serverLifecycleUtc(v: unknown): string { if (typeof v !== "string") return fail(); try { return canonicalUtcTimestamp(v.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u,"$1")); } catch { return fail(); } }
export function serverLifecycleWindow(fromUtc: string, toUtc: string) {
  const from = serverLifecycleUtc(fromUtc), to = serverLifecycleUtc(toUtc);
  if (at(from) >= at(to) || at(to)-at(from) > 31n*86400n*1000000n) return fail(); return {fromUtc:from,toUtc:to};
}
export function serverLifecycleCursor(v: unknown): v is string {
  if (typeof v !== "string" || !v.length || v.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(v)) return false;
  try { return btoa(atob(v)) === v; } catch { return false; }
}
export function validServerLifecycleSelection(v: unknown): v is ServerLifecycleSelection {
  if (!v || typeof v !== "object") return false; const s = v as ServerLifecycleSelection;
  try { return notificationId(s.eventId) && integer(s.sampleTargetRevision) && notificationId(s.afterRunId) && Boolean(serverLifecycleUtc(s.detectedAtUtc)); } catch { return false; }
}
export function serverLifecycleBindingIdentity(binding: ServerLifecycleBinding | undefined): string {
  if (!binding) return "absent"; try { return JSON.stringify([binding.targetId.toLowerCase(),binding.targetRevision,at(serverLifecycleUtc(binding.fromUtc)).toString(),at(serverLifecycleUtc(binding.toUtc)).toString()]); } catch { return JSON.stringify(binding); }
}
export function serverLifecycleSelectionIdentity(selection: ServerLifecycleSelection | null | undefined): string {
  if (!selection) return JSON.stringify(selection)??"absent"; try { return JSON.stringify([selection.eventId.toLowerCase(),selection.sampleTargetRevision,selection.afterRunId.toLowerCase(),at(serverLifecycleUtc(selection.detectedAtUtc)).toString()]); } catch { return JSON.stringify(selection); }
}
export function serverLifecycleEventIdentity(event: Pick<ServerLifecycleSelection,"eventId"|"sampleTargetRevision"|"afterRunId">): string {
  return `server-lifecycle:${event.eventId.toLowerCase()}:${event.sampleTargetRevision}:${event.afterRunId.toLowerCase()}`;
}
export function matchesServerLifecycleSelection(event: ServerLifecycleSelection, selection: ServerLifecycleSelection): boolean {
  return event.eventId.toLowerCase() === selection.eventId.toLowerCase() && event.sampleTargetRevision === selection.sampleTargetRevision &&
    event.afterRunId.toLowerCase() === selection.afterRunId.toLowerCase() && at(event.detectedAtUtc) === at(serverLifecycleUtc(selection.detectedAtUtc));
}
const common = "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason";
function header(r: Record<string,unknown>, binding: ServerLifecycleBinding): ServerLifecycleHeader {
  const window = serverLifecycleWindow(binding.fromUtc,binding.toUtc), fromUtc=serverLifecycleUtc(r.fromUtc),toUtc=serverLifecycleUtc(r.toUtc),cutoffUtc=serverLifecycleUtc(r.cutoffUtc),repositoryTimeUtc=serverLifecycleUtc(r.repositoryTimeUtc);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision) || id(r.targetId) !== binding.targetId.toLowerCase() || r.targetRevision !== binding.targetRevision ||
    at(fromUtc)!==at(window.fromUtc) || at(toUtc)!==at(window.toUtc) || at(cutoffUtc)>at(repositoryTimeUtc) || !digest(r.readEpoch) || !["available","partial","unavailable"].includes(String(r.state)) || typeof r.reason!=="string") return fail();
  return {targetId:id(r.targetId),targetRevision:binding.targetRevision,fromUtc,toUtc,cutoffUtc,readEpoch:r.readEpoch,repositoryTimeUtc,state:r.state as ServerLifecycleHeader["state"],reason:r.reason};
}
function event(v: unknown,h: ServerLifecycleHeader): ServerLifecycleEvent {
  const r=exact(v,"eventId|targetId|sampleTargetRevision|beforeRunId|afterRunId|kind|affectedIdentityFingerprint|beforeObservedAtUtc|afterObservedAtUtc|detectedAtUtc|occurredAtUtc|sourceSchemaVersion");
  const eventId=id(r.eventId),targetId=id(r.targetId),beforeRunId=id(r.beforeRunId),afterRunId=id(r.afterRunId),beforeObservedAtUtc=serverLifecycleUtc(r.beforeObservedAtUtc),afterObservedAtUtc=serverLifecycleUtc(r.afterObservedAtUtc),detectedAtUtc=serverLifecycleUtc(r.detectedAtUtc);
  if (!/^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(eventId) || targetId!==h.targetId || beforeRunId===afterRunId ||
    !integer(r.sampleTargetRevision) || r.sampleTargetRevision>h.targetRevision || !["restart_detected","responding_node_changed"].includes(String(r.kind)) || !digest(r.affectedIdentityFingerprint) ||
    r.occurredAtUtc!==null || r.sourceSchemaVersion!==1 || at(beforeObservedAtUtc)>=at(afterObservedAtUtc) || at(afterObservedAtUtc)>at(detectedAtUtc) ||
    at(detectedAtUtc)<at(h.fromUtc) || at(detectedAtUtc)>=at(h.toUtc) || at(detectedAtUtc)>at(h.cutoffUtc)) return fail();
  return {...r as unknown as ServerLifecycleEvent,eventId,targetId,beforeRunId,afterRunId,beforeObservedAtUtc,afterObservedAtUtc,detectedAtUtc};
}
function coverage(v: unknown,h: ServerLifecycleHeader): ServerLifecycleCoverage {
  const r=exact(v,"runCount|retainedSnapshotCount|completeRunCount|gapRunCount|failedRunCount|lostRunCount|retiredRunCount|comparisonBoundaryCount|eventCount|captureBoundaryUtc|hasVisibilityGap|availabilityQualification|sourceConsistencyVerified");
  const names=["runCount","retainedSnapshotCount","completeRunCount","gapRunCount","failedRunCount","lostRunCount","retiredRunCount","comparisonBoundaryCount","eventCount"];
  if (names.some(name=>!integer(r[name],0,10000)) || typeof r.hasVisibilityGap!=="boolean" || r.availabilityQualification!=="unqualified" || r.sourceConsistencyVerified!==false) return fail();
  const c={...r,captureBoundaryUtc:r.captureBoundaryUtc===null?null:serverLifecycleUtc(r.captureBoundaryUtc)} as unknown as ServerLifecycleCoverage;
  if (c.retainedSnapshotCount>c.runCount || c.completeRunCount>c.retainedSnapshotCount || c.completeRunCount+c.gapRunCount!==c.runCount || c.failedRunCount>c.gapRunCount ||
    c.lostRunCount>c.runCount || c.retiredRunCount>c.gapRunCount || c.comparisonBoundaryCount>c.runCount || c.eventCount>c.runCount ||
    c.gapRunCount>0&&!c.hasVisibilityGap || c.captureBoundaryUtc!==null&&at(c.captureBoundaryUtc)>at(h.cutoffUtc) ||
    !c.hasVisibilityGap&&c.runCount>0&&(c.captureBoundaryUtc===null||at(c.captureBoundaryUtc)>at(h.fromUtc))) return fail();
  if (h.state!==(c.retainedSnapshotCount===0?"unavailable":c.hasVisibilityGap?"partial":"available") || h.reason!==(c.runCount===0?"no_source_receipts":c.hasVisibilityGap?"source_visibility_gap":"retained_source_coverage")) return fail();
  return c;
}
function coverageIdentity(c: ServerLifecycleCoverage): string {
  return JSON.stringify([c.runCount,c.retainedSnapshotCount,c.completeRunCount,c.gapRunCount,c.failedRunCount,c.lostRunCount,c.retiredRunCount,
    c.comparisonBoundaryCount,c.eventCount,c.captureBoundaryUtc===null?null:at(c.captureBoundaryUtc).toString(),c.hasVisibilityGap,c.availabilityQualification,c.sourceConsistencyVerified]);
}
export function parseServerLifecyclePage(v: unknown,binding: ServerLifecycleBinding,prior: readonly ServerLifecyclePage[]=[],cursor?: string): ServerLifecyclePage {
  const r=exact(v,common+"|coverage|items|nextCursor"),h=header(r,binding),c=coverage(r.coverage,h);
  if (!Array.isArray(r.items) || r.items.length>100 || r.nextCursor!==null&&(!serverLifecycleCursor(r.nextCursor)||r.items.length!==100||prior.some(p=>p.nextCursor===r.nextCursor)) ||
    Boolean(prior.length)!==(cursor!==undefined) || cursor!==undefined&&(!serverLifecycleCursor(cursor)||prior.at(-1)?.nextCursor!==cursor||cursor===r.nextCursor)) return fail();
  if (prior.length&&(prior[0]!.readEpoch!==h.readEpoch||at(prior[0]!.cutoffUtc)!==at(h.cutoffUtc)||coverageIdentity(prior[0]!.coverage)!==coverageIdentity(c))) return fail();
  const received=prior.flatMap(p=>p.items),seen=new Set(received.map(e=>e.eventId));let tail=received.at(-1);
  const items=r.items.map(raw=>{const item=event(raw,h);if(seen.has(item.eventId)||tail&&(at(item.detectedAtUtc)<at(tail.detectedAtUtc)||at(item.detectedAtUtc)===at(tail.detectedAtUtc)&&item.eventId<=tail.eventId))return fail();seen.add(item.eventId);tail=item;return item;});
  const count=received.length+items.length;
  if(count>c.eventCount||r.nextCursor!==null&&count>=c.eventCount||r.nextCursor===null&&count!==c.eventCount)return fail();
  return {...h,coverage:c,items,nextCursor:r.nextCursor as string|null};
}
function counter(v: unknown): string { if(typeof v!=="string"||!/^(0|[1-9]\d{0,18})$/u.test(v)||BigInt(v)>9223372036854775807n)return fail();return v; }
function localStart(v: unknown): string {
  if(typeof v!=="string"||!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?$/u.test(v))return fail();
  const base=v.slice(0,19),value=base+"."+(v.slice(20)||"").padEnd(7,"0"),ms=Date.parse(base+"Z");
  if(value<"1753-01-01T00:00:00.0000000"||!Number.isFinite(ms)||new Date(ms).toISOString().slice(0,19)!==base)return fail();return value;
}
function localTicks(v: unknown): bigint {
  const value=localStart(v);return BigInt(Date.parse(value.slice(0,19)+"Z"))*10000n+BigInt(value.slice(20))+621355968000000000n;
}
function proof(v: unknown): ServerLifecycleEngineProof {
  const r=exact(v,"physicalNodeFingerprint|logicalInstanceFingerprint|startupIncarnationFingerprint|processId|sourceStartLocal|startMsTicks|currentMsTicks|coverage");
  if(!digest(r.physicalNodeFingerprint)||!digest(r.logicalInstanceFingerprint)||!digest(r.startupIncarnationFingerprint)||!integer(r.processId,1,2147483647)||r.coverage!=="complete")return fail();
  const sourceStartLocal=localStart(r.sourceStartLocal),startMsTicks=counter(r.startMsTicks),currentMsTicks=counter(r.currentMsTicks);if(BigInt(startMsTicks)>BigInt(currentMsTicks))return fail();
  return {...r as unknown as ServerLifecycleEngineProof,sourceStartLocal,startMsTicks,currentMsTicks};
}
export function parseServerLifecycleDetail(v: unknown,binding: ServerLifecycleBinding,selection: ServerLifecycleSelection): ServerLifecycleDetail {
  if(!validServerLifecycleSelection(selection)||selection.sampleTargetRevision>binding.targetRevision)return fail();
  const r=exact(v,common+"|event|beforeEngine|afterEngine"),h=header(r,binding),item=r.event===null?null:event(r.event,h),selectedAt=at(serverLifecycleUtc(selection.detectedAtUtc));
  if(selectedAt<at(h.fromUtc)||selectedAt>=at(h.toUtc)||h.state!==(item===null?"unavailable":"available")||h.reason!==(item===null?"event_not_retained":"retained_lifecycle_event"))return fail();
  if(item===null){if(r.beforeEngine!==null||r.afterEngine!==null)return fail();return {...h,event:null,beforeEngine:null,afterEngine:null};}
  if(!matchesServerLifecycleSelection(item,selection))return fail();const beforeEngine=proof(r.beforeEngine),afterEngine=proof(r.afterEngine);
  if(beforeEngine.logicalInstanceFingerprint!==afterEngine.logicalInstanceFingerprint)return fail();
  const matches=item.kind==="responding_node_changed"?beforeEngine.physicalNodeFingerprint!==afterEngine.physicalNodeFingerprint&&item.affectedIdentityFingerprint===afterEngine.logicalInstanceFingerprint
    :beforeEngine.physicalNodeFingerprint===afterEngine.physicalNodeFingerprint&&beforeEngine.startupIncarnationFingerprint!==afterEngine.startupIncarnationFingerprint&&
     (beforeEngine.processId!==afterEngine.processId||beforeEngine.startMsTicks!==afterEngine.startMsTicks)&&beforeEngine.sourceStartLocal!==afterEngine.sourceStartLocal&&item.affectedIdentityFingerprint===afterEngine.physicalNodeFingerprint;
  if(!matches)return fail();return {...h,event:item,beforeEngine,afterEngine};
}
async function hash(input: string): Promise<Uint8Array> { return new Uint8Array(await crypto.subtle.digest("SHA-256",new TextEncoder().encode(input))); }
const hex=(bytes: Uint8Array)=>Array.from(bytes,b=>b.toString(16).padStart(2,"0")).join("");
export async function verifyServerLifecycleIdentity(item: ServerLifecycleEvent): Promise<void> {
  const bytes=await hash(["sqlobserver-lifecycle-event-v1",item.targetId,item.sampleTargetRevision,1,item.kind==="restart_detected"?1:2,item.beforeRunId,item.afterRunId,item.affectedIdentityFingerprint].join("|"));
  bytes[6]=(bytes[6]!&15)|128;bytes[8]=(bytes[8]!&63)|128;const raw=hex(bytes.slice(0,16)),uuid=`${raw.slice(0,8)}-${raw.slice(8,12)}-${raw.slice(12,16)}-${raw.slice(16,20)}-${raw.slice(20)}`;
  if(uuid!==item.eventId)return fail();
}
export async function verifyServerLifecycleProof(value: ServerLifecycleEngineProof): Promise<void> {
  const expected=hex(await hash(`sqlobserver-lifecycle-startup-v1\0${value.physicalNodeFingerprint}|${value.logicalInstanceFingerprint}|${value.processId}|${localTicks(value.sourceStartLocal)}|${value.startMsTicks}`));
  if(expected!==value.startupIncarnationFingerprint)return fail();
}
export function serverLifecycleTimelineEvents(events: readonly ServerLifecycleEvent[],scope: OverviewScope): readonly TimelineEvent[] {
  if(events.some(item=>item.targetId.toLowerCase()!==scope.target?.toLowerCase()))return fail();
  return events.map(item=>({id:serverLifecycleEventIdentity(item),kind:"server_lifecycle" as const,timeUtc:item.detectedAtUtc,timeBasis:"detected" as const,
    title:item.kind==="restart_detected"?"SQL responding instance restart detected":"SQL responding node changed",
    detail:`Detected at monitoring completion ${item.detectedAtUtc}, between source observations ${item.beforeObservedAtUtc} and ${item.afterObservedAtUtc}. SQL-reported physical/logical identities are not hardware UUIDs. Exact occurrence time, cause and intervening transitions are unknown. This source does not prove physical failover.`,
    href:dashboardTimelineSelectionHref(scope,{eventId:serverLifecycleEventIdentity(item),timeUtc:item.detectedAtUtc})}));
}
