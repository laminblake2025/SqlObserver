import { createHash } from 'node:crypto';
export const actorSid='S-1-5-21-100-200-300-1001',otherSid='S-1-5-21-100-200-300-1002';
export const templateId='77777777-7777-4777-8777-777777777777',deploymentId='88888888-8888-4888-8888-888888888888';
export const now='2026-10-03T15:00:00.1234560+00:00',auditId='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
export const opaqueId=seed=>{const digits=createHash('sha256').update(seed).digest('hex').slice(0,32).split('');digits[12]='5';digits[16]=((parseInt(digits[16],16)&3)|8).toString(16);const h=digits.join('');return[h.slice(0,8),h.slice(8,12),h.slice(12,16),h.slice(16,20),h.slice(20)].join('-');};
export const values=changes=>({name:'connections.production',kind:'metric_threshold',metricId:'engine.user_connections',comparison:'greater_than_or_equal',threshold:100,hysteresis:10,confirmationCount:2,confirmationSeconds:300,evaluationSeconds:15,clearConfirmationCount:2,enabled:true,...changes});
export const catalogItems=[{name:'metric.threshold',kind:'metric_threshold',metricId:'engine.user_connections',unit:'count',comparison:'greater_than_or_equal',threshold:1,hysteresis:0,confirmationCount:1,confirmationSeconds:0,evaluationSeconds:15,clearConfirmationCount:2},
 {name:'collector.health',kind:'collector_health',metricId:null,unit:'count',comparison:'less_than',threshold:1,hysteresis:0,confirmationCount:2,confirmationSeconds:300,evaluationSeconds:30,clearConfirmationCount:2},
 {name:'blocking.duration',kind:'blocking_duration_v1',metricId:null,unit:'seconds',comparison:'greater_than_or_equal',threshold:60,hysteresis:10,confirmationCount:2,confirmationSeconds:300,evaluationSeconds:15,clearConfirmationCount:2}];
export const catalogDigest=createHash('sha256').update('SYNTHETIC_NATIVE_CATALOG_FIXTURE').digest('hex');
export const catalog=changes=>({catalogDigest,items:catalogItems,actorSid,allTargetAdministrator:false,...changes});
export const version=(id=templateId,n=1,changes={})=>({templateId:id,version:n,currentVersion:n,title:'Synthetic production connections',ownerSid:actorSid,actorSid,sourceTemplateName:'metric.threshold',catalogDigest,
 versionDigest:createHash('sha256').update(id+'|'+n).digest('hex'),values:values(),reason:'Reviewed production threshold',operationId:opaqueId(id+'|version|'+n),auditId,createdAtUtc:now,serverNowUtc:now,...changes});
export const member=(i=0,changes={})=>({targetId:opaqueId('target|'+i),targetName:'Synthetic SQL '+String(i+1).padStart(2,'0'),targetRevision:1,groupingRevision:0,ruleId:opaqueId('rule|'+i),expectedRuleRevision:null,beforeValues:null,desiredValues:values(),decision:'create',compatibility:'available',compatibilityReason:null,operationId:opaqueId('child|'+i),status:'pending',resultRevision:null,resultAuditId:null,resultAtUtc:null,resultCode:null,currentRuleRevision:null,currentValues:null,matchesApplied:null,...changes});
export const job=(id=deploymentId,count=12,changes={})=>({deploymentId:id,revision:1,actorSid,group:{kind:'environment',value:'production'},mode:'create_missing',sourceDeploymentId:null,template:version(),previewAtUtc:now,
 expiresAtUtc:'2026-10-03T15:15:00.1234560+00:00',sealed:false,pendingCount:count,serverNowUtc:now,reason:'Deploy reviewed connections to production',items:Array.from({length:count},(_,i)=>member(i)).sort((a,b)=>a.targetId.localeCompare(b.targetId)),...changes});
