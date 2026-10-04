import {useEffect,useLayoutEffect,useState} from "react";
import {createRoot} from "react-dom/client";
import {TimeDisplayProvider} from "../src/TimeDisplayContext.tsx";
import {readOverviewScope,overviewWindow} from "../src/features/overview/overviewModel.ts";
import {replicationWindow,type ReplicationBinding} from "../src/features/operations/replicationWindowHistoryModel.ts";
import {readReplicationSelection,type ReplicationSelection} from "../src/features/operations/replicationSelectionModel.ts";
import {useReplicationWindowHistory} from "../src/features/operations/useReplicationWindowHistory.ts";
import {ReplicationWindowHistoryPanel} from "../src/features/operations/ReplicationWindowHistoryPanel.tsx";
import "../src/styles.css";
import "./harness.css";

const alpha="11111111-1111-4111-8111-000000000001",beta="11111111-1111-4111-8111-000000000002";
declare global {interface Window {replicationHistoryCommits:string[]}}
function Content({binding,selected,refresh,access,pin}:{binding?:ReplicationBinding;selected?:ReplicationSelection|null;refresh:number;access:string;pin:(value?:ReplicationSelection,retain?:boolean)=>void}){
 const history=useReplicationWindowHistory(binding,selected,refresh,access);
 useLayoutEffect(()=>{window.replicationHistoryCommits??=[];window.replicationHistoryCommits.push(document.querySelector('[aria-label="Selected replication receipt"]')?.textContent??"");});
 return <ReplicationWindowHistoryPanel history={history} selected={selected} onSelectReceipt={value=>pin(value)} onRetainWindow={()=>pin(history.selected??undefined,true)}/>;
}
function Harness(){
 const [hash,setHash]=useState(location.hash),[revision,setRevision]=useState(1),[access,setAccess]=useState("grant-a"),[refresh,setRefresh]=useState(0),[poll,setPoll]=useState(0),[mounted,setMounted]=useState(true),[equivalent,setEquivalent]=useState(false);
 useEffect(()=>{const change=()=>setHash(location.hash);window.addEventListener("hashchange",change);return()=>window.removeEventListener("hashchange",change);},[]);
 const raw=readOverviewScope(hash),scope={...raw,target:raw.target||alpha},selected=readReplicationSelection(hash,scope.target);
 let binding:ReplicationBinding|undefined,error:string|undefined;
 try {const value=overviewWindow(scope,Date.now()),window=replicationWindow(value.fromUtc,value.toUtc);binding={targetId:scope.target,targetRevision:revision,...window,...(equivalent?{fromUtc:window.fromUtc.replace('Z','0+00:00'),toUtc:window.toUtc.replace('Z','0+00:00')}:{})};}
 catch(failure){error=failure instanceof Error?failure.message:"Invalid shared window";}
 function change(p:URLSearchParams){location.hash=`/operations?${p}`;setHash(location.hash);}
 function query(key:string,value:string){const p=new URLSearchParams(location.hash.split('?')[1]);p.set(key,value);if(key==='target'){p.delete('replicationRun');p.delete('replicationRevision');}change(p);}
 function pin(value?:ReplicationSelection,retain=false){if(!binding)return;const p=new URLSearchParams(location.hash.split('?')[1]);p.set('target',binding.targetId);p.set('check','replication');p.set('range','custom');p.set('from',binding.fromUtc);p.set('to',binding.toUtc);if(!retain||value){p.delete('replicationRun');p.delete('replicationRevision');if(value){p.set('replicationRun',value.runId);p.set('replicationRevision',String(value.sampleTargetRevision));}}change(p);}
 return <main className="component-harness"><h1>Replication completion history harness</h1><div className="harness-controls">
  <label>Selected server<select value={scope.target} onChange={e=>query('target',e.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
  <label>Target revision<select value={revision} onChange={e=>setRevision(Number(e.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
  <label>Authority context<select value={access} onChange={e=>setAccess(e.target.value)}><option value="grant-a">Original grant</option><option value="grant-b">Changed grant</option></select></label>
  <button type="button" onClick={()=>setPoll(poll+1)}>Advance workspace poll</button><output aria-label="Workspace poll">{poll}</output>
  <button type="button" onClick={()=>setRefresh(refresh+1)}>Refresh workspace</button>
  <button type="button" onClick={()=>query('to','2026-10-04T11:00:00.123457Z')}>Change shared window one microsecond</button>
  <button type="button" onClick={()=>{query('from','2026-10-04T10:00:00.123456Z');query('to','2026-10-04T11:00:00.123456Z');}}>Restore shared window</button>
  <button type="button" onClick={()=>setEquivalent(!equivalent)}>Toggle equivalent UTC spelling</button>
  <button type="button" onClick={()=>query('from','2026-08-01T00:00:00Z')}>Use invalid long window</button>
  <button type="button" onClick={()=>setMounted(!mounted)}>{mounted?"Unmount Replication history":"Mount Replication history"}</button>
 </div><h2>Replication evidence</h2>{error&&<p role="alert">{error}</p>}{mounted&&<Content binding={binding} selected={selected} refresh={refresh} access={access} pin={pin}/>}</main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
