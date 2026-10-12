export interface ObservationSample {
  readonly time: string;
  readonly value: number | null;
}

export interface ObservationSeries {
  readonly id: string;
  readonly label: string;
  readonly items: readonly ObservationSample[];
}

export interface ObservationBand {
  readonly lower: number;
  readonly upper: number;
  readonly label: string;
}

export interface ObservationThreshold {
  readonly value: number;
  readonly label: string;
}

export interface PlottedObservation {
  readonly time: string;
  readonly timestamp: number;
  readonly value: number;
}

export interface PreparedObservationSeries {
  readonly id: string;
  readonly label: string;
  readonly items: readonly (PlottedObservation | null)[];
}

export interface PreparedObservationChart {
  readonly from: number;
  readonly to: number;
  readonly minimum: number;
  readonly maximum: number;
  readonly pointCount: number;
  readonly series: readonly PreparedObservationSeries[];
}

export function prepareObservationChart(
  series: readonly ObservationSeries[], fromUtc: string, toUtc: string,
  band?: ObservationBand, threshold?: ObservationThreshold,
): PreparedObservationChart | null {
  const from = Date.parse(fromUtc), to = Date.parse(toUtc);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from) return null;

  const prepared = series.slice(0, 10).map((entry) => ({
    id: entry.id,
    label: entry.label,
    items: entry.items.slice(0, 1000)
      .map((item, index) => ({ item, index, timestamp: Date.parse(item.time) }))
      .filter(({ timestamp }) => Number.isFinite(timestamp) && timestamp >= from && timestamp <= to)
      .sort((a, b) => a.timestamp - b.timestamp || a.index - b.index)
      .map(({ item, timestamp }) => item.value === null || !Number.isFinite(item.value)
        ? null : { time: item.time, timestamp, value: item.value }),
  }));
  const values = prepared.flatMap((entry) => entry.items.flatMap((item) => item === null ? [] : [item.value]));
  if (values.length === 0) return null;
  if (band && Number.isFinite(band.lower) && Number.isFinite(band.upper) && band.lower <= band.upper)
    values.push(band.lower, band.upper);
  if (threshold && Number.isFinite(threshold.value)) values.push(threshold.value);

  const low = Math.min(...values), high = Math.max(...values);
  const padding = low === high ? Math.max(1, Math.abs(low) * .05) : (high - low) * .05;
  return {
    from, to, minimum: low - padding, maximum: high + padding,
    pointCount: prepared.reduce((count, entry) => count + entry.items.filter((item) => item !== null).length, 0),
    series: prepared,
  };
}

export function observationSegments(items: readonly (PlottedObservation | null)[]): readonly (readonly PlottedObservation[])[] {
  const segments: PlottedObservation[][] = [];
  let current: PlottedObservation[] = [];
  for (const item of items) {
    if (item !== null) current.push(item);
    else if (current.length > 0) { segments.push(current); current = []; }
  }
  if (current.length > 0) segments.push(current);
  return segments;
}

export function nearestObservation(items: readonly (PlottedObservation | null)[], timestamp: number): PlottedObservation | undefined {
  let nearest: PlottedObservation | undefined;
  for (const item of items) {
    if (item !== null && (nearest === undefined || Math.abs(item.timestamp - timestamp) < Math.abs(nearest.timestamp - timestamp)))
      nearest = item;
  }
  return nearest;
}
