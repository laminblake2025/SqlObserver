const plotLeft = 48;
const plotRight = 620;
const viewWidth = 650;

export function chartObservedPoints<T extends { readonly timeUtc: string; readonly value: number | null }>(
  points: readonly T[], fromUtc: string, toUtc: string,
): readonly (T | null)[] {
  const from = Date.parse(fromUtc), to = Date.parse(toUtc);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from)
    throw new Error("Chart window is unavailable.");
  const duration = to - from;
  const bucket = duration > 86_400_000 ? 3_600_000 : duration > 21_600_000 ? 900_000 : 300_000;
  const visible: (T | null)[] = [];
  let previous: number | undefined;
  for (const point of points) {
    const at = Date.parse(point.timeUtc);
    if (!Number.isFinite(at) || at < from || at >= to) continue;
    if (point.value === null || !Number.isFinite(point.value)) {
      if (visible.length && visible.at(-1) !== null) visible.push(null);
      previous = undefined;
      continue;
    }
    if (previous !== undefined && at - previous > bucket) visible.push(null);
    visible.push(point);
    previous = at;
  }
  return visible;
}

export function chartX(clientX: number, left: number, width: number): number {
  if (!Number.isFinite(clientX) || !Number.isFinite(left) || !Number.isFinite(width) || width <= 0)
    throw new Error("Chart geometry is unavailable.");
  return Math.min(plotRight, Math.max(plotLeft, (clientX - left) / width * viewWidth));
}

export function chartUtcAtX(x: number, fromUtc: string, toUtc: string): string {
  const from = Date.parse(fromUtc), to = Date.parse(toUtc);
  if (!Number.isFinite(x) || !Number.isFinite(from) || !Number.isFinite(to) || from >= to)
    throw new Error("Chart window is unavailable.");
  const fraction = (Math.min(plotRight, Math.max(plotLeft, x)) - plotLeft) / (plotRight - plotLeft);
  return new Date(from + fraction * (to - from)).toISOString();
}

export function chartSelection(startX: number, endX: number, fromUtc: string, toUtc: string): { fromUtc: string; toUtc: string } | undefined {
  const first = Date.parse(chartUtcAtX(startX, fromUtc, toUtc));
  const last = Date.parse(chartUtcAtX(endX, fromUtc, toUtc));
  if (Math.abs(last - first) < 60_000) return undefined;
  return { fromUtc: new Date(Math.min(first, last)).toISOString(), toUtc: new Date(Math.max(first, last)).toISOString() };
}
