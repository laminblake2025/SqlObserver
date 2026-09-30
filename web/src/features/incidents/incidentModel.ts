import { overviewHref } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";
import { utcMicroseconds as incidentUtc, canonicalUtcTimestamp } from "../../utcTimestamp.ts";
export { utcMicroseconds as incidentUtc } from "../../utcTimestamp.ts";

export interface IncidentWindow { readonly fromUtc: string; readonly toUtc: string; }
export interface SaveIncidentCommand extends IncidentWindow {
  readonly expectedTargetRevision: number;
  readonly operationId: string;
  readonly title: string;
  readonly notes: string;
}
export interface SavedIncidentEvidence {
  readonly packetId: string; readonly occurredAtUtc: string; readonly evidenceKind: string;
  readonly sourceRunId: string | null; readonly sourceSha256: string; readonly identitySha256: string;
  readonly sourceCutoffSha256: string; readonly sourceCutoffUtc: string | null;
  readonly visibilityState: "complete" | "partial" | "unavailable" | "unsupported";
  readonly confidence: number | null; readonly evidence: Readonly<Record<string, unknown>>;
}
export interface SavedIncident extends IncidentWindow {
  readonly targetId: string; readonly targetRevision: number; readonly threadId: string;
  readonly operationId: string; readonly savedAtUtc: string; readonly title: string; readonly notes: string;
  readonly evidenceTruncated: boolean; readonly evidence: readonly SavedIncidentEvidence[];
}

export const validIncidentId = (value: unknown): value is string => typeof value === "string"
  && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
  && value !== "00000000-0000-0000-0000-000000000000";
const bytes = (value: string) => new TextEncoder().encode(value).length;

function validText(value: unknown, maximum: number, multiline: boolean): value is string {
  return typeof value === "string" && bytes(value) <= maximum
    && ![...value].some(character => { const point = character.codePointAt(0)!; return point >= 0xd800 && point <= 0xdfff; })
    && !(multiline ? /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/ : /[\u0000-\u001f\u007f-\u009f]/).test(value);
}

export function validateIncidentWindow(window: IncidentWindow, nowMilliseconds?: number): void {
  const from = incidentUtc(window.fromUtc), to = incidentUtc(window.toUtc);
  if (to <= from || to-from > 7n*86400n*1000000n) throw new Error("Select a window of at most seven days.");
  if (nowMilliseconds !== undefined && to > BigInt(Math.trunc(nowMilliseconds))*1000n) throw new Error("Select a past window before opening an incident.");
}

export function prepareIncident(window: IncidentWindow, revision: number, title: string, notes: string,
  operationId: string = crypto.randomUUID(), now = Date.now()): SaveIncidentCommand {
  validateIncidentWindow(window,now);
  if (!Number.isSafeInteger(revision) || revision < 1 || !validIncidentId(operationId)) throw new Error("Refresh the server before saving an incident.");
  if (!validText(title,200,false) || title.trim().length === 0) throw new Error("Enter a title of at most 200 UTF-8 bytes without line breaks.");
  if (!validText(notes,8192,true)) throw new Error("Notes must fit within 8192 UTF-8 bytes and contain plain text.");
  const command = Object.freeze({ expectedTargetRevision: revision, operationId, fromUtc: window.fromUtc, toUtc: window.toUtc, title, notes });
  if (bytes(JSON.stringify(command)) > 16384) throw new Error("Shorten the notes so the save request fits within 16 KiB.");
  return command;
}

function object(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)
    || Object.keys(value).sort().join("|") !== [...keys].sort().join("|")) throw new Error("The saved incident could not be validated.");
  return value as Record<string, unknown>;
}

export function parseSavedIncident(value: unknown, targetId: string, threadId?: string): SavedIncident {
  const row = object(value,["targetId","targetRevision","threadId","operationId","fromUtc","toUtc","savedAtUtc","title","notes","evidenceTruncated","evidence"]);
  if (!validIncidentId(row.targetId) || row.targetId.toLowerCase() !== targetId.toLowerCase()
    || !Number.isSafeInteger(row.targetRevision) || (row.targetRevision as number) < 1
    || !validIncidentId(row.threadId) || threadId !== undefined && row.threadId.toLowerCase() !== threadId.toLowerCase()
    || !validIncidentId(row.operationId) || !validText(row.title,200,false) || row.title.trim().length === 0
    || !validText(row.notes,8192,true) || typeof row.evidenceTruncated !== "boolean"
    || !Array.isArray(row.evidence) || row.evidence.length > 200) throw new Error("The saved incident could not be validated.");
  const window = { fromUtc: row.fromUtc as string, toUtc: row.toUtc as string };
  validateIncidentWindow(window);
  const from = incidentUtc(row.fromUtc), to = incidentUtc(row.toUtc), saved = incidentUtc(row.savedAtUtc);
  if (to > saved) throw new Error("The saved incident could not be validated.");
  const identities = new Set<string>(); let total = 0; let prior: bigint | undefined;
  for (const input of row.evidence) {
    const packet = object(input,["packetId","occurredAtUtc","evidenceKind","sourceRunId","sourceSha256","identitySha256","sourceCutoffSha256","sourceCutoffUtc","visibilityState","confidence","evidence"]);
    const at = incidentUtc(packet.occurredAtUtc); const identity = `${at}:${String(packet.packetId).toLowerCase()}`;
    if (!validIncidentId(packet.packetId) || at < from || at >= to || prior !== undefined && at < prior || identities.has(identity)
      || !validText(packet.evidenceKind,64,false) || packet.evidenceKind.length === 0
      || packet.sourceRunId !== null && !validIncidentId(packet.sourceRunId)
      || [packet.sourceSha256,packet.identitySha256,packet.sourceCutoffSha256].some(digest => typeof digest !== "string" || !/^[0-9a-f]{64}$/i.test(digest))
      || packet.sourceCutoffUtc !== null && incidentUtc(packet.sourceCutoffUtc) > saved
      || !["complete","partial","unavailable","unsupported"].includes(packet.visibilityState as string)
      || packet.confidence !== null && (typeof packet.confidence !== "number" || !Number.isFinite(packet.confidence) || packet.confidence < 0 || packet.confidence > 1)
      || packet.evidence === null || typeof packet.evidence !== "object" || Array.isArray(packet.evidence)) throw new Error("The saved incident evidence could not be validated.");
    total += bytes(JSON.stringify(packet.evidence)); identities.add(identity); prior = at;
  }
  if (total > 1048576) throw new Error("The saved incident evidence exceeds its bounds.");
  return row as unknown as SavedIncident;
}

