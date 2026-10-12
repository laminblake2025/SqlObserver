import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
const origin='http://127.0.0.1:4432',target='11111111-1111-4111-8111-000000000001';
const from='2026-09-26T08:00:00.123456Z',to='2026-09-26T12:00:00.654321Z';
const id=index=>`33333333-3333-4333-8333-${String(index+1).padStart(12,'0')}`;
const panel=page=>page.locator('section.analytics-surface');
const category=page=>page.getByRole('combobox',{name:'Evidence surface',exact:true});
const scope=page=>Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const href=(surface='diagnostics/search',options={})=>`/#/analytics?${new URLSearchParams({target,range:'custom',from,to,compare:'1',surface,...options})}`;
const sessions=new WeakMap();
test.beforeEach(async({page})=>{
 const state={errors:[],reads:[]};sessions.set(page,state);
 page.on('pageerror',error=>state.errors.push(error.message));
 page.on('request',request=>{const url=new URL(request.url());if(url.pathname.includes('/analytics/'))state.reads.push(url);});
 await page.context().addCookies([{name:'dashboardRefactor',value:'1',url:origin},{name:'databaseScopeFixture',value:'1',url:origin},{name:'analyticsWorkflowFixture',value:'1',url:origin}]);
 await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
});
test.afterEach(async({page})=>expect(sessions.get(page).errors).toEqual([]));
async function checked(page){
 expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1)).toBe(true);
 expect((await new AxeBuilder({page}).analyze()).violations).toEqual([]);
}
async function inspectRecord(screen,index){
 const row=screen.getByRole('row').filter({hasText:id(index)});
 await expect(row).toHaveCount(1);
 const details=row.locator('details');
 if(!await details.evaluate(element=>element.open))await details.locator('summary').click();
 await expect(row.locator('pre')).toBeVisible();await expect(row.locator('pre')).toContainText(id(index));
}
function held(){
 let start,release,end;const started=new Promise(done=>start=done),gate=new Promise(done=>release=done),ended=new Promise(done=>end=done);
 return {started,release:async()=>{release();await ended;},handle:async(route,response)=>{start(route.request());await gate;await route.fulfill({response}).catch(()=>{});end();}};
}

for(const width of [320,1440])test(`Analytics URL category, database and exact Rewind window restore at ${width}`,async({page})=>{
 await page.setViewportSize({width,height:1000});await page.goto(href());
 await expect(category(page)).toHaveValue('diagnostics/search');await inspectRecord(panel(page),0);
 await category(page).selectOption('jobs');await expect(panel(page)).toContainText('not filtered');
 expect(scope(page)).toMatchObject({surface:'jobs',from,to,compare:'1'});await page.reload();
 await expect(category(page)).toHaveValue('jobs');await inspectRecord(panel(page),0);
 await category(page).selectOption('diagnostics/search');
 await page.getByRole('combobox',{name:'Workspace database',exact:true}).selectOption('6');
 await expect.poll(()=>scope(page)).toMatchObject({surface:'diagnostics/search',databaseId:'6',from,to,compare:'1'});
 await page.getByRole('button',{name:'Edit UTC range',exact:true}).click();
 await page.getByRole('button',{name:'Apply range',exact:true}).click();
 expect(scope(page)).toMatchObject({surface:'diagnostics/search',databaseId:'6',from,to});
 await page.reload();await expect(category(page)).toHaveValue('diagnostics/search');
 await inspectRecord(panel(page),0);await checked(page);
});

test('Analytics malformed and duplicate category URLs refuse reads until an explicit choice',async({page})=>{
 for(const surface of ['invalid','','jobs&surface=incidents']){
  await page.goto(href().replace('surface=diagnostics%2Fsearch',`surface=${surface}`));
  await expect(page.getByRole('alert').filter({hasText:'analytics surface in this URL is invalid'})).toBeVisible();
  await expect(panel(page)).toHaveCount(0);expect(sessions.get(page).reads).toHaveLength(0);
 }
 await category(page).selectOption('evidence-packets');
 await inspectRecord(panel(page),0);
 await expect(panel(page)).toContainText('Unavailable');expect(scope(page).surface).toBe('evidence-packets');
});

test('Analytics inventory remains usable outside the time-filtered surface range',async({page})=>{
 await page.goto(href('jobs',{from:'2026-09-01T00:00:00Z',to:'2026-09-20T00:00:00Z'}));
 await inspectRecord(panel(page),0);
 const first=sessions.get(page).reads[0];expect(first.searchParams.has('fromUtc')).toBe(false);expect(first.searchParams.has('toUtc')).toBe(false);
 await category(page).selectOption('diagnostics/search');
 await expect(page.getByRole('alert').filter({hasText:'up to 7 days'})).toBeVisible();await expect(panel(page)).toHaveCount(0);
 expect(sessions.get(page).reads).toHaveLength(1);
});

