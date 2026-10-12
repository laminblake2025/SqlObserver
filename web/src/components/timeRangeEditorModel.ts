import { customUtcInputValue } from "../features/overview/overviewModel.ts";

export function displayRangeInput(value: string | undefined, mode: "utc" | "local"): string {
  if (mode === "utc" || !value) return customUtcInputValue(value);
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "";
  const pad = (value: number, length = 2) => String(value).padStart(length, "0");
  return `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}

export function editedRangeUtc(value: string, mode: "utc" | "local"): string {
  if (mode === "utc") return value;
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$/.test(value)) throw new Error("Enter a valid local date and time.");
  const normalized = value.length === 16 ? `${value}:00.000` : value.length === 19 ? `${value}.000` : value.padEnd(23, "0");
  const date = new Date(value);
  if (!Number.isFinite(date.getTime()) || displayRangeInput(date.toISOString(), "local") !== normalized)
    throw new Error("This local time does not exist in the selected timezone. Choose a valid time or switch to UTC.");
  // Probe all offset changes around the date, including non-hour DST changes.
  const offsets = new Set<number>();
  for (let hour = -36; hour <= 36; hour++) offsets.add(new Date(date.getTime() + hour * 3600000).getTimezoneOffset());
  const candidates = [...offsets].map(offset => date.getTime() + (offset - date.getTimezoneOffset()) * 60000)
    .filter(instant => displayRangeInput(new Date(instant).toISOString(), "local") === normalized);
  if (new Set(candidates).size > 1) throw new Error("This local time occurs twice when daylight saving time ends. Switch to UTC to choose the exact instant.");
  return date.toISOString().replace(/Z$/, "");
}
