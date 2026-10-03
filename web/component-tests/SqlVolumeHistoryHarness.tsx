import { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext.tsx";
import { SqlVolumeHistoryPanel } from "../src/features/resources/SqlVolumeHistoryPanel.tsx";
import { SqlVolumePanel } from "../src/features/resources/SqlVolumePanel.tsx";
import { readOverviewScope, overviewWindow } from "../src/features/overview/overviewModel.ts";
import { readResourceSelection, resourceSelectionHref } from "../src/features/resources/resourceSelectionModel.ts";
import "../src/styles.css";
import "./harness.css";
import { canonicalUtcTimestamp, utcMicroseconds } from "../src/utcTimestamp.ts";
const initial = "#/resources?target=11111111-1111-4111-8111-000000000001&range=custom&from=2026-10-02T10%3A00%3A00.123456Z&to=2026-10-02T11%3A00%3A00.123456Z";
function Harness() {
  const [hash,setHash] = useState(location.hash || initial), [revision,setRevision] = useState(1), [refresh,setRefresh] = useState(0), [mounted,setMounted] = useState(true), [crosshair,setCrosshair] = useState<string | null>(null);
  const [nativeBounds,setNativeBounds] = useState(false);
  const { mode,setMode } = useTimeDisplay();
  useEffect(() => { if (!location.hash) location.hash = initial; const update = () => setHash(location.hash); addEventListener("hashchange",update); return () => removeEventListener("hashchange",update); },[]);
  const scope = readOverviewScope(hash), selection = readResourceSelection(hash,scope.target);
  const window = useMemo(() => overviewWindow(scope,Date.now()),[hash,refresh]);
  const equivalentUtc = (value: string) => value.replace(/\.(\d+?)0*Z$/u,".$1+00:00");
  const receivedWindow = nativeBounds ? {fromUtc:equivalentUtc(window.fromUtc),toUtc:equivalentUtc(window.toUtc)} : window;
  const shiftMicrosecond = (value: string) => { const ticks=utcMicroseconds(value)+1n; return canonicalUtcTimestamp(new Date(Number(ticks/1000n)).toISOString().slice(0,19)+"."+String(ticks%1000000n).padStart(6,"0")+"Z"); };
  const navigate = (href: string) => { location.hash = href; setHash(location.hash); };
  const update = (change: Partial<typeof scope>) => navigate(resourceSelectionHref({...scope,...change},selection));
  return <main className="component-harness"><h1>SQL volume history harness</h1><fieldset className="harness-controls"><legend>Context</legend>
    <label>Selected server<select aria-label="Selected server" value={scope.target} onChange={event => navigate(resourceSelectionHref({...scope,target:event.target.value},{}))}><option value="11111111-1111-4111-8111-000000000001">Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option></select></label>
    <label>Current target revision<select aria-label="Current target revision" value={revision} onChange={event=>setRevision(Number(event.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
    <label>Time context<select aria-label="Time context" value={scope.range==='custom'?'rewind':'live'} onChange={event=>update(event.target.value==='live'?{range:'1h',from:undefined,to:undefined}:{range:'custom',from:'2026-10-02T10:00:00.123456Z',to:'2026-10-02T11:00:00.123456Z'})}><option value="rewind">Rewind</option><option value="live">Live</option></select></label>
    <label className="harness-checkbox"><input type="checkbox" checked={scope.compare} onChange={event=>update({compare:event.target.checked})} />Compare previous window</label>
    <label>Time display<select aria-label="Time display" value={mode} onChange={event=>setMode(event.target.value==='utc'?'utc':'local')}><option value="local">Local</option><option value="utc">UTC</option></select></label>
    <button type="button" onClick={()=>update({range:'custom',from:'2026-10-02T13:00:00.123456Z',to:'2026-10-02T14:00:00.123456Z'})}>Later shared window</button><button type="button" onClick={()=>update({range:'custom',from:'2026-10-02T10:00:00.123456Z',to:'2026-10-02T11:00:00.123456Z'})}>Initial shared window</button>
    <button type="button" onClick={()=>setRefresh(n=>n+1)}>Advance workspace poll</button><button type="button" onClick={()=>setCrosshair(new Date((Date.parse(window.fromUtc)+Date.parse(window.toUtc))/2).toISOString())}>Align completion time</button><output aria-label="Shared crosshair">{crosshair ?? 'None'}</output>
    <button type="button" onClick={()=>setNativeBounds(value=>!value)}>Use equivalent UTC spelling</button><output aria-label="Passed history bounds">{receivedWindow.fromUtc} / {receivedWindow.toUtc}</output>
    <button type="button" onClick={()=>update({range:'custom',from:shiftMicrosecond(window.fromUtc),to:shiftMicrosecond(window.toUtc)})}>Advance shared window one microsecond</button>
    <button type="button" onClick={()=>setMounted(v=>!v)}>{mounted?'Unmount volume history':'Mount volume history'}</button>
  </fieldset><h2>Resources</h2><SqlVolumePanel instanceId={scope.target} targetRevision={revision} refresh={refresh} isRewind={scope.range==='custom'} />
    {mounted && <SqlVolumeHistoryPanel instanceId={scope.target} targetRevision={revision} {...receivedWindow} compare={scope.compare} refresh={refresh}
      selectedVolume={selection.invalidVolume || selection.invalidContext ? null : selection.volume} onSelectVolume={volume=>navigate(resourceSelectionHref(scope,{...selection,volume,invalidVolume:undefined}))}
      crosshairUtc={crosshair} onCrosshairChange={setCrosshair} onSelectWindow={selected=>update({range:'custom',from:selected.fromUtc,to:selected.toUtc})} />}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