for(const surface of ['diagnostics/search','jobs'])test(`Analytics ${surface} held continuation survives a moving Live workspace tick`,async({page})=>{
 await page.clock.install({time:new Date('2026-09-30T13:00:00Z')});await page.goto(href(surface,{range:'1h'}));
 const screen=panel(page),read=held();await expect(screen.getByRole('button',{name:'Next evidence page',exact:true})).toBeEnabled();
 await page.clock.runFor(50_000);
 await page.route(`**/analytics/${surface}?*`,async route=>{
  if(!new URL(route.request().url()).searchParams.has('cursor'))return route.continue();
  await read.handle(route,await route.fetch());
 });
 await screen.getByRole('button',{name:'Next evidence page',exact:true}).click();const request=await read.started;
 const bounds=Object.fromEntries(new URL(request.url()).searchParams);
 await page.clock.runFor(10_000);expect(request.failure()).toBeNull();
  await read.release();await inspectRecord(screen,100);
  const receivedScope=await screen.locator('.analytics-workflow-scope').textContent();
  if(surface==='diagnostics/search')expect(receivedScope).toContain('Received evidence window:');
 const reads=sessions.get(page).reads.length;
 await page.getByRole('button',{name:'↻ Refresh',exact:true}).click();await expect(screen).toContainText('Retained');
  expect(sessions.get(page).reads).toHaveLength(reads);expect(scope(page)).toMatchObject({surface,range:'1h'});
  await expect(screen.locator('.analytics-workflow-scope')).toHaveText(receivedScope);
 await screen.getByRole('button',{name:'Previous evidence page',exact:true}).click();
 await inspectRecord(screen,0);expect(sessions.get(page).reads).toHaveLength(reads);
 await screen.getByRole('button',{name:'Return analytics to latest',exact:true}).click();
 await expect.poll(()=>sessions.get(page).reads.length).toBe(reads+1);
 const latest=sessions.get(page).reads.at(-1);expect(latest.searchParams.has('cursor')).toBe(false);
 if(surface==='diagnostics/search')expect(latest.searchParams.get('toUtc')).not.toBe(bounds.toUtc);
});

test('Analytics categories restore their own visited receipts without a new read',async({page})=>{
 await page.goto(href());await panel(page).getByRole('button',{name:'Next evidence page',exact:true}).click();
 await inspectRecord(panel(page),100);
 await category(page).selectOption('jobs');await panel(page).getByRole('button',{name:'Next evidence page',exact:true}).click();
 await inspectRecord(panel(page),100);
 const before=sessions.get(page).reads.length;await category(page).selectOption('diagnostics/search');
 await inspectRecord(panel(page),100);
 expect(sessions.get(page).reads).toHaveLength(before);
});

for(const status of [401,403])test(`Analytics HTTP ${status} latest read removes previously retained protected receipt`,async({page})=>{
 await page.goto(href());await panel(page).getByRole('button',{name:'Next evidence page',exact:true}).click();
 await inspectRecord(panel(page),100);
 await page.route('**/analytics/diagnostics/search?*',route=>route.fulfill({status,body:'SECRET_PROVIDER_DENIAL'}));
 await panel(page).getByRole('button',{name:'Return analytics to latest',exact:true}).click();
 await expect(panel(page).getByRole('alert')).toBeVisible();await expect(panel(page).getByRole('table')).toHaveCount(0);
 await expect(page.locator('body')).not.toContainText('SECRET_PROVIDER_DENIAL');await checked(page);
});

for(const width of [320,1440])test(`Analytics retained native diagnostic metadata stays readable and bounded at ${width}`,async({page})=>{
 await page.setViewportSize({width,height:1000});await page.goto(href());
 await panel(page).getByRole('button',{name:'Next evidence page',exact:true}).click();
 await inspectRecord(panel(page),100);
 await panel(page).getByText('Evidence details',{exact:true}).click();await expect(panel(page)).toContainText('SYNTHETIC evidence 101');
 const previous=panel(page).getByRole('button',{name:'Previous evidence page',exact:true});await previous.focus();await expect(previous).toBeFocused();
 await checked(page);await page.evaluate(()=>document.activeElement?.blur());
 const box=await panel(page).boundingBox();await page.setViewportSize({width,height:Math.ceil(box.height)+800});await panel(page).scrollIntoViewIfNeeded();
 await mkdir('../artifacts/dashboard-analytics-workflow/screenshots',{recursive:true});
 await panel(page).screenshot({path:`../artifacts/dashboard-analytics-workflow/screenshots/built-analytics-${width}.png`});
});
