import { createRoot } from "react-dom/client";
import { useEffect, useRef, useState } from "react";
import { TimeDisplayProvider } from "../src/TimeDisplayContext.tsx";
import { ChartAlignmentControls } from "../src/components/ChartAlignmentControls.tsx";
import { chartInspectionHref, readChartInspection } from "../src/components/chartInspectionModel.ts";
import { useChartInspection } from "../src/components/useChartInspection.ts";
import { OverviewChart } from "../src/features/overview/OverviewChart.tsx";
import "../src/styles.css";
const alpha="11111111-1111-4111-8111-000000000001",beta="11111111-1111-4111-8111-000000000002";
const fromUtc="2026-09-26T08:00:00.000001Z",toUtc="2026-09-26T12:00:00.000001Z";
const initial=`#/health?target=${alpha}&range=custom&from=${fromUtc}&to=${toUtc}&databaseId=6&compare=1`;
function Harness() {
  const [hash,setHash]=useState(location.hash||initial),[revision,setRevision]=useState(1),[nativeBounds,setNativeBounds]=useState(false),[nativeEvent,setNativeEvent]=useState(false);
  const loadedWindow=nativeBounds?{fromUtc:'2026-09-26T08:00:00.0000010+00:00',toUtc:'2026-09-26T12:00:00.0000010+00:00'}:{fromUtc,toUtc};
  useEffect(()=>{if(!location.hash)location.hash=initial;const receive=()=>setHash(location.hash);window.addEventListener("hashchange",receive);return()=>window.removeEventListener("hashchange",receive);},[]);
  const parameters=new URLSearchParams(hash.split('?')[1]);parameters.delete('chartAt');
  const controlsContext=JSON.stringify([String(parameters),revision]),context=JSON.stringify([hash,revision]),requested=readChartInspection(hash);
  const inspection=useChartInspection(context,loadedWindow.fromUtc,loadedWindow.toUtc,requested,nativeEvent?'2026-09-26T10:00:00.1234560+00:00':undefined);
  const held=useRef<((utc:string|null)=>void)|undefined>(undefined);
  const target=new URLSearchParams(hash.split('?')[1]).get('target')??alpha;
  const changeTarget=()=>{const q=new URLSearchParams(hash.split('?')[1]);q.set('target',target===alpha?beta:alpha);q.delete('chartAt');location.hash='#/health?'+q;};
  const series=[{targetId:target,label:'Loaded SQL workload',metric:'engine.batch_requests_per_second',unit:'requests/sec',state:'fresh',dimension:null,
    points:[{timeUtc:'2026-09-26T09:00:00Z',value:5,samples:1},{timeUtc:'2026-09-26T11:00:00Z',value:10,samples:1}]}];
  return <main className="content" style={{margin:0,maxWidth:1000}}><h1>Chart alignment harness</h1>
    <button onClick={changeTarget}>Change target</button><button onClick={()=>setRevision(value=>value+1)}>Change revision</button>
    <button onClick={()=>{held.current=inspection.setCrosshairUtc;}}>Capture pointer callback</button>
    <button onClick={()=>held.current?.('2026-09-26T10:00:00Z')}>Deliver held callback</button>
    <button onClick={()=>setNativeBounds(true)}>Use native UTC bounds</button>
    <button onClick={()=>setNativeEvent(true)}>Use native event alignment</button>
    <ChartAlignmentControls key={controlsContext} context={controlsContext} label="Dashboard" {...loadedWindow}
      requestedUtc={requested} crosshairUtc={inspection.crosshairUtc}
      onAlign={utc=>{inspection.setCrosshairUtc(null);location.hash=chartInspectionHref(hash,utc);}}
      onClear={()=>{inspection.setCrosshairUtc(null);location.hash=chartInspectionHref(hash);}} />
    <section className="panel" style={{padding:20}} aria-label="Primary chart"><h2>Loaded SQL workload</h2>
      <OverviewChart series={series} {...loadedWindow} scrollablePlotLabel="Loaded SQL workload plot" crosshairUtc={inspection.crosshairUtc} onCrosshairChange={inspection.setCrosshairUtc}/></section>
    <section className="panel" style={{padding:20}} aria-label="Companion chart"><h2>Companion workload</h2>
      <OverviewChart series={series} {...loadedWindow} scrollablePlotLabel="Companion workload plot" crosshairUtc={inspection.crosshairUtc}/></section>
  </main>;
}
createRoot(document.getElementById('root')!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
