import {useEffect,useLayoutEffect,useState} from "react";
import {createRoot} from "react-dom/client";
import {TimeDisplayProvider} from "../src/TimeDisplayContext.tsx";
import {overviewWindow,readOverviewScope} from "../src/features/overview/overviewModel.ts";
import {readBackupAvailabilitySelection,retainedBackupAvailabilitySelectionHref,retainBackupAvailabilityRawPin,type BackupAvailabilityKind,type BackupAvailabilitySelection} from "../src/features/operations/backupAvailabilitySelectionModel.ts";
import {useBackupAvailabilityWindowHistory} from "../src/features/operations/useBackupAvailabilityWindowHistory.ts";
import {BackupAvailabilityWindowHistoryPanel} from "../src/features/operations/BackupAvailabilityWindowHistoryPanel.tsx";
import type {BackupAvailabilityBinding} from "../src/features/operations/backupAvailabilityWindowHistoryModel.ts";
import "../src/styles.css";
import "./harness.css";
const alpha="11111111-1111-4111-8111-000000000001",beta="11111111-1111-4111-8111-000000000002";
declare global {interface Window {backupAvailabilityCommits:string[]}}
function Source({binding,kind,selected,refresh,authority,pin}:{binding?:BackupAvailabilityBinding;kind:BackupAvailabilityKind;selected?:BackupAvailabilitySelection|null;refresh:number;authority:string;pin:(value?:BackupAvailabilitySelection,retain?:boolean,window?:{fromUtc:string;toUtc:string})=>void}){
 const history=useBackupAvailabilityWindowHistory(binding,kind,selected,refresh,authority);
 useLayoutEffect(()=>{window.backupAvailabilityCommits??=[];window.backupAvailabilityCommits.push(document.querySelector('[data-history-harness]')?.textContent??"");});
 return <BackupAvailabilityWindowHistoryPanel history={history} kind={kind} selected={selected} onSelectReceipt={value=>pin(value,false,history.list.pages[0]??binding)} onRetainWindow={()=>pin(history.selected??undefined,true,history.list.pages[0]??binding)}/>;
}
function Harness(){
 const [hash,setHash]=useState(location.hash),[revision,setRevision]=useState(1),[authority,setAuthority]=useState("grant-a"),[refresh,setRefresh]=useState(0),[poll,setPoll]=useState(0),[mounted,setMounted]=useState(true),[equivalent,setEquivalent]=useState(false);
 useEffect(()=>{const onHash=()=>setHash(location.hash);addEventListener("hashchange",onHash);return()=>removeEventListener("hashchange",onHash);},[]);
 const raw=readOverviewScope(hash),scope={...raw,target:raw.target||alpha},kind:BackupAvailabilityKind=new URLSearchParams(hash.split('?')[1]).get('check')==='backups'?'backup':'availability',selected=readBackupAvailabilitySelection(hash,kind,scope.target);
 let binding:BackupAvailabilityBinding|undefined;
 try {const window=overviewWindow(scope,Date.now());binding={targetId:scope.target,targetRevision:revision,...window,...(equivalent?{fromUtc:window.fromUtc.replace('Z','0+00:00'),toUtc:window.toUtc.replace('Z','0+00:00')}:{})};}catch{binding=undefined;}
 function change(params:URLSearchParams){location.hash=`/operations?${params}`;setHash(location.hash);}
 function query(key:string,value:string){const params=new URLSearchParams(location.hash.split('?')[1]);params.set(key,value);if(key==='target')for(const key of ['backupRun','backupRevision','availabilityRun','availabilityRevision'])params.delete(key);change(params);}
 function pin(value?:BackupAvailabilitySelection,retain=false,window?:{fromUtc:string;toUtc:string}){if(!binding)return;const href=retainedBackupAvailabilitySelectionHref(scope,kind,window??binding,value);location.hash=retain&&selected===null?retainBackupAvailabilityRawPin(href,location.hash,kind):href;setHash(location.hash);}
 return <main className="component-harness"><h1>Backup and availability completion history harness</h1><div className="harness-controls">
  <label>Selected server<select value={scope.target} onChange={e=>query('target',e.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
  <label>Target revision<select value={revision} onChange={e=>setRevision(Number(e.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
  <label>Authority context<select value={authority} onChange={e=>setAuthority(e.target.value)}><option value="grant-a">Original grant</option><option value="grant-b">Changed grant</option></select></label>
  <button type="button" onClick={()=>setPoll(poll+1)}>Advance workspace poll</button><output aria-label="Workspace poll">{poll}</output>
  <button type="button" onClick={()=>setRefresh(refresh+1)}>Refresh workspace</button>
  <button type="button" onClick={()=>query('to','2026-10-04T11:00:00.123457Z')}>Change shared window one microsecond</button>
  <button type="button" onClick={()=>{const p=new URLSearchParams(location.hash.split('?')[1]);p.set('range','custom');p.set('from','2026-10-04T10:00:00.123456Z');p.set('to','2026-10-04T11:00:00.123456Z');change(p);}}>Restore shared window</button>
  <button type="button" onClick={()=>setEquivalent(!equivalent)}>Toggle equivalent UTC spelling</button>
  <button type="button" onClick={()=>query('from','2026-08-01T00:00:00Z')}>Use invalid long window</button>
  <button type="button" onClick={()=>setMounted(!mounted)}>{mounted?'Unmount supporting history':'Mount supporting history'}</button>
 </div><section aria-label="Supporting diagnostic workflow"><h2>Health checks</h2><h3>{kind==='backup'?'Primary backup assessments':'Primary availability evidence'}</h3><p>The supporting browser retains earlier legacy collection attempts independently of primary diagnostic evidence.</p><div data-history-harness>{mounted&&<Source binding={binding} kind={kind} selected={selected} refresh={refresh} authority={authority} pin={pin}/>}</div></section></main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
