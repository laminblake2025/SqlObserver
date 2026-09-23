import { responseFailure } from "../../api/responseFailure.ts";
export type ReportKind = "instance-health" | "performance-window" | "incident-evidence" | "capacity-readiness";
export interface ReportRun { readonly runId: string; readonly targetId: string; readonly reportKind?: ReportKind; readonly kind?: string; readonly snapshotUtc: string; readonly expiresAtUtc: string; readonly state: string; }

/** Convert a datetime-local control value as an explicit UTC wall-clock value. */
export function localDateTimeToUtc(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,3}))?)?$/.exec(value);
  if (match === null) throw new Error("Choose a valid UTC date and time.");
  const year = Number(match[1]); const month = Number(match[2]); const day = Number(match[3]); const hour = Number(match[4]); const minute = Number(match[5]);
  const second = Number(match[6] ?? 0); const millisecond = Number((match[7] ?? "").padEnd(3, "0"));
  const date = new Date(`${value}Z`);
  if (date.getUTCFullYear() !== year || date.getUTCMonth() !== month - 1 || date.getUTCDate() !== day || date.getUTCHours() !== hour || date.getUTCMinutes() !== minute || date.getUTCSeconds() !== second || date.getUTCMilliseconds() !== millisecond) throw new Error("Choose a valid UTC date and time.");
  return date.toISOString();
}

export function utcDateTimeControlValue(value?: string): string {
  return value === undefined ? "" : new Date(value).toISOString().replace(/Z$/, "");
}

export function validateReportWindow(reportKind: ReportKind, fromUtc?: string, toUtc?: string): void {
  if (reportKind === "instance-health") { if (fromUtc !== undefined || toUtc !== undefined) throw new Error("Instance Health does not use a time window."); return; }
  if (fromUtc === undefined || toUtc === undefined || !fromUtc.endsWith("Z") || !toUtc.endsWith("Z")) throw new Error("Choose a UTC start and end time.");
  const duration = Date.parse(toUtc) - Date.parse(fromUtc); const maximum = reportKind === "capacity-readiness" ? 31 : 7;
  if (!Number.isFinite(duration) || duration <= 0 || duration > maximum * 86_400_000) throw new Error(`Choose a window of at most ${maximum} days.`);
}

export async function createReport(targetId: string, reportKind: ReportKind, signal: AbortSignal, fromUtc?: string, toUtc?: string): Promise<ReportRun> {
  validateReportWindow(reportKind, fromUtc, toUtc);
  const response = await fetch(`/api/v1/observation-targets/${targetId}/reports`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ reportKind, operationId: globalThis.crypto.randomUUID(), ...(fromUtc === undefined ? {} : { fromUtc, toUtc }) }), signal });
  if (!response.ok) throw responseFailure(response, response.status === 403 ? "Report access is not authorized." : "The report could not be created.");
  return (await response.json()) as ReportRun;
}

export function reportHtmlUrl(targetId: string, runId: string): string { return `/api/v1/observation-targets/${targetId}/reports/${runId}/html`; }
export function reportCsvUrl(targetId: string, runId: string, section: string): string { return `/api/v1/observation-targets/${targetId}/reports/${runId}/${section}.csv`; }
