import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetMaintenancePanel } from "../src/features/alerts/TargetMaintenancePanel";
import { readOverviewScope, overviewHref } from "../src/features/overview/overviewModel";
import { readAdminSelection, adminSelectionHref } from "../src/features/alerts/adminSelectionModel";
import { readMaintenanceSeriesSelection, maintenanceSeriesSelectionHref, retainMaintenanceSeriesSelectionHref } from "../src/features/alerts/maintenanceSeriesSelection";
import "../src/styles.css";
import "./harness.css";
const alpha="11111111-1111-4111-8111-111111111111",beta="44444444-4444-4444-8444-444444444444";
function Harness(){
 const [hash,setHash]=useState(location.hash),[refresh,setRefresh]=useState(0),[allowed,setAllowed]=useState(true),[mounted,setMounted]=useState(true);
 useEffect(()=>{const update=()=>setHash(location.hash);addEventListener("hashchange",update);return()=>removeEventListener("hashchange",update);},[]);
 const scope=readOverviewScope(hash),target=scope.target||alpha;
 return <main className="component-harness"><h1>Maintenance series harness</h1><fieldset className="harness-controls"><legend>Context</legend>
  <label>Server context<select value={target} onChange={event=>{location.hash=overviewHref({...scope,target:event.target.value},"maintenance");}}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
  <label>Window context<select value={scope.range} onChange={event=>{location.hash=retainMaintenanceSeriesSelectionHref(overviewHref({...scope,range:event.target.value as "1h"|"custom",from:"2026-10-03T14:00:00Z",to:"2026-10-03T15:00:00Z"},"maintenance"),hash);}}><option value="1h">Live hour</option><option value="custom">Rewind</option></select></label>
  <label><input type="checkbox" checked={allowed} onChange={event=>setAllowed(event.target.checked)} />Target Administrator permission</label>
  <button type="button" onClick={()=>setRefresh(value=>value+1)}>Workspace tick</button><button type="button" onClick={()=>setMounted(value=>!value)}>{mounted?"Unmount maintenance":"Mount maintenance"}</button>
 </fieldset>{mounted&&<TargetMaintenancePanel instanceId={target} displayName={target===alpha?"Alpha":"Beta"} canManage={allowed} refresh={refresh} onClose={()=>setMounted(false)}
  selectedId={readAdminSelection(hash,"maintenance",target)} onSelect={id=>{location.hash=retainMaintenanceSeriesSelectionHref(adminSelectionHref(scope,"maintenance",id),hash);}}
  selectedSeriesId={readMaintenanceSeriesSelection(hash,target)} onSelectSeries={id=>{location.hash=maintenanceSeriesSelectionHref(hash,id);}} />}</main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