export function verifySavedIncident(saved: SavedIncident, command: SaveIncidentCommand): void {
  if (saved.targetRevision !== command.expectedTargetRevision || saved.operationId.toLowerCase() !== command.operationId.toLowerCase()
    || incidentUtc(saved.fromUtc) !== incidentUtc(command.fromUtc) || incidentUtc(saved.toUtc) !== incidentUtc(command.toUtc)
    || saved.title !== command.title || saved.notes !== command.notes) throw new Error("The save result did not match this investigation.");
}

/** undefined is absent; null is an invalid or ambiguous URL selection. */
export function incidentSelection(hash: string): string | null | undefined {
  const values = new URLSearchParams(hash.split("?")[1]).getAll("incident");
  return values.length === 0 ? undefined : values.length === 1 && validIncidentId(values[0]) ? values[0].toLowerCase() : null;
}

export function savedIncidentHref(saved: Pick<SavedIncident,"targetId"|"threadId"|"fromUtc"|"toUtc">, scope: OverviewScope): string {
  return `${overviewHref({ ...scope, range: "custom", from: canonicalUtcTimestamp(saved.fromUtc), to: canonicalUtcTimestamp(saved.toUtc) },"health",saved.targetId)}&incident=${encodeURIComponent(saved.threadId)}`;
}

export interface SavedIncidentSummary extends IncidentWindow {
  readonly targetId: string; readonly targetRevision: number; readonly threadId: string;
  readonly savedAtUtc: string; readonly title: string; readonly evidenceCount: number; readonly evidenceTruncated: boolean;
}
export interface SavedIncidentPage {
  readonly targetId: string; readonly snapshotUtc: string; readonly items: readonly SavedIncidentSummary[];
  readonly hasMore: boolean; readonly nextCursor: string | null;
}

export function parseSavedIncidentPage(value: unknown, targetId: string, limit = 20, prior?: SavedIncidentPage): SavedIncidentPage {
  const page=object(value,["targetId","snapshotUtc","items","hasMore","nextCursor"]);
  if (!validIncidentId(page.targetId) || page.targetId.toLowerCase()!==targetId.toLowerCase()
    || !Number.isInteger(limit) || limit<1 || limit>50 || !Array.isArray(page.items) || page.items.length>limit
    || typeof page.hasMore!=="boolean" || page.hasMore && page.items.length!==limit
    || (page.hasMore ? typeof page.nextCursor!=="string" || !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(page.nextCursor) || page.nextCursor.length>512
      : page.nextCursor!==null)) throw new Error("The saved investigation list could not be validated.");
  const snapshot=incidentUtc(page.snapshotUtc);
  if (prior && (snapshot!==incidentUtc(prior.snapshotUtc) || !prior.hasMore || prior.items.length===0 || page.nextCursor===prior.nextCursor))
    throw new Error("The saved investigation list changed while paging.");
  let previous=prior?.items.at(-1);
  for (const input of page.items) {
    const item=object(input,["targetId","targetRevision","threadId","fromUtc","toUtc","savedAtUtc","title","evidenceCount","evidenceTruncated"]);
    const at=incidentUtc(item.savedAtUtc);
    if (item.targetId!==page.targetId || !validIncidentId(item.threadId) || !Number.isSafeInteger(item.targetRevision) || (item.targetRevision as number)<1
      || !validText(item.title,200,false) || item.title.trim().length===0 || !Number.isInteger(item.evidenceCount)
      || (item.evidenceCount as number)<0 || (item.evidenceCount as number)>200 || typeof item.evidenceTruncated!=="boolean"
      || at>snapshot || incidentUtc(item.toUtc)>at || previous && (at>incidentUtc(previous.savedAtUtc)
        || at===incidentUtc(previous.savedAtUtc) && String(item.threadId).toLowerCase()>=previous.threadId.toLowerCase()))
      throw new Error("The saved investigation summary is invalid or out of order.");
    validateIncidentWindow({fromUtc:item.fromUtc as string,toUtc:item.toUtc as string});
    previous=item as unknown as SavedIncidentSummary;
  }
  return page as unknown as SavedIncidentPage;
}
