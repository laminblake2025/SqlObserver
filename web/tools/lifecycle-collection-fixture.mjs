// Synthetic loopback-only current configuration; independent of retained history.
const states=new Map();
export function handleLifecycleCollectionFixture(url,res,req,targets,send){
 const match=/^\/api\/v1\/observation-targets\/([a-f0-9-]{36})\/lifecycle\/collection$/u.exec(url.pathname);if(!match)return false;
 const target=targets.find(t=>t.instanceId===match[1]);if(!target){send(res,{},404);return true;}
 let state=states.get(target.instanceId);if(!state||state.targetRevision!==target.configurationRevision){state={targetId:target.instanceId,targetRevision:target.configurationRevision,scheduleRevision:1,collectorEnabled:false};states.set(target.instanceId,state);}
 res.setHeader('Cache-Control','private, no-store');
 if(req.method==='GET'){if([...url.searchParams.keys()].join('|')!=='targetRevision'||url.searchParams.get('targetRevision')!==String(state.targetRevision)){send(res,{},409);}else send(res,state);return true;}
 if(req.method!=='PUT'){send(res,{},405);return true;}
 if(/(?:chartReferenceRole=viewer|alertOperatorFixture=1)/u.test(req.headers.cookie??'')){send(res,{},403);return true;}
 let bytes=0,chunks=[],sent=false;
 req.on('data',chunk=>{bytes+=chunk.length;if(bytes>4096){if(!sent){sent=true;send(res,{},413);}return;}chunks.push(chunk);});
 req.on('end',()=>{if(sent)return;try{
  const body=JSON.parse(Buffer.concat(chunks).toString('utf8'));
  if(!body||Object.keys(body).sort().join('|')!=='enabled|expectedEnabled|expectedScheduleRevision|expectedTargetRevision'||typeof body.enabled!=='boolean'||typeof body.expectedEnabled!=='boolean'){send(res,{},400);return;}
  if(body.expectedTargetRevision!==state.targetRevision||body.expectedScheduleRevision!==state.scheduleRevision||body.expectedEnabled!==state.collectorEnabled){send(res,{},409);return;}
  if(body.enabled!==state.collectorEnabled){state={...state,collectorEnabled:body.enabled,scheduleRevision:state.scheduleRevision+1};states.set(target.instanceId,state);}send(res,state);
 }catch{send(res,{},400);}});return true;
}
