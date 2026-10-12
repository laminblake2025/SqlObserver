import { createRoot } from "react-dom/client";
import { ChartEvidenceIndicator } from "../src/components/CompactEvidenceStatus";
import type { OverviewSeries } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const scenario = new URLSearchParams(location.search).get("scenario") ?? "unavailable";
const received = [
  { timeUtc: "2026-10-03T14:00:00Z", value: 0, samples: 1 },
  { timeUtc: "2026-10-03T14:01:00Z", value: 12, samples: 1 },
];
const missing = [
  { timeUtc: "2026-10-03T14:00:00Z", value: null, samples: 0 },
  { timeUtc: "2026-10-03T14:01:00Z", value: null, samples: 0 },
];
const series: readonly OverviewSeries[] = scenario === "absent" ? [] : [{
  targetId: "11111111-1111-4111-8111-111111111111", metric: "engine.sql_scheduler_cpu_percent",
  label: "SQL scheduler CPU", unit: "percent", dimension: null,
  state: scenario === "unavailable-received" ? "unavailable" : scenario,
  points: ["unavailable-received", "stale", "complete"].includes(scenario) ? received : missing,
}];
createRoot(document.getElementById("root")!).render(<main className="component-harness">
  <h1>Compact evidence harness</h1>
  <section className="harness-chart-panel" aria-label="SQL scheduler CPU evidence">
    <h2>SQL scheduler CPU</h2><ChartEvidenceIndicator label="SQL scheduler CPU" series={series} />
  </section>
</main>);
