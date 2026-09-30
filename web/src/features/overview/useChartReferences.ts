import { useEffect, useState } from "react";
import { getBaselineWindow } from "../analytics/analyticsApi.ts";
import { readChartRuleThresholds } from "./chartReferenceApi.ts";
import { baselineLookupWindow, memoryBaselineBands, type ChartReferences } from "./chartReferenceModel.ts";

export function useChartReferences(targetId: string, revision: number, window: { fromUtc: string; toUtc: string } | undefined, canReadRules: boolean, refresh: number) {
  const key = JSON.stringify([targetId.toLowerCase(), revision, window?.fromUtc, window?.toUtc, canReadRules, refresh]);
  const [result, setResult] = useState<{ key: string; references: ChartReferences; baselineStatus: string; ruleStatus: string }>();
  useEffect(() => {
    if (!window) return;
    const controller = new AbortController();
    let references: ChartReferences = { bands: [], thresholds: [] };
    let baselineStatus = "Loading collector-host memory baseline…";
    let ruleStatus = canReadRules ? "Loading current rule thresholds…" : "Target Administrator access unlocks current rule threshold lines.";
    const publish = () => { if (!controller.signal.aborted) setResult({ key, references, baselineStatus, ruleStatus }); };
    publish();
    void getBaselineWindow(targetId, "host.memory.available_bytes", baselineLookupWindow(window.fromUtc), controller.signal)
      .then(rows => {
        references = { ...references, bands: memoryBaselineBands(targetId, rows, window.fromUtc, window.toUtc) };
        baselineStatus = references.bands.length
          ? "Shaded band: collector-host memory p10–p90 by UTC hour of week. Its source window ends before this selection. Confidence and source dates are available in the reference details. This is a descriptive comparison, not a healthy range or proof of what was known at Rewind time."
          : "No complete, dimension-matched collector-host memory baseline covers these UTC hours. Other SQL metrics have no baseline band.";
        publish();
      })
      .catch(() => { baselineStatus = "Collector-host memory baseline is unavailable. Check analytics collection and derivation, then refresh."; publish(); });
    if (canReadRules) void readChartRuleThresholds(targetId, controller.signal).then(page => {
      references = { ...references, thresholds: page.thresholds };
      ruleStatus = `Dashed threshold lines use enabled metric rules read now, including in Rewind. They do not establish historical rule settings or an alert verdict; confirmation and clear settings still apply.${page.truncated ? " Only the first four rule pages were read (up to 400 rules); more rules may exist." : references.thresholds.length ? "" : " No matching enabled metric thresholds were returned."}`;
      publish();
    }).catch(() => { ruleStatus = "Current rule thresholds are unavailable. Check Target Administrator access and refresh."; publish(); });
    return () => controller.abort();
  }, [key]);
  return result?.key === key ? result : { references: { bands: [], thresholds: [] } as ChartReferences,
    baselineStatus: window ? "Loading collector-host memory baseline…" : "",
    ruleStatus: window ? canReadRules ? "Loading current rule thresholds…" : "Target Administrator access unlocks current rule threshold lines." : "" };
}
