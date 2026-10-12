import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { installRuleTemplatesFixture, ruleTemplateBackend, version, job, templateId, deploymentId, values } from '../component-tests/ruleTemplatesFixture.mjs';
const origin='http://127.0.0.1:4432';
const region=page=>page.getByRole('region',{name:'Rule templates',exact:true});
const button=(page,name)=>region(page).getByRole('button',{name,exact:true});
async function json(route,body,status=200){await route.fulfill({status,contentType:'application/json',headers:{'cache-control':'no-store'},body:JSON.stringify(body)});}
async function fixture(page,{viewer=false,backend=ruleTemplateBackend(),handler,hash='#/rule-templates'}={}){
  await page.clock.install({time:new Date('2026-10-03T15:01:00Z')});
  await page.context().addCookies([{name:'dashboardRefactor',value:'1',url:origin},{name:'chartReferenceRole',value:'viewer',url:origin}]);
  await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
  const parent={errors:[],meStatus:200};page.on('pageerror',e=>parent.errors.push(e.message));
  await page.route('**/api/v1/me**',route=>{const targetId=new URL(route.request().url()).searchParams.get('targetId');const roles=viewer?['Viewer']:['Viewer','TargetAdministrator'];
    return json(route,{active:true,grantedRoles:roles,allTargetRoles:viewer?['Viewer']:[],targetId,targetRoles:targetId?roles:[]},parent.meStatus);});
  const session=await installRuleTemplatesFixture(page,{origin,backend,handler});session.parent=parent;
  await page.goto('/'+hash);await expect(region(page)).toBeVisible();return session;
}
async function create(page,session){
  const editor=region(page).getByRole('form',{name:'Rule template editor',exact:true});
  await expect(editor.getByRole('combobox',{name:'Product signal',exact:true})).toBeEnabled();
  await editor.getByRole('textbox',{name:'Template title',exact:true}).fill('SYNTHETIC production connections');
  await editor.getByRole('textbox',{name:'Intended rule name',exact:true}).fill('connections.production');
  await editor.getByRole('spinbutton',{name:'Threshold',exact:true}).fill('100');
  await editor.getByRole('spinbutton',{name:'Hysteresis',exact:true}).fill('10');
  await editor.getByRole('textbox',{name:'Template reason',exact:true}).fill('SYNTHETIC reviewed baseline');
  await button(page,'Save template').click();
  await expect(region(page).getByRole('region',{name:'Current template version',exact:true})).toContainText('selected immutable version 1');
  const command=session.writes.find(item=>item.kind==='create_template');
  expect(command.body.values).toMatchObject({name:'connections.production',threshold:100,hysteresis:10,enabled:true});
  await expect.poll(()=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('templateId')).toBe(command.body.templateId);return command.body.templateId;
}
async function preview(page,session){
  const form=region(page).getByRole('form',{name:'Group deployment preview',exact:true});
  await form.getByRole('textbox',{name:'Group label',exact:true}).fill(' Production ');
  await form.getByRole('textbox',{name:'Deployment reason',exact:true}).fill('SYNTHETIC deploy to complete native cohort');
  await button(page,'Preview group deployment').click();
  const status=region(page).getByRole('region',{name:'Current deployment status',exact:true});await expect(status).toContainText('12 complete frozen members');
  const command=session.writes.findLast(item=>item.kind==='preview');expect(command.body.group).toEqual({kind:'environment',value:'production'});
  await expect.poll(()=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('deploymentId')).toBe(command.body.deploymentId);return command.body.deploymentId;
}
for(const width of [320,390,768,1440])test(`Built template library and complete group apply stop rollback are accessible at ${width}`,async({page},info)=>{
  await page.setViewportSize({width,height:1100});const session=await fixture(page);const id=await create(page,session);
  await expect(page.getByRole('combobox',{name:'Target server',exact:true})).toHaveCount(0);
  await expect(page.getByRole('region',{name:'Workspace time context',exact:true})).toHaveCount(0);
  await button(page,'Edit template version').click();
  await region(page).getByRole('spinbutton',{name:'Threshold',exact:true}).fill('120');
  await region(page).getByRole('textbox',{name:'Template reason',exact:true}).fill('SYNTHETIC explicit next version');
  await button(page,'Publish template version').click();
  await expect(region(page).getByRole('region',{name:'Current template version',exact:true})).toContainText('selected immutable version 2');
  expect(session.backend.readTemplate(id,1).values.threshold).toBe(100);expect(session.backend.readTemplate(id,2).values.threshold).toBe(120);
  const deployment=await preview(page,session);const status=region(page).getByRole('region',{name:'Current deployment status',exact:true});
  await status.getByRole('textbox',{name:'Operation reason',exact:true}).fill('SYNTHETIC explicit first batch');await button(page,'Apply next batch').click();
  await expect(status).toContainText('2 pending');expect(session.writes.findLast(item=>item.kind==='apply').body.expectedRevision).toBe(1);
  await button(page,'Stop remaining deployment').click();await button(page,'Confirm stop').click();await expect(status).toContainText('0 pending');
  expect(session.backend.readJob(deployment).items.filter(item=>item.status==='applied')).toHaveLength(10);
  expect(session.backend.readJob(deployment).items.filter(item=>item.status==='stopped')).toHaveLength(2);
  const changed=session.backend.readJob(deployment).items.find(item=>item.status==='applied');const key=changed.targetId+'|'+changed.desiredValues.name;
  const current=session.backend.rules.get(key);session.backend.rules.set(key,{...current,revision:current.revision+1,values:{...current.values,threshold:250}});
  await button(page,'Preview rollback').click();await expect(status).toContainText('rollback');await expect(status).toContainText('10 complete frozen members');
  await status.getByRole('textbox',{name:'Operation reason',exact:true}).fill('SYNTHETIC guarded rollback');
  await button(page,'Apply next batch').click();await expect(status).toContainText('0 pending');
  const rollback=session.backend.readJob(session.writes.findLast(item=>item.kind==='rollback').body.deploymentId);
  expect(rollback.items.filter(item=>item.status==='conflict')).toHaveLength(1);expect(rollback.items.filter(item=>item.status==='applied')).toHaveLength(9);
  expect(session.backend.rules.get(key).values.threshold).toBe(250);
  await button(page,'Load deployment history').click();await expect(region(page).getByRole('region',{name:'Immutable deployment history',exact:true})).toContainText('apply: 10 processed');
  const axe=await new AxeBuilder({page}).include('.rule-templates-page').analyze();expect(axe.violations).toEqual([]);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
  await page.keyboard.press('Tab');await expect.poll(()=>page.evaluate(()=>document.activeElement?.matches(':focus-visible'))).toBe(true);
  const folder=fileURLToPath(new URL('../../artifacts/dashboard-rule-templates/screenshots/',import.meta.url));await mkdir(folder,{recursive:true});
  await page.evaluate(()=>scrollTo(0,0));await page.screenshot({path:folder+`workspace-templates-${width}.png`,fullPage:true,animations:'disabled'});
  expect(session.errors).toEqual([]);expect(session.parent.errors).toEqual([]);
  await info.attach('native-shaped fixture provenance',{body:JSON.stringify({templateId:id,deploymentId:deployment,writes:session.writes},null,2),contentType:'application/json'});
});
test('Built exact immutable version and deployment URL restore independently of copied diagnostic scope',async({page})=>{
  const backend=ruleTemplateBackend();backend.seedTemplate(version(templateId,1));backend.seedTemplate(version(templateId,2,{values:values({threshold:200})}));backend.seedJob(job(deploymentId,1));
  const session=await fixture(page,{backend,hash:'#/rule-templates?'+new URLSearchParams({templateId,templateVersion:'1',deploymentId,target:'11111111-1111-4111-8111-000000000001',databaseId:'7',range:'custom',from:'2026-09-26T08:00:00Z',to:'2026-09-26T12:00:00Z'})});
  await expect(region(page).getByRole('region',{name:'Current template version',exact:true})).toContainText('selected immutable version 1');
  await expect(region(page).getByRole('region',{name:'Current deployment status',exact:true})).toContainText(deploymentId);
  await expect(page.getByRole('combobox',{name:'Target server',exact:true})).toHaveCount(0);expect(session.writes).toEqual([]);
  await page.reload();await expect(region(page).getByRole('region',{name:'Current template version',exact:true})).toContainText('selected immutable version 1');
  expect(session.reads.filter(item=>item.kind==='template').every(item=>item.version===1)).toBe(true);expect(session.errors).toEqual([]);
});
test('Built viewer sees required role and no protected requests or admin navigation',async({page})=>{
  const session=await fixture(page,{viewer:true});await expect(region(page)).toContainText('Target Administrator role is required');
  await expect(button(page,'Save template')).toHaveCount(0);expect(session.reads).toEqual([]);expect(session.writes).toEqual([]);
  await page.getByRole('navigation',{name:'Main navigation',exact:true}).getByRole('link',{name:'Alerts',exact:true}).click();
  await expect(page.getByRole('link',{name:'Rule templates',exact:true})).toHaveCount(0);
});
test('Built confirmed creation survives an unrelated transient workspace authority refresh and retries GET only',async({page})=>{
  let failRead=true;const session=await fixture(page,{handler:async(route,item)=>{if(item.kind==='template'&&failRead){await json(route,{},503);return true;}return false;}});
  const editor=region(page).getByRole('form',{name:'Rule template editor',exact:true});await expect(editor.getByRole('combobox',{name:'Product signal',exact:true})).toBeEnabled();
  await editor.getByRole('textbox',{name:'Template title',exact:true}).fill('SYNTHETIC recovery');await editor.getByRole('textbox',{name:'Intended rule name',exact:true}).fill('recovery.connections');await editor.getByRole('textbox',{name:'Template reason',exact:true}).fill('SYNTHETIC captured intention');
  await button(page,'Save template').click();await expect(button(page,'Retry template status')).toBeVisible();const writes=session.writes.length;
  session.parent.meStatus=503;await page.getByRole('button',{name:'↻ Refresh',exact:true}).click();await expect(page.getByText('Workspace access refresh is unavailable.',{exact:false})).toBeVisible();
  await expect(button(page,'Retry template status')).toBeVisible();failRead=false;await button(page,'Retry template status').click();
  await expect(region(page).getByRole('region',{name:'Current template version',exact:true})).toContainText('SYNTHETIC recovery');expect(session.writes).toHaveLength(writes);
});
test('Built uncertain apply retains captured bytes after a successful refresh and never silently advances the batch',async({page})=>{
  const backend=ruleTemplateBackend();backend.seedTemplate();backend.seedJob();let uncertain=true;
  const session=await fixture(page,{backend,hash:'#/rule-templates?deploymentId='+deploymentId,handler:async(route,item)=>{
    if(item.kind==='apply'&&uncertain){uncertain=false;backend.change(item.kind,item.id,item.body,item.raw);await json(route,{},503);return true;}return false;}});
  await expect(region(page).getByRole('region',{name:'Current deployment status',exact:true})).toContainText('12 complete frozen members');
  await region(page).getByRole('textbox',{name:'Operation reason',exact:true}).fill('SYNTHETIC exact captured batch');await button(page,'Apply next batch').click();await expect(button(page,'Retry template operation')).toBeVisible();
  await button(page,'Refresh deployment status').click();await expect(region(page).getByRole('region',{name:'Current deployment status',exact:true})).toContainText('2 pending');
  await expect(button(page,'Retry template operation')).toBeVisible();await expect(button(page,'Apply next batch')).toBeDisabled();
  await button(page,'Retry template operation').click();await expect(button(page,'Retry template operation')).toHaveCount(0);
  const writes=session.writes.filter(item=>item.kind==='apply');expect(writes).toHaveLength(2);expect(writes[1].raw).toBe(writes[0].raw);
  expect(backend.readJob(deploymentId).pendingCount).toBe(2);expect(backend.histories.get(deploymentId)).toHaveLength(1);
});
