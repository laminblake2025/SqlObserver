import { useEffect, useId, useMemo, useRef, useState } from "react";
import { cacheRankingTrend, queryStoreRankingTrend, rankingTrendRequestKey, rankingTrendUtc } from "./rankingTrendModel.ts";
import type { RankingTrendPreview, RankingTrendRequest } from "./rankingTrendModel.ts";
import type { QueryPerformanceMetric } from "./queryPerformanceTypes.ts";
import { useRankingTrends } from "./useRankingTrends.tsx";

export function RankingTrend({ request, metric, category = null, label, onInspect, formatTime, unavailableReason }: {
  readonly request?: RankingTrendRequest; readonly metric: QueryPerformanceMetric | "waits"; readonly category?: number | null;
  readonly label: string; readonly onInspect: () => void; readonly formatTime: (value: string) => string; readonly unavailableReason?: string;
}) {
  const queue = useRankingTrends(), wrapper = useRef<HTMLDivElement>(null), detailsId = useId();
  const key = request ? rankingTrendRequestKey(request) : "unavailable";
  const [limit, setLimit] = useState<string>();
  useEffect(() => {
    if (!request || !queue || !wrapper.current || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) { if (!queue.schedule(request)) setLimit(key); observer.disconnect(); }
    }, { rootMargin: "80px" });
    observer.observe(wrapper.current); return () => observer.disconnect();
  }, [key, queue]);
  const state = request && queue ? queue.read(request) : undefined;
  const { preview, invalid } = useMemo((): { readonly preview?: RankingTrendPreview; readonly invalid?: boolean } => {
    if (state?.status !== "ready" || !request) return {};
    try { return { preview: state.value.kind === "query_store" ? queryStoreRankingTrend(state.value.trend, request, metric, category)
      : metric === "waits" ? undefined : cacheRankingTrend(state.value.history, request, metric) }; }
    catch { return { invalid: true }; }
  }, [state, key, metric, category]);
  const count = preview?.segments.reduce((sum, segment) => sum + segment.points.length, 0) ?? 0;
  const values = preview?.segments.flatMap(segment => segment.points.map(point => point.value)) ?? [];
  const high = Math.max(1, ...values);
  const x = (time: string) => request ? 6 + Number(rankingTrendUtc(time) - rankingTrendUtc(request.fromUtc)) / Number(rankingTrendUtc(request.toUtc) - rankingTrendUtc(request.fromUtc)) * 148 : 6;
  const y = (value: number) => 42 - value / high * 34;
  const load = (retry = false) => { if (request && queue && !queue.schedule(request, retry)) setLimit(key); };
  return <div ref={wrapper} className="ranking-trend" role="group" aria-label={`Ranking trend: ${label}`}>
    {!request ? <small>{unavailableReason ?? "Trend unavailable: exact query/plan identity is missing."}</small>
      : state?.status === "error" ? <><small>{state.message}</small><button type="button" onClick={() => load(true)}>Retry row trend</button></>
      : invalid ? <small>Trend unavailable: source evidence did not match the qualified window.</small>
      : !state ? <><small>{limit === key ? "Loaded-page preview bound reached. Inspect this exact query for history." : "Trend loads when visible."}</small><button type="button" onClick={() => load()}>Load row trend</button></>
      : state.status === "queued" || state.status === "loading" ? <small>Loading row trend…</small>
      : preview && <>
        {count > 0 ? <svg viewBox="0 0 160 48" role="img" aria-labelledby={detailsId}><title id={detailsId}>{label}: {preview.unit}. {count} plotted points; gaps and excluded comparisons break lines.</title>
          {preview.segments.map(segment => <g key={segment.id}>
            {segment.points.length > 1 && <path d={segment.points.map((point, index) => `${index === 0 ? "M" : "L"}${x(point.timeUtc).toFixed(3)},${y(point.value).toFixed(3)}`).join(" ")} />}
            {segment.points.map((point, index) => <circle key={`${point.timeUtc}:${index}`} cx={x(point.timeUtc)} cy={y(point.value)} r="2.4" />)}
          </g>)}
        </svg> : <small>No qualified comparable trend points in this loaded history.</small>}
        <small>{preview.unit}{count === 1 ? " · One point; no trend line" : ""}{preview.precisionOmissions ? ` · ${preview.precisionOmissions} values exceed plotting precision` : ""}</small>
        <details><summary>Trend values and coverage</summary><p>{preview.source}.</p><p>{preview.coverage}</p>
          <p>Window: {formatTime(request.fromUtc)} to {formatTime(request.toUtc)}, end excluded. Missing observations are gaps, not zero.</p>
          {preview.values.length > 0 && <div className="ranking-trend-values" tabIndex={0} role="region" aria-label={`${label} trend values`}><ul>{preview.values.map((value, index) => <li key={`${value.timeUtc}:${index}`}>
            {formatTime(value.timeUtc)}{value.endUtc ? ` to ${formatTime(value.endUtc)}` : ""}: {value.exact ?? "Unavailable"} · {value.note}
          </li>)}</ul></div>}
        </details>
      </>}
    <button className="ranking-trend-inspect" type="button" onClick={onInspect}>Inspect trend</button>
  </div>;
}
