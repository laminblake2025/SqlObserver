import { utcMicroseconds } from '../../utcTimestamp.ts';

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

export function chartSvgX(svg: SVGSVGElement, clientX: number, clientY: number): number | null {
  const matrix = svg.getScreenCTM();
  if (!matrix) return null;
  const point = svg.createSVGPoint(); point.x = clientX; point.y = clientY;
  const local = point.matrixTransform(matrix.inverse());
  return Number.isFinite(local.x) ? Math.min(plotRight, Math.max(plotLeft, local.x)) : null;
}

export function chartUtcAtX(x: number, fromUtc: string, toUtc: string): string {
  const from = chartInstant(fromUtc), to = chartInstant(toUtc);
  if (!Number.isFinite(x) || from >= to || to - from > BigInt(Number.MAX_SAFE_INTEGER))
    throw new Error("Chart window is unavailable.");
  const fraction = (Math.min(plotRight, Math.max(plotLeft, x)) - plotLeft) / (plotRight - plotLeft);
  return formatChartInstant(from + BigInt(Math.round(fraction * Number(to - from))));
}

export function chartSelection(startX: number, endX: number, fromUtc: string, toUtc: string): { fromUtc: string; toUtc: string } | undefined {
  const firstUtc = chartUtcAtX(startX, fromUtc, toUtc), lastUtc = chartUtcAtX(endX, fromUtc, toUtc);
  const first = chartInstant(firstUtc), last = chartInstant(lastUtc);
  if ((last >= first ? last - first : first - last) < 60_000_000n) return undefined;
  return first <= last ? { fromUtc: firstUtc, toUtc: lastUtc } : { fromUtc: lastUtc, toUtc: firstUtc };
}

function chartInstant(value: string): bigint {
  return utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, '$1'));
}

function formatChartInstant(value: bigint): string {
  // Keep familiar millisecond spelling when exact; native endpoints retain all six digits.
  if (value % 1000n === 0n) return new Date(Number(value / 1000n)).toISOString();
  const seconds = value >= 0n ? value / 1_000_000n : (value - 999_999n) / 1_000_000n;
  const fraction = value - seconds * 1_000_000n;
  return `${new Date(Number(seconds * 1000n)).toISOString().slice(0, 19)}.${fraction.toString().padStart(6, '0')}Z`;
}
