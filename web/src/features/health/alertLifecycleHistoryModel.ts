import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { notificationId } from "../alerts/alertSnoozeModel.ts";
import type { TimelineEvent } from "../../components/dashboardTimelineModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";
import { dashboardTimelineSelectionHref } from "../../components/dashboardTimelineSelectionModel.ts";

export const lifecycleKinds = ["fired","acknowledged","resolved","snoozed","unsnoozed","assigned","reassigned","unassigned"] as const;
export type AlertLifecycleKind = typeof lifecycleKinds[number];
export interface AlertLifecycleSelection { readonly eventId: string; readonly alertId: string; readonly episodeId: string | null; readonly eventAtUtc: string }
export interface AlertLifecycleEvent extends AlertLifecycleSelection {
  readonly ruleId: string | null; readonly kind: AlertLifecycleKind; readonly timeBasis: "observed" | "applied"; readonly recordedAtUtc: string | null;
  readonly source: "evaluation" | "administrative" | "snooze" | "assignment"; readonly provenance: "complete" | "legacy_partial";
  readonly sourceTargetRevision: number | null; readonly stateRevision: number | null; readonly ruleName: string | null;
  readonly fromState: string | null; readonly toState: string | null; readonly value: number | null; readonly reason: string | null;
  readonly actorSid: string | null; readonly deliverySuppressed: boolean | null; readonly snoozedUntilUtc: string | null;
  readonly assigneeSid: string | null; readonly assigneeName: string | null; readonly previousAssigneeSid: string | null; readonly previousAssigneeName: string | null;
}
export interface AlertLifecycleCoverage { readonly eventCount: number; readonly completeEventCount: number; readonly legacyPartialEventCount: number;
  readonly cycleCount: number; readonly provenanceBoundaryUtc: string | null; readonly retentionDays: number | null }
