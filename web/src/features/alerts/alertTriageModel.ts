import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { ActiveAlert, ActiveAlertPage, FleetAlert, FleetAlertPage } from "./alertTypes.ts";

export const maximumAlertTriagePages = 20;
export const maximumAlertTriageRows = 2_000;
export type AlertInboxKind = "fleet" | "target";
export type AlertInboxPage = FleetAlertPage | ActiveAlertPage;
export interface AlertInboxReceipt { readonly page: AlertInboxPage; readonly receivedAtUtc: string; readonly cursor?: string; }
const fail = (): never => { throw new Error("Alert continuation could not be verified. Return to latest before continuing."); };
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/iu;
export const alertTriageUtc = (value: unknown): bigint => utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value);
export function alertTriageIdentity(item: ActiveAlert, targetId: string): string {
  return `${("targetId" in item ? String(item.targetId) : targetId).toLowerCase()}:${item.alertId.toLowerCase()}`;
}
function sortTime(item: ActiveAlert): bigint { return alertTriageUtc(item.firedUtc ?? item.firstObservedUtc); }
function rowTarget(item: ActiveAlert, targetId: string): string { return "targetId" in item ? String(item.targetId).toLowerCase() : targetId.toLowerCase(); }
function order(left: ActiveAlert, right: ActiveAlert, kind: AlertInboxKind, targetId: string): number {
  const delta = sortTime(right) - sortTime(left);
  if (delta) return delta < 0n ? -1 : 1;
  return kind === "fleet" ? rowTarget(right, targetId).localeCompare(rowTarget(left, targetId)) || right.alertId.toLowerCase().localeCompare(left.alertId.toLowerCase())
    : left.alertId.toLowerCase().localeCompare(right.alertId.toLowerCase());
}
export function decodeAlertTriageCursor(cursor: string, kind: AlertInboxKind, targetId = "") {
  if (!cursor || cursor.length > 1024 || cursor.length % 4 || !/^[A-Za-z0-9+/]+={0,2}$/u.test(cursor)) return fail();
  let parts: string[];
  try { const binary = atob(cursor); if (btoa(binary) !== cursor) return fail(); parts = new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(binary, character => character.charCodeAt(0))).split("|"); }
  catch { return fail(); }
  if (parts.length !== 4) return fail();
  const [id, snapshot, sort, alert] = kind === "target" ? parts : [parts[2], parts[0], parts[1], parts[3]];
  if (!id || !alert || !guid.test(id) || !guid.test(alert) || kind === "target" && id.toLowerCase() !== targetId.toLowerCase()) return fail();
  try { if (alertTriageUtc(sort) > alertTriageUtc(snapshot)) return fail(); } catch { return fail(); }
  return { targetId: id.toLowerCase(), snapshotUtc: snapshot!, sortAtUtc: sort!, alertId: alert.toLowerCase() };
}
/** Native snapshot is a row-time cutoff, not a historical alert-state snapshot. */
export function validateAlertTriagePage(page: AlertInboxPage, kind: AlertInboxKind, targetId = "", prior: readonly AlertInboxReceipt[] = [], cursor?: string): void {
  try {
    if (!page || !Array.isArray(page.items) || page.items.length > 100 || !page.snapshotUtc || kind === "target" &&
      (!("targetId" in page) || page.targetId.toLowerCase() !== targetId.toLowerCase())) return fail();
    const snapshot = alertTriageUtc(page.snapshotUtc);
    const anchor = prior[0]?.page;
    if (anchor && alertTriageUtc(anchor.snapshotUtc) !== snapshot || Boolean(prior.length) !== Boolean(cursor)) return fail();
    const after = cursor ? decodeAlertTriageCursor(cursor, kind, targetId) : undefined;
    if (after && alertTriageUtc(after.snapshotUtc) !== snapshot) return fail();
    const seen = new Set(prior.flatMap(receipt => receipt.page.items.map(item => alertTriageIdentity(item, targetId))));
    let previous = prior.at(-1)?.page.items.at(-1);
    for (const item of page.items) {
      if (!guid.test(item.alertId) || kind === "fleet" && (!guid.test(rowTarget(item, targetId)) || !("targetName" in item)) || sortTime(item) > snapshot) return fail();
      const identity = alertTriageIdentity(item, targetId);
      if (seen.has(identity) || previous && order(previous, item, kind, targetId) >= 0) return fail();
      seen.add(identity); previous = item;
    }
    if (after && previous && prior.at(-1)?.page.nextCursor !== cursor) return fail();
    if (page.nextCursor !== undefined) {
      if (page.items.length !== 100 || prior.some(receipt => receipt.cursor === page.nextCursor || receipt.page.nextCursor === page.nextCursor)) return fail();
      const next = decodeAlertTriageCursor(page.nextCursor, kind, targetId), tail = page.items.at(-1)!;
      if (alertTriageUtc(next.snapshotUtc) !== snapshot || alertTriageUtc(next.sortAtUtc) !== sortTime(tail) ||
        next.targetId !== rowTarget(tail, targetId) || next.alertId !== tail.alertId.toLowerCase()) return fail();
    }
  } catch { return fail(); }
}
export function alertTriageRows(receipts: readonly AlertInboxReceipt[], targetId = ""): readonly FleetAlert[] {
  return receipts.flatMap(receipt => receipt.page.items.map(item => ({ ...item,
    targetId: rowTarget(item, targetId), targetName: "targetName" in item ? String(item.targetName) : "" })));
}
