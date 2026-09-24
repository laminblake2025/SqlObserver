const plotLeft = 48;
const plotRight = 620;
const viewWidth = 650;

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
