import {useEffect,useState} from "react";
import {createRoot} from "react-dom/client";
import {TimeDisplayProvider} from "../src/TimeDisplayContext.tsx";
import {readOverviewScope,overviewWindow} from "../src/features/overview/overviewModel.ts";
import {readResourceSelection,resourceSelectionHref,preserveInvalidCpuObservationHref} from "../src/features/resources/resourceSelectionModel.ts";
import {previousCpuObservationWindow,cpuObservationWindow,type CpuObservationBinding,type CpuObservationSelection} from "../src/features/resources/cpuObservationHistoryModel.ts";
import {useCpuObservationHistory} from "../src/features/resources/useCpuObservationHistory.ts";
import {CpuObservationHistoryPanel} from "../src/features/resources/CpuObservationHistoryPanel.tsx";
import {CpuCollectionControl} from "../src/features/resources/CpuCollectionControl.tsx";
import "../src/styles.css";
import "./harness.css";
const alpha="11111111-1111-4111-8111-000000000001";
function Content({binding,selection,refresh,access,error,pin}:{binding?:CpuObservationBinding;selection:ReturnType<typeof readResourceSelection>;refresh:number;access:string;error?:string;pin:(value?:CpuObservationSelection,retain?:boolean)=>void}){
 const history=useCpuObservationHistory(binding,selection.invalidContext||selection.invalidCpuObservation?null:selection.cpuObservation,refresh,access,binding?{...binding,...previousCpuObservationWindow(binding.fromUtc,binding.toUtc)}:undefined);
 return <><CpuCollectionControl targetId={binding?.targetId??alpha} targetRevision={binding?.targetRevision??1} canManage={access==="grant-a"} accessContext={access}/><CpuObservationHistoryPanel comparison fromUtc={binding?.fromUtc} toUtc={binding?.toUtc} history={history} windowError={error} selectionInvalid={selection.invalidContext||selection.invalidCpuObservation} explicitSelection={selection.cpuObservation} onSelectObservation={value=>pin(value)} onRetainWindow={()=>pin(history.selected??undefined,true)}/></>;
}
function Harness(){
 const [hash,setHash]=useState(location.hash),[revision,setRevision]=useState(1),[access,setAccess]=useState("grant-a"),[refresh,setRefresh]=useState(0),[poll,setPoll]=useState(0),[mounted,setMounted]=useState(true),[equivalent,setEquivalent]=useState(false);
 useEffect(()=>{const update=()=>setHash(location.hash);window.addEventListener("hashchange",update);return()=>window.removeEventListener("hashchange",update);},[]);
 const raw=readOverviewScope(hash),scope={...raw,target:raw.target||alpha},selection=readResourceSelection(hash,scope.target);
 let resolvedWindow:{fromUtc:string;toUtc:string}|undefined,error:string|undefined;try{const range=overviewWindow(scope,Date.now());resolvedWindow=cpuObservationWindow(range.fromUtc,range.toUtc);}catch(failure){error=failure instanceof Error?failure.message:"Invalid shared window";}
 const binding=resolvedWindow&&!selection.invalidContext?{targetId:scope.target,targetRevision:revision,...resolvedWindow,...(equivalent?{fromUtc:resolvedWindow.fromUtc.replace('Z','0+00:00'),toUtc:resolvedWindow.toUtc.replace('Z','0+00:00')}:{})}:undefined;
 function change(params:URLSearchParams){location.hash=`resources?${params}`;setHash(location.hash);}
 function query(key:string,value:string){const params=new URLSearchParams(location.hash.split('?')[1]);params.set(key,value);change(params);}
 function pin(value?:CpuObservationSelection,retain=false){if(!resolvedWindow)return;const next={...selection,cpuObservation:retain&&selection.invalidCpuObservation?undefined:value};let href=resourceSelectionHref({...scope,range:"custom",from:resolvedWindow.fromUtc,to:resolvedWindow.toUtc},next);if(retain)href=preserveInvalidCpuObservationHref(href,location.hash);location.hash=href;setHash(location.hash);}
 return <main className="component-harness" style={{maxWidth:1280}}><h1>CPU component observation harness</h1><div className="harness-controls">
  <label>Selected server<select value={scope.target} onChange={e=>query('target',e.target.value)}><option value={alpha}>Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option></select></label>
  <label>Target revision<select value={revision} onChange={e=>setRevision(Number(e.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
  <label>Authority context<select value={access} onChange={e=>setAccess(e.target.value)}><option value="grant-a">Original grant</option><option value="grant-b">Changed grant</option></select></label>
  <button type="button" onClick={()=>setPoll(poll+1)}>Advance workspace poll</button><output aria-label="Workspace poll">{poll}</output>
  <button type="button" onClick={()=>setRefresh(refresh+1)}>Refresh workspace</button>
  <button type="button" onClick={()=>query('to','2026-10-02T11:00:00.123457Z')}>Change shared window one microsecond</button>
  <button type="button" onClick={()=>query('to','2026-10-02T11:00:00.123456Z')}>Restore shared window</button>
  <button type="button" onClick={()=>setEquivalent(!equivalent)}>Toggle equivalent UTC spelling</button>
  <button type="button" onClick={()=>query('from','2026-08-01T00:00:00Z')}>Use invalid long window</button>
  <button type="button" onClick={()=>setMounted(!mounted)}>{mounted?"Unmount CPU history":"Mount CPU history"}</button>
 </div><h2>Resources</h2>{mounted&&<Content binding={binding} selection={selection} refresh={refresh} access={access} error={error} pin={pin}/>}</main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
