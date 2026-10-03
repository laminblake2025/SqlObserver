import test from 'node:test';
import assert from 'node:assert/strict';
import {parseProductCatalog,parseRuleValues,parseTemplateVersion,parseRuleGroup,parseDeploymentStatus,parseDeploymentMember,parseDeploymentSummary,parseBatchReceipt,parseCommandReceipt,parseNativePage,commandResource} from '../src/features/alerts/ruleTemplatesModel.ts';
import {getTemplateCatalog,getTemplateVersion,getDeploymentStatus,getTemplatePage,getVersionPage,getBatchHistory,sendTemplateCommand} from '../src/features/alerts/ruleTemplatesApi.ts';
import {actorSid,otherSid,templateId,deploymentId,now,auditId,opaqueId,values,catalog,version,job,member,ruleTemplateBackend,catalogDigest} from '../component-tests/ruleTemplatesFixture.mjs';
const op=opaqueId('operation'),previewCommand=()=>({kind:'preview',body:{operationId:op,deploymentId,templateId,templateVersion:1,group:{kind:'environment',value:'production'},mode:'create_missing',reason:'Reviewed complete production group'}});
test('global catalog includes verified actor identity and global authority without inventing target grants',()=>{
 const result=parseProductCatalog(catalog());assert.equal(result.actorSid,actorSid);assert.equal(result.allTargetAdministrator,false);assert.equal(result.items[0].name,'metric.threshold');
 for(const patch of [{actorSid:'Unknown'},{allTargetAdministrator:'true'},{targetId:templateId},{catalogDigest:'bad'},{items:[{...catalog().items[0],sourceCollector:'engine.core'}]}])assert.throws(()=>parseProductCatalog(catalog(patch)));
});
test('exact rule settings preserve nullable metric, enabled state and native kind-specific bounds',()=>{
 assert.deepEqual(parseRuleValues(values()),values());const collector=catalog().items[1],{unit,...rest}=collector;assert.equal(parseRuleValues({...rest,enabled:false}).metricId,null);
 for(const patch of [{extra:1},{name:'unsafe name'},{threshold:Infinity},{threshold:-1},{hysteresis:101},{evaluationSeconds:0},{clearConfirmationCount:0},{kind:'blocking_duration_v1',metricId:null,evaluationSeconds:61},{kind:'deadlock_count_v1',metricId:null,threshold:1.5},{enabled:1}])assert.throws(()=>parseRuleValues(values(patch)));
});
test('immutable versions bind exact identity/version and accept native fractional UTC while keeping title separate from rule name',()=>{
 const result=parseTemplateVersion(version(),templateId,1);assert.equal(result.createdAtUtc,'2026-10-03T15:00:00.123456Z');assert.notEqual(result.title,result.values.name);
 for(const patch of [{version:2},{currentVersion:0},{title:'é'.repeat(129)},{ownerSid:'raw actor'},{reason:'a\nb'},{versionDigest:'f'.repeat(63)},{createdAtUtc:'2026-10-03T15:00:00.1234561+00:00'},{unexpected:true}])assert.throws(()=>parseTemplateVersion(version(templateId,1,patch),templateId,1));
});
test('groups are exact normalized environment/tag labels and never loaded target arrays',()=>{
 assert.deepEqual(parseRuleGroup({kind:'tag',value:'critical.east'}),{kind:'tag',value:'critical.east'});
 for(const value of [{kind:'groupId',value:templateId},{kind:'environment',value:'Production'},{kind:'environment',value:''},{kind:'tag',value:'x'.repeat(65)},{kind:'tag',value:'prod',members:[]}])assert.throws(()=>parseRuleGroup(value));
});
test('complete deployment validates native bounded membership, pending counts and all nullable fields',()=>{
 assert.equal(parseDeploymentStatus(job(),deploymentId).items.length,12);
 for(const patch of [{items:[]},{items:Array.from({length:101},(_,i)=>member(i)),pendingCount:101},{items:[member(),member()],pendingCount:2},{pendingCount:1},{group:null},{sourceDeploymentId:templateId},{sealed:true},{extra:true}])assert.throws(()=>parseDeploymentStatus(job(deploymentId,12,patch),deploymentId));
});
test('current rule match requires exact applied revision/settings and never relabels later manual edits inherited',()=>{
 const applied=member(0,{status:'applied',resultRevision:2,resultAuditId:auditId,resultAtUtc:now,currentRuleRevision:2,currentValues:values(),matchesApplied:true});assert.equal(parseDeploymentMember(applied).matchesApplied,true);
 assert.equal(parseDeploymentMember({...applied,currentRuleRevision:3,matchesApplied:false}).matchesApplied,false);
 assert.throws(()=>parseDeploymentMember({...applied,currentRuleRevision:3}));assert.throws(()=>parseDeploymentMember({...applied,currentValues:values({threshold:99})}));
 assert.throws(()=>parseDeploymentMember({...applied,currentValues:null}));
});
test('source compatibility and decision/status are independent native disclosures',()=>{
 const result=parseDeploymentMember(member(0,{compatibility:'unavailable',compatibilityReason:'enabled_source_schedule_unavailable',decision:'incompatible',status:'pending'}));assert.equal(result.compatibility,'unavailable');assert.equal(result.status,'pending');
 assert.throws(()=>parseDeploymentMember(member(0,{compatibilityReason:'bad\u0000reason'})));
});
test('immutable apply receipt uses exact captured revision+1 and no more than ten bound members',()=>{
 const backend=ruleTemplateBackend();backend.seedJob(job());const command={kind:'apply',deploymentId,body:{operationId:op,expectedRevision:1,reason:'First batch'}};
 const receipt=backend.change('apply',deploymentId,command.body);assert.equal(parseBatchReceipt(receipt,command,job().items).items.length,10);
 for(const patch of [{operationId:auditId},{revision:3},{items:[...receipt.items,receipt.items[0]]},{pendingCount:-1},{action:'stop'},{deploymentId:templateId}])assert.throws(()=>parseBatchReceipt({...receipt,...patch},command,job().items));
 assert.throws(()=>parseBatchReceipt({...receipt,items:[{...receipt.items[0],targetId:templateId}]},command,job().items));
});
test('preview receipt captures historical deployment and binds exact template/version/group/action',()=>{
 const c=previewCommand(),receipt={operationId:op,auditId,recordedAtUtc:now,deployment:job()};assert.equal(parseCommandReceipt(receipt,c).deployment.revision,1);
 for(const patch of [{operationId:auditId},{deployment:job(templateId)},{deployment:job(deploymentId,12,{revision:2})},{deployment:job(deploymentId,12,{template:version(templateId,2)})},{deployment:job(deploymentId,12,{group:{kind:'tag',value:'production'}})}])assert.throws(()=>parseCommandReceipt({...receipt,...patch},c));
});
test('library receipts bind immutable version and cannot substitute a later current library revision',()=>{
 const c={kind:'publish_template',templateId,body:{operationId:op,expectedVersion:7,title:'Review',sourceTemplateName:'metric.threshold',catalogDigest,values:values(),reason:'Version update'}};
 const r={operationId:op,auditId,recordedAtUtc:now,templateId,version:8,versionDigest:'a'.repeat(64)};assert.equal(parseCommandReceipt(r,c).version,8);assert.throws(()=>parseCommandReceipt({...r,version:9},c));
 assert.deepEqual(commandResource(c),{kind:'template',id:templateId,version:8});
});
test('full page anchors and duplicate identities validate retained library/versions without guessing private UUID ordering',()=>{
 const rows=Array.from({length:50},(_,i)=>version(opaqueId('template|'+i))),first=parseNativePage({serverNowUtc:now,items:rows,nextBeforeTemplateId:rows.at(-1).templateId},'templates');
 assert.throws(()=>parseNativePage({serverNowUtc:now,items:[rows[0]],nextBeforeTemplateId:null},'templates',[first],first.next));
 assert.throws(()=>parseNativePage({serverNowUtc:now,items:rows,nextBeforeTemplateId:rows[0].templateId},'templates'));
 const versions=Array.from({length:50},(_,i)=>version(templateId,51-i,{currentVersion:51}));assert.equal(parseNativePage({templateId,serverNowUtc:now,items:versions,nextBeforeVersion:2},'versions',[],undefined,templateId).next,2);
 assert.throws(()=>parseNativePage({templateId,serverNowUtc:now,items:[versions[1],versions[0]],nextBeforeVersion:null},'versions',[],undefined,templateId));
});
test('history accepts equal/backward timestamps with opaque private sequence anchors and rejects another deployment',()=>{
 const backend=ruleTemplateBackend();backend.seedJob(job());const one=backend.change('apply',deploymentId,{operationId:op,expectedRevision:1,reason:'Batch1'}),two=backend.change('apply',deploymentId,{operationId:auditId,expectedRevision:2,reason:'Batch2'});
 const rows=[{...two,recordedAtUtc:'2026-10-03T14:00:00Z'},one];assert.equal(parseNativePage({deploymentId,serverNowUtc:now,items:rows,nextBeforeOperationId:null},'history',[],undefined,deploymentId).items.length,2);
 assert.throws(()=>parseNativePage({deploymentId:templateId,serverNowUtc:now,items:rows,nextBeforeOperationId:null},'history',[],undefined,deploymentId));
});
test('fresh explicit batches and exact replay are different operations; history retains only immutable original receipts',()=>{
 const backend=ruleTemplateBackend();backend.seedJob(job());const body={operationId:op,expectedRevision:1,reason:'Batch1'},one=backend.change('apply',deploymentId,body);
 assert.equal(one.pendingCount,2);assert.deepEqual(backend.change('apply',deploymentId,body),one);assert.equal(backend.readJob(deploymentId).revision,2);
 const two=backend.change('apply',deploymentId,{operationId:auditId,expectedRevision:2,reason:'Batch2'});assert.equal(two.pendingCount,0);assert.equal(two.sealed,true);assert.deepEqual(backend.history(deploymentId).items,[two,one]);
});
test('stop seals pending work and rollback disables created rules rather than deleting later current evidence',()=>{
 const backend=ruleTemplateBackend();backend.seedJob(job());backend.change('apply',deploymentId,{operationId:op,expectedRevision:1,reason:'Batch1'});
 backend.change('stop',deploymentId,{operationId:auditId,expectedRevision:2,reason:'Stop remainder'});assert.equal(backend.readJob(deploymentId).sealed,true);
 const rollback=opaqueId('rollback');backend.change('rollback',deploymentId,{operationId:opaqueId('rollback-op'),deploymentId:rollback,reason:'Guarded rollback'});
 assert.equal(backend.readJob(rollback).items.length,10);assert.ok(backend.readJob(rollback).items.every(item=>item.decision==='disable_created'&&!item.desiredValues.enabled));
 backend.change('apply',rollback,{operationId:opaqueId('rollback-apply'),expectedRevision:1,reason:'Apply rollback'});assert.equal(backend.rules.size,10);assert.ok([...backend.rules.values()].every(rule=>!rule.values.enabled));
});
test('same setting values after manual revision drift still conflict instead of being overwritten',()=>{
 const backend=ruleTemplateBackend();backend.seedJob(job());backend.change('apply',deploymentId,{operationId:op,expectedRevision:1,reason:'Batch1'});
 const source=backend.readJob(deploymentId),row=source.items.find(item=>item.status==='applied'),rule=backend.rules.get(row.targetId+'|'+row.desiredValues.name);rule.revision++;
 assert.equal(backend.readJob(deploymentId).items.find(item=>item.targetId===row.targetId).matchesApplied,false);
});
test('summary current metadata does not claim per-target applied inheritance',()=>{
 const backend=ruleTemplateBackend();backend.seedJob(job());assert.equal(parseDeploymentSummary(backend.listJobs().items[0]).memberCount,12);
 assert.throws(()=>parseDeploymentSummary({...backend.listJobs().items[0],memberCount:101}));
});
test('native HTTP operations preserve captured body and query anchors; confirmed readback uses a separate GET',async()=>{
 const previous=globalThis.fetch,calls=[],backend=ruleTemplateBackend();backend.seedTemplate();
 globalThis.fetch=async(url,init)=>{calls.push({url:String(url),init});const path=String(url);const result=path.endsWith('/catalog')?catalog():path.includes('/versions?')?backend.versions(templateId):path.includes('/history?')?{deploymentId,serverNowUtc:now,items:[],nextBeforeOperationId:null}:init.method==='POST'?backend.change('preview',deploymentId,JSON.parse(init.body)):path.includes('/rule-deployments/')?backend.readJob(deploymentId):backend.readTemplate(templateId,1);return new Response(JSON.stringify(result),{headers:{'content-type':'application/json'}});};
 try{const signal=new AbortController().signal;await getTemplateCatalog(signal);await sendTemplateCommand(previewCommand(),signal);await getDeploymentStatus(deploymentId,signal);await getTemplateVersion(templateId,1,signal);await getVersionPage(templateId,signal);await getBatchHistory(deploymentId,signal);
 assert.deepEqual(JSON.parse(calls[1].init.body),previewCommand().body);assert.equal(calls[2].init.method,'GET');assert.ok(calls[3].url.endsWith('?version=1'));assert.ok(calls[4].url.endsWith('/versions?limit=50'));assert.equal(calls[1].init.cache,'no-store');}
 finally{globalThis.fetch=previous;}
});
for(const code of [401,403,404,409,422,503])test('native HTTP '+code+' remains typed for ownership decisions without provider detail',async()=>{
 const previous=globalThis.fetch;globalThis.fetch=async()=>new Response('{"detail":"PRIVATE_PROVIDER_ERROR"}',{status:code});
 try{await assert.rejects(()=>getTemplateCatalog(new AbortController().signal),error=>error.status===code&&!error.message.includes('PRIVATE'));}finally{globalThis.fetch=previous;}
});
