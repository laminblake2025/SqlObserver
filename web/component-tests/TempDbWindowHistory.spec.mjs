import {expect,test} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {tempDbHistoryBinding as binding,tempDbHistoryAlpha as alpha,tempDbHistoryBeta as beta,tempDbHistoryReceipt,installTempDbHistoryFixture,tempDbHistoryJson,tempDbHistoryHeld} from './tempDbWindowHistoryFixture.mjs';

const sessions=new WeakMap(),panel=p=>p.getByRole('region',{name:'TempDB window history',exact:true}),detail=p=>panel(p).getByRole('region',{name:'Selected TempDB receipt',exact:true});
const button=(p,name)=>panel(p).getByRole('button',{name,exact:true}),receipts=p=>panel(p).locator('details').filter({hasText:'Browse TempDB collection receipts'});
const params=p=>Object.fromEntries(new URLSearchParams(new URL(p.url()).hash.split('?')[1]));
const href=(extra={})=>'/component-tests/tempdb-history.html#/operations?'+new URLSearchParams({target:alpha,check:'tempdb',range:'custom',from:binding.fromUtc,to:binding.toUtc,...extra});
async function mount(page,extra={}){await page.goto(href(extra));await expect(detail(page)).toContainText(extra.tempDbRun??tempDbHistoryReceipt().runId);}
async function browse(page){const summary=receipts(page).locator('summary').first();if(!await receipts(page).evaluate(e=>e.open))await summary.click();}
async function files(page){const summary=detail(page).locator('summary').filter({hasText:/Browse TempDB file/iu});await expect(summary).toBeVisible();if(!await summary.evaluate(e=>e.parentElement.open))await summary.click();return detail(page).getByRole('table').last();}
const responseFor=(session,read)=>read.selection?session.backend.snapshot(read.binding,read.selection,read.cursor,read.limit):session.backend.read(read.binding,read.cursor,read.limit);

async function containedEvidence(page){
 const geometry=await panel(page).evaluate(root=>{
  const box=root.getBoundingClientRect(),style=getComputedStyle(root);
  const left=box.left+parseFloat(style.borderLeftWidth)+parseFloat(style.paddingLeft);
  const right=box.right-parseFloat(style.borderRightWidth)-parseFloat(style.paddingRight);
  const visible=element=>element.getClientRects().length>0&&element.getBoundingClientRect().width>0;
  const nodes=[...root.querySelectorAll(':scope > *, header, p, details, summary, .tempdb-receipt, .table-scroll')].filter(visible).map(element=>{
   const rect=element.getBoundingClientRect();
   return{label:element.tagName+' '+(element.getAttribute('aria-label')??element.className),left:rect.left,right:rect.right};
  });
  const scrolls=[...root.querySelectorAll('.table-scroll')].filter(visible).map(element=>{
   const parent=element.parentElement,parentBox=parent.getBoundingClientRect(),parentStyle=getComputedStyle(parent);
   const available=parentBox.width-parseFloat(parentStyle.paddingLeft)-parseFloat(parentStyle.paddingRight)-parseFloat(parentStyle.borderLeftWidth)-parseFloat(parentStyle.borderRightWidth);
   return{label:element.getAttribute('aria-label'),client:element.clientWidth,scroll:element.scrollWidth,available,overflow:getComputedStyle(element).overflowX};
  });
  return{left,right,viewport:innerWidth,nodes,scrolls};
 });
 expect(geometry.right-geometry.left).toBeGreaterThan(0);
 for(const node of geometry.nodes){
  expect(node.left,node.label+' left edge must fit panel content').toBeGreaterThanOrEqual(geometry.left-1);
  expect(node.right,node.label+' right edge must fit panel content').toBeLessThanOrEqual(geometry.right+1);
  expect(node.left,node.label+' must fit viewport').toBeGreaterThanOrEqual(-1);
  expect(node.right,node.label+' must fit viewport').toBeLessThanOrEqual(geometry.viewport+1);
 }
 for(const scroll of geometry.scrolls){
  expect(scroll.client,scroll.label+' must have a real local viewport').toBeGreaterThan(0);
  expect(scroll.client,scroll.label+' must fit parent content').toBeLessThanOrEqual(scroll.available+1);
  expect(scroll.client,scroll.label+' must fit panel content').toBeLessThanOrEqual(geometry.right-geometry.left+1);
  expect(['auto','scroll'],scroll.label+' must scroll locally').toContain(scroll.overflow);
  if(geometry.viewport<=390)expect(scroll.scroll,scroll.label+' must overflow locally on mobile').toBeGreaterThan(scroll.client);
 }
 return geometry;
}
async function openReceiptIdentities(page){
 for(const summary of await panel(page).locator('.tempdb-receipt summary').all()){
  if(!await summary.evaluate(element=>element.parentElement.open))await summary.press('Enter');
 }
}

