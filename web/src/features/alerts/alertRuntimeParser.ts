import type { ActiveAlert, ActiveAlertPage, AlertState } from "./alertTypes";

export const MAX_ALERT_RESPONSE_BYTES = 256 * 1024;

export async function readJsonBounded(response: Response, maximumBytes = MAX_ALERT_RESPONSE_BYTES): Promise<unknown> {
  const declared = response.headers.get("content-length");
  if (declared !== null && (!/^\d+$/.test(declared) || Number(declared) > maximumBytes)) throw new Error("Alerts response is too large.");
  if (!response.body) throw new Error("Alerts response body is missing.");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = []; let total = 0;
  try {
    for (;;) {
      const part = await reader.read(); if (part.done) break;
      total += part.value.byteLength; if (total > maximumBytes) throw new Error("Alerts response is too large.");
      chunks.push(part.value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(total); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

export function parseActiveAlerts(value: unknown, targetId: string): ActiveAlertPage {
  if (!value || typeof value !== "object") throw new Error("Alerts are unavailable.");
  const row = value as { targetId?: unknown; items?: unknown; nextCursor?: unknown; snapshotUtc?: unknown };
  if (row.targetId !== targetId || !Array.isArray(row.items) || row.items.length > 100 || row.nextCursor !== undefined && row.nextCursor !== null && (typeof row.nextCursor !== "string" || row.nextCursor.length === 0 || row.nextCursor.length > 1024 || !/^[A-Za-z0-9+/]+={0,2}$/.test(row.nextCursor)) || row.snapshotUtc !== undefined && row.snapshotUtc !== null && (typeof row.snapshotUtc !== "string" || !isUtc(row.snapshotUtc))) throw new Error("Alerts response is invalid.");
  const items = row.items.map((item): ActiveAlert => {
    if (!item || typeof item !== "object") throw new Error("Alerts response is invalid.");
    const candidate = item as Record<string, unknown>;
    if (typeof candidate.alertId !== "string" || !isUuid(candidate.alertId) || typeof candidate.ruleId !== "string" || !isUuid(candidate.ruleId) || !isState(candidate.state) || typeof candidate.firstObservedUtc !== "string" || !isUtc(candidate.firstObservedUtc) || typeof candidate.deliverySuppressed !== "boolean") throw new Error("Alerts response is invalid.");
    if (candidate.value !== undefined && candidate.value !== null && (typeof candidate.value !== "number" || !Number.isFinite(candidate.value))) throw new Error("Alerts response is invalid.");
    if (candidate.reason !== undefined && candidate.reason !== null && typeof candidate.reason !== "string") throw new Error("Alerts response is invalid.");
    return { alertId: candidate.alertId, ruleId: candidate.ruleId, state: candidate.state, firstObservedUtc: candidate.firstObservedUtc, firedUtc: optionalUtc(candidate.firedUtc), acknowledgedUtc: optionalUtc(candidate.acknowledgedUtc), value: candidate.value as number | null | undefined, reason: candidate.reason as string | undefined, deliverySuppressed: candidate.deliverySuppressed };
  });
  return { targetId, items, nextCursor: row.nextCursor === null ? undefined : row.nextCursor as string | undefined, snapshotUtc: row.snapshotUtc === null ? undefined : row.snapshotUtc as string | undefined };
}

const isUuid = (value: string): boolean => /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
const isUtc = (value: string): boolean =>
  /^(?:\d{4})-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value) && !Number.isNaN(Date.parse(value));
const optionalUtc = (value: unknown): string | undefined => value === undefined || value === null ? undefined : typeof value === "string" && isUtc(value) ? value : (() => { throw new Error("Alerts response is invalid."); })();
const isState = (value: unknown): value is AlertState => value === "normal" || value === "pending" || value === "firing" || value === "acknowledged" || value === "resolved";
