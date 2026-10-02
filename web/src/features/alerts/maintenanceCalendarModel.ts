import type { MaintenanceAdminItem } from "./alertAdminListsApi.ts";
import type { TimeDisplayMode } from "../../timeDisplay.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";

// Date supplies civil-day boundaries; source overlap uses exact 100 ns UTC ticks.
function ticks(value: string): bigint {
  const seventh = /\.\d{6}(\d)(?=Z$|\+00:00$)/u.exec(value)?.[1];
  return utcMicroseconds(value.replace(/(\.\d{6})\d(?=Z$|\+00:00$)/u, "$1")) * 10n + BigInt(seventh ?? 0);
}
function parts(month: string) {
  if (!/^(?!0000)\d{4}-(?:0[1-9]|1[0-2])$/u.test(month)) throw new Error("Choose a valid calendar month.");
  return month.split("-").map(Number) as [number, number];
}
function boundary(year: number, month: number, day: number, mode: TimeDisplayMode): Date {
  const value = new Date(0);
  if (mode === "utc") { value.setUTCFullYear(year, month - 1, day); value.setUTCHours(0, 0, 0, 0); }
  else { value.setFullYear(year, month - 1, day); value.setHours(0, 0, 0, 0); }
  return value;
}
export function maintenanceMonthAt(value: string, mode: TimeDisplayMode): string | undefined {
  try { ticks(value); } catch { return undefined; }
  const date = new Date(value);
  const year = mode === "utc" ? date.getUTCFullYear() : date.getFullYear();
  const month = (mode === "utc" ? date.getUTCMonth() : date.getMonth()) + 1;
  return `${String(year).padStart(4, "0")}-${String(month).padStart(2, "0")}`;
}
export function shiftMaintenanceMonth(month: string, delta: number): string | undefined {
  const [year, ordinal] = parts(month);
  if (!Number.isInteger(delta)) return undefined;
  const shifted = year * 12 + ordinal - 1 + delta;
  const nextYear = Math.floor(shifted / 12), nextMonth = shifted % 12 + 1;
  return nextYear >= 1 && nextYear <= 9999 ? `${String(nextYear).padStart(4, "0")}-${String(nextMonth).padStart(2, "0")}` : undefined;
}
export function maintenanceCalendarMonth(month: string, items: readonly MaintenanceAdminItem[], mode: TimeDisplayMode) {
  const [year, ordinal] = parts(month);
  const end = boundary(year, ordinal + 1, 0, "utc").getUTCDate();
  const formatter = new Intl.DateTimeFormat("en-US", { timeZone: mode === "utc" ? "UTC" : undefined, year: "numeric", month: "long", day: "numeric" });
  const ranges: { item: MaintenanceAdminItem; start: bigint; end: bigint }[] = [];
  let unplaced = 0;
  for (const item of items) {
    try {
      const start = ticks(item.startsAtUtc), finish = ticks(item.endsAtUtc);
      if (finish <= start) throw new Error("Invalid maintenance interval.");
      ranges.push({ item, start, end: finish });
    } catch { unplaced++; }
  }
  ranges.sort((a, b) => a.start < b.start ? -1 : a.start > b.start ? 1 : a.item.windowId.localeCompare(b.item.windowId));
  const days = Array.from({ length: end }, (_, index) => {
    const start = boundary(year, ordinal, index + 1, mode), finish = boundary(year, ordinal, index + 2, mode);
    const from = BigInt(start.getTime()) * 10000n, to = BigInt(finish.getTime()) * 10000n;
    const windows = to > from ? ranges.filter(row => row.start < to && row.end > from).map(row => row.item) : [];
    return { day: index + 1, label: formatter.format(start), fromUtc: start.toISOString(), toUtc: finish.toISOString(), windows,
      cancelled: windows.filter(item => item.cancelledAtUtc !== null).length };
  });
  const first = boundary(year, ordinal, 1, mode);
  const title = new Intl.DateTimeFormat("en-US", { timeZone: mode === "utc" ? "UTC" : undefined, year: "numeric", month: "long" }).format(first);
  const offset = mode === "utc" ? first.getUTCDay() : first.getDay();
  const matched = new Set(days.flatMap(day => day.windows.map(item => item.windowId.toLowerCase())));
  return { month, title, offset, days, overlappingWindows: matched.size, unplaced };
}