export interface AlertLifecycleBinding { readonly targetId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string }
export interface AlertLifecycleHeader extends AlertLifecycleBinding { readonly cutoffUtc: string; readonly readEpoch: string; readonly repositoryTimeUtc: string; readonly state: string; readonly reason: string }
export interface AlertLifecyclePage extends AlertLifecycleHeader { readonly coverage: AlertLifecycleCoverage; readonly items: readonly AlertLifecycleEvent[]; readonly nextCursor: string | null }
export interface AlertLifecycleDetail extends AlertLifecycleHeader { readonly event: AlertLifecycleEvent | null }
export const invalidAlertLifecycle = "Alert lifecycle evidence returned an invalid or mismatched receipt. Refresh this exact read to recover.";
const fail = (): never => { throw new Error(invalidAlertLifecycle); };
const integer = (v: unknown, min = 1, max = Number.MAX_SAFE_INTEGER): v is number => typeof v === "number" && Number.isSafeInteger(v) && v >= min && v <= max;
const states = ["normal","pending","firing","acknowledged","resolved"];
const at = utcMicroseconds;
export function lifecycleUtc(v: unknown): string { if (typeof v !== "string") return fail(); try { return canonicalUtcTimestamp(v.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u,"$1")); } catch { return fail(); } }
export function lifecycleWindow(fromUtc: string, toUtc: string) { const from = lifecycleUtc(fromUtc), to = lifecycleUtc(toUtc); if (at(from) >= at(to) || at(to)-at(from) > 31n*86400n*1000000n) return fail(); return {fromUtc:from,toUtc:to}; }
export function lifecycleCursor(v: unknown): v is string { if (typeof v !== "string" || !v.length || v.length > 4096 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(v)) return false; try { return btoa(atob(v)) === v; } catch { return false; } }
function exact(v: unknown, fields: string): Record<string,unknown> { if (!v || typeof v !== "object" || Array.isArray(v) || Object.keys(v).sort().join("|") !== fields.split("|").sort().join("|")) return fail(); return v as Record<string,unknown>; }
function id(v: unknown): string | null { if (v === null) return null; if (!notificationId(v)) return fail(); return v.toLowerCase(); }
function text(v: unknown, max: number): string | null { if (v === null) return null; if (typeof v !== "string" || !v.trim() || new TextEncoder().encode(v).length > max || /\p{Cc}/u.test(v) || new TextDecoder().decode(new TextEncoder().encode(v)) !== v) return fail(); return v; }
function sid(v: unknown): string | null { if (v === null) return null; if (typeof v !== "string" || v.length > 184 || !/^S-1-[0-9]+(?:-[0-9]+)+$/u.test(v)) return fail(); return v; }
function time(v: unknown): string | null { return v === null ? null : lifecycleUtc(v); }
export function validLifecycleSelection(v: unknown): v is AlertLifecycleSelection { if (!v || typeof v !== "object") return false; const s = v as AlertLifecycleSelection; try { return notificationId(s.eventId) && notificationId(s.alertId) && (s.episodeId === null || notificationId(s.episodeId)) && Boolean(lifecycleUtc(s.eventAtUtc)); } catch { return false; } }
export function lifecycleBindingIdentity(binding: AlertLifecycleBinding | undefined): string { if (!binding) return "absent"; try { return JSON.stringify([binding.targetId.toLowerCase(),binding.targetRevision,at(lifecycleUtc(binding.fromUtc)).toString(),at(lifecycleUtc(binding.toUtc)).toString()]); } catch { return JSON.stringify(binding); } }
export function lifecycleSelectionIdentity(selection: AlertLifecycleSelection | null | undefined): string { if (!selection) return JSON.stringify(selection)??"absent"; try { return JSON.stringify([selection.eventId.toLowerCase(),selection.alertId.toLowerCase(),selection.episodeId?.toLowerCase()??null,at(lifecycleUtc(selection.eventAtUtc)).toString()]); } catch { return JSON.stringify(selection); } }
export function lifecycleEventIdentity(event: Pick<AlertLifecycleSelection,"eventId"|"alertId"|"episodeId">): string { return `alert-history:${event.eventId.toLowerCase()}:${event.alertId.toLowerCase()}:${event.episodeId?.toLowerCase() ?? "legacy"}`; }
export function matchesLifecycleSelection(event: AlertLifecycleSelection, selection: AlertLifecycleSelection) { return event.eventId.toLowerCase() === selection.eventId.toLowerCase() && event.alertId.toLowerCase() === selection.alertId.toLowerCase() && event.episodeId?.toLowerCase() === selection.episodeId?.toLowerCase() && at(event.eventAtUtc) === at(lifecycleUtc(selection.eventAtUtc)); }
const commonFields = "targetId|targetRevision|fromUtc|toUtc|cutoffUtc|readEpoch|repositoryTimeUtc|state|reason";
function header(row: Record<string,unknown>, binding: AlertLifecycleBinding): AlertLifecycleHeader {
  const window = lifecycleWindow(binding.fromUtc,binding.toUtc), fromUtc = lifecycleUtc(row.fromUtc), toUtc = lifecycleUtc(row.toUtc), cutoffUtc = lifecycleUtc(row.cutoffUtc), repositoryTimeUtc = lifecycleUtc(row.repositoryTimeUtc);
  if (!notificationId(binding.targetId) || !integer(binding.targetRevision) || !notificationId(row.targetId) || row.targetId.toLowerCase() !== binding.targetId.toLowerCase() || row.targetRevision !== binding.targetRevision
    || at(fromUtc) !== at(window.fromUtc) || at(toUtc) !== at(window.toUtc) || at(cutoffUtc) > at(repositoryTimeUtc) || typeof row.readEpoch !== "string" || !/^[a-f0-9]{64}$/u.test(row.readEpoch)) return fail();
  return { targetId:row.targetId.toLowerCase(),targetRevision:binding.targetRevision,fromUtc,toUtc,cutoffUtc,repositoryTimeUtc,readEpoch:row.readEpoch,state:String(row.state),reason:String(row.reason) };
}
function event(v: unknown, h: AlertLifecycleHeader): AlertLifecycleEvent {
  const r = exact(v,"eventId|alertId|episodeId|ruleId|kind|timeBasis|eventAtUtc|recordedAtUtc|source|provenance|sourceTargetRevision|stateRevision|ruleName|fromState|toState|value|reason|actorSid|deliverySuppressed|snoozedUntilUtc|assigneeSid|assigneeName|previousAssigneeSid|previousAssigneeName");
  const eventId = id(r.eventId), alertId = id(r.alertId), episodeId = id(r.episodeId), ruleId = id(r.ruleId), eventAtUtc = lifecycleUtc(r.eventAtUtc), recordedAtUtc = time(r.recordedAtUtc);
  if (eventId === null || alertId === null || !lifecycleKinds.includes(r.kind as AlertLifecycleKind) || !["observed","applied"].includes(String(r.timeBasis))
    || !["evaluation","administrative","snooze","assignment"].includes(String(r.source)) || !["complete","legacy_partial"].includes(String(r.provenance))
    || at(eventAtUtc) < at(h.fromUtc) || at(eventAtUtc) >= at(h.toUtc) || recordedAtUtc !== null && at(recordedAtUtc) > at(h.cutoffUtc)
    || r.sourceTargetRevision !== null && (!integer(r.sourceTargetRevision) || r.sourceTargetRevision > h.targetRevision) || r.stateRevision !== null && !integer(r.stateRevision)
    || r.fromState !== null && !states.includes(String(r.fromState)) || r.toState !== null && !states.includes(String(r.toState))
    || r.value !== null && (typeof r.value !== "number" || !Number.isFinite(r.value)) || r.deliverySuppressed !== null && typeof r.deliverySuppressed !== "boolean") return fail();
  const item: AlertLifecycleEvent = {...r as unknown as AlertLifecycleEvent,eventId,alertId,episodeId,ruleId,eventAtUtc,recordedAtUtc,ruleName:text(r.ruleName,128),reason:text(r.reason,8192),actorSid:sid(r.actorSid),snoozedUntilUtc:time(r.snoozedUntilUtc),assigneeSid:sid(r.assigneeSid),assigneeName:text(r.assigneeName,128),previousAssigneeSid:sid(r.previousAssigneeSid),previousAssigneeName:text(r.previousAssigneeName,128)};
  const transition = ["fired","acknowledged","resolved"].includes(item.kind);
  if (item.timeBasis !== (item.source === "evaluation" ? "observed" : "applied") || transition !== ["evaluation","administrative"].includes(item.source)
    || item.source === "snooze" && !["snoozed","unsnoozed"].includes(item.kind) || item.source === "assignment" && !["assigned","reassigned","unassigned"].includes(item.kind)
    || transition && (item.toState !== (item.kind === "fired" ? "firing" : item.kind === "acknowledged" ? "acknowledged" : "resolved")
      || item.kind === "fired" && ["firing","acknowledged"].includes(item.fromState??"") || item.kind === "acknowledged" && ![null,"firing"].includes(item.fromState) || item.kind === "resolved" && ![null,"firing","acknowledged"].includes(item.fromState))
    || (item.assigneeSid === null) !== (item.assigneeName === null) || (item.previousAssigneeSid === null) !== (item.previousAssigneeName === null)
    || (item.kind === "snoozed") !== (item.snoozedUntilUtc !== null) || ["assigned","reassigned"].includes(item.kind) && item.assigneeSid === null || item.kind === "unassigned" && item.assigneeSid !== null || item.kind === "reassigned" && item.previousAssigneeSid === null
    || item.provenance === "complete" && (item.recordedAtUtc === null || item.episodeId === null || item.ruleId === null || item.ruleName === null || item.stateRevision === null)) return fail();
  return item;
}
export function parseAlertLifecyclePage(v: unknown, binding: AlertLifecycleBinding, prior: readonly AlertLifecyclePage[] = [], cursor?: string): AlertLifecyclePage {
  const r = exact(v,commonFields+"|coverage|items|nextCursor"), h = header(r,binding), c = exact(r.coverage,"eventCount|completeEventCount|legacyPartialEventCount|cycleCount|provenanceBoundaryUtc|retentionDays");
  if (!integer(c.eventCount,0,10000) || !integer(c.completeEventCount,0,10000) || !integer(c.legacyPartialEventCount,0,10000) || c.completeEventCount+c.legacyPartialEventCount !== c.eventCount || !integer(c.cycleCount,0,Math.min(1000,c.eventCount))
    || c.eventCount > 0 && c.cycleCount === 0 || c.retentionDays !== null && !integer(c.retentionDays) || !Array.isArray(r.items) || r.items.length > 100
    || r.nextCursor !== null && (!lifecycleCursor(r.nextCursor) || r.items.length !== 100 || prior.some(p=>p.nextCursor===r.nextCursor))
    || Boolean(prior.length) !== (cursor !== undefined) || cursor !== undefined && (!lifecycleCursor(cursor) || prior.at(-1)?.nextCursor !== cursor || cursor === r.nextCursor)) return fail();
  const coverage: AlertLifecycleCoverage = {eventCount:c.eventCount,completeEventCount:c.completeEventCount,legacyPartialEventCount:c.legacyPartialEventCount,cycleCount:c.cycleCount,provenanceBoundaryUtc:time(c.provenanceBoundaryUtc),retentionDays:c.retentionDays as number|null};
  const expectedState = c.eventCount === 0 ? "unavailable" : c.legacyPartialEventCount > 0 ? "partial" : "available", expectedReason = c.eventCount === 0 ? "no_lifecycle_events" : c.legacyPartialEventCount > 0 ? "legacy_provenance" : "retained_lifecycle";
  if (h.state !== expectedState || h.reason !== expectedReason || prior.length && (prior[0]!.readEpoch !== h.readEpoch || at(prior[0]!.cutoffUtc) !== at(h.cutoffUtc) || JSON.stringify(prior[0]!.coverage) !== JSON.stringify(coverage))) return fail();
  const received = prior.flatMap(p=>p.items), seen = new Set(received.map(item=>item.eventId)); let tail = received.at(-1);
  const items = r.items.map(raw=>{const item=event(raw,h);if (seen.has(item.eventId) || tail && (at(item.eventAtUtc)<at(tail.eventAtUtc) || at(item.eventAtUtc)===at(tail.eventAtUtc) && item.eventId<=tail.eventId)) return fail();seen.add(item.eventId);tail=item;return item;});
  const all = [...received,...items], complete = all.filter(e=>e.provenance==="complete").length, cycles = new Set(all.map(e=>e.alertId+":"+(e.episodeId??"legacy"))).size;
  if (all.length > c.eventCount || complete > c.completeEventCount || all.length-complete > c.legacyPartialEventCount || cycles > c.cycleCount
    || r.nextCursor !== null && all.length >= c.eventCount || r.nextCursor === null && (all.length !== c.eventCount || complete !== c.completeEventCount || cycles !== c.cycleCount)) return fail();
  return {...h,coverage,items,nextCursor:r.nextCursor as string|null};
}
export function parseAlertLifecycleDetail(v: unknown, binding: AlertLifecycleBinding, selected: AlertLifecycleSelection): AlertLifecycleDetail {
  if (!validLifecycleSelection(selected)) return fail(); const r = exact(v,commonFields+"|event"), h = header(r,binding), item = r.event === null ? null : event(r.event,h);
  if (at(lifecycleUtc(selected.eventAtUtc)) < at(h.fromUtc) || at(lifecycleUtc(selected.eventAtUtc)) >= at(h.toUtc)) return fail();
  if (item !== null && !matchesLifecycleSelection(item,selected) || h.state !== (item === null ? "unavailable" : item.provenance === "complete" ? "available" : "partial")
    || h.reason !== (item === null ? "event_not_retained" : item.provenance === "complete" ? "retained_lifecycle" : "legacy_provenance")) return fail();
  return {...h,event:item};
}
export function alertLifecycleTimelineEvents(events: readonly AlertLifecycleEvent[], scope: OverviewScope): readonly TimelineEvent[] {
  return events.map(item=>({id:lifecycleEventIdentity(item),kind:"alert" as const,timeUtc:item.eventAtUtc,timeBasis:item.timeBasis,title:`Alert ${item.kind} · ${item.ruleName ?? "historical rule name unavailable"}`,
    detail:`Native ${item.timeBasis} event time. ${item.provenance === "legacy_partial" ? "Legacy provenance is partial; unavailable metadata is not filled from current state. " : ""}Recorded ${item.recordedAtUtc ?? "time unavailable"}. Server-wide alert evidence; notification suppression does not establish notification delivery.`,
    href:dashboardTimelineSelectionHref(scope,{eventId:lifecycleEventIdentity(item),timeUtc:item.eventAtUtc})}));
}
