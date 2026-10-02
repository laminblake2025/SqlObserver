import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const target='11111111-1111-4111-8111-000000000001';
const run='22222222-2222-4222-8222-222222222222', entry='d'.repeat(64);
const from='2026-09-28T00:00:00Z',to='2026-09-29T00:00:00Z';
const selection={targetId:target,databaseId:6,databaseIncarnation:'a'.repeat(64),queryFingerprint:'b'.repeat(64),planFingerprint:'c'.repeat(64)};
const href=`/#/queries?${new URLSearchParams({target,range:'custom',from,to,queryView:'cache',cacheDatabase:'6',cacheIncarnation:selection.databaseIncarnation,cacheQuery:selection.queryFingerprint,cachePlan:selection.planFingerprint})}`;
const row={collectionRunId:run,cacheEntryFingerprint:entry,sourceEpoch:'e'.repeat(64),observedAtUtc:'2026-09-28T12:00:00Z',committedAtUtc:'2026-09-28T12:00:01Z',captureComplete:true,transition:'comparable',intervalFromUtc:'2026-09-28T11:00:00Z',intervalToUtc:'2026-09-28T12:00:00Z',cpuMilliseconds:'1.000',durationMilliseconds:'2.000',executions:'1',logicalReads:'2',writes:'3',rows:'4'};
async function setup(page,reader=true) {
 await page.route('**/api/v1/me*',route=>{
  const roles=reader?['Viewer','QueryTextReader']:['Viewer'];const targetId=new URL(route.request().url()).searchParams.get('targetId');
  return route.fulfill({json:{active:true,targetId,grantedRoles:roles,allTargetRoles:roles,targetRoles:targetId?roles:[]}});
 });
 await page.route('**/query-performance/cache/top?*',route=>{
  const url=new URL(route.request().url());return route.fulfill({json:{targetId:target,fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc'),metric:url.searchParams.get('metric'),semantics:'plan_cache_delta',coverage:'observed_entries_only',sourceFrames:2,incompleteFrames:0,comparableRows:1,unavailableRows:0,boundaryRows:0,hasMore:false,items:[{...selection,...row,comparableRows:1,firstObservedAtUtc:row.intervalFromUtc,lastObservedAtUtc:row.observedAtUtc}]}});
 });
 await page.route('**/query-performance/cache/databases/6/history/*/plans/*?*',route=>{
  const url=new URL(route.request().url());return route.fulfill({json:{...selection,fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc'),snapshotUtc:'2026-09-29T00:00:00Z',revision:1,totalRows:1,nextCursor:null,semantics:'plan_cache_delta',coverage:'observed_entries_only',items:[row]}});
 });
}
const content=(kind,value)=>({...selection,collectionRunId:run,cacheEntryFingerprint:entry,payloadKind:kind,status:'available',reason:'available',observedAtUtc:row.observedAtUtc,text:kind==='text'?value:null,xml:kind==='plan'?value:null});
async function openHistory(page) {
 await page.goto(href);
 // Use the real drilldown action as well as the shareable query view.
 const inspect=page.getByRole('button',{name:'Inspect cccccccccccc…'});
 await inspect.click();
 return page.getByRole('region',{name:'Cache query interval history'});
}
test('cache content opens only on demand, stays inert, is uncached and is absent after reload',async({page})=>{
 await setup(page);const requests=[];
 const text='<script>window.cacheContentExecuted=true</script><img src=x onerror="window.cacheContentExecuted=true">';
 await page.route('**/runs/*/entries/*/content/*?*',route=>{
  requests.push(route.request().url());const kind=new URL(route.request().url()).pathname.split('/').at(-1);
  return route.fulfill({json:content(kind,kind==='text'?text:'<ShowPlanXML><script>window.cacheContentExecuted=true</script></ShowPlanXML>')});
 });
 const history=await openHistory(page);await expect(history.getByRole('button',{name:'Open text',exact:true})).toBeVisible();
 expect(requests).toHaveLength(0);
 await history.getByRole('button',{name:'Open text',exact:true}).click();
 const panel=page.getByRole('region',{name:'Cache observation content'});
 await expect(panel.getByLabel('Captured query text')).toHaveText(text);
 expect(requests).toHaveLength(1);expect(requests[0]).toContain(`/runs/${run}/entries/${entry}/content/text`);
 expect(new URL(requests[0]).searchParams.get('databaseIncarnation')).toBe(selection.databaseIncarnation);
 await expect(panel.locator('script,img')).toHaveCount(0);expect(await page.evaluate(()=>window.cacheContentExecuted)).toBeUndefined();
 await history.getByRole('button',{name:'Open plan XML',exact:true}).click();
 await expect(panel.getByLabel('Captured plan XML')).toContainText('<ShowPlanXML>');
 expect(requests).toHaveLength(2);await expect(panel.locator('script')).toHaveCount(0);
 expect((await new AxeBuilder({page}).include('section[aria-label="Cache observation content"]').analyze()).violations).toEqual([]);
 await panel.getByRole('button',{name:'Close cache content'}).click();await expect(panel).toHaveCount(0);
 await history.getByRole('button',{name:'Open text',exact:true}).click();await expect(panel.getByLabel('Captured query text')).toHaveText(text);
 expect(requests).toHaveLength(3);await expect(page).not.toHaveURL(new RegExp(run));
 await page.reload();await expect(history.getByRole('table')).toBeVisible();await expect(panel).toHaveCount(0);expect(requests).toHaveLength(3);
});
test('changing the window cancels pending content and prevents stale plaintext from appearing',async({page})=>{
 await setup(page);let release,started;const gate=new Promise(resolve=>{release=resolve;});const requested=new Promise(resolve=>{started=resolve;});
 await page.route('**/runs/*/entries/*/content/*?*',async route=>{started();await gate;await route.fulfill({json:content('text','stale cache plaintext')}).catch(()=>{});});
 const history=await openHistory(page);await history.getByRole('button',{name:'Open text',exact:true}).click();await requested;
 await page.getByRole('button',{name:'Edit UTC range'}).click();
 await page.getByLabel('From (UTC)',{exact:true}).fill('2026-09-28T08:00');await page.getByLabel('To (UTC)',{exact:true}).fill('2026-09-28T20:00');
 await page.getByRole('button',{name:'Apply range'}).click();
 await expect(page.getByRole('region',{name:'Cache observation content'})).toHaveCount(0);release();
 await expect(history.getByRole('table')).toBeVisible();await expect(page.getByText('stale cache plaintext')).toHaveCount(0);
});
test('viewer sees the required role and cannot trigger a content request',async({page})=>{
 await setup(page,false);let reads=0;await page.route('**/runs/*/entries/*/content/*?*',route=>{reads++;return route.fulfill({status:403});});
 const history=await openHistory(page);await expect(history.getByText('Opening captured query text and plan XML requires the QueryTextReader role and a read role on this server.')).toBeVisible();
 await expect(history.getByRole('button',{name:'Open text',exact:true})).toHaveCount(0);await expect(history.getByRole('button',{name:'Open plan XML',exact:true})).toHaveCount(0);expect(reads).toBe(0);
});
