import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { DeadlockPage, DeadlockSummary } from "./deadlockTypes";

export interface DeadlockWindow { readonly fromUtc: string; readonly toUtc: string; }
export interface DeadlockReceipt { readonly page: DeadlockPage; readonly window: DeadlockWindow; readonly requestedCursor?: string; }
export interface DeadlockCursor extends DeadlockWindow {
  readonly targetId: string; readonly occurredAtUtc: string; readonly eventId: string; readonly collectedCutoffUtc: string;
}
export const deadlockPageLimit = 25;
export const maximumDeadlockPages = 20;
export const maximumDeadlockRows = maximumDeadlockPages * deadlockPageLimit;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
export function deadlockUtc(value: unknown): bigint {
  return utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value);
}
const sameUtc = (left: string, right: string) => deadlockUtc(left) === deadlockUtc(right);
export function validateDeadlockWindow(window: DeadlockWindow): void {
  const duration = deadlockUtc(window.toUtc) - deadlockUtc(window.fromUtc);
  if (duration <= 0n || duration > 31n * 86_400_000_000n) throw new Error("Invalid deadlock window.");
}
export function decodeDeadlockCursor(value: string): DeadlockCursor {
  if (!value || value.length > 512 || !/^[A-Za-z0-9+/]+={0,2}$/u.test(value)) throw new Error("Invalid deadlock continuation.");
  const bytes = Uint8Array.from(atob(value), character => character.charCodeAt(0));
  if (btoa(String.fromCharCode(...bytes)) !== value) throw new Error("Invalid deadlock continuation.");
  const fields: unknown = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
  if (!Array.isArray(fields) || fields.length !== 6 || !fields.every(item => typeof item === "string"))
    throw new Error("Invalid deadlock continuation.");
  const [targetId, occurredAtUtc, eventId, collectedCutoffUtc, fromUtc, toUtc] = fields as [string, string, string, string, string, string];
  if (!uuid.test(targetId) || !uuid.test(eventId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/u.test(eventId))
    throw new Error("Invalid deadlock continuation.");
  const cursor = { targetId: targetId.toLowerCase(), occurredAtUtc, eventId: eventId.toLowerCase(),
    collectedCutoffUtc, fromUtc, toUtc };
  validateDeadlockWindow(cursor); deadlockUtc(cursor.occurredAtUtc); deadlockUtc(cursor.collectedCutoffUtc);
  return cursor;
}
function compare(left: Pick<DeadlockSummary, "occurredAtUtc" | "eventId">, right: Pick<DeadlockSummary, "occurredAtUtc" | "eventId">): number {
  const time = deadlockUtc(left.occurredAtUtc) - deadlockUtc(right.occurredAtUtc);
  return time === 0n ? left.eventId.toLowerCase().localeCompare(right.eventId.toLowerCase(), "en") : time < 0n ? -1 : 1;
}
function bindCursor(cursor: DeadlockCursor, targetId: string, window: DeadlockWindow): void {
  if (cursor.targetId !== targetId.toLowerCase() || !sameUtc(cursor.fromUtc, window.fromUtc) || !sameUtc(cursor.toUtc, window.toUtc))
    throw new Error("Deadlock continuation did not match the investigation.");
}
export function validateDeadlockRequest(targetId: string, window: DeadlockWindow, cursor?: string): void {
  validateDeadlockWindow(window);
  if (cursor !== undefined) bindCursor(decodeDeadlockCursor(cursor), targetId, window);
}
/** Validate visible native fields, without treating the cursor as a signed token or promising durable retention. */
export function validateDeadlockReceipt(page: DeadlockPage, targetId: string, window: DeadlockWindow,
  previous: readonly DeadlockReceipt[] = [], requestedCursor?: string): void {
  validateDeadlockWindow(window);
  const from = deadlockUtc(window.fromUtc), to = deadlockUtc(window.toUtc), cutoff = deadlockUtc(page.repositoryTimeUtc);
  if (page.targetId.toLowerCase() !== targetId.toLowerCase() || page.items.length > deadlockPageLimit ||
      page.items.some(item => deadlockUtc(item.occurredAtUtc) < from || deadlockUtc(item.occurredAtUtc) >= to || deadlockUtc(item.collectedAtUtc) > cutoff) ||
      page.items.some((item, index) => index > 0 && compare(page.items[index - 1]!, item) <= 0))
    throw new Error("Deadlock evidence did not match its native page contract.");
  const ids = new Set(previous.flatMap(receipt => receipt.page.items.map(item => item.eventId.toLowerCase())));
  for (const item of page.items) {
    if (ids.has(item.eventId.toLowerCase())) throw new Error("Deadlock continuation repeated received evidence.");
    ids.add(item.eventId.toLowerCase());
  }
  if (requestedCursor !== undefined) {
    const incoming = decodeDeadlockCursor(requestedCursor); bindCursor(incoming, targetId, window);
    if (!sameUtc(incoming.collectedCutoffUtc, page.repositoryTimeUtc) || page.items.some(item => compare(item, incoming) >= 0) ||
        previous.length && previous.at(-1)?.page.nextCursor !== requestedCursor)
      throw new Error("Deadlock continuation changed the received cutoff or position.");
  }
  if (page.nextCursor !== undefined) {
    const outgoing = decodeDeadlockCursor(page.nextCursor); bindCursor(outgoing, targetId, window);
    const tail = page.items.at(-1);
    if (page.items.length !== deadlockPageLimit || !tail || compare(tail, outgoing) !== 0 || !sameUtc(outgoing.collectedCutoffUtc, page.repositoryTimeUtc))
      throw new Error("Deadlock continuation did not match the received page.");
  }
  if (previous.length >= maximumDeadlockPages || previous.reduce((total, receipt) => total + receipt.page.items.length, 0) + page.items.length > maximumDeadlockRows)
    throw new Error("Deadlock investigation reached its retained evidence bound.");
}
