import type { OverviewSeries } from "../overview/overviewTypes";
import type { BufferNodePlePoint, BufferNodePleWindow } from "./bufferNodePleApi";

function gaugeSeries(window: BufferNodePleWindow, source: readonly BufferNodePlePoint[],
  metric: string, label: string): readonly OverviewSeries[] {
  const from = Date.parse(window.fromUtc);
  const to = Date.parse(window.toUtc);
  const hour = 3_600_000;
  const byBucket = new Map(source.map(point => [Date.parse(point.bucketStartUtc), point]));
  const buckets: readonly { timeUtc: string; point: BufferNodePlePoint | undefined }[] =
    Number.isFinite(from) && Number.isFinite(to) && to > from && to - from <= 7 * 86_400_000
      ? Array.from({ length: Math.ceil(to / hour) - Math.floor(from / hour) }, (_, index) => {
        const bucket = (Math.floor(from / hour) + index) * hour;
        return { timeUtc: new Date(Math.max(bucket, from)).toISOString(), point: byBucket.get(bucket) };
      }) : [];
  const variants = [
    { dimension: "Minimum", value: "minimumSeconds" },
    { dimension: "Average", value: "averageSeconds" },
    { dimension: "Maximum", value: "maximumSeconds" },
  ] as const;
  return variants.map(variant => ({
    targetId: window.targetId,
    label,
    metric,
    unit: "s",
    state: "observed",
    dimension: variant.dimension,
    points: buckets.map(({ timeUtc, point }) => ({
      timeUtc,
      value: point ? point[variant.value] : null,
      samples: point?.sampleCount ?? 0,
    })),
  }));
}

export const bufferNodePleSeries = (window: BufferNodePleWindow): readonly OverviewSeries[] =>
  window.selectedNodeId === null ? [] : gaugeSeries(window, window.points,
    "engine.buffer_node_page_life_expectancy_seconds",
    `Buffer Node ${window.selectedNodeId} PLE`);

export const bufferManagerPleSeries = (window: BufferNodePleWindow): readonly OverviewSeries[] =>
  gaugeSeries(window, window.aggregatePoints, "engine.page_life_expectancy_seconds",
    "Buffer Manager aggregate PLE");
