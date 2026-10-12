import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {installRuleTemplatesFixture,ruleTemplateBackend,actorSid,otherSid,templateId,deploymentId,version,job,values,member,opaqueId,now,auditId,catalog} from './ruleTemplatesFixture.mjs';
const origin='http://127.0.0.1:4328',sessions=new WeakMap();
const screen=page=>page.getByRole('region',{name:'Rule templates',exact:true}),button=(page,name)=>screen(page).getByRole('button',{name,exact:true});
const field=(page,name,role='textbox')=>screen(page).getByRole(role,{name,exact:true});
const current=page=>screen(page).getByRole('region',{name:'Current deployment status',exact:true});
const currentTemplate=page=>screen(page).getByRole('region',{name:'Current template version',exact:true});
const history=page=>screen(page).getByRole('region',{name:'Immutable deployment history',exact:true});
async function json(route,body,status=200){await route.fulfill({status,contentType:'application/json',headers:{'cache-control':'no-store'},body:JSON.stringify(body)});}
function held(){let notify,release,finish;const started=new Promise(resolve=>notify=resolve),gate=new Promise(resolve=>release=resolve),done=new Promise(resolve=>finish=resolve);
 return{started,async release(){release();await done;},async handle(route,body,status=200){notify(route.request());await gate;try{await json(route,body,status);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{finish();}return true;}};}
test.beforeEach(async({page})=>{
 const backend=ruleTemplateBackend();backend.seedTemplate();await page.clock.setFixedTime(new Date(now));
 await page.route('**/*',async route=>{const url=new URL(route.request().url());if(url.origin!==origin||url.pathname.startsWith('/api/')){sessions.get(page)?.errors.push('Unregistered '+route.request().method()+' '+url.href);return route.abort();}return route.continue();});
 const session=await installRuleTemplatesFixture(page,{origin,backend});sessions.set(page,session);page.on('pageerror',error=>session.errors.push(error.message));
});
test.afterEach(async({page})=>expect(sessions.get(page).errors,'Only native registered API routes and no runtime errors').toEqual([]));
async function mount(page,query=''){await page.goto('/component-tests/rule-templates.html#/rule-templates'+query);await expect(page.getByRole('heading',{name:'Rule templates harness',exact:true})).toBeVisible();await expect(screen(page)).toBeVisible();}
async function selected(page){await mount(page,'?templateId='+templateId+'&templateVersion=1');await expect(currentTemplate(page)).toContainText('selected immutable version 1');}
async function draft(page){await field(page,'Product signal','combobox').selectOption('metric.threshold');await field(page,'Template title').fill('Production connection threshold');await field(page,'Intended rule name').fill('connections.production');await field(page,'Template reason').fill('Approved reviewed settings');
 for(const[name,value]of[['Threshold',100],['Hysteresis',10],['Fire observations',2],['Confirmation seconds',300],['Evaluation seconds',15],['Clear observations',2]])await field(page,name,'spinbutton').fill(String(value));}
async function preview(page){await field(page,'Group label').fill('Production');await field(page,'Deployment reason').fill('Apply reviewed threshold to complete production group');await button(page,'Preview group deployment').click();await expect(current(page)).toContainText('12 complete frozen members');}
async function prepared(page){await selected(page);await preview(page);await field(page,'Operation reason').fill('Review and apply bounded batch');}
test('library create freezes native approved settings, distinct title/name and exact immutable receipt/readback',async({page})=>{
 const session=sessions.get(page);await mount(page);await draft(page);await button(page,'Save template').click();await expect(currentTemplate(page)).toContainText('Production connection threshold');
 expect(session.writes).toHaveLength(1);const body=session.writes[0].body;expect(body.values).toEqual(values());expect(body.title).not.toBe(body.values.name);expect(body).not.toHaveProperty('scope');
 expect(session.reads.at(-1)).toMatchObject({kind:'template',id:body.templateId,version:1});await expect(screen(page).getByRole('region',{name:'Immutable operation receipt',exact:true})).toContainText(body.operationId);
 expect(new URL(page.url()).hash).toContain('templateId='+body.templateId);
});
test('owner publishes immutable next version without propagating settings into a previously frozen deployment',async({page})=>{
 const session=sessions.get(page);await prepared(page);const id=session.writes[0].body.deploymentId;
 await button(page,'Edit template version').click();await field(page,'Threshold','spinbutton').fill('200');await field(page,'Template reason').fill('Reviewed next settings');await button(page,'Publish template version').click();
 await expect(currentTemplate(page)).toContainText('selected immutable version 2');await expect(current(page)).toContainText('immutable version 1');expect(session.backend.readJob(id).template.values.threshold).toBe(100);
 expect(session.writes[1].body.expectedVersion).toBe(1);expect(session.backend.readTemplate(templateId,1).values.threshold).toBe(100);expect(session.backend.readTemplate(templateId,2).values.threshold).toBe(200);
});
test('owner controls use native catalog principal and global authority, never UI-supplied owner claims',async({page})=>{
 const session=sessions.get(page);session.backend.settings.actorSid=otherSid;await selected(page);await expect(button(page,'Edit template version')).toBeDisabled();await expect(currentTemplate(page)).toContainText('Only the owner');
 session.backend.settings.allTargetAdministrator=true;await button(page,'Refresh product catalog').click();await expect(button(page,'Edit template version')).toBeEnabled();expect(session.writes).toHaveLength(0);
});
test('complete native preview has no loaded overview cohort; explicit batches process ten then the remaining two',async({page})=>{
 const session=sessions.get(page);await prepared(page);const body=session.writes[0].body;expect(body.group).toEqual({kind:'environment',value:'production'});expect(body).not.toHaveProperty('targets');
 await expect(current(page).getByRole('region',{name:'Frozen deployment settings',exact:true})).toContainText('connections.production');await button(page,'Apply next batch').click();await expect(current(page)).toContainText('revision 2');await expect(current(page)).toContainText('2 pending');
 await expect(screen(page).getByRole('region',{name:'Immutable operation receipt',exact:true})).toContainText('processed 10 members');await button(page,'Apply next batch').click();await expect(current(page)).toContainText('revision 3');await expect(current(page)).toContainText('0 pending · sealed');
 expect(session.writes[1].body.expectedRevision).toBe(1);expect(session.writes[2].body.expectedRevision).toBe(2);expect(session.writes[2].body.operationId).not.toBe(session.writes[1].body.operationId);await expect(button(page,'Apply next batch')).toBeDisabled();
});
for(const count of [0,101])test('native group size '+count+' is refused without a partial client cohort',async({page})=>{
 const session=sessions.get(page);session.backend.settings.memberCount=count;await selected(page);await field(page,'Group label').fill('production');await field(page,'Deployment reason').fill('Reviewed group');await button(page,'Preview group deployment').click();
 await expect(screen(page)).toContainText('server refused this operation');await expect(current(page)).toHaveCount(0);expect(session.backend.jobs.size).toBe(0);await expect(button(page,'Review refused draft')).toBeVisible();
});
test('source incompatibility names recovery and current configuration never inherits a later manual edit',async({page})=>{
 const session=sessions.get(page);await prepared(page);const id=session.writes[0].body.deploymentId,native=session.backend.jobs.get(id);native.items[0].compatibility='unavailable';native.items[0].compatibilityReason='enabled_source_schedule_unavailable';native.items[0].decision='incompatible';
 await button(page,'Refresh deployment status').click();await expect(current(page)).toContainText('Enable a compatible engine.user_connections collection schedule for the current server configuration, then create a new preview');
 await button(page,'Apply next batch').click();await expect(current(page)).toContainText('failed');const applied=session.backend.readJob(id).items.find(item=>item.status==='applied'),rule=session.backend.rules.get(applied.targetId+'|'+applied.desiredValues.name);rule.revision++;rule.values.threshold=999;
 await button(page,'Refresh deployment status').click();await expect(current(page)).toContainText('Current configuration differs from the applied result');await expect(current(page)).toContainText('threshold 999');
});
test('stop is revision checked and explicitly confirmed; rollback preview preserves applied members and disables creations',async({page})=>{
 const session=sessions.get(page);await prepared(page);await button(page,'Apply next batch').click();await expect(current(page)).toContainText('2 pending');
 await button(page,'Stop remaining deployment').click();expect(session.writes).toHaveLength(2);await button(page,'Confirm stop').click();await expect(current(page)).toContainText('0 pending · sealed');expect(session.writes[2].body.expectedRevision).toBe(2);
 await button(page,'Preview rollback').click();await expect(current(page)).toContainText('rollback');await expect(current(page)).toContainText('10 complete frozen members');await expect(current(page)).toContainText('never deletes rules');
 await button(page,'Apply next batch').click();await expect(current(page)).toContainText('0 pending · sealed');expect(session.backend.rules.size).toBe(10);expect([...session.backend.rules.values()].every(rule=>!rule.values.enabled)).toBe(true);
});
test('confirmed preview with failed current readback retries only GET using the exact unselected job',async({page})=>{
 const session=sessions.get(page);await selected(page);session.handler=async(route,item)=>item.kind==='deployment'?(await json(route,{},503),true):false;
 await field(page,'Group label').fill('production');await field(page,'Deployment reason').fill('Reviewed group');await button(page,'Preview group deployment').click();await expect(button(page,'Retry deployment status')).toBeVisible();await expect(current(page)).toHaveCount(0);
 const id=session.writes[0].body.deploymentId;expect(new URL(page.url()).hash).not.toContain('deploymentId=');session.handler=undefined;await button(page,'Retry deployment status').click();await expect(current(page)).toContainText(id);expect(session.writes).toHaveLength(1);
});
test('successful refresh after uncertain apply retains byte-identical operation and excludes a fresh next batch',async({page})=>{
 const session=sessions.get(page);await prepared(page);session.handler=async(route,item)=>{if(item.kind!=='apply'||session.writes.filter(write=>write.kind==='apply').length!==1)return false;session.backend.change(item.kind,item.id,item.body,item.raw);await route.abort('failed');return true;};
 await button(page,'Apply next batch').click();await expect(button(page,'Retry template operation')).toBeVisible();const uncertain=session.writes.at(-1);await field(page,'Operation reason').fill('Changed draft');await button(page,'Refresh deployment status').click();await expect(current(page)).toContainText('2 pending');
 await expect(button(page,'Retry template operation')).toBeVisible();await expect(button(page,'Apply next batch')).toBeDisabled();await page.getByRole('button',{name:'Workspace tick',exact:true}).click();session.handler=undefined;
 await button(page,'Retry template operation').click();await expect(current(page)).toContainText('revision 2');expect(session.writes.at(-1).raw).toBe(uncertain.raw);expect(session.backend.history(uncertain.id).items).toHaveLength(1);
});
test('failed/cancelled status refresh preserves uncertain command; cancelling mutation keeps exact retry',async({page})=>{
 const session=sessions.get(page);await prepared(page);const pending=held();session.handler=(route,item)=>item.kind==='apply'?pending.handle(route,{},503):false;
 await button(page,'Apply next batch').click();const request=await pending.started;await button(page,'Cancel template request').click();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();await expect(button(page,'Retry template operation')).toBeVisible();
 session.handler=async(route,item)=>item.kind==='deployment'?(await json(route,{},503),true):false;await button(page,'Refresh deployment status').click();await expect(button(page,'Retry deployment status')).toBeVisible();await expect(button(page,'Retry template operation')).toBeVisible();
 session.handler=undefined;await button(page,'Retry template operation').click();await expect(current(page)).toContainText('revision 2');expect(session.writes.at(-1).raw).toBe(session.writes[1].raw);
});
test('uncertain new template B refresh targets B even when another library version A is selected',async({page})=>{
 const session=sessions.get(page);await selected(page);await draft(page);session.handler=async(route,item)=>item.kind==='create_template'?(await json(route,{},503),true):false;
 await button(page,'Save template').click();await expect(button(page,'Retry template operation')).toBeVisible();const command=session.writes[0];await button(page,'Refresh template status').click();await expect(button(page,'Retry template status')).toBeVisible();expect(session.reads.at(-1).id).toBe(command.id);
 await expect(currentTemplate(page)).toContainText(templateId);session.handler=undefined;await button(page,'Retry template operation').click();await expect(currentTemplate(page)).toContainText(command.id);expect(session.writes[1].raw).toBe(command.raw);
});
test('revision conflict needs a new exact status before new explicit intent, with history receipts left as received',async({page})=>{
 const session=sessions.get(page);await prepared(page);const id=session.writes[0].body.deploymentId;session.backend.jobs.get(id).revision=2;
 await button(page,'Apply next batch').click();await expect(screen(page)).toContainText('server refused this operation');await expect(button(page,'Retry template operation')).toHaveCount(0);await button(page,'Refresh deployment status').click();await expect(current(page)).toContainText('revision 2');
 await button(page,'Apply next batch').click();await expect(current(page)).toContainText('revision 3');expect(session.writes.at(-1).body.expectedRevision).toBe(2);expect(session.writes.at(-1).body.operationId).not.toBe(session.writes[1].body.operationId);
});
test('confirmed GET-only recovery survives role loss/regrant without replaying a dispatched preview',async({page})=>{
 const session=sessions.get(page);await selected(page);const pending=held();session.handler=(route,item)=>item.kind==='deployment'?pending.handle(route,session.backend.readJob(item.id)):false;
 await field(page,'Group label').fill('production');await field(page,'Deployment reason').fill('Confirmed preview');await button(page,'Preview group deployment').click();const request=await pending.started;
 await page.getByRole('checkbox',{name:'Target Administrator permission',exact:true}).uncheck();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();await expect(screen(page)).not.toContainText('Synthetic SQL');
 session.handler=undefined;await page.getByRole('checkbox',{name:'Target Administrator permission',exact:true}).check();await expect(button(page,'Retry deployment status')).toBeEnabled();await button(page,'Retry deployment status').click();await expect(current(page)).toContainText('12 complete frozen members');expect(session.writes).toHaveLength(1);
 await expect(screen(page).getByRole('region',{name:'Immutable operation receipt',exact:true})).toContainText(session.writes[0].body.operationId);
});
test('catalog principal change clears former actor operation rather than replaying it under another actor',async({page})=>{
 const session=sessions.get(page);await prepared(page);session.handler=async(route,item)=>item.kind==='apply'?(await json(route,{},503),true):false;await button(page,'Apply next batch').click();await expect(button(page,'Retry template operation')).toBeVisible();
 session.backend.settings.actorSid=otherSid;session.handler=undefined;await button(page,'Refresh product catalog').click();await expect(screen(page)).toContainText('Catalog authority changed');await expect(button(page,'Retry template operation')).toHaveCount(0);expect(session.writes.filter(item=>item.kind==='apply')).toHaveLength(1);
});
test('different selected template clears a healthy edit draft and never publishes the old resource with the new owner gate',async({page})=>{
 const session=sessions.get(page),other=opaqueId('other-template');session.backend.seedTemplate(version(other,1,{title:'Other current template',ownerSid:otherSid}));await selected(page);await button(page,'Edit template version').click();await field(page,'Template reason').fill('Old edit draft');
 await button(page,'Select template '+other+' version 1 · Other current template').click();await expect(currentTemplate(page)).toContainText('Other current template');await expect(button(page,'Publish template version')).toHaveCount(0);await expect(field(page,'Template reason')).toHaveValue('');await expect(button(page,'Edit template version')).toBeDisabled();expect(session.writes).toHaveLength(0);
});
test('a different exact URL selection retires an unrelated failed ordinary read without retrying its resource',async({page})=>{
 const session=sessions.get(page),other=opaqueId('ordinary-read-other');session.backend.seedTemplate(version(other,1,{title:'Other exact selected template'}));session.handler=async(route,item)=>item.kind==='template'&&item.id===templateId?(await json(route,{},503),true):false;
 await mount(page,'?templateId='+templateId+'&templateVersion=1');await expect(button(page,'Retry template status')).toBeVisible();await button(page,'Select template '+other+' version 1 · Other exact selected template').click();await expect(currentTemplate(page)).toContainText('Other exact selected template');await expect(button(page,'Retry template status')).toHaveCount(0);
 expect(session.reads.filter(item=>item.kind==='template'&&item.id===templateId)).toHaveLength(1);expect(session.reads.at(-1)).toMatchObject({kind:'template',id:other});expect(session.writes).toHaveLength(0);
});
test('disabled proposals retain source-unavailable disclosure and can apply without an enabled schedule',async({page})=>{
 const session=sessions.get(page);session.backend.seedTemplate(version(templateId,1,{values:values({enabled:false})}));await prepared(page);const id=session.writes[0].body.deploymentId,native=session.backend.jobs.get(id);native.items[0].compatibility='unavailable';native.items[0].compatibilityReason='source_schedule_unavailable';
 await button(page,'Refresh deployment status').click();await expect(current(page)).toContainText('This disabled proposal can proceed without enabling a schedule');await expect(current(page)).not.toContainText('Enable a compatible');await button(page,'Apply next batch').click();await expect(current(page)).toContainText('revision 2');expect(session.backend.readJob(id).items.find(item=>item.targetId===native.items[0].targetId).status).toBe('applied');
});
test('collector-health clear confirmation is configurable while approved fixed fields remain protected',async({page})=>{
 const session=sessions.get(page);await mount(page);await field(page,'Product signal','combobox').selectOption('collector.health');await field(page,'Template title').fill('Collector recovery policy');await field(page,'Template reason').fill('Confirm three recovery observations');await expect(field(page,'Threshold','spinbutton')).toBeDisabled();await expect(field(page,'Clear observations','spinbutton')).toBeEnabled();await field(page,'Clear observations','spinbutton').fill('3');await button(page,'Save template').click();await expect(currentTemplate(page)).toContainText('Collector recovery policy');expect(session.writes[0].body.values.clearConfirmationCount).toBe(3);expect(session.writes[0].body.values.name).toBe('collector.health');
});
test('native missing-resource write is a definite refusal with explicit new-intention recovery',async({page})=>{
 const session=sessions.get(page);await prepared(page);session.handler=async(route,item)=>item.kind==='apply'?(await json(route,{},404),true):false;await button(page,'Apply next batch').click();await expect(screen(page)).toContainText('server refused this operation');await expect(button(page,'Retry template operation')).toHaveCount(0);await expect(button(page,'Review refused operation')).toBeVisible();await button(page,'Review refused operation').click();await expect(button(page,'Apply next batch')).toBeEnabled();expect(session.writes.filter(item=>item.kind==='apply')).toHaveLength(1);
});
for(const source of ['catalog','templates','deployment','history','apply'])for(const code of [401,403])test(source+' HTTP '+code+' clears protected rows and fences parallel receipt recovery',async({page})=>{
 const session=sessions.get(page);await prepared(page);session.handler=async(route,item)=>item.kind===source?(await json(route,{detail:'PRIVATE_PROVIDER_ERROR'},code),true):false;
 if(source==='catalog')await button(page,'Refresh product catalog').click();else if(source==='templates')await button(page,'Refresh template library').click();else if(source==='history')await button(page,'Load deployment history').click();else if(source==='deployment')await button(page,'Refresh deployment status').click();else await button(page,'Apply next batch').click();
 await expect(screen(page)).toContainText('Protected library, deployment and history evidence was removed');await expect(current(page)).toHaveCount(0);await expect(currentTemplate(page)).toHaveCount(0);await expect(screen(page)).not.toContainText('PRIVATE_PROVIDER_ERROR');
 const count=session.requests.length;await page.getByRole('button',{name:'Workspace tick',exact:true}).click();expect(session.requests).toHaveLength(count);await expect(button(page,'Retry rule template access')).toBeVisible();
});
for(const context of ['selection','role','unmount'])test('held apply fences late results on '+context+' change',async({page})=>{
 const session=sessions.get(page);await prepared(page);const pending=held();session.handler=(route,item)=>item.kind==='apply'?pending.handle(route,session.backend.change(item.kind,item.id,item.body,item.raw)):false;
 await button(page,'Apply next batch').click();const request=await pending.started;
 if(context==='role')await page.getByRole('checkbox',{name:'Target Administrator permission',exact:true}).uncheck();else if(context==='unmount')await page.getByRole('button',{name:'Unmount templates',exact:true}).click();else await page.evaluate(()=>{location.hash='#/rule-templates?templateId=99999999-9999-4999-8999-999999999999&templateVersion=1';});
 await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();if(context==='unmount')await expect(screen(page)).toHaveCount(0);else await expect(screen(page)).not.toContainText('processed 10 members');expect(session.writes.filter(item=>item.kind==='apply')).toHaveLength(1);
});
test('selection ABA cannot accept the first held mutation receipt into its later owner',async({page})=>{
 const session=sessions.get(page);await prepared(page);const hash=new URL(page.url()).hash,pending=held();session.handler=(route,item)=>item.kind==='apply'?pending.handle(route,session.backend.change(item.kind,item.id,item.body,item.raw)):false;
 await button(page,'Apply next batch').click();const request=await pending.started;await page.evaluate(()=>{location.hash='#/rule-templates?templateId=99999999-9999-4999-8999-999999999999&templateVersion=1';});await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await page.evaluate(value=>{location.hash=value;},hash);await pending.release();await expect(screen(page)).not.toContainText('processed 10 members');
});
test('library full50 paging retains exact native tail and successful refresh is the only replacement',async({page})=>{
 const session=sessions.get(page);session.backend.templates.clear();for(let i=0;i<51;i++)session.backend.seedTemplate(version(opaqueId('lib|'+i),1,{title:'Recorded library '+i}));await mount(page);const library=screen(page).getByRole('region',{name:'Template library',exact:true});await expect(library).toContainText('50 received rows');
 session.handler=async(route,item)=>item.kind==='templates'&&item.before?(await json(route,{},503),true):false;await button(page,'Load older templates').click();await expect(button(page,'Retry template library')).toBeVisible();const anchor=session.reads.at(-1).before;
 await expect(library).toContainText('50 received rows');session.handler=undefined;await button(page,'Retry template library').click();await expect(library).toContainText('51 received rows');expect(session.reads.at(-1).before).toBe(anchor);
 session.handler=async(route,item)=>item.kind==='templates'?(await json(route,{},503),true):false;await button(page,'Refresh template library').click();await expect(button(page,'Retry template library')).toBeVisible();await expect(library).toContainText('51 received rows');session.handler=undefined;await button(page,'Retry template library').click();await expect(library).toContainText('50 received rows');
});
test('equal-timestamp history uses native operation anchor and retains prior evidence until explicit verified refresh',async({page})=>{
 const session=sessions.get(page);session.backend.seedJob(job());const row={operationId:auditId,auditId,recordedAtUtc:now,deploymentId,revision:2,action:'apply',items:[],pendingCount:12,sealed:false};
 session.backend.histories.set(deploymentId,Array.from({length:51},(_,i)=>({...row,operationId:opaqueId('history|'+i),revision:i+2})));
 await mount(page,'?deploymentId='+deploymentId);await expect(current(page)).toBeVisible();await button(page,'Load deployment history').click();await expect(history(page)).toContainText('50 received rows');
 const pending=held();session.handler=(route,item)=>item.kind==='history'&&item.before?pending.handle(route,session.backend.history(deploymentId,item.before),503):false;
 await button(page,'Load older deployment history').click();const request=await pending.started;await page.evaluate(()=>{location.hash='#/rule-templates';});await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();await expect(history(page)).toHaveCount(0);
 session.handler=undefined;await page.evaluate(id=>{location.hash='#/rule-templates?deploymentId='+id;},deploymentId);await expect(current(page)).toBeVisible();await button(page,'Load deployment history').click();await button(page,'Load older deployment history').click();await expect(history(page)).toContainText('51 received rows');
 const ids=await history(page).locator('tbody tr td:first-child').allTextContents();expect(ids).toEqual(session.backend.history(deploymentId).items.map(item=>item.operationId).concat(session.backend.history(deploymentId,session.backend.history(deploymentId).nextBeforeOperationId).items.map(item=>item.operationId)));
});
test('version history restores exact immutable version through reload and browser history without default substitution',async({page})=>{
 const session=sessions.get(page);session.backend.seedTemplate(version(templateId,2,{title:'Second immutable version'}));await selected(page);await button(page,'Load template versions').click();await currentTemplate(page).getByRole('region',{name:'Received immutable versions',exact:true}).getByRole('button',{name:'Select template '+templateId+' version 2',exact:true}).click();await expect(currentTemplate(page)).toContainText('selected immutable version 2');await page.reload();await expect(currentTemplate(page)).toContainText('selected immutable version 2');
 await page.goBack();await expect(currentTemplate(page)).toContainText('selected immutable version 1');await page.goForward();await expect(currentTemplate(page)).toContainText('selected immutable version 2');expect(session.writes).toHaveLength(0);
});
test('invalid exact URL selection never substitutes newest library or deployment',async({page})=>{
 await mount(page,'?templateId=bad&templateVersion=1&deploymentId=bad');await expect(screen(page)).toContainText('URL selection is invalid');await expect(currentTemplate(page)).toHaveCount(0);await expect(current(page)).toHaveCount(0);expect(sessions.get(page).writes).toHaveLength(0);
});
test('synchronous writer guard excludes apply and stop dispatched in the same browser batch',async({page})=>{
 const session=sessions.get(page);await prepared(page);await button(page,'Stop remaining deployment').click();const pending=held();session.handler=(route,item)=>item.kind==='apply'?pending.handle(route,session.backend.change(item.kind,item.id,item.body,item.raw)):false;
 await button(page,'Apply next batch').evaluate(node=>{node.click();Array.from(document.querySelectorAll('button')).find(item=>item.textContent==='Confirm stop')?.click();});await pending.started;expect(session.writes.filter(item=>item.kind==='stop')).toHaveLength(0);await pending.release();await expect(current(page)).toContainText('revision 2');
});
for(const width of [320,390,768,1440])test('dark fleet template workflow remains keyboard/axe/responsive accessible at '+width+'px',async({page},info)=>{
 await page.setViewportSize({width,height:1200});await prepared(page);await button(page,'Apply next batch').click();await expect(current(page)).toContainText('2 pending');
 await field(page,'Operation reason').focus();await page.keyboard.press('Tab');await expect(button(page,'Apply next batch')).toBeFocused();const outline=await button(page,'Apply next batch').evaluate(node=>({style:getComputedStyle(node).outlineStyle,width:parseFloat(getComputedStyle(node).outlineWidth)}));expect(outline.style).not.toBe('none');expect(outline.width).toBeGreaterThanOrEqual(2);
 await page.keyboard.press('Enter');await expect(current(page)).toContainText('0 pending · sealed');await button(page,'Load deployment history').click();await expect(history(page)).toContainText('2 received rows');
 const evidence=history(page).getByRole('region',{name:'Received deployment history',exact:true});
 await expect(evidence).toHaveCount(1);await evidence.focus();await page.keyboard.press('ArrowRight');
 const focus=await evidence.evaluate(node=>({offset:parseFloat(getComputedStyle(node).outlineOffset),width:parseFloat(getComputedStyle(node).outlineWidth),style:getComputedStyle(node).outlineStyle}));expect(focus.offset).toBeLessThanOrEqual(-focus.width);expect(focus.width).toBeGreaterThanOrEqual(2);expect(focus.style).not.toBe('none');
 expect(await page.evaluate(()=>getComputedStyle(document.documentElement).colorScheme)).toBe('dark');expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const violations=(await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a', 'wcag21aa','best-practice']).analyze()).violations;await info.attach('templates-'+width+'-axe',{body:JSON.stringify(violations,null,2),contentType:'application/json'});expect(violations).toEqual([]);
 const directory=fileURLToPath(new URL('../../artifacts/dashboard-rule-templates/screenshots/',import.meta.url));await mkdir(directory,{recursive:true});await page.evaluate(()=>scrollTo({top:0,left:0,behavior:'instant'}));await page.screenshot({path:directory+'/templates-'+width+'.png',fullPage:true});
});
