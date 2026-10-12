import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { ChartBaselineBand } from "../overview/chartReferenceModel.ts";
import type { OverviewSeries } from "../overview/overviewTypes.ts";

const metrics = [
  { metric: "engine.batch_requests_per_second", label: "SQL workload", unit: "batches/sec", semantics: "Mean batch requests per second from comparable SQL samples." },
  { metric: "engine.sql_scheduler_cpu_percent", label: "SQL scheduler CPU", unit: "%", semantics: "Mean visible online SQL scheduler capacity used by non-preemptive workers; not total SQL process CPU." },
  { metric: "engine.memory_grants_pending", label: "SQL memory grants pending", unit: "grants", semantics: "Mean point-in-time count of queries waiting for execution memory grants." },
  { metric: "engine.os_available_memory_bytes", label: "OS available memory via SQL", unit: "GiB", semantics: "Mean available OS memory reported by SQL; separate from SQL process used memory and collector-host memory." },
] as const;

type ObservationState = "available" | "partial" | "missing" | "invalid" | "ambiguous";
type ReferenceState = "available" | "missing" | "invalid" | "ambiguous";
export interface DashboardBaselineCard {
  readonly metric: string; readonly label: string; readonly unit: string; readonly semantics: string;
  readonly observationState: ObservationState; readonly observationNote: string;
  readonly referenceState: ReferenceState; readonly referenceNote: string;
  readonly observation?: { readonly timeUtc: string; readonly value: number | null; readonly samples: number; readonly sourceState: string };
  readonly reference?: ChartBaselineBand;
  readonly referenceQuality?: "complete" | "partial" | "unknown";
}
export interface DashboardBaselineInput {
  readonly targetId: string; readonly series: readonly OverviewSeries[]; readonly bands: readonly ChartBaselineBand[];
  readonly fromUtc: string; readonly toUtc: string;
}
const utc = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
const hour = 3_600_000_000n;
const hourStart = (time: bigint) => time / hour * hour - (time % hour < 0n ? hour : 0n);
const nonnegative = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value) && value >= 0;

/** Compare loaded buckets only; source labels remain intact, without inferring their coverage. */
export function dashboardBaselineCards(input: DashboardBaselineInput): readonly DashboardBaselineCard[] {
  let from: bigint, to: bigint;
  try {
    from = utc(input.fromUtc); to = utc(input.toUtc);
    if (to <= from) throw new Error("Invalid window.");
  } catch {
    return metrics.map(metric => ({ ...metric, observationState: "invalid", observationNote: "The loaded UTC window is invalid.",
      referenceState: "missing", referenceNote: "No matching hourly reference." }));
  }
  return metrics.map(metric => {
    const base: DashboardBaselineCard = { ...metric, observationState: "missing", observationNote: "No matching SQL series in this window.",
      referenceState: "missing", referenceNote: "No matching hourly reference." };
    const series = input.series.filter(item => item.targetId.toLowerCase() === input.targetId.toLowerCase() &&
      item.metric === metric.metric && item.unit === metric.unit && item.dimension === null);
    if (series.length > 1) return { ...base, observationState: "ambiguous", observationNote: "Matching SQL series are duplicated; no value was selected." };
    const source = series[0];
    if (!source) return base;
    let points: { time: bigint; point: OverviewSeries["points"][number] }[];
    try {
      if (!Array.isArray(source.points) || source.points.length > 1000) throw new Error("Invalid points.");
      points = source.points.map(point => ({ point, time: utc(point.timeUtc) })).filter(point => point.time >= from && point.time < to);
    } catch { return { ...base, observationState: "invalid", observationNote: "SQL bucket timestamps or count are invalid; no older bucket was substituted." }; }
    points.sort((left, right) => left.time < right.time ? 1 : left.time > right.time ? -1 : 0);
    const latest = points[0];
    if (!latest) return { ...base, observationNote: "No SQL bucket was observed inside this window." };
    if (points[1]?.time === latest.time) return { ...base, observationState: "ambiguous", observationNote: "The latest SQL bucket is duplicated; no value was selected." };
    const point = latest.point;
    if (!Number.isSafeInteger(point.samples) || point.samples < 0 || point.value !== null && !nonnegative(point.value))
      return { ...base, observationState: "invalid", observationNote: "The latest SQL bucket is invalid; no older bucket was substituted." };
    const usable = point.value !== null && point.samples > 0 && ["observed", "current", "complete", "partial"].includes(source.state);
    const observationState: ObservationState = usable ? source.state === "partial" ? "partial" : "available" : "missing";
    const observation = { timeUtc: point.timeUtc, value: usable ? point.value : null, samples: point.samples, sourceState: source.state };
    const current: DashboardBaselineCard = { ...base, observation, observationState,
      observationNote: usable ? source.state === "partial" ? "Partial SQL observations; this mean describes only available samples." : "Latest observed bucket mean."
        : "Latest bucket has no usable samples." };
    const references = input.bands.filter(band => band.targetId.toLowerCase() === input.targetId.toLowerCase() &&
      band.metric === metric.metric && band.unit === metric.unit && band.dimension === null);
    const matches: ChartBaselineBand[] = [];
    for (const band of references) {
      try {
        const start = utc(band.fromUtc), end = utc(band.toUtc);
        if (end <= start) return { ...current, referenceState: "invalid", referenceNote: "Matching reference bounds are invalid." };
        if (latest.time < start || latest.time >= end) continue;
        if (start < from || end > to || start < hourStart(latest.time) || end > hourStart(latest.time) + hour ||
            !nonnegative(band.low) || !nonnegative(band.median) || !nonnegative(band.high) || band.low > band.median || band.median > band.high ||
            typeof band.label !== "string" || !band.label.trim() || band.label.length > 4096)
          return { ...current, referenceState: "invalid", referenceNote: "The matching hourly reference is invalid; no alternative was substituted." };
        for (const value of [band.coverage, band.confidence]) if (value !== undefined && (!nonnegative(value) || value > 1))
          return { ...current, referenceState: "invalid", referenceNote: "Hourly reference quality metadata is invalid." };
        if ((band.sourceFromUtc === undefined) !== (band.sourceToUtc === undefined) || band.sourceFromUtc !== undefined &&
            (utc(band.sourceToUtc!) <= utc(band.sourceFromUtc) || utc(band.sourceToUtc!) > from))
          return { ...current, referenceState: "invalid", referenceNote: "Hourly reference source dates do not precede the loaded window." };
        matches.push(band);
      } catch { return { ...current, referenceState: "invalid", referenceNote: "Matching reference timestamps are invalid." }; }
    }
    if (matches.length > 1) return { ...current, referenceState: "ambiguous", referenceNote: "Matching hourly references overlap; no reference was selected." };
    const reference = matches[0];
    if (!reference) return current;
    if (reference.visibilityState === "unavailable") return { ...current, referenceNote: "Hourly reference source is unavailable." };
    const referenceQuality = reference.visibilityState === "partial" || reference.coverage !== undefined && reference.coverage < 1 ? "partial"
      : reference.visibilityState === "complete" && reference.coverage !== undefined && reference.confidence !== undefined && reference.sourceFromUtc !== undefined ? "complete" : "unknown";
    return { ...current, reference, referenceQuality, referenceState: "available",
      referenceNote: "Descriptive hourly reference. Source coverage, confidence and dates remain in the source details." };
  });
}
