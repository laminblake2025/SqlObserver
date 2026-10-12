import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { healthCheckHref } from "./agentHistoryModel.ts";

export type BackupAvailabilityKind = "backup" | "availability";
export interface BackupAvailabilitySelection { readonly runId: string; readonly sampleTargetRevision: number }
const names = { backup: ["backupRun", "backupRevision"], availability: ["availabilityRun", "availabilityRevision"] } as const;
const guid = (value: string | undefined): value is string => value !== undefined &&
  /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(value) && value !== "00000000-0000-0000-0000-000000000000";
const revision = (value: string | undefined): number | undefined => {
  if (value === undefined || !/^[1-9]\d{0,15}$/u.test(value)) return undefined;
  const number = Number(value);
  return Number.isSafeInteger(number) ? number : undefined;
};
const clock = (value: string): bigint => {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6}|\.\d{6}0)?Z$/u.test(value)) throw new Error("Invalid UTC window.");
  return utcMicroseconds(value.replace(/(\.\d{6})0Z$/u, "$1Z"));
};
function context(hash: string, kind: BackupAvailabilityKind, expectedTarget?: string): URLSearchParams | undefined {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  const params = new URLSearchParams(query);
  const targets = params.getAll("target"), checks = params.getAll("check"), ranges = params.getAll("range");
  if (path !== "operations" || checks.length !== 1 || checks[0] !== (kind === "backup" ? "backups" : "availability") ||
      targets.length !== 1 || !guid(targets[0]) || expectedTarget !== undefined && targets[0].toLowerCase() !== expectedTarget.toLowerCase() ||
      ranges.length !== 1 || !["1h", "6h", "24h", "7d", "custom"].includes(ranges[0]!)) return undefined;
  const from = params.getAll("from"), to = params.getAll("to"), database = params.getAll("databaseId"), compare = params.getAll("compare");
  if (from.length > 1 || to.length > 1 || database.length > 1 || compare.length > 1 ||
      database.length === 1 && (!/^[1-9]\d{0,4}$/u.test(database[0]!) || Number(database[0]) > 32767) ||
      compare.length === 1 && !["0", "1"].includes(compare[0]!)) return undefined;
  try {
    if (ranges[0] === "custom") {
      if (from.length !== 1 || to.length !== 1) return undefined;
      const duration = clock(to[0]!) - clock(from[0]!);
      if (duration <= 0n || duration > 7n * 86400n * 1000000n) return undefined;
    } else if (from.length || to.length) return undefined;
  } catch { return undefined; }
  return params;
}

/** Undefined means no pin; null keeps an invalid explicit pin from choosing a substitute. */
export function readBackupAvailabilitySelection(hash: string, kind: BackupAvailabilityKind, expectedTarget?: string): BackupAvailabilitySelection | null | undefined {
  const raw = new URLSearchParams(hash.split("?")[1]), [runKey, revisionKey] = names[kind];
  if (!raw.has(runKey) && !raw.has(revisionKey)) return undefined;
  const params = context(hash, kind, expectedTarget);
  if (!params) return null;
  const runs = params.getAll(runKey), revisions = params.getAll(revisionKey), sampleTargetRevision = revision(revisions[0]);
  return runs.length === 1 && revisions.length === 1 && guid(runs[0]) && sampleTargetRevision !== undefined
    ? { runId: runs[0].toLowerCase(), sampleTargetRevision } : null;
}

export function backupAvailabilitySelectionHref(scope: OverviewScope, kind: BackupAvailabilityKind, selection?: BackupAvailabilitySelection): string {
  const href = healthCheckHref(scope, scope.target, kind === "backup" ? "backups" : "availability");
  if (!context(href, kind)) throw new Error("Choose a valid supporting-evidence server and window of up to seven days.");
  if (selection === undefined) return href;
  if (!guid(selection.runId) || revision(String(selection.sampleTargetRevision)) === undefined) throw new Error("Choose an exact received collection receipt.");
  const [runKey, revisionKey] = names[kind];
  return `${href}&${runKey}=${selection.runId.toLowerCase()}&${revisionKey}=${selection.sampleTargetRevision}`;
}

/** Clear only this source pin, preserving malformed context for explicit correction. */
export function clearBackupAvailabilitySelectionHref(hash: string, kind: BackupAvailabilityKind): string {
  const [path, query] = hash.split("?"), params = new URLSearchParams(query);
  for (const key of names[kind]) params.delete(key);
  return `${path}?${params}`;
}

/** Server-wide receipts survive database/compare edits within the same owning window. */
export function retainBackupAvailabilitySelectionHref(href: string, currentHash: string): string {
  for (const kind of ["backup", "availability"] as const) {
    const next = context(href, kind), current = context(currentHash, kind);
    if (!next || !current || next.get("target")!.toLowerCase() !== current.get("target")!.toLowerCase() || next.get("range") !== current.get("range")) continue;
    if (next.get("range") === "custom") {
      try { if (clock(next.get("from")!) !== clock(current.get("from")!) || clock(next.get("to")!) !== clock(current.get("to")!)) continue; }
      catch { continue; }
    }
    for (const key of names[kind]) {
      next.delete(key);
      for (const value of current.getAll(key)) next.append(key, value);
    }
    return `${href.split("?")[0]}?${next}`;
  }
  return href;
}

/** A Live investigation restores its actual received shared window on reload. */
export function retainedBackupAvailabilitySelectionHref(scope: OverviewScope, kind: BackupAvailabilityKind,
  window: { readonly fromUtc: string; readonly toUtc: string }, selection?: BackupAvailabilitySelection): string {
  return backupAvailabilitySelectionHref({ ...scope, range: "custom", from: window.fromUtc, to: window.toUtc }, kind, selection);
}

/** Retention must not silently repair an invalid/repeated explicit pin. */
export function retainBackupAvailabilityRawPin(href: string, currentHash: string, kind: BackupAvailabilityKind): string {
  const source = new URLSearchParams(currentHash.split("?")[1]), destination = new URLSearchParams(href.split("?")[1]);
  for (const key of names[kind]) {
    destination.delete(key);
    for (const value of source.getAll(key)) destination.append(key, value);
  }
  return `${href.split("?")[0]}?${destination}`;
}