const clone=value=>structuredClone(value),error=code=>Object.assign(new Error('Fixture native refusal'),{status:code});
export function ruleTemplateBackend(options={}) {
 const templates=new Map(),jobs=new Map(),operations=new Map(),rules=new Map(),histories=new Map();
 const settings={actorSid,allTargetAdministrator:false,memberCount:12,denyMembers:false,...options};
 const summary=j=>({deploymentId:j.deploymentId,revision:j.revision,actorSid:j.actorSid,group:j.group,mode:j.mode,sourceDeploymentId:j.sourceDeploymentId,templateId:j.template.templateId,templateVersion:j.template.version,templateTitle:j.template.title,previewAtUtc:j.previewAtUtc,sealed:j.sealed,pendingCount:j.pendingCount,memberCount:j.items.length,serverNowUtc:now});
 const replay=(key,raw)=>{const previous=operations.get(key);if(!previous)return;
  if(previous.actor!==settings.actorSid)throw error(403);if(previous.raw!==raw)throw error(409);return clone(previous.receipt);};
 const page=(items,key,before,extra={})=>{const index=before===undefined?0:items.findIndex(item=>item[key]===before)+1,rows=items.slice(index,index+50);return {serverNowUtc:now,items:clone(rows),[extra.anchor]:index+rows.length<items.length?rows.at(-1)[key]:null,...extra.resource};};
 return {settings,templates,jobs,operations,rules,histories,
  seedTemplate(v=version()){const versions=templates.get(v.templateId)??new Map();versions.set(v.version,clone(v));templates.set(v.templateId,versions);},
  seedJob(j=job()){jobs.set(j.deploymentId,clone(j));histories.set(j.deploymentId,[]);},
  readTemplate(id,n){const versions=templates.get(id);if(!versions)throw error(404);const latest=Math.max(...versions.keys()),v=versions.get(n??latest);if(!v)throw error(404);return {...clone(v),currentVersion:latest,serverNowUtc:now};},
  readJob(id){const j=jobs.get(id);if(!j)throw error(404);if(settings.denyMembers)throw error(403);const result=clone(j);
   result.serverNowUtc=now;for(const item of result.items){const current=rules.get(item.targetId+'|'+item.desiredValues.name);item.currentRuleRevision=current?.revision??null;item.currentValues=current?.values??null;item.matchesApplied=item.status==='applied'?current?.revision===item.resultRevision&&JSON.stringify(current?.values)===JSON.stringify(item.desiredValues):null;}return result;},
  listTemplates(before){const entries=[...templates.keys()].sort().reverse().map(id=>this.readTemplate(id));return page(entries,'templateId',before,{anchor:'nextBeforeTemplateId'});},
  versions(id,before){const versions=templates.get(id);if(!versions)throw error(404);const entries=[...versions.keys()].sort((a,b)=>b-a).map(n=>this.readTemplate(id,n));return page(entries,'version',before,{anchor:'nextBeforeVersion',resource:{templateId:id}});},
  listJobs(before){const entries=[...jobs.values()].filter(()=>!settings.denyMembers).sort((a,b)=>b.deploymentId.localeCompare(a.deploymentId)).map(summary);return page(entries,'deploymentId',before,{anchor:'nextBeforeDeploymentId'});},
  history(id,before){if(!jobs.has(id))throw error(404);return page(histories.get(id)??[],'operationId',before,{anchor:'nextBeforeOperationId',resource:{deploymentId:id}});},
  change(kind,id,body,raw=JSON.stringify(body)) {
   const opkey=kind+'|'+id+'|'+body.operationId,replayed=replay(opkey,raw);if(replayed)return replayed;
   let receipt;
   if(kind==='create_template'||kind==='publish_template'){
    const previous=templates.get(id),latest=previous?Math.max(...previous.keys()):0;
    if(kind==='publish_template'&&!previous)throw error(404);if(kind==='create_template'?latest!==0:latest!==body.expectedVersion)throw error(409);
    if(previous&&this.readTemplate(id).ownerSid!==settings.actorSid&&!settings.allTargetAdministrator)throw error(403);
    const v=version(id,latest+1,{title:body.title,ownerSid:previous?this.readTemplate(id).ownerSid:settings.actorSid,actorSid:settings.actorSid,sourceTemplateName:body.sourceTemplateName,catalogDigest:body.catalogDigest,values:clone(body.values),reason:body.reason,operationId:body.operationId});this.seedTemplate(v);
    receipt={operationId:body.operationId,auditId,recordedAtUtc:now,templateId:id,version:v.version,versionDigest:v.versionDigest};
   }else if(kind==='preview'||kind==='rollback'){
    if(settings.denyMembers)throw error(403);if(settings.memberCount<1||settings.memberCount>100)throw error(422);
    let result;if(kind==='preview'){const t=this.readTemplate(body.templateId,body.templateVersion);result=job(body.deploymentId,settings.memberCount,{actorSid:settings.actorSid,group:body.group,mode:body.mode,template:t,reason:body.reason});
     for(const item of result.items){item.ruleId=opaqueId(body.deploymentId+'|rule|'+item.targetId);item.operationId=opaqueId(body.deploymentId+'|child|'+item.targetId);item.desiredValues=clone(t.values);const old=rules.get(item.targetId+'|'+t.values.name);if(old){item.ruleId=old.ruleId;item.expectedRuleRevision=old.revision;item.beforeValues=clone(old.values);item.decision=body.mode==='create_missing'?'skip_existing':old.values.kind!==t.values.kind||old.values.metricId!==t.values.metricId?'incompatible':JSON.stringify(old.values)===JSON.stringify(t.values)?'skip_unchanged':'update';}}}
    else{const source=this.readJob(id);if(!source.sealed||source.mode==='rollback')throw error(409);const candidates=source.items.filter(item=>item.status==='applied');if(!candidates.length)throw error(422);
     result=job(body.deploymentId,candidates.length,{actorSid:settings.actorSid,group:null,mode:'rollback',sourceDeploymentId:id,template:source.template,reason:body.reason,items:candidates.map(item=>({...clone(item),desiredValues:item.beforeValues?clone(item.beforeValues):{...item.desiredValues,enabled:false},beforeValues:clone(item.desiredValues),expectedRuleRevision:item.resultRevision,decision:item.beforeValues?'restore':'disable_created',operationId:opaqueId(body.deploymentId+'|child|'+item.targetId),status:'pending',resultRevision:null,resultAuditId:null,resultAtUtc:null,resultCode:null,matchesApplied:null}))});}
    this.seedJob(result);receipt={operationId:body.operationId,auditId,recordedAtUtc:now,deployment:this.readJob(body.deploymentId)};
   }else{
    if(settings.denyMembers)throw error(403);const j=jobs.get(id);if(!j)throw error(404);if(j.sealed||j.revision!==body.expectedRevision)throw error(409);
    const pending=j.items.filter(item=>item.status==='pending'),selected=kind==='stop'?pending:pending.slice(0,10),processed=[];
    for(const item of selected){const key=item.targetId+'|'+item.desiredValues.name,current=rules.get(key);
     if(kind==='stop')item.status='stopped';else if(item.expectedRuleRevision!==null&&current?.revision!==item.expectedRuleRevision||item.expectedRuleRevision===null&&current&&item.decision==='create'){item.status='conflict';item.resultCode='rule_revision_changed';}
     else if(item.decision.startsWith('skip_'))item.status='skipped';else if(item.decision==='incompatible'||item.compatibility==='unavailable'&&item.desiredValues.enabled){item.status='failed';item.resultCode='source_incompatible';}
     else{const next={ruleId:item.ruleId,revision:(current?.revision??0)+1,values:clone(item.desiredValues)};rules.set(key,next);item.status='applied';item.resultRevision=next.revision;item.resultAuditId=auditId;item.resultAtUtc=now;}
     processed.push({targetId:item.targetId,ruleId:item.ruleId,status:item.status,code:item.resultCode,ruleRevision:item.resultRevision,auditId:item.resultAuditId,recordedAtUtc:item.resultAtUtc});
    }j.revision++;j.pendingCount=j.items.filter(item=>item.status==='pending').length;j.sealed=j.pendingCount===0;
    receipt={operationId:body.operationId,auditId,recordedAtUtc:now,deploymentId:id,revision:j.revision,action:kind,items:processed,pendingCount:j.pendingCount,sealed:j.sealed};histories.get(id).unshift(clone(receipt));
   }
   operations.set(opkey,{raw,actor:settings.actorSid,receipt:clone(receipt)});return clone(receipt);
  }
 };
}
/** Isolated native routes only; callers retain ownership of navigation and other application APIs. */
export async function installRuleTemplatesFixture(page,{origin='http://127.0.0.1:4328',backend=ruleTemplateBackend(),handler}={}) {
 const session={backend,requests:[],writes:[],reads:[],errors:[],handler};
 const json=async(route,body,status=200)=>route.fulfill({status,contentType:'application/json',headers:{'cache-control':'no-store'},body:JSON.stringify(body)});
 await page.route('**/api/v1/alerts/rule-**',async route=>{
  const request=route.request(),url=new URL(request.url());if(url.origin!==origin){session.errors.push('Foreign fixture origin '+url.origin);return route.abort();}
  const match=/^\/api\/v1\/alerts\/(rule-templates|rule-deployments)(?:\/([^/]+)(?:\/(versions|history|apply|stop|rollback-preview))?)?$/u.exec(url.pathname);
  if(!match){session.errors.push('Unregistered '+request.method()+' '+url.pathname);return route.abort();}
  const library=match[1]==='rule-templates',id=match[2],resource=match[3],method=request.method(),body=method==='GET'?undefined:request.postDataJSON();let item;
  if(method==='GET')item={kind:library?id==='catalog'?'catalog':resource==='versions'?'versions':id?'template':'templates':resource==='history'?'history':id?'deployment':'deployments',id,before:url.searchParams.get(library?resource==='versions'?'beforeVersion':'beforeTemplateId':resource==='history'?'beforeOperationId':'beforeDeploymentId')??undefined,version:url.searchParams.has('version')?Number(url.searchParams.get('version')):undefined};
  else if((method==='POST'||method==='PUT')&&!url.search)item={kind:library?method==='PUT'?'publish_template':'create_template':id==='preview'?'preview':resource==='rollback-preview'?'rollback':resource,id:library?method==='PUT'?id:body.templateId:id==='preview'?body.deploymentId:id,body,raw:request.postData()};
  if(!item?.kind){session.errors.push('Unregistered request '+url.href);return route.abort();}
  session.requests.push(request);if(method==='GET')session.reads.push(item);else session.writes.push(item);
  if(session.handler&&await session.handler(route,item,session))return;
  try{const result=item.kind==='catalog'?catalog({actorSid:backend.settings.actorSid,allTargetAdministrator:backend.settings.allTargetAdministrator}):item.kind==='templates'?backend.listTemplates(item.before):item.kind==='versions'?backend.versions(id,item.before===undefined?undefined:Number(item.before)):item.kind==='template'?backend.readTemplate(id,item.version):item.kind==='deployment'?backend.readJob(id):item.kind==='deployments'?backend.listJobs(item.before):item.kind==='history'?backend.history(id,item.before):backend.change(item.kind,item.id,item.body,item.raw);await json(route,result);}
  catch(failure){await json(route,{detail:'PRIVATE_PROVIDER_ERROR'},failure.status??503);}
 });return session;
}
