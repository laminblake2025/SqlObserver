import {useEffect,useLayoutEffect,useState} from "react";
import {createRoot} from "react-dom/client";
import {TimeDisplayProvider} from "../src/TimeDisplayContext";
import {readOverviewScope,overviewWindow} from "../src/features/overview/overviewModel";
import {readResourceSelection,resourceSelectionHref,preserveInvalidFileSnapshotHref} from "../src/features/resources/resourceSelectionModel";
import {fileHistoryWindow,type FileSnapshotSelection,type FileHistoryBinding} from "../src/features/resources/databaseFileWindowHistoryModel";
import {useDatabaseFileWindowHistory} from "../src/features/resources/useDatabaseFileWindowHistory";
import {DatabaseFileWindowHistoryPanel} from "../src/features/resources/DatabaseFileWindowHistoryPanel";
import "../src/styles.css";
import "./harness.css";
const alpha="11111111-1111-4111-8111-000000000001";
declare global{interface Window{fileHistoryCommits:string[]}}
function HistoryContent({binding,selection,refresh,access,error,pin}:{binding?:FileHistoryBinding;selection:ReturnType<typeof readResourceSelection>;refresh:number;access:string;error?:string;pin:(snapshot?:FileSnapshotSelection,file?:{databaseId:number;fileId:number},clear?:boolean)=>void}){
 const history=useDatabaseFileWindowHistory(binding,selection.invalidFileSnapshot||selection.invalidContext?null:selection.fileSnapshot,refresh,access);
 useLayoutEffect(()=>{window.fileHistoryCommits??=[];window.fileHistoryCommits.push(document.querySelector('[aria-label="Selected file snapshot"]')?.textContent??"");});
 return <DatabaseFileWindowHistoryPanel history={history} windowError={error} selectionInvalid={selection.invalidFileSnapshot||selection.invalidContext}
 explicitSelection={selection.fileSnapshot} selectedFile={selection.file}
 onSelectReceipt={snapshot=>pin(snapshot,undefined,snapshot===undefined)} onStartInvestigation={snapshot=>pin(snapshot)} onSelectFile={(file,snapshot)=>pin(snapshot,file)}/>;
}
function Harness(){
 const [hash,setHash]=useState(location.hash),[revision,setRevision]=useState(1),[poll,setPoll]=useState(0),[refresh,setRefresh]=useState(0),[access,setAccess]=useState("grant-a"),[mounted,setMounted]=useState(true);
 useEffect(()=>{const change=()=>setHash(location.hash);window.addEventListener("hashchange",change);return()=>window.removeEventListener("hashchange",change);},[]);
 const parsed=readOverviewScope(hash),scope={...parsed,target:parsed.target||alpha},selection=readResourceSelection(hash,scope.target);
 let resolvedWindow:{fromUtc:string;toUtc:string}|undefined,error:string|undefined;try{const bounds=overviewWindow(scope,Date.now());resolvedWindow=fileHistoryWindow(bounds.fromUtc,bounds.toUtc);}catch(failure){error=failure instanceof Error?failure.message:"Invalid window";}
 const binding=resolvedWindow?{targetId:scope.target,targetRevision:revision,...resolvedWindow}:undefined;
 function query(key:string,value:string){const p=new URLSearchParams(location.hash.split('?')[1]);p.set(key,value);location.hash=`resources?${p}`;setHash(location.hash);}
 function pin(snapshot?:FileSnapshotSelection,file?:{databaseId:number;fileId:number},clear=false){if(!resolvedWindow)return;const next={...selection,...(file?{file}:{}),...(clear?{fileSnapshot:undefined}:snapshot?{fileSnapshot:snapshot}:{})};const href=resourceSelectionHref({...scope,range:"custom",from:resolvedWindow.fromUtc,to:resolvedWindow.toUtc},next);location.hash=clear||snapshot?href:preserveInvalidFileSnapshotHref(href,location.hash);}
 return <main className="component-harness"><h1>Database file completion history harness</h1><div className="harness-controls">
  <label>Selected server<select value={scope.target} onChange={e=>query('target',e.target.value)}><option value={alpha}>Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option></select></label>
  <label>Target revision<select value={revision} onChange={e=>setRevision(Number(e.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
  <label>Authority context<select value={access} onChange={e=>setAccess(e.target.value)}><option value="grant-a">Original grant</option><option value="grant-b">Changed grant</option></select></label>
  <button type="button" onClick={()=>{setPoll(poll+1);}}>Advance workspace poll</button><output aria-label="Workspace poll">{poll}</output>
  <button type="button" onClick={()=>setRefresh(refresh+1)}>Refresh workspace</button>
  <button type="button" onClick={()=>query('to','2026-10-02T11:00:00.000002Z')}>Change shared window one microsecond</button>
  <button type="button" onClick={()=>query('from','2026-09-01T00:00:00Z')}>Use invalid long window</button>
  <button type="button" onClick={()=>query('from','2026-10-02T10:00:00.000001Z')}>Restore shared window</button>
  <button type="button" onClick={()=>{query('from','2026-10-02T10:00:00.0000010Z');query('to','2026-10-02T11:00:00.0000010Z');}}>Use equivalent UTC spelling</button>
  <button type="button" onClick={()=>setMounted(!mounted)}>{mounted?"Unmount file history":"Mount file history"}</button>
 </div>{mounted&&<HistoryContent binding={binding} selection={selection} refresh={refresh} access={access} error={error} pin={pin}/>}
 </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
