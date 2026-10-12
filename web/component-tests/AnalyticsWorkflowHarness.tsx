import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { AnalyticsPage } from "../src/features/analytics/AnalyticsPage";
import { AnalyticsSurfacePanel } from "../src/features/analytics/AnalyticsSurfacePanel";
import { analyticsSurfaceCatalog } from "../src/features/analytics/analyticsSurfaceCatalog";
import type { AnalyticsSurface } from "../src/features/analytics/analyticsTypes";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const pageMode = new URLSearchParams(location.search).get("page") === "1";
  const [target, setTarget] = useState(alpha), [revision, setRevision] = useState(1), [surface, setSurface] = useState<AnalyticsSurface>("jobs");
  const [refresh, setRefresh] = useState(0), [windowVersion, setWindowVersion] = useState(0), [liveStep, setLiveStep] = useState(0), [mounted, setMounted] = useState(true);
  const [commit, setCommit] = useState<object>();
  const start = Date.parse(windowVersion % 2 ? "2026-10-01T10:00:00.123Z" : "2026-10-02T10:00:00.123Z") + liveStep * 300000;
  const timeWindow = { fromUtc: new Date(start).toISOString(), toUtc: new Date(start + 3600000).toISOString() };
  const contextKey = JSON.stringify([target, revision, windowVersion]);
  const identity = JSON.stringify([contextKey, surface, mounted]); const previous = useRef(identity);
  useLayoutEffect(() => {
    if (previous.current === identity) return; previous.current = identity;
    setCommit({ target, revision, surface, text: document.querySelector(".analytics-workflow-owner")?.textContent ?? "" });
  }, [identity, target, revision, surface]);
  useEffect(() => {
    const keys = (event: KeyboardEvent) => {
      if (event.key === "F2") { event.preventDefault(); setTarget(value => value === alpha ? beta : alpha); }
      else if (event.key === "F3") { event.preventDefault(); setRevision(value => value + 1); }
      else if (event.key === "F5") { event.preventDefault(); setWindowVersion(value => value + 1); }
      else if (event.key === "F6") { event.preventDefault(); setRefresh(value => value + 1); }
      else if (event.key === "F7") { event.preventDefault(); setLiveStep(value => value + 1); setRefresh(value => value + 1); }
      else if (event.key === "F8") { event.preventDefault(); setMounted(value => !value); }
    }; window.addEventListener("keydown", keys); return () => window.removeEventListener("keydown", keys);
  }, []);
  const scope: OverviewScope = { target, range: "custom", compare: false, from: timeWindow.fromUtc, to: timeWindow.toUtc };
  return <main className="component-harness" style={{ maxWidth: 1200 }}>
    <h1>Analytics workflow harness</h1>
    <p>Native synthetic receipts. F2 changes target, F3 revision, F5 window, F6 refresh, F7 the moving live window, and F8 mounting.</p>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      {!pageMode && <label>Evidence surface<select value={surface} onChange={event => setSurface(event.target.value as AnalyticsSurface)}>{analyticsSurfaceCatalog.map(item => <option key={item.value} value={item.value}>{item.label}</option>)}</select></label>}
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh workspace</button>
    </div>
    <div className="analytics-workflow-owner">
      {mounted && (pageMode ? <AnalyticsPage targetId={target} targetRevision={revision} scope={scope} refresh={refresh} surface={surface} onSurfaceChange={setSurface} /> :
        analyticsSurfaceCatalog.map(item => <AnalyticsSurfacePanel key={item.value} targetId={target} targetRevision={revision} surface={item.value} timeWindow={timeWindow} contextKey={contextKey} refresh={refresh} active={surface === item.value} />))}
    </div>
    {commit && <output className="harness-observations" aria-label="First committed analytics context">{JSON.stringify(commit)}</output>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
