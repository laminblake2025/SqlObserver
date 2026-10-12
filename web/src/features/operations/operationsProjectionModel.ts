import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { OperationalKind, OperationalPage } from "./operationsTypes.ts";
export type OperationalProjectionKind = Exclude<OperationalKind, "agent">;
export const projectionPageLimit = 50;
export const maximumProjectionPages = 400;
export const maximumProjectionRows = 10_000;
const guid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu;
const hash = /^[0-9a-f]{64}$/u;
const mismatch = "Operational projection could not be verified or its snapshot changed. Return to latest before continuing.";
const fail = (): never => { throw new Error(mismatch); };
const clock = (value: unknown): bigint => utcMicroseconds(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value);
const integer = (value: unknown, minimum = 0): value is number => typeof value === "number" && Number.isSafeInteger(value) && value >= minimum;
const kinds = { backups: "backups", "tempdb-summary": "tempdb", "tempdb-files": "tempdb", "ag-replicas": "ag.replicas", "ag-databases": "ag.databases" } as const;
export interface OperationalProjectionCursor {
  readonly Kind: string; readonly RunId: string; readonly Revision: number; readonly From: null; readonly To: null;
  readonly A: string; readonly B: string; readonly C: string; readonly D: string; readonly snapshotUtc: string;
}
function decodeBase64(value: string): string {
  if (!value || !/^[A-Za-z0-9+/]+={0,2}$/u.test(value) || value.length % 4 !== 0) return fail();
  try {
    const decoded = atob(value);
    if (btoa(decoded) !== value) return fail();
    return new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(decoded, character => character.charCodeAt(0)));
  } catch { return fail(); }
}
export function parseOperationalProjectionCursor(cursor: string, kind: OperationalProjectionKind, targetId: string, page?: OperationalPage): OperationalProjectionCursor {
  if (cursor.length > 1024) return fail();
  const parts = decodeBase64(cursor).split("|");
  if (parts.length !== 3 || !guid.test(parts[0]!) || parts[0]!.toLowerCase() !== targetId.toLowerCase() || parts[2]!.length > 512) return fail();
  let value: unknown;
  try { clock(parts[1]); value = JSON.parse(decodeBase64(parts[2]!)); } catch { return fail(); }
  if (!value || typeof value !== "object" || Array.isArray(value)) return fail();
  const item = value as Record<string, unknown>;
  const fields = ["Kind", "RunId", "Revision", "From", "To", "A", "B", "C", "D"];
  if (Object.keys(item).length !== fields.length || fields.some(field => !Object.hasOwn(item, field)) || item.Kind !== kinds[kind] ||
    typeof item.RunId !== "string" || !guid.test(item.RunId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/u.test(item.RunId) ||
    !integer(item.Revision, 1) || item.From !== null || item.To !== null || ["A", "B", "C", "D"].some(field => typeof item[field] !== "string")) return fail();
  if (page && (page.runId?.toLowerCase() !== item.RunId.toLowerCase() || page.targetRevision !== item.Revision || clock(page.observedAtUtc) !== clock(parts[1]))) return fail();
  if (kind.startsWith("ag-")) {
    if (!hash.test(item.A as string) || !hash.test(item.B as string) || item.C !== "" || item.D !== "") return fail();
  } else if (kind.startsWith("tempdb-")) {
    if (!/^\d+$/u.test(item.A as string) || !integer(Number(item.A)) || String(Number(item.A)) !== item.A || item.B !== "" || item.C !== "" || item.D !== "") return fail();
  } else {
    try { if (item.A !== "<null-utc>") clock(item.A); } catch { return fail(); }
    if (item.B !== "" && (!/^\d+$/u.test(item.B as string) || !integer(Number(item.B)) || String(Number(item.B)) !== item.B) ||
      !hash.test(item.C as string) || !["1", "2", "3"].includes(item.D as string)) return fail();
  }
  return { ...item, snapshotUtc: parts[1]! } as unknown as OperationalProjectionCursor;
}
function field(item: Record<string, unknown>, key: string): unknown {
  return Object.hasOwn(item, key) ? item[key] : item[key[0]!.toUpperCase() + key.slice(1)];
}
function order(left: Record<string, unknown>, right: Record<string, unknown>, kind: OperationalProjectionKind): number {
  if (kind.startsWith("ag-")) {
    const key = kind === "ag-replicas" ? "replicaFingerprint" : "databaseFingerprint";
    return String(field(left, "groupFingerprint")).localeCompare(String(field(right, "groupFingerprint"))) || String(field(left, key)).localeCompare(String(field(right, key)));
  }
  if (kind === "tempdb-files") return Number(field(left, "fileId")) - Number(field(right, "fileId"));
  const finishLeft = field(left, "lastFinishUtc"), finishRight = field(right, "lastFinishUtc");
  if (finishLeft !== null && finishRight !== null) { const difference = clock(finishRight) - clock(finishLeft); if (difference) return difference > 0n ? 1 : -1; }
  else if (finishLeft !== finishRight) return finishLeft === null ? 1 : -1;
  const idLeft = field(left, "backupSetId"), idRight = field(right, "backupSetId");
  if (idLeft !== null && idRight !== null && idLeft !== idRight) return Number(idRight) - Number(idLeft);
  if (idLeft !== idRight) return idLeft === null ? 1 : -1;
  return String(field(left, "databaseFingerprint")).localeCompare(String(field(right, "databaseFingerprint"))) ||
    ["Full", "Differential", "Log"].indexOf(String(field(left, "kind"))) - ["Full", "Differential", "Log"].indexOf(String(field(right, "kind")));
}
function validateProjectionPage(page: OperationalPage, kind: OperationalProjectionKind, targetId: string,
  anchor?: OperationalPage, after?: Record<string, unknown>, previousCursors: readonly (string | undefined)[] = []): void {
  if (page.targetId.toLowerCase() !== targetId.toLowerCase() || !guid.test(page.targetId) || !integer(page.targetRevision, 1) ||
    !Array.isArray(page.items) || page.items.length > projectionPageLimit || typeof page.hasMore !== "boolean" || typeof page.truncated !== "boolean" ||
    !["Complete", "Partial", "Degraded", "Unsupported", "PermissionDenied", "NoData"].includes(page.state) ||
    page.hasMore !== (page.nextCursor !== null) || page.runId !== null && (!guid.test(page.runId) || /^0{8}-0{4}-0{4}-0{4}-0{12}$/u.test(page.runId))) return fail();
  clock(page.observedAtUtc);
  if ((page.state === "NoData") !== (page.runId === null) || page.state === "NoData" && (page.items.length || page.hasMore || page.evidence != null)) return fail();
  if (page.state !== "NoData" && (!page.evidence || clock(page.evidence.observedAtUtc) !== clock(page.observedAtUtc))) return fail();
  if (kind === "tempdb-summary" && page.items.length !== 0 || page.hasMore && kind !== "tempdb-summary" && page.items.length !== projectionPageLimit) return fail();
  // The native AG adapter derives these two fields from the returned page:
  // truncation is its lookahead result, and visibility is its first row's scope.
  const pageRelativeAvailability = kind.startsWith("ag-");
  if (anchor && (anchor.targetId.toLowerCase() !== page.targetId.toLowerCase() || anchor.targetRevision !== page.targetRevision ||
    anchor.runId?.toLowerCase() !== page.runId?.toLowerCase() || clock(anchor.observedAtUtc) !== clock(page.observedAtUtc) ||
    anchor.state !== page.state || !pageRelativeAvailability && (anchor.truncated !== page.truncated || anchor.visibilityScope !== page.visibilityScope) ||
    ["totalBytes", "usedBytes", "logTotalBytes", "logUsedBytes"].some(key => anchor[key as keyof OperationalPage] !== page[key as keyof OperationalPage]))) return fail();
  let previous = after;
  for (const row of page.items) {
    if (!row || typeof row !== "object" || Array.isArray(row) || previous && order(previous, row, kind) >= 0) return fail();
    previous = row;
  }
  if (page.nextCursor !== null) {
    if (previousCursors.includes(page.nextCursor)) return fail();
    const cursor = parseOperationalProjectionCursor(page.nextCursor, kind, targetId, page);
    const tail = page.items.at(-1);
    if (kind === "tempdb-summary") return;
    if (!tail) return fail();
    if (kind.startsWith("ag-")) {
      if (cursor.A !== field(tail, "groupFingerprint") || cursor.B !== field(tail, kind === "ag-replicas" ? "replicaFingerprint" : "databaseFingerprint")) return fail();
    } else if (kind === "tempdb-files") {
      if (cursor.A !== String(field(tail, "fileId"))) return fail();
    } else {
      const finish = field(tail, "lastFinishUtc"), id = field(tail, "backupSetId");
      if ((finish === null ? cursor.A !== "<null-utc>" : cursor.A === "<null-utc>" || clock(cursor.A) !== clock(finish)) ||
        cursor.B !== (id === null ? "" : String(id)) || cursor.C !== field(tail, "databaseFingerprint") ||
        cursor.D !== String(["Full", "Differential", "Log"].indexOf(String(field(tail, "kind"))) + 1)) return fail();
    }
  }
}

export function validateOperationalProjectionPage(page: OperationalPage, kind: OperationalProjectionKind, targetId: string,
  anchor?: OperationalPage, after?: Record<string, unknown>, previousCursors: readonly (string | undefined)[] = []): void {
  try { validateProjectionPage(page, kind, targetId, anchor, after, previousCursors); }
  catch { return fail(); }
}
