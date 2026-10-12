import type { WaitTrendResponse } from "./waitTrendApi";

export const waitTrendCategories = ["Lock", "I/O", "CPU/signal", "Memory", "Parallelism", "Log", "Other"] as const;

export function waitTrendChartData(trend: WaitTrendResponse) {
  const maximum = BigInt(Number.MAX_SAFE_INTEGER);
  const incomplete = new Set<string>();
  let oversized = 0;
  const series = waitTrendCategories.map(category => ({
    category,
    points: trend.points.filter(point => point.category === category)
      .sort((left, right) => Date.parse(left.bucketStartUtc) - Date.parse(right.bucketStartUtc))
      .map(point => {
        const tooLarge = point.waitMilliseconds !== null && BigInt(point.waitMilliseconds) > maximum;
        if (tooLarge) oversized++;
        if (point.waitMilliseconds === null || tooLarge) incomplete.add(point.bucketStartUtc);
        return {
          timeUtc: point.bucketStartUtc,
          value: point.waitMilliseconds === null || tooLarge ? null : Number(point.waitMilliseconds),
          samples: point.runCount,
        };
      }),
  }));
  const buckets = new Set(trend.points.map(point => point.bucketStartUtc)).size;
  return { series, comparableBuckets: buckets - incomplete.size, totalBuckets: buckets, oversizedValues: oversized };
}
