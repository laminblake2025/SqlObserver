import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";

const repositoryPrecision = (value: string) => value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");

/** Align an instant inside the shared half-open window without rounding its bounds. */
export function resourceInspectionUtc(value: string, fromUtc: string, toUtc: string): string {
  const time = utcMicroseconds(repositoryPrecision(value));
  const from = utcMicroseconds(repositoryPrecision(fromUtc));
  const to = utcMicroseconds(repositoryPrecision(toUtc));
  if (from >= to || time < from || time >= to) throw new Error("Choose an inspection time inside the shared window, before its end.");
  return canonicalUtcTimestamp(repositoryPrecision(value));
}

export function resourceInspectionInputUtc(value: string, fromUtc: string, toUtc: string): string {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,6})?)?$/u.test(value)) {
    throw new Error("Enter a valid inspection date and time in UTC.");
  }
  const utc = `${value.length === 16 ? `${value}:00` : value}Z`;
  return resourceInspectionUtc(utc, fromUtc, toUtc);
}