test.beforeEach(async({page})=>{await page.clock.setFixedTime(new Date('2026-10-04T12:00:00Z'));await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));sessions.set(page,await installTempDbHistoryFixture(page));});
test.afterEach(async({page})=>expect(sessions.get(page).errors,'Only bounded registered native-shaped reads may run').toEqual([]));

for(const mode of ['failedLatest','missingLatest','emptyLatest'])test(mode+' is the default completion receipt; no older successful state is substituted',async({page})=>{
 const session=sessions.get(page);session.backend.settings[mode]=true;await mount(page);await expect(detail(page)).not.toContainText('9007199254740993');await expect(detail(page)).toContainText(mode==='failedLatest'?/collection attempt failed|permission_denied/iu:mode==='missingLatest'?/payload|missing|not retained/iu:/empty/iu);
 expect(session.reads.filter(r=>r.selection).map(r=>r.selection.runId)).toEqual([tempDbHistoryReceipt().runId]);
 await browse(page);await button(page,'Inspect TempDB receipt '+tempDbHistoryReceipt(1).runId).click();await expect(detail(page)).toContainText(tempDbHistoryReceipt(1).runId);await expect(detail(page)).toContainText('9007199254741505');
});
test('summary and files share the selected run and source observation; exact bytes survive beyond JS safe precision',async({page})=>{
 await mount(page);await expect(detail(page)).toContainText('9007199254744047');await expect(detail(page)).toContainText('9007199254741505');await expect(detail(page)).toContainText(/source|observed/iu);const table=await files(page);await expect(table.locator('tbody>tr')).toHaveCount(3);await expect(table).toContainText('9007199254741999');await expect(table).toContainText('9007199254740993');await expect(table).toContainText('1006');
 const exact=sessions.get(page).reads.find(r=>r.selection);expect(exact.selection).toEqual({runId:tempDbHistoryReceipt().runId,sampleTargetRevision:1});await expect(detail(page)).not.toContainText(/growth forecast|current healthy|long.running/iu);
});
test('partial source retains its numeric facts and makes loss and truncation visible without an all-clear',async({page})=>{
 sessions.get(page).backend.settings.partial=true;await mount(page);await expect(detail(page)).toContainText(/partial|loss/iu);await expect(detail(page)).toContainText(/truncat|incomplete/iu);await expect(detail(page)).toContainText('9007199254744047');await files(page);await expect(detail(page)).not.toContainText(/All clear/iu);
});
test('explicit pin outside loaded list page restores independently and browser Back/Forward preserves each tuple',async({page})=>{
 sessions.get(page).backend.settings.count=201;const old=tempDbHistoryReceipt(200),next=tempDbHistoryReceipt(1);await page.goto(href({tempDbRun:old.runId,tempDbRevision:'1'}));await expect(detail(page)).toContainText(old.runId);await browse(page);await expect(button(page,'Inspect TempDB receipt '+old.runId)).toHaveCount(0);await button(page,'Inspect TempDB receipt '+next.runId).click();await expect(detail(page)).toContainText(next.runId);await page.goBack();await expect(detail(page)).toContainText(old.runId);await page.goForward();await expect(detail(page)).toContainText(next.runId);await page.reload();await expect(detail(page)).toContainText(next.runId);
});
test('explicit older sample revision remains distinct from the current configuration revision',async({page})=>{
 await mount(page,{tempDbRun:tempDbHistoryReceipt().runId,tempDbRevision:'1'});await page.getByRole('combobox',{name:'Target revision',exact:true}).selectOption('2');await expect(detail(page)).toContainText(tempDbHistoryReceipt().runId);await expect.poll(()=>sessions.get(page).reads.at(-1).binding.targetRevision).toBe(2);expect(sessions.get(page).reads.at(-1).selection.sampleTargetRevision).toBe(1);await expect(panel(page)).toContainText(/revision/iu);
});
test('invalid repeated pin has no exact read or default substitution until explicit clear',async({page})=>{
 await page.goto(href({tempDbRun:'bad',tempDbRevision:'01'})+'&tempDbRun=also-bad');await expect(detail(page)).toContainText(/invalid/iu);expect(sessions.get(page).reads.some(r=>r.selection)).toBe(false);await button(page,'Clear selected TempDB receipt').click();await expect(detail(page)).toContainText(tempDbHistoryReceipt().runId);expect(params(page).tempDbRun).toBeUndefined();
});
test('retired or out-of-window selected receipt is explicit and never falls back to the latest run',async({page})=>{
 const session=sessions.get(page),selected=tempDbHistoryReceipt(1);session.backend.settings.detailAbsent=true;await page.goto(href({tempDbRun:selected.runId,tempDbRevision:'1'}));await expect(detail(page)).toContainText(/not retained|unavailable/iu);await expect(detail(page)).toContainText(selected.runId);await expect(detail(page)).not.toContainText('9007199254740993');expect(session.reads.filter(r=>r.selection).every(r=>r.selection.runId===selected.runId)).toBe(true);
});
test('receipt paging keeps native cursor bindings and cached navigation adds no reads',async({page})=>{
 const session=sessions.get(page);session.backend.settings.count=101;await mount(page);await browse(page);await button(page,'Next TempDB receipts page').click();await expect.poll(()=>session.reads.filter(r=>!r.selection&&r.cursor).length).toBe(1);const count=session.reads.length,continuation=session.reads.find(r=>!r.selection&&r.cursor);expect(continuation.binding).toEqual(binding);await button(page,'First TempDB receipts page').click();await button(page,'Next TempDB receipts page').click();await button(page,'Previous TempDB receipts page').click();expect(session.reads).toHaveLength(count);
});
test('file continuation promotes the same default tuple without cancel, restart or lost page',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await mount(page);const table=await files(page);await expect(table.locator('tbody>tr')).toHaveCount(100);const held=tempDbHistoryHeld();session.handler=(route,read)=>read.selection&&read.cursor?held.handle(route,responseFor(session,read)):false;const before=session.reads.length;await button(page,'Next TempDB files page').click();const request=await held.started;await expect(page).toHaveURL(/tempDbRun=.*tempDbRevision=1/u);expect(request.failure()).toBeNull();expect(session.reads).toHaveLength(before+1);await held.finish();await expect(detail(page).getByRole('table').last().locator('tbody>tr')).toHaveCount(1);await button(page,'Previous TempDB files page').click();await expect(detail(page).getByRole('table').last().locator('tbody>tr')).toHaveCount(100);expect(session.reads).toHaveLength(before+1);
});
for(const expired of [false,true])test((expired?'expired cursor':'changed epoch')+' retains received files and exact retry before explicit replacement',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await mount(page);await files(page);if(expired)session.backend.settings.expired=true;else session.backend.settings.epoch++;await button(page,'Next TempDB files page').click();await expect(button(page,'Retry selected TempDB receipt read')).toBeVisible();await expect(detail(page)).toContainText('9007199254740993');const first=session.reads.at(-1),beforeRetry=session.reads.length;await button(page,'Retry selected TempDB receipt read').click();await expect.poll(()=>session.reads.length).toBe(beforeRetry+1);expect(session.reads.at(-1).cursor).toBe(first.cursor);session.backend.settings.expired=false;await button(page,'Refresh selected TempDB receipt').click();await expect(detail(page).getByRole('alert')).toHaveCount(0);await button(page,'Next TempDB files page').click();await expect(detail(page).getByRole('table').last().locator('tbody>tr')).toHaveCount(1);
});
test('ordinary refresh failure preserves investigated source; poll cannot silently replace it and retry is exact',async({page})=>{
 await mount(page,{tempDbRun:tempDbHistoryReceipt(1).runId,tempDbRevision:'1'});const session=sessions.get(page);session.handler=async(route,read)=>read.selection?(await tempDbHistoryJson(route,{detail:'SECRET_PROVIDER'},503),true):false;await button(page,'Refresh selected TempDB receipt').click();await expect(button(page,'Retry selected TempDB receipt read')).toBeVisible();await expect(detail(page)).toContainText('9007199254741505');await expect(panel(page)).not.toContainText('SECRET_PROVIDER');const before=session.reads.length;await page.getByRole('button',{name:'Advance workspace poll',exact:true}).click();expect(session.reads).toHaveLength(before);session.handler=undefined;await button(page,'Retry selected TempDB receipt read').click();await expect(detail(page).getByRole('alert')).toHaveCount(0);expect(params(page).tempDbRun).toBe(tempDbHistoryReceipt(1).runId);
});
test('same-window manual workspace refresh rereads list and exact source without clearing retained facts on 503',async({page})=>{
 await mount(page);const session=sessions.get(page),before=session.reads.length;session.handler=async route=>(await tempDbHistoryJson(route,{},503),true);await page.getByRole('button',{name:'Refresh workspace',exact:true}).click();await expect(button(page,'Retry TempDB history read')).toBeVisible();await expect(button(page,'Retry selected TempDB receipt read')).toBeVisible();expect(session.reads).toHaveLength(before+2);await expect(detail(page)).toContainText('9007199254741505');
});
for(const family of ['list','detail'])for(const status of [401,403])test(`${family} ${status} denies both sources, fences held sibling success and requires explicit access recovery`,async({page})=>{
 await mount(page);const session=sessions.get(page),held=tempDbHistoryHeld();session.handler=async(route,read)=>{
  const isDetail=Boolean(read.selection);if(isDetail===(family==='detail')){await held.started;await tempDbHistoryJson(route,{detail:'SECRET_DENIAL'},status);return true;}return held.handle(route,responseFor(session,read));
 };await page.getByRole('button',{name:'Refresh workspace',exact:true}).click();const sibling=await held.started;await expect(button(page,'Retry TempDB history access')).toBeVisible();await expect(panel(page)).not.toContainText('9007199254740993');await expect(panel(page)).not.toContainText('SECRET_DENIAL');await expect.poll(()=>sibling.failure()?.errorText??'').toContain('ERR_ABORTED');await held.finish();await expect(panel(page)).not.toContainText('9007199254740993');const before=session.reads.length;await page.getByRole('button',{name:'Advance workspace poll',exact:true}).click();expect(session.reads).toHaveLength(before);session.handler=undefined;await button(page,'Retry TempDB history access').click();await expect(detail(page)).toContainText('9007199254741505');
});
test('denial remains latched across invalid-window detour and restoration under unchanged authority',async({page})=>{
 await mount(page);const session=sessions.get(page);session.handler=async(route,read)=>read.selection?(await tempDbHistoryJson(route,{},403),true):false;await button(page,'Refresh selected TempDB receipt').click();await expect(button(page,'Retry TempDB history access')).toBeVisible();const before=session.reads.length;await page.getByRole('button',{name:'Use invalid long window',exact:true}).click();await page.getByRole('button',{name:'Restore shared window',exact:true}).click();expect(session.reads).toHaveLength(before);await expect(panel(page)).not.toContainText('9007199254740993');
});
for(const transition of ['target','revision','authority','window','unmount','ABA'])test(`held exact response is fenced across ${transition} context changes`,async({page})=>{
 await mount(page);const session=sessions.get(page),held=tempDbHistoryHeld();let handled=false;session.handler=(route,read)=>read.selection&&!handled?(handled=true,held.handle(route,responseFor(session,read),403)):false;await button(page,'Refresh selected TempDB receipt').click();const pending=await held.started;
 if(transition==='target'||transition==='ABA'){await page.getByRole('combobox',{name:'Selected server',exact:true}).selectOption(beta);await expect(detail(page)).toContainText('9007199254741505');if(transition==='ABA')await page.getByRole('combobox',{name:'Selected server',exact:true}).selectOption(alpha);}
 else if(transition==='revision')await page.getByRole('combobox',{name:'Target revision',exact:true}).selectOption('2');
 else if(transition==='authority')await page.getByRole('combobox',{name:'Authority context',exact:true}).selectOption('grant-b');
 else if(transition==='window')await page.getByRole('button',{name:'Change shared window one microsecond',exact:true}).click();
 else await page.getByRole('button',{name:'Unmount TempDB history',exact:true}).click();
 await expect.poll(()=>pending.failure()?.errorText??'').toContain('ERR_ABORTED');await held.finish();if(transition==='unmount')await page.getByRole('button',{name:'Mount TempDB history',exact:true}).click();await expect(detail(page)).toContainText('9007199254741505');await expect(button(page,'Retry TempDB history access')).toHaveCount(0);
});
test('equivalent microsecond spelling preserves held exact request and received continuation chain',async({page})=>{
 await mount(page);const session=sessions.get(page),held=tempDbHistoryHeld();session.handler=(route,read)=>read.selection?held.handle(route,responseFor(session,read)):false;await button(page,'Refresh selected TempDB receipt').click();const request=await held.started,before=session.reads.length;await page.getByRole('button',{name:'Toggle equivalent UTC spelling',exact:true}).click();expect(request.failure()).toBeNull();expect(session.reads).toHaveLength(before);await held.finish();await expect(detail(page)).toContainText('9007199254741505');
});
test('keyboard cancel preserves facts, stops pending read and retry retains the same requested run',async({page})=>{
 await mount(page);const session=sessions.get(page),held=tempDbHistoryHeld();session.handler=(route,read)=>read.selection?held.handle(route,responseFor(session,read)):false;await button(page,'Refresh selected TempDB receipt').click();const request=await held.started;await button(page,'Cancel selected TempDB receipt read').press('Enter');await expect.poll(()=>request.failure()?.errorText??'').toContain('ERR_ABORTED');await expect(detail(page)).toContainText('9007199254741505');await expect(button(page,'Retry selected TempDB receipt read')).toBeVisible();await held.finish();session.handler=undefined;await button(page,'Retry selected TempDB receipt read').press('Enter');await expect(detail(page).getByRole('alert')).toHaveCount(0);expect(session.reads.at(-1).selection.runId).toBe(tempDbHistoryReceipt().runId);
});
for(const malformed of ['summary','file','empty','unexpected'])test('malformed '+malformed+' selected source is refused without discarding received facts',async({page})=>{
 await mount(page);const session=sessions.get(page);session.handler=async(route,read)=>{if(!read.selection)return false;const body=responseFor(session,read);if(malformed==='summary')body.summary.observedAtUtc='2026-10-04T09:00:00Z';else if(malformed==='file')body.items[0].sizeBytes=9007199254741999;else if(malformed==='empty')body.items=[];else body.rawSocketAddress='PRIVATE';await tempDbHistoryJson(route,body);return true;};await button(page,'Refresh selected TempDB receipt').click();await expect(detail(page).getByRole('alert')).toBeVisible();await expect(detail(page)).toContainText('9007199254741505');await expect(panel(page)).not.toContainText('PRIVATE');
});
test('whole-window overflow does not suppress independent exact selected receipt',async({page})=>{
 sessions.get(page).backend.settings.count=10001;await page.goto(href({tempDbRun:tempDbHistoryReceipt().runId,tempDbRevision:'1'}));await expect(button(page,'Retry TempDB history read')).toBeVisible();await expect(detail(page)).toContainText('9007199254741505');expect(sessions.get(page).reads.filter(r=>r.selection)).toHaveLength(1);
});
for(const width of [320,390,768,1440])test(`dark TempDB evidence at ${width}px has strict overflow, keyboard access, visible focus and WCAG21A`,async({page},info)=>{
 await page.setViewportSize({width,height:1000});await mount(page);await containedEvidence(page);await browse(page);await containedEvidence(page);const table=await files(page),scroll=table.locator('..');await openReceiptIdentities(page);const geometry=await containedEvidence(page);expect(geometry.scrolls).toHaveLength(2);await info.attach('tempdb-panel-geometry',{body:JSON.stringify(geometry,null,2),contentType:'application/json'});await scroll.scrollIntoViewIfNeeded();await scroll.press('ArrowRight');await expect(scroll).toBeFocused();if(await scroll.evaluate(e=>e.scrollWidth>e.clientWidth))await expect.poll(()=>scroll.evaluate(e=>e.scrollLeft)).toBeGreaterThan(0);const focus=await scroll.evaluate(e=>({style:getComputedStyle(e).outlineStyle,width:parseFloat(getComputedStyle(e).outlineWidth)}));expect(focus.style).not.toBe('none');expect(focus.width).toBeGreaterThanOrEqual(2);
 for(const name of ['Refresh TempDB window history','Refresh selected TempDB receipt','Next TempDB receipts page']){const box=await button(page,name).boundingBox();expect(box.height).toBeGreaterThanOrEqual(44);expect(box.width).toBeGreaterThanOrEqual(44);}
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBe(0);expect(await page.evaluate(()=>getComputedStyle(document.documentElement).colorScheme)).toBe('dark');const axe=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();await info.attach('tempdb-axe',{body:JSON.stringify(axe.violations,null,2),contentType:'application/json'});expect(axe.violations).toEqual([]);const folder=fileURLToPath(new URL('../../artifacts/dashboard-tempdb-history-2026-10-04/screenshots/',import.meta.url));await mkdir(folder,{recursive:true});await page.screenshot({path:folder+`tempdb-component-${width}-focus.png`});await page.evaluate(()=>{document.activeElement?.blur();scrollTo({top:0,left:0,behavior:'instant'});});await page.screenshot({path:folder+`tempdb-component-${width}.png`,fullPage:true});
});
for(const width of [320,1440])test(`forced colors TempDB controls retain keyboard focus and source boundaries at ${width}px`,async({page})=>{
 await page.emulateMedia({forcedColors:'active'});await page.setViewportSize({width,height:1000});await mount(page);const refresh=button(page,'Refresh selected TempDB receipt');await refresh.press('Tab');await page.keyboard.press('Shift+Tab');await expect(refresh).toBeFocused();expect(await refresh.evaluate(e=>parseFloat(getComputedStyle(e).outlineWidth))).toBeGreaterThanOrEqual(2);expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBe(0);expect((await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze()).violations).toEqual([]);const folder=fileURLToPath(new URL('../../artifacts/dashboard-tempdb-history-2026-10-04/screenshots/',import.meta.url));await mkdir(folder,{recursive:true});await page.screenshot({path:folder+`tempdb-component-${width}-forced-focus.png`});
});

