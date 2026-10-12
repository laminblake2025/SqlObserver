// Owned loopback fixture only. Matches native M10 surface cursors and legacy incidents.
const guid=index=>`33333333-3333-4333-8333-${String(index+1).padStart(12,'0')}`;
export function nativeAnalyticsCursor(page, item) {
  const inventory=page.surface==='jobs'||page.surface==='backfill';
  return Buffer.from(JSON.stringify({kind:'m10.surface.v1',surface:page.surface,targetId:page.targetId,
    targetRevision:page.targetRevision,generation:page.generation,snapshotUtc:page.snapshotUtc,
    sourceCutoffUtc:page.cutoffUtc,fromUtc:page.fromUtc,toUtc:page.toUtc,filters:{},
    tieAt:inventory?item.requestedAtUtc:item.occurredAtUtc,tieId:inventory?item.jobId:page.surface==='evidence-packets'?item.packetId:item.eventId,
    tieMetric:null,tieDimensions:null,tieFingerprint:null})).toString('base64url');
}
export function analyticsWorkflowFixturePage(url,target) {
  const surface=url.pathname.split('/analytics/')[1],inventory=surface==='jobs'||surface==='backfill';
  if(!['jobs','backfill','diagnostics/search','evidence-packets','incidents'].includes(surface))return undefined;
  let anchor;
  if(url.searchParams.has('cursor')){
    try{anchor=JSON.parse(Buffer.from(url.searchParams.get('cursor'),'base64url').toString('utf8'));}catch{return undefined;}
    if(anchor.kind!=='m10.surface.v1'||anchor.surface!==surface||anchor.targetId!==target.instanceId)return undefined;
  }
  const toUtc=anchor?.toUtc??url.searchParams.get('toUtc')??new Date().toISOString();
  const fromUtc=anchor?.fromUtc??url.searchParams.get('fromUtc')??new Date(Date.parse(toUtc)-3600000).toISOString();
  const snapshotUtc=anchor?.snapshotUtc??new Date().toISOString(),limit=surface==='evidence-packets'?200:100;
  const row=index=>{
    const at=new Date(Date.parse(fromUtc)+(index+1)*(Date.parse(toUtc)-Date.parse(fromUtc))/(limit+2)).toISOString();
    if(inventory)return {jobId:guid(index),jobKind:surface==='backfill'?'backfill':'rollup',status:'queued',
      requestedAtUtc:'2026-09-20T01:00:00Z',startedAtUtc:null,completedAtUtc:null,attempt:1,targetRevision:target.configurationRevision};
    if(surface==='evidence-packets')return {occurredAtUtc:at,packetId:guid(index),evidenceKind:'SYNTHETIC diagnostic',sourceRunId:null,sourceDigest:'a'.repeat(64),confidence:null,visibilityState:'partial',targetRevision:target.configurationRevision};
    return {occurredAtUtc:at,eventId:guid(index),eventKind:'SYNTHETIC diagnostic',severity:1,
      safeMetadata:JSON.stringify({message:`SYNTHETIC evidence ${index+1}`,database:'SYNTHETIC server-wide receipt'}),
      collectedAtUtc:snapshotUtc,targetRevision:target.configurationRevision};
  };
  if(surface==='incidents')return {targetId:target.instanceId,fromUtc,toUtc,state:'complete',items:[{threadId:guid(0),openedAtUtc:new Date(Date.parse(fromUtc)+1000).toISOString(),closedAtUtc:null,currentGeneration:1,packets:[],generations:[]}]};
  const page={targetId:target.instanceId,surface,fromUtc,toUtc,state:'complete',snapshotUtc,cutoffUtc:anchor?.sourceCutoffUtc??snapshotUtc,
    generation:anchor?.generation??1,targetRevision:target.configurationRevision,items:anchor?[row(limit)]:Array.from({length:limit},(_,index)=>row(index)),nextCursor:null};
  if(!anchor)page.nextCursor=nativeAnalyticsCursor(page,page.items.at(-1));
  return page;
}
export function handleAnalyticsWorkflowFixture(url,res,req,targets,send){
  if(!req.headers.cookie?.includes('analyticsWorkflowFixture=1'))return false;
  const route=/^\/api\/v1\/observation-targets\/([^/]+)\/analytics\/(.+)$/.exec(url.pathname);
  if(!route)return false;
  const target=targets.find(item=>item.instanceId===route[1]);
  const page=target?analyticsWorkflowFixturePage(url,target):undefined;
  send(res,page??{},page?200:400);return true;
}
