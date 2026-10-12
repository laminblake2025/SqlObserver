import type { OverviewSeries } from "../overview/overviewTypes";
import type { DatabaseFileIoTrend, DatabaseFileIoTrendPoint } from "./databaseFileIoTrendApi";
import { lifetimeAverageStallMilliseconds } from "./resourceFileModel.ts";

function average(stall: string | null, count: string | null): number | null {
  if (stall === null || count === null) return null;
  const value = lifetimeAverageStallMilliseconds(stall, count);
  if (value === null) return null;
  const numeric = Number(value);
  return Number.isFinite(numeric) && numeric <= 1_000_000_000 ? numeric : null;
}

export function fileIoTrendSeries(trend: DatabaseFileIoTrend): readonly OverviewSeries[] {
  const from = Date.parse(trend.fromUtc);
  const to = Date.parse(trend.toUtc);
  const bucketMilliseconds = 3_600_000;
  const byBucket = new Map(trend.points.map(point => [Date.parse(point.bucketStartUtc), point]));
  const buckets: readonly { timeUtc: string; point: DatabaseFileIoTrendPoint | undefined }[] =
    Number.isFinite(from) && Number.isFinite(to) && to > from && to - from <= 7 * 86_400_000
      ? Array.from({ length: Math.ceil(to / bucketMilliseconds) - Math.floor(from / bucketMilliseconds) }, (_, index) => {
        const bucket = (Math.floor(from / bucketMilliseconds) + index) * bucketMilliseconds;
        return { timeUtc: new Date(Math.max(bucket, from)).toISOString(), point: byBucket.get(bucket) };
      }) : [];
  const dimensions = [
    { name: "Read", stall: "readStallMilliseconds", operations: "readOperations" },
    { name: "Write", stall: "writeStallMilliseconds", operations: "writeOperations" },
  ] as const;
  return dimensions.map(dimension => ({
    targetId: trend.targetId,
    label: "SQL file latency",
    metric: `file.${dimension.name.toLowerCase()}.latency`,
    unit: "ms/op",
    state: "observed",
    dimension: dimension.name,
    points: buckets.map(({ timeUtc, point }) => ({
      timeUtc,
      value: point ? average(point[dimension.stall], point[dimension.operations]) : null,
      samples: point?.comparableRows ?? 0,
    })),
  }));
}
