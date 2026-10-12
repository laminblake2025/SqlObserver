import { apiFetch } from "../../apiClient.ts";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { readBoundedBody } from "../activity/activityParser.mjs";
export type ReportKind = "instance-health" | "performance-window" | "incident-evidence" | "capacity-readiness";
export interface ReportRun {
  readonly runId: string; readonly targetId: string; readonly targetRevision: number;
  readonly kind: 0 | 1 | 2 | 3; readonly definitionVersion: 1;
  readonly snapshotUtc: string; readonly expiresAtUtc: string;
  readonly parameterDigest: string; readonly state: "finalized";
}

const kindValues: Readonly<Record<ReportKind, 0 | 1 | 2 | 3>> = {
  "instance-health": 0, "performance-window": 1, "incident-evidence": 2, "capacity-readiness": 3,
};
const receiptBytes = 16 * 1024;
const reportLifetime = 24n * 3600n * 1_000_000n;
function validId(value: unknown): value is string {
  return typeof value === "string" && /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value)
    && value !== "00000000-0000-0000-0000-000000000000";
}
function knownKind(value: unknown): value is ReportKind {
  return typeof value === "string" && Object.hasOwn(kindValues, value);
}
function invalidReceipt(): Error { return new Error("The report response could not be verified."); }
function receiptUtc(value: string): string {
  // PostgreSQL DateTimeOffset receipts have a seventh round-trip digit of zero.
  // Other sub-microsecond fractions remain invalid; request/digest inputs are unchanged.
  return canonicalUtcTimestamp(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
}

function parseReceipt(value: unknown, targetId: string, reportKind: ReportKind, expectedDigest: string): ReportRun {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw invalidReceipt();
  const run = value as Record<string, unknown>;
  if (!validId(run.runId) || !validId(run.targetId) || run.targetId.toLowerCase() !== targetId.toLowerCase()
    || run.kind !== kindValues[reportKind] || !Number.isSafeInteger(run.targetRevision) || (run.targetRevision as number) < 1
    || run.definitionVersion !== 1 || run.state !== "finalized"
    || run.parameterDigest !== expectedDigest
    || typeof run.snapshotUtc !== "string" || typeof run.expiresAtUtc !== "string") throw invalidReceipt();
  const snapshotUtc = receiptUtc(run.snapshotUtc), expiresAtUtc = receiptUtc(run.expiresAtUtc);
  if (utcMicroseconds(expiresAtUtc) - utcMicroseconds(snapshotUtc) !== reportLifetime) throw invalidReceipt();
  return { runId: run.runId, targetId: run.targetId, targetRevision: run.targetRevision as number,
    kind: kindValues[reportKind], definitionVersion: 1, snapshotUtc, expiresAtUtc,
    parameterDigest: expectedDigest, state: "finalized" };
}

/** Match ReportRequestValidation.CanonicalDigest's UTC DateTimeOffset round-trip form. */
function roundTripUtc(value: string): string {
  utcMicroseconds(value);
  const fraction = value.match(/\.(\d{1,6})Z$/)?.[1] ?? "";
  return `${value.slice(0, 19)}.${fraction.padEnd(7, "0")}+00:00`;
}
async function requestDigest(reportKind: ReportKind, fromUtc?: string, toUtc?: string): Promise<string> {
  const canonical = fromUtc === undefined || toUtc === undefined ? { reportKind }
    : { fromUtc: roundTripUtc(fromUtc), reportKind, toUtc: roundTripUtc(toUtc) };
  const digest = await globalThis.crypto.subtle.digest("SHA-256", new TextEncoder().encode(JSON.stringify(canonical)));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, "0")).join("");
}

/** Convert a datetime-local control value as an explicit UTC wall-clock value. */
export function localDateTimeToUtc(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (match === null) throw new Error("Choose a valid UTC date and time.");
  const year = Number(match[1]); const month = Number(match[2]); const day = Number(match[3]); const hour = Number(match[4]); const minute = Number(match[5]);
  const date = new Date(Date.UTC(year, month - 1, day, hour, minute));
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day || date.getUTCHours() !== hour || date.getUTCMinutes() !== minute) throw new Error("Choose a valid UTC date and time.");
  return date.toISOString();
}

export function validateReportWindow(reportKind: ReportKind, fromUtc?: string, toUtc?: string): void {
  if (!knownKind(reportKind)) throw new Error("Choose a supported report type.");
  if (reportKind === "instance-health") { if (fromUtc !== undefined || toUtc !== undefined) throw new Error("Instance Health does not use a time window."); return; }
  if (fromUtc === undefined || toUtc === undefined || !fromUtc.endsWith("Z") || !toUtc.endsWith("Z")) throw new Error("Choose a UTC start and end time.");
  const maximum = reportKind === "capacity-readiness" ? 31 : 7;
  let duration: bigint;
  try { duration = utcMicroseconds(toUtc) - utcMicroseconds(fromUtc); }
  catch { throw new Error("Choose a UTC start and end time."); }
  if (duration <= 0n || duration > BigInt(maximum) * 86_400_000_000n) throw new Error(`Choose a window of at most ${maximum} days.`);
}

export async function createReport(targetId: string, reportKind: ReportKind, signal: AbortSignal, fromUtc?: string, toUtc?: string, operationId: string = globalThis.crypto.randomUUID()): Promise<ReportRun> {
  if (!validId(targetId)) throw new Error("Choose a valid server before generating a report.");
  if (!validId(operationId)) throw new Error("The report operation is invalid. Start a new report.");
  validateReportWindow(reportKind, fromUtc, toUtc);
  if (signal.aborted) throw signal.reason;
  const expectedDigest = await requestDigest(reportKind, fromUtc, toUtc);
  let response: Response;
  try {
    response = await apiFetch(`/api/v1/observation-targets/${targetId}/reports`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ reportKind, operationId, ...(fromUtc === undefined ? {} : { fromUtc, toUtc }) }), signal }, { maximumResponseBytes: receiptBytes });
  } catch (error) { if (signal.aborted) throw error; throw invalidReceipt(); }
  if (!response.ok) throw new Error(response.status === 403 ? "Report access is not authorized." : "The report could not be created.");
  try { return parseReceipt(JSON.parse(await readBoundedBody(response, signal, receiptBytes)), targetId, reportKind, expectedDigest); }
  catch (error) { if (signal.aborted) throw error; throw invalidReceipt(); }
}

export function reportHtmlUrl(targetId: string, runId: string): string { return `/api/v1/observation-targets/${targetId}/reports/${runId}/html`; }
export function reportCsvUrl(targetId: string, runId: string, section: string): string { return `/api/v1/observation-targets/${targetId}/reports/${runId}/${section}.csv`; }