for(const changed of ['cutoffUtc','readEpoch'])test(`native ${changed} continuation drift is refused without replacing validated summary or files`,async({page})=>{
 const s=sessions.get(page);s.backend.settings.fileCount=101;await mount(page);await files(page);s.handler=async(route,r)=>{if(!r.selection||!r.cursor)return false;const body=responseFor(s,r);if(changed==='cutoffUtc')body.cutoffUtc='2026-10-04T11:00:00.123455Z';else body.readEpoch='f'.repeat(64);await tempDbHistoryJson(route,body);return true;};await button(page,'Next TempDB files page').press('Enter');await expect(detail(page).getByRole('alert')).toBeVisible();await expect(detail(page).getByRole('table').locator('tbody tr')).toHaveCount(100);await expect(detail(page)).toContainText('9007199254740993');
});
test('retained orphan file rows do not fabricate a missing summary or source observation',async({page})=>{
 const s=sessions.get(page);s.handler=async(route,r)=>{if(!r.selection)return false;const body=responseFor(s,r);body.state='partial';body.reason='payload_missing';body.receipt={...body.receipt,state:'partial',reason:'payload_missing',summaryPresent:false,observedAtUtc:null};body.summary=null;await tempDbHistoryJson(route,body);return true;};await mount(page);await expect(detail(page).getByRole('region',{name:'TempDB summary from selected receipt',exact:true})).toHaveCount(0);await expect(detail(page)).toContainText('expected source payload is missing');const table=await files(page);await expect(table.locator('tbody tr')).toHaveCount(3);await expect(table).toContainText('9007199254740993');
});
