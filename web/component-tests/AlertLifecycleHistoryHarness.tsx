import { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext.tsx";
import { DashboardTimeline } from "../src/components/DashboardTimeline.tsx";
import { timelineEventsInWindow } from "../src/components/dashboardTimelineModel.ts";
import { readDashboardTimelineSelection, readAlertLifecycleTimelineIdentity, dashboardTimelineSelectionHref, type DashboardTimelineSelection } from "../src/components/dashboardTimelineSelectionModel.ts";
import { readOverviewScope, overviewWindow, overviewHref } from "../src/features/overview/overviewModel.ts";
import { useAlertLifecycleHistory } from "../src/features/health/useAlertLifecycleHistory.ts";
import { AlertLifecycleHistoryPanel } from "../src/features/health/AlertLifecycleHistoryPanel.tsx";
import { alertLifecycleTimelineEvents, lifecycleEventIdentity, type AlertLifecycleSelection } from "../src/features/health/alertLifecycleHistoryModel.ts";
import "../src/styles.css";
import "./harness.css";
const initial="#/health?target=11111111-1111-4111-8111-000000000001&range=custom&from=2026-10-02T10%3A00%3A00.123456Z&to=2026-10-02T11%3A00%3A00.123456Z&healthSection=summary";
function History({hash,revision,refresh,nativeOffset,navigate}:{hash:string;revision:number;refresh:number;nativeOffset:boolean;navigate:(href:string)=>void}) {
 const scope=readOverviewScope(hash),window=useMemo(()=>overviewWindow(scope,Date.now()),[hash,refresh]),selected=readDashboardTimelineSelection(hash,scope.target),identity=selected&&readAlertLifecycleTimelineIdentity(selected.eventId);
 const selectedEvent:AlertLifecycleSelection|null|undefined=selected===null?null:identity&&selected?{...identity,eventAtUtc:selected.timeUtc}:undefined;
 const native=(value:string)=>nativeOffset?value.replace(/(\.\d*?[1-9])0+Z$/u,"$1Z").replace(/Z$/u,"+00:00"):value;
 const history=useAlertLifecycleHistory({targetId:scope.target,targetRevision:revision,fromUtc:native(window.fromUtc),toUtc:native(window.toUtc)},selectedEvent?{...selectedEvent,eventAtUtc:native(selectedEvent.eventAtUtc)}:selectedEvent),fixedScope={...scope,range:"custom" as const,from:window.fromUtc,to:window.toUtc};
 const exact=history.detail.receipt?.event,exactEvent=exact?alertLifecycleTimelineEvents([exact],fixedScope)[0]:undefined;
 const events=timelineEventsInWindow([...(exactEvent?[exactEvent]:[]),...alertLifecycleTimelineEvents(history.list.pages.flatMap(p=>p.items),fixedScope)],window.fromUtc,window.toUtc,exactEvent);
 const [hover,setHover]=useState<string|null>(null),align=(selection:DashboardTimelineSelection|undefined)=>{setHover(null);navigate(dashboardTimelineSelectionHref(selection?fixedScope:scope,selection));};
 return<><h3>Timeline evidence</h3><DashboardTimeline events={events} {...window} selection={selected} onSelectionChange={align} crosshairUtc={hover??exact?.eventAtUtc} onSelectTime={setHover} pinnedExactEvent={exactEvent} loading={history.list.loading||history.detail.loading} sourceNotes={["Native retained event history; current inbox state is independent."]}/>
 <AlertLifecycleHistoryPanel history={history} scope={fixedScope} selectedEvent={selectedEvent} onSelectEvent={event=>align(event?{eventId:lifecycleEventIdentity(event),timeUtc:event.eventAtUtc}:undefined)} onRetainWindow={()=>{if(scope.range!=="custom")navigate(overviewHref(fixedScope,"health",scope.target));}}/></>;
}
function Harness(){const[hash,setHash]=useState(location.hash||initial),[revision,setRevision]=useState(1),[refresh,setRefresh]=useState(0),[mounted,setMounted]=useState(true),[nativeOffset,setNativeOffset]=useState(false);const{mode,setMode}=useTimeDisplay();
 useEffect(()=>{if(!location.hash)location.hash=initial;const update=()=>setHash(location.hash);addEventListener("hashchange",update);return()=>removeEventListener("hashchange",update);},[]);
 const navigate=(href:string)=>{location.hash=href;setHash(location.hash);},scope=readOverviewScope(hash),change=(patch:Partial<typeof scope>)=>navigate(overviewHref({...scope,...patch},"health"));
 return<main className="component-harness"><h1>Alert lifecycle harness</h1><fieldset className="harness-controls"><legend>Context</legend>
 <label>Selected server<select aria-label="Selected server" value={scope.target} onChange={event=>change({target:event.target.value})}><option value="11111111-1111-4111-8111-000000000001">Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option></select></label>
 <label>Current target revision<select aria-label="Current target revision" value={revision} onChange={event=>setRevision(Number(event.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
 <label>Time display<select aria-label="Time display" value={mode} onChange={event=>setMode(event.target.value==='local'?'local':'utc')}><option value="utc">UTC</option><option value="local">Local</option></select></label>
 <button type="button" onClick={()=>change({range:"custom",from:"2026-10-02T13:00:00.123456Z",to:"2026-10-02T14:00:00.123456Z"})}>Later shared window</button><button type="button" onClick={()=>navigate(initial)}>Initial shared window</button>
 <button type="button" onClick={()=>change({range:"1h",from:undefined,to:undefined})}>Use Live window</button><button type="button" onClick={()=>setRefresh(n=>n+1)}>Advance workspace poll</button><button type="button" onClick={()=>setMounted(v=>!v)}>{mounted?"Unmount alert history":"Mount alert history"}</button></fieldset>
 <button type="button" onClick={()=>setNativeOffset(v=>!v)}>Use equivalent native UTC spelling</button>
 <h2>Server dashboard</h2>{mounted&&<History hash={hash} revision={revision} refresh={refresh} nativeOffset={nativeOffset} navigate={navigate}/>}</main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
