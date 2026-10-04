import {expect,test} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {fileHistoryBinding,fileHistoryAlpha,fileHistoryBeta,databaseFileWindowHistoryBackend} from '../tools/database-file-window-history-fixture.mjs';
import {installDatabaseFileWindowHistoryFixture,fileHistoryJson,fileHistoryHeld} from './databaseFileWindowHistoryFixture.mjs';
const sessions=new WeakMap(),panel=p=>p.getByRole('region',{name:'Database file allocation and autogrowth history',exact:true});
const source=(p,name)=>panel(p).getByRole('region',{name,exact:true}),snapshot=p=>source(p,'Selected file snapshot'),receipts=p=>source(p,'File collection receipts');
const button=(p,name)=>panel(p).getByRole('button',{name,exact:true}),facts=p=>panel(p).getByRole('region',{name:'Historical database file facts',exact:true});
const params=(extra={})=>new URLSearchParams({target:fileHistoryAlpha,range:'custom',from:fileHistoryBinding.fromUtc,to:fileHistoryBinding.toUtc,...extra});
test.beforeEach(async({page})=>{await page.clock.setFixedTime(new Date('2026-10-03T12:00:00Z'));await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));sessions.set(page,await installDatabaseFileWindowHistoryFixture(page));});
test.afterEach(async({page})=>expect(sessions.get(page).errors,'Only registered local native-shaped GETs may run').toEqual([]));
async function mount(page,extra={}){await page.goto('/component-tests/database-file-window-history.html#resources?'+params(extra));await expect(receipts(page)).toContainText('2 received receipts');await expect(facts(page).locator('tbody>tr')).toHaveCount(3);}
async function openReceipts(page){await receipts(page).locator('summary').click();}
const selectedRun=page=>sessions.get(page).reads.find(item=>item.selection)?.selection;
test('completion receipt default includes failures and never falls back to an older healthy file payload',async({page})=>{
 const session=sessions.get(page);session.backend.settings.failedLatest=true;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText('Collection failed');await expect(snapshot(page)).toContainText('0 retained file rows');await expect(facts(page)).toHaveCount(0);
 const first=session.backend.read(fileHistoryBinding).items[0];expect(session.reads.find(r=>r.selection).selection.runId).toBe(first.runId);await expect(panel(page)).toContainText('Target Administrator');await expect(snapshot(page)).not.toContainText('SYNTHETIC_exact_data');
});
for(const kind of ['missingLatest','emptyLatest'])test(kind+' remains an explicit most-recent receipt with truthful empty facts',async({page})=>{
 sessions.get(page).backend.settings[kind]=true;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText(kind==='missingLatest'?'File payload unavailable':'Complete empty snapshot');await expect(facts(page)).toHaveCount(0);await expect(snapshot(page)).toContainText(kind==='missingLatest'?'Refresh cannot recreate':'does not establish healthy allocation');
});
test('exact Int64 allocation, three growth modes and cumulative counters remain distinct from window latency',async({page})=>{
 await mount(page);await expect(facts(page)).toContainText('9223372036854775807 bytes');await expect(facts(page)).toContainText('67108864 bytes per growth event');await expect(facts(page)).toContainText('10% per growth event');await expect(facts(page)).toContainText('Autogrowth disabled');
 await button(page,'View read and write latency trend for database 5, file SYNTHETIC_exact_data').click();await expect(page).toHaveURL(/fileSnapshotRun=.*fileSnapshotRevision=1/u);const allocation=panel(page).getByRole('group',{name:'Selected file allocation and autogrowth',exact:true});await allocation.locator('summary').click();await expect(allocation).toContainText('9223372036854775807');await expect(allocation).toContainText('Completed');await expect(allocation).toContainText('not used log space');
 const first=facts(page).locator('tbody>tr').first();await first.locator('summary').click();await expect(first).toContainText('9007199254740993');await expect(first).toContainText('9007199254741093');expect(sessions.get(page).reads.every(r=>r.url.includes('/resources/files/history'))).toBe(true);
});
test('receipt URL restoration reloads the exact tuple independently of current list and preserves browser Back/Forward',async({page})=>{
 const session=sessions.get(page);await mount(page);await openReceipts(page);const items=session.backend.read(fileHistoryBinding).items;await button(page,'Inspect file receipt '+items[1].runId).click();await expect(snapshot(page)).toContainText(items[1].runId);await page.reload();await expect(snapshot(page)).toContainText(items[1].runId);
 await openReceipts(page);await button(page,'Inspect file receipt '+items[0].runId).click();await expect(snapshot(page)).toContainText(items[0].runId);await page.goBack();await expect(snapshot(page)).toContainText(items[1].runId);await page.goForward();await expect(snapshot(page)).toContainText(items[0].runId);
});
test('invalid duplicate URL pin survives a shared-window change without choosing newest; explicit Clear restores default',async({page})=>{
 await page.goto('/component-tests/database-file-window-history.html#resources?'+params({fileSnapshotRun:'bad',fileSnapshotRevision:'1'})+'&fileSnapshotRun=also-bad');await expect(snapshot(page)).toContainText('identity is invalid');expect(sessions.get(page).reads.filter(r=>r.selection)).toHaveLength(0);
 await page.getByRole('button',{name:'Change shared window one microsecond',exact:true}).click();await expect(snapshot(page)).toContainText('identity is invalid');expect(new URLSearchParams(page.url().split('?')[1]).getAll('fileSnapshotRun')).toEqual(['bad','also-bad']);expect(sessions.get(page).reads.filter(r=>r.selection)).toHaveLength(0);
 await button(page,'Clear file snapshot selection').click();await expect(facts(page)).toBeVisible();await expect(snapshot(page)).not.toContainText('identity is invalid');
});
test('absent exact pin remains visible without substituting the newest available receipt',async({page})=>{
 await page.goto('/component-tests/database-file-window-history.html#resources?'+params({fileSnapshotRun:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',fileSnapshotRevision:'1'}));await expect(snapshot(page)).toContainText('Exact snapshot is not retained');await expect(snapshot(page)).toContainText('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb');await expect(facts(page)).toHaveCount(0);await expect(snapshot(page)).toContainText('Refresh cannot recreate missing historical payload');
});
test('selected missing numeric file never substitutes another file allocation',async({page})=>{
 await mount(page,{resourceDatabaseId:'5',resourceFileId:'99'});await expect(snapshot(page)).toContainText('Its numeric identity is retained');const allocation=panel(page).getByRole('group',{name:'Selected file allocation and autogrowth',exact:true});await allocation.locator('summary').click();await expect(allocation).toContainText("No other file's configuration is substituted");await expect(allocation).not.toContainText('9223372036854775807');
});
test('receipt paging uses original native continuation and cached pages without extra reads',async({page})=>{
 const session=sessions.get(page);session.backend.settings.receiptCount=51;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(receipts(page)).toContainText('50 received receipts');await button(page,'Next file receipts page').click();await expect(receipts(page)).toContainText('51 received receipts');const count=session.reads.length;
 await button(page,'First file receipts page').click();await button(page,'Next file receipts page').click();await button(page,'Previous file receipts page').click();expect(session.reads).toHaveLength(count);await expect(receipts(page)).toContainText('Page 1');
});
test('file paging and source expiry preserve locally received facts and exact retry cursor',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText('100 received file rows');session.backend.settings.epoch++;
 await button(page,'Next file snapshot page').click();await expect(snapshot(page)).toContainText('changed or expired');await expect(snapshot(page)).toContainText('100 received file rows');await button(page,'Retry file snapshot read').click();expect(session.reads.at(-1).cursor).toBe(session.reads.at(-2).cursor);
 await button(page,'Refresh file snapshot').click();await expect(snapshot(page).getByRole('alert')).toHaveCount(0);await button(page,'Next file snapshot page').click();await expect(snapshot(page)).toContainText('101 received file rows');const count=session.reads.length;await button(page,'First file snapshot page').click();await button(page,'Next file snapshot page').click();expect(session.reads).toHaveLength(count);
});
test('failed explicit refresh preserves received facts and exact retry; ordinary poll does not replace them',async({page})=>{
 await mount(page);const session=sessions.get(page),text=await facts(page).locator('tbody').textContent();session.handler=async(route,read)=>read.selection?(await fileHistoryJson(route,{detail:'PRIVATE_PROVIDER_ERROR'},503),true):false;
 await button(page,'Refresh file snapshot').click();await expect(button(page,'Retry file snapshot read')).toBeVisible();await expect(facts(page).locator('tbody')).toHaveText(text);const count=session.reads.length;await page.getByRole('button',{name:'Advance workspace poll',exact:true}).click();expect(session.reads).toHaveLength(count);await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_ERROR');
 session.handler=undefined;await button(page,'Retry file snapshot read').click();await expect(snapshot(page).getByRole('alert')).toHaveCount(0);await expect(facts(page).locator('tbody')).toHaveText(text);
});
test('global Refresh explicitly rereads both same-window sources while retaining inspected facts on transient failure',async({page})=>{
 await mount(page);const session=sessions.get(page),count=session.reads.length,text=await facts(page).locator('tbody').textContent();session.handler=async(route)=>{await fileHistoryJson(route,{},503);return true;};await page.getByRole('button',{name:'Refresh workspace',exact:true}).click();await expect(button(page,'Retry file receipts read')).toBeVisible();await expect(button(page,'Retry file snapshot read')).toBeVisible();expect(session.reads).toHaveLength(count+2);await expect(facts(page).locator('tbody')).toHaveText(text);
});
test('response bound refusal offers explicit smaller pages bound to the same run and window',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;session.handler=async(route,read)=>read.selection&&read.limit===100?(await fileHistoryJson(route,{code:'response_too_large'},422),true):false;
 await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(button(page,'Use smaller file pages')).toBeVisible();const attempted=session.reads.at(-1);
 const pending=fileHistoryHeld();session.handler=(route,read)=>read.selection&&read.limit===50?pending.handle(route,session.backend.snapshot(read.binding,read.selection,read.cursor,read.limit)):false;
 await button(page,'Use smaller file pages').focus();await page.keyboard.press('Enter');await pending.started;await expect(button(page,'Cancel file snapshot read')).toBeFocused();await pending.finish();await expect(button(page,'Refresh file snapshot')).toBeFocused();
 session.handler=undefined;await expect(snapshot(page)).toContainText('50 received file rows');expect(session.reads.at(-1)).toMatchObject({selection:attempted.selection,binding:attempted.binding,limit:50});
 await button(page,'Next file snapshot page').click();await expect(snapshot(page)).toContainText('100 received file rows');expect(session.reads.at(-1).limit).toBe(50);await button(page,'Next file snapshot page').click();await expect(snapshot(page)).toContainText('101 received file rows');expect(session.reads.at(-1).limit).toBe(50);
});
test('default receipt promotion retains an owned continuation without redispatch or cancellation',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText('100 received file rows');
 const before=session.reads.length,pending=fileHistoryHeld(),run=selectedRun(page).runId;session.handler=(route,read)=>read.selection&&read.cursor?pending.handle(route,session.backend.snapshot(read.binding,read.selection,read.cursor,read.limit)):false;
 await button(page,'Next file snapshot page').click();const request=await pending.started;await expect(page).toHaveURL(new RegExp('fileSnapshotRun='+run));await expect(button(page,'Cancel file snapshot read')).toBeEnabled();expect(request.failure()).toBeNull();expect(session.reads).toHaveLength(before+1);await expect(snapshot(page)).toContainText('100 received file rows');
 await pending.finish();await expect(snapshot(page)).toContainText('Snapshot page 2; 101 received file rows');expect(session.reads).toHaveLength(before+1);
});
for(const family of ['receipts','snapshot'])test('held '+family+' Retry keeps keyboard focus in the source through pending and success',async({page})=>{
 await mount(page);const session=sessions.get(page),isRead=read=>family==='snapshot'?Boolean(read.selection):!read.selection;
 session.handler=async(route,read)=>isRead(read)?(await fileHistoryJson(route,{},503),true):false;await button(page,'Refresh file '+family).click();const retry=button(page,'Retry file '+family+' read');await expect(retry).toBeVisible();
 const pending=fileHistoryHeld();session.handler=(route,read)=>isRead(read)?pending.handle(route,read.selection?session.backend.snapshot(read.binding,read.selection,read.cursor,read.limit):session.backend.read(read.binding,read.cursor,read.limit)):false;
 await retry.focus();await page.keyboard.press('Enter');await pending.started;await expect(button(page,'Cancel file '+family+' read')).toBeFocused();await expect(facts(page)).toBeVisible();await pending.finish();await expect(button(page,'Refresh file '+family)).toBeFocused();await expect((family==='snapshot'?snapshot(page):receipts(page)).getByRole('alert')).toHaveCount(0);
});
test('explicit access Retry hands keyboard focus to the owned list Cancel then Refresh',async({page})=>{
 await mount(page);const session=sessions.get(page);session.handler=async(route,read)=>read.selection?(await fileHistoryJson(route,{},403),true):false;await button(page,'Refresh file snapshot').click();await expect(button(page,'Retry database file access')).toBeVisible();
 const pending=fileHistoryHeld();session.handler=(route,read)=>!read.selection?pending.handle(route,session.backend.read(read.binding,read.cursor,read.limit)):false;await button(page,'Retry database file access').focus();await page.keyboard.press('Enter');await pending.started;await expect(button(page,'Cancel file receipts read')).toBeFocused();await pending.finish();await expect(button(page,'Refresh file receipts')).toBeFocused();await expect(facts(page)).toBeVisible();
});
test('a different selected tuple and selection ABA abort an old snapshot replacement',async({page})=>{
 await mount(page);await openReceipts(page);const session=sessions.get(page),items=session.backend.read(fileHistoryBinding).items,pending=fileHistoryHeld();session.handler=(route,read)=>read.selection?pending.handle(route,session.backend.snapshot(read.binding,read.selection,read.cursor,read.limit)):false;
 await button(page,'Refresh file snapshot').click();const request=await pending.started;session.handler=undefined;await button(page,'Inspect file receipt '+items[1].runId).click();await expect(snapshot(page)).toContainText(items[1].runId);await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await button(page,'Inspect file receipt '+items[0].runId).click();await expect(snapshot(page)).toContainText(items[0].runId);await pending.finish();await expect(snapshot(page)).toContainText('Snapshot page 1; 3 received file rows');await expect(button(page,'Cancel file snapshot read')).toBeDisabled();
});
test('held continuation survives a poll; keyboard Cancel remains a stable focused element and retry is exact',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText('100 received file rows');const pending=fileHistoryHeld();session.handler=(route,read)=>read.selection&&read.cursor?pending.handle(route,session.backend.snapshot(read.binding,read.selection,read.cursor)):false;
 await button(page,'Next file snapshot page').click();const request=await pending.started,count=session.reads.length;await page.getByRole('button',{name:'Advance workspace poll',exact:true}).click();expect(session.reads).toHaveLength(count);
 const cancel=button(page,'Cancel file snapshot read');await cancel.focus();await page.keyboard.press('Enter');await expect(cancel).toHaveCount(1);await expect(cancel).toBeDisabled();await expect(button(page,'Refresh file snapshot')).toBeFocused();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.finish();await expect(snapshot(page)).toContainText('100 received file rows');
 session.handler=undefined;await button(page,'Retry file snapshot read').click();await expect(snapshot(page)).toContainText('101 received file rows');expect(session.reads.at(-1).cursor).toBe(session.reads.at(-2).cursor);
});
for(const code of [401,403])test('HTTP'+code+' on continuation purges all protected sources and keeps same-authority denial through invalid windows',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText('100 received file rows');session.handler=async(route,read)=>read.cursor?(await fileHistoryJson(route,{detail:'PRIVATE_DENIAL'},code),true):false;
 await button(page,'Next file snapshot page').click();await expect(panel(page)).toContainText('cached pages were removed');await expect(facts(page)).toHaveCount(0);await expect(panel(page)).not.toContainText('SYNTHETIC_exact_data');const count=session.reads.length;
 await page.getByRole('button',{name:'Use invalid long window',exact:true}).click();await page.getByRole('button',{name:'Restore shared window',exact:true}).click();await page.getByRole('button',{name:'Refresh workspace',exact:true}).click();expect(session.reads).toHaveLength(count);await expect(panel(page)).not.toContainText('PRIVATE_DENIAL');session.handler=undefined;await button(page,'Retry database file access').click();await expect(facts(page)).toBeVisible();
});
test('parallel snapshot200 cannot restore facts after list403 and late responses are aborted',async({page})=>{
 await mount(page);const session=sessions.get(page),pending=fileHistoryHeld();session.handler=async(route,read)=>read.selection?pending.handle(route,session.backend.snapshot(read.binding,read.selection)): (await pending.started,await fileHistoryJson(route,{},403),true);
 await button(page,'Refresh file snapshot').click();const request=await pending.started;await button(page,'Refresh file receipts').click();await expect(panel(page)).toContainText('access was denied');await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.finish();await expect(facts(page)).toHaveCount(0);
});
for(const change of ['target','revision','grant','window','unmount'])test(change+' change fences a held snapshot read before late publication',async({page})=>{
 await mount(page);const session=sessions.get(page),pending=fileHistoryHeld();session.handler=(route,read)=>read.selection?pending.handle(route,session.backend.snapshot(read.binding,read.selection)):false;
 await button(page,'Refresh file snapshot').click();const request=await pending.started;session.handler=undefined;const oldRun=selectedRun(page).runId;
 if(change==='target')await page.getByRole('combobox',{name:'Selected server',exact:true}).selectOption(fileHistoryBeta);
 if(change==='revision')await page.getByRole('combobox',{name:'Target revision',exact:true}).selectOption('2');
 if(change==='grant')await page.getByRole('combobox',{name:'Authority context',exact:true}).selectOption('grant-b');
 if(change==='window')await page.getByRole('button',{name:'Change shared window one microsecond',exact:true}).click();
 if(change==='unmount')await page.getByRole('button',{name:'Unmount file history',exact:true}).click();
 await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.finish();if(change==='unmount'){await expect(panel(page)).toHaveCount(0);await page.getByRole('button',{name:'Mount file history',exact:true}).click();}
 await expect(facts(page)).toBeVisible();if(change==='target'||change==='window')await expect(snapshot(page)).not.toContainText(oldRun);
});
test('equivalent microsecond bounds preserve held receipt reads without extra GETs or aborts',async({page})=>{
 await mount(page);const session=sessions.get(page),pending=fileHistoryHeld();session.handler=(route,read)=>read.selection?pending.handle(route,session.backend.snapshot(read.binding,read.selection)):false;await button(page,'Refresh file snapshot').click();const request=await pending.started,count=session.reads.length;await page.getByRole('button',{name:'Use equivalent UTC spelling',exact:true}).click();await expect(button(page,'Cancel file snapshot read')).toBeEnabled();expect(request.failure()).toBeNull();expect(session.reads).toHaveLength(count);await pending.finish();await expect(facts(page)).toBeVisible();
});
test('malformed continuation refuses advancement without losing received file evidence',async({page})=>{
 const session=sessions.get(page);session.backend.settings.fileCount=101;await page.goto('/component-tests/database-file-window-history.html#resources?'+params());await expect(snapshot(page)).toContainText('100 received file rows');session.handler=async(route,read)=>{if(!read.cursor||!read.selection)return false;const value=session.backend.snapshot(read.binding,read.selection,read.cursor);value.items[0].fileId=1;await fileHistoryJson(route,value);return true;};await button(page,'Next file snapshot page').click();await expect(snapshot(page).getByRole('alert')).toBeVisible();await expect(snapshot(page)).toContainText('100 received file rows');
});
for(const width of [320,390,768,1440])test('dark file history is keyboard accessible, focus-contained and locally scrollable at '+width,async({page})=>{
 await page.setViewportSize({width,height:900});await mount(page);const scroller=facts(page);await scroller.focus();await page.keyboard.press('ArrowRight');if(width<1000)await expect.poll(()=>scroller.evaluate(node=>node.scrollLeft)).toBeGreaterThan(0);
 await expect(page.locator('body')).toHaveCSS('background-color','rgb(13, 20, 30)');expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
 const audit=await new AxeBuilder({page}).include('.database-file-window-history').withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();expect(audit.violations).toEqual([]);
 const summary=panel(page).getByRole('group',{name:'Selected file allocation and autogrowth',exact:true}).locator('summary');await summary.focus();await expect(summary).toHaveCSS('outline-offset','-2px');await page.keyboard.press('Enter');await expect(panel(page)).toContainText('Choose an exact file');
 const directory=fileURLToPath(new URL('../../artifacts/dashboard-file-window-history/screenshots/',import.meta.url));await mkdir(directory,{recursive:true});await panel(page).screenshot({path:`${directory}/file-history-${width}.png`,animations:'disabled'});
});
