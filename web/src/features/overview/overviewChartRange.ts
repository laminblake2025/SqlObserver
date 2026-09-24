import type { OverviewSeries } from "./overviewTypes";

export interface OverviewChartRange {
  readonly minimum: number;
  readonly maximum: number;
}

export function overviewChartRange(
  series: readonly OverviewSeries[], previous: readonly OverviewSeries[] | undefined,
  fromUtc: string, toUtc: string,
): OverviewChartRange | null {
  const from = Date.parse(fromUtc), to = Date.parse(toUtc);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from) return null;

  let low = Infinity, high = -Infinity, currentCount = 0;
  const include = (entries: readonly OverviewSeries[], shift: number) => {
    for (const entry of entries) for (const point of entry.points) {
      const at = Date.parse(point.timeUtc) + shift;
      if (!Number.isFinite(at) || at < from || at >= to || point.value === null || !Number.isFinite(point.value)) continue;
      low = Math.min(low, point.value);
      high = Math.max(high, point.value);
      if (shift === 0) currentCount++;
    }
  };
  include(series, 0);
  if (currentCount === 0) return null;
  include(previous ?? [], to - from);

  const padding = low === high ? (low === 0 ? 1 : Math.abs(low) * .05) : (high - low) * .05;
  if (!Number.isFinite(padding)) return null;
  const minimum = Math.max(low >= 0 ? 0 : -Infinity, low - padding);
  const maximum = Math.min(high < 0 ? 0 : Infinity, high + padding);
  return Number.isFinite(minimum) && Number.isFinite(maximum) && maximum > minimum
    ? { minimum, maximum } : null;
}

export function formatOverviewChartValue(value: number | null, range: OverviewChartRange): string {
  if (value === null || !Number.isFinite(value)) return "—";
  const tickStep = (range.maximum - range.minimum) / 4;
  const decimals = Math.min(10, Math.max(2, Math.ceil(-Math.log10(tickStep)) + 1));
  return value.toLocaleString(undefined, { maximumFractionDigits: decimals });
}
