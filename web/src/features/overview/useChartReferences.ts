import { useEffect, useState } from "react";
import { getBaselineWindow, getSqlBaselineWindow } from "../analytics/analyticsApi.ts";
import { sqlBaselineMetrics } from "../analytics/sqlBaselineModel.ts";
import { readChartRuleThresholds } from "./chartReferenceApi.ts";
import { baselineBandsNeedAttention, baselineLookupWindow, memoryBaselineBands, sqlBaselineBands, type ChartBaselineBand, type ChartReferences } from "./chartReferenceModel.ts";

export function useChartReferences(targetId: string, revision: number, window: { fromUtc: string; toUtc: string } | undefined, canReadRules: boolean, refresh: number, visibleMetrics: readonly string[] = []) {
  const metrics = [...new Set(visibleMetrics)].filter(metric => sqlBaselineMetrics[metric]).sort();
  const key = JSON.stringify([targetId.toLowerCase(), revision, window?.fromUtc, window?.toUtc, canReadRules, refresh, metrics]);
  const [result, setResult] = useState<{ key: string; references: ChartReferences; baselineStatus: string; ruleStatus: string; baselineAttention: boolean; ruleAttention: boolean }>();
  useEffect(() => {
    if (!window) return;
    const controller = new AbortController();
    let references: ChartReferences = { bands: [], thresholds: [] };
    let hostBands: readonly ChartBaselineBand[] = [], sqlBands: readonly ChartBaselineBand[] = [];
    let hostStatus = "Loading collector-host memory baseline…";
    let sqlStatus = metrics.length ? "Loading SQL baseline references…" : "";
    let ruleStatus = canReadRules ? "Loading current rule thresholds…" : "Target Administrator access unlocks current rule threshold lines.";
    let hostAttention = true, sqlAttention = metrics.length > 0, ruleAttention = true;
    const publish = () => { if (!controller.signal.aborted) setResult({ key, references: { ...references, bands: [...hostBands, ...sqlBands] }, baselineStatus: `${hostStatus} ${sqlStatus}`.trim(), ruleStatus, baselineAttention: hostAttention || sqlAttention, ruleAttention }); };
    publish();
    void getBaselineWindow(targetId, "host.memory.available_bytes", baselineLookupWindow(window.fromUtc), controller.signal)
      .then(rows => {
        hostBands = memoryBaselineBands(targetId, rows, window.fromUtc, window.toUtc);
        hostAttention = baselineBandsNeedAttention(hostBands, window.fromUtc, window.toUtc);
        hostStatus = hostBands.length
          ? "Shaded band: collector-host memory p10–p90 by UTC hour of week. Its source window ends before this selection. Confidence and source dates are available in the reference details. This is a descriptive comparison, not a healthy range or proof of what was known at Rewind time."
          : "No complete collector-host memory baseline covers these UTC hours.";
        publish();
      })
      .catch(() => { hostStatus = "Collector-host memory baseline is unavailable. Check analytics collection and derivation, then refresh."; publish(); });
    if (metrics.length) void Promise.allSettled(metrics.map(async metric => {
      const page = await getSqlBaselineWindow(targetId, revision, metric, baselineLookupWindow(window.fromUtc), controller.signal);
      return sqlBaselineBands(page, window.fromUtc, window.toUtc);
    })).then(results => {
      sqlBands = results.flatMap(result => result.status === "fulfilled" ? result.value : []);
      const failures = results.filter(result => result.status === "rejected").length;
      sqlAttention = failures > 0 || results.some(result => result.status === "fulfilled" && baselineBandsNeedAttention(result.value, window.fromUtc, window.toUtc));
      sqlStatus = sqlBands.length
        ? `SQL p10–p90 bands use matching hourly history, with coverage and confidence in each reference. Partial history remains labelled. These are descriptive comparisons, not healthy ranges or proof of what was known at Rewind time.${failures ? ` ${failures} SQL metric reference reads failed; check access and derivation, then refresh.` : ""}`
        : failures ? "SQL baseline references are unavailable. Check target access and analytics derivation, then refresh."
          : "No SQL baseline covers these UTC hours. Enable SQL core collection and retain complete hourly history for the preceding 28 UTC days.";
      publish();
    });
    if (canReadRules) void readChartRuleThresholds(targetId, controller.signal).then(page => {
      references = { ...references, thresholds: page.thresholds };
      ruleAttention = page.truncated || page.thresholds.length === 0;
      ruleStatus = `Dashed threshold lines use enabled metric rules read now, including in Rewind. They do not establish historical rule settings or an alert verdict; confirmation and clear settings still apply.${page.truncated ? " Only the first four rule pages were read (up to 400 rules); more rules may exist." : references.thresholds.length ? "" : " No matching enabled metric thresholds were returned."}`;
      publish();
    }).catch(() => { ruleStatus = "Current rule thresholds are unavailable. Check Target Administrator access and refresh."; publish(); });
    return () => controller.abort();
  }, [key]);
  return result?.key === key ? result : { references: { bands: [], thresholds: [] } as ChartReferences,
    baselineStatus: window ? "Loading collector-host memory baseline…" : "",
    baselineAttention: Boolean(window), ruleAttention: Boolean(window),
    ruleStatus: window ? canReadRules ? "Loading current rule thresholds…" : "Target Administrator access unlocks current rule threshold lines." : "" };
}
