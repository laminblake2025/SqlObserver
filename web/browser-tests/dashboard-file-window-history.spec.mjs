import {expect,test} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {resolve} from 'node:path';
import {databaseFileWindowHistoryBackend,fileHistoryBinding,fileHistoryAlpha as alpha,fileHistoryBeta as beta,fileHistoryFixtureReceipt} from '../tools/database-file-window-history-fixture.mjs';

const origin='http://127.0.0.1:4432';
const contexts=new WeakMap();
const panel=page=>page.getByRole('region',{name:'Database file allocation and autogrowth history',exact:true});
const snapshot=page=>panel(page).getByRole('region',{name:'Selected file snapshot',exact:true});
const params=page=>Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const href=(extra={},target=alpha)=>'/#/resources?'+new URLSearchParams({target,range:'custom',from:fileHistoryBinding.fromUtc,to:fileHistoryBinding.toUtc,compare:'1',databaseId:'5',...extra});
const exact=(binding=fileHistoryBinding,index=0)=>fileHistoryFixtureReceipt(index,binding);
async function json(route,body,status=200){await route.fulfill({status,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(body)});}
test.beforeEach(async({page})=>{
 await page.clock.install({time:new Date('2026-10-02T12:00:00Z')});
 await page.context().addCookies(['dashboardRefactor','resourceInspectionFixture'].map(name=>({name,value:'1',url:origin})).concat([{name:'chartReferenceRole',value:'viewer',url:origin}]));
 await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
 const context={backend:databaseFileWindowHistoryBackend(),reads:[],errors:[],handler:undefined};contexts.set(page,context);
 page.on('pageerror',error=>context.errors.push(error.message));
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());
  if(url.origin!==origin){context.errors.push('External traffic '+url.origin);return route.abort();}
  if(!/\/resources\/files\/history(?:\/[a-f0-9-]+)?$/.test(url.pathname))return route.continue();
  if(request.method()!=='GET'){context.errors.push('Unexpected mutation');return route.abort();}
  context.reads.push(url);if(context.handler&&await context.handler(route,url))return;
  const p=url.searchParams,binding={targetId:url.pathname.split('/')[4],targetRevision:Number(p.get('targetRevision')),fromUtc:p.get('fromUtc'),toUtc:p.get('toUtc')};
  const run=/\/history\/([a-f0-9-]+)$/.exec(url.pathname),token=p.get('cursor')??undefined,limit=Number(p.get('limit'));
  try{return await json(route,run?context.backend.snapshot(binding,{runId:run[1],sampleTargetRevision:Number(p.get('sampleTargetRevision'))},token,limit):context.backend.read(binding,token,limit));}
  catch(error){return json(route,{detail:'SECRET_CURSOR_OR_SOURCE_ERROR'},error.status??400);}
 });
});
test.afterEach(async({page})=>expect(contexts.get(page).errors).toEqual([]));
async function open(page,extra={}){await page.goto(href(extra));await expect(snapshot(page)).toContainText('9223372036854775807 bytes');}
async function browse(page){await panel(page).getByText(/^Browse file collection receipts \(/).click();}
for(const width of [320,390,768,1440])test(`exact historical file settings are dark, keyboard operable and accessible at ${width}px`,async({page},info)=>{
 await page.setViewportSize({width,height:1000});const receipt=exact();await open(page,{fileSnapshotRun:receipt.runId,fileSnapshotRevision:'1',resourceDatabaseId:'5',resourceFileId:'1'});
 await expect(snapshot(page)).toContainText('67108864');await expect(snapshot(page)).toContainText('9007199254740993');
 expect(params(page)).toMatchObject({range:'custom',from:fileHistoryBinding.fromUtc,to:fileHistoryBinding.toUtc,fileSnapshotRun:receipt.runId,fileSnapshotRevision:'1'});
 const scroll=snapshot(page).getByRole('region',{name:'Historical database file facts',exact:true});await scroll.focus();await expect(scroll).toBeFocused();
 const focus=await scroll.evaluate(el=>({width:getComputedStyle(el).outlineWidth,style:getComputedStyle(el).outlineStyle}));expect(focus.style).not.toBe('none');expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
 if(await scroll.evaluate(el=>el.scrollWidth>el.clientWidth)){await scroll.press('ArrowRight');await expect.poll(()=>scroll.evaluate(el=>el.scrollLeft)).toBeGreaterThan(0);await scroll.evaluate(el=>{el.scrollLeft=0;});}
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 expect(await page.evaluate(()=>getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
 const violations=(await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','best-practice']).analyze()).violations;
 await info.attach('file-history-axe',{body:JSON.stringify(violations,null,2),contentType:'application/json'});expect(violations).toEqual([]);
 const folder=resolve('../artifacts/dashboard-file-window-history-2026-10-04/screenshots');await mkdir(folder,{recursive:true});
 await scroll.scrollIntoViewIfNeeded();await scroll.focus();await page.screenshot({path:resolve(folder,`file-history-${width}-keyboard-focus.png`)});
 await page.evaluate(()=>{document.activeElement?.blur();document.querySelectorAll('.table-scroll').forEach(el=>{el.scrollLeft=0;});scrollTo({top:0,left:0,behavior:'instant'});});
 await page.screenshot({path:resolve(folder,`file-history-${width}-full.png`),fullPage:true});
 await snapshot(page).scrollIntoViewIfNeeded();await page.screenshot({path:resolve(folder,`file-history-${width}-viewport.png`)});
 await page.reload();await expect(snapshot(page)).toContainText(receipt.runId);await expect(snapshot(page)).toContainText('9223372036854775807 bytes');
});
for(const width of [320,1440])test(`partial receipt details and a long inert file name remain accessible at ${width}px`,async({page},info)=>{
 await page.setViewportSize({width,height:1000});const context=contexts.get(page);context.backend.settings.partial=true;
 const name='<script>window.__fileHistoryInjected = true</script>'+'X'.repeat(150);
 context.handler=async(route,url)=>{
  if(!/\/history\//.test(url.pathname))return false;
  const p=url.searchParams,binding={targetId:url.pathname.split('/')[4],targetRevision:Number(p.get('targetRevision')),fromUtc:p.get('fromUtc'),toUtc:p.get('toUtc')};
  const body=context.backend.snapshot(binding,{runId:url.pathname.split('/').at(-1),sampleTargetRevision:Number(p.get('sampleTargetRevision'))});body.items[0].logicalName=name;await json(route,body);return true;
 };
 await open(page,{resourceDatabaseId:'5',resourceFileId:'1'});await expect(snapshot(page)).toContainText('Collection lost evidence');
 await snapshot(page).getByText('File source receipt and loss details',{exact:true}).click();
 await snapshot(page).locator('summary').filter({hasText:'Selected file allocation and autogrowth'}).click();
 await snapshot(page).getByText('Exact cumulative counters',{exact:true}).first().click();
 await expect(snapshot(page)).toContainText(name);expect(await page.evaluate(()=>window.__fileHistoryInjected)).toBeUndefined();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const violations=(await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','best-practice']).analyze()).violations;await info.attach('partial-source-axe',{body:JSON.stringify(violations,null,2),contentType:'application/json'});expect(violations).toEqual([]);
 const folder=resolve('../artifacts/dashboard-file-window-history-2026-10-04/screenshots');await mkdir(folder,{recursive:true});await snapshot(page).scrollIntoViewIfNeeded();await page.screenshot({path:resolve(folder,`file-history-${width}-partial-details.png`),fullPage:true});
});
test('selecting a received receipt in Live freezes the global window and exact run together',async({page})=>{
 await page.goto('/#/resources?'+new URLSearchParams({target:alpha,range:'1h',databaseId:'5',compare:'1'}));await expect(snapshot(page)).toContainText('9223372036854775807 bytes');await browse(page);
 const context=contexts.get(page),read=context.reads.findLast(url=>url.pathname.endsWith('/history'));
 const bound={targetId:alpha,targetRevision:1,fromUtc:read.searchParams.get('fromUtc'),toUtc:read.searchParams.get('toUtc')};const receipt=exact(bound,1);
 const pinOffset=context.reads.length;
 await panel(page).getByRole('button',{name:`Inspect file receipt ${receipt.runId}`,exact:true}).press('Enter');
 await expect.poll(()=>params(page)).toMatchObject({range:'custom',from:bound.fromUtc,to:bound.toUtc,fileSnapshotRun:receipt.runId,fileSnapshotRevision:'1'});
 await expect(snapshot(page)).toContainText(receipt.runId);const fixed=params(page),count=context.reads.length;await page.clock.runFor(61000);
 expect(params(page)).toEqual(fixed);expect(context.reads).toHaveLength(count);
 await page.locator('.page-actions').getByRole('button',{name:/Refresh/}).click();
 await expect.poll(()=>context.reads.slice(count).some(url=>url.pathname.endsWith('/'+receipt.runId))).toBe(true);
 await expect(panel(page).getByRole('button',{name:'Refresh file snapshot',exact:true})).toBeEnabled();await expect(snapshot(page)).toContainText(receipt.runId);
 const pinnedReads=context.reads.slice(pinOffset).filter(url=>/\/history\//.test(url.pathname));
 expect(pinnedReads.length).toBeGreaterThanOrEqual(2);
 expect(pinnedReads.every(url=>url.pathname.endsWith('/'+receipt.runId)&&url.searchParams.get('fromUtc')===bound.fromUtc&&url.searchParams.get('toUtc')===bound.toUtc)).toBe(true);
});
for(const source of ['receipts','snapshot'])test(`Live ${source} paging freezes the shared window without losing the requested continuation`,async({page})=>{
 const context=contexts.get(page);Object.assign(context.backend.settings,{receiptCount:51,fileCount:101});
 await page.goto('/#/resources?'+new URLSearchParams({target:alpha,range:'1h',databaseId:'5'}));await expect(snapshot(page)).toContainText('9223372036854775807 bytes');
 await panel(page).getByRole('button',{name:`Next file ${source} page`,exact:true}).click();
 await expect(source==='snapshot'?snapshot(page):panel(page)).toContainText(source==='snapshot'?'Snapshot page 2; 101 received file rows':'Page 2; 51 received receipts');
 const continued=context.reads.findLast(url=>url.searchParams.has('cursor'));
 expect(continued).toBeDefined();
 expect(params(page)).toMatchObject({range:'custom',from:continued.searchParams.get('fromUtc'),to:continued.searchParams.get('toUtc')});
 const original=context.reads.findLast(url=>!url.searchParams.has('cursor')&&url.pathname===continued.pathname&&url.searchParams.get('fromUtc')===continued.searchParams.get('fromUtc')&&url.searchParams.get('toUtc')===continued.searchParams.get('toUtc'));
 expect(original).toBeDefined();
});
for(const [setting,label] of [['failedLatest','Collection failed'],['missingLatest','File payload unavailable'],['emptyLatest','Complete empty snapshot']])test(`${setting}: the most recent attempt is retained without substituting an older successful receipt`,async({page})=>{
 const context=contexts.get(page);context.backend.settings[setting]=true;await page.goto(href());await expect(snapshot(page)).toContainText(label);
 await expect(snapshot(page)).not.toContainText('9223372036854775807 bytes');expect(context.reads.filter(url=>/\/history\//.test(url.pathname)).every(url=>url.pathname.endsWith(exact().runId))).toBe(true);
 await browse(page);await panel(page).getByRole('button',{name:`Inspect file receipt ${exact(fileHistoryBinding,1).runId}`,exact:true}).click();await expect(snapshot(page)).toContainText('9223372036854775807 bytes');
});
test('receipt and file continuations keep their exact identity; a changed epoch preserves received evidence until refresh',async({page})=>{
 const context=contexts.get(page);Object.assign(context.backend.settings,{receiptCount:51,fileCount:101});await open(page);
 await panel(page).getByRole('button',{name:'Next file receipts page',exact:true}).click();await expect(panel(page)).toContainText('Page 2; 51 received receipts');
 const run=params(page).fileSnapshotRun;expect(run).toBe(exact().runId);await panel(page).getByRole('button',{name:'Next file snapshot page',exact:true}).click();await expect(snapshot(page)).toContainText('Snapshot page 2; 101 received file rows');
 await panel(page).getByRole('button',{name:'Previous file snapshot page',exact:true}).click();await expect(snapshot(page)).toContainText('Snapshot page 1; 101 received file rows');
 await panel(page).getByRole('button',{name:'Refresh file snapshot',exact:true}).click();await expect(snapshot(page)).toContainText('Snapshot page 1; 100 received file rows');
 context.backend.settings.epoch++;await panel(page).getByRole('button',{name:'Next file snapshot page',exact:true}).click();await expect(snapshot(page).getByRole('alert')).toBeVisible();await expect(snapshot(page)).toContainText('9223372036854775807 bytes');await expect(snapshot(page)).not.toContainText('SECRET_CURSOR_OR_SOURCE_ERROR');
 expect(params(page).fileSnapshotRun).toBe(run);await panel(page).getByRole('button',{name:'Refresh file snapshot',exact:true}).click();await expect(snapshot(page)).toContainText('9223372036854775807 bytes');await expect(snapshot(page).getByRole('alert')).toHaveCount(0);
});
test('an out-of-window exact pin survives shared window changes and never falls back to the newest run',async({page})=>{
 const receipt=exact();await open(page,{fileSnapshotRun:receipt.runId,fileSnapshotRevision:'1'});
 await page.getByRole('combobox',{name:'Time range',exact:true}).selectOption('1h');await expect(snapshot(page)).toContainText('outside the shared window');expect(params(page).fileSnapshotRun).toBe(receipt.runId);
 await page.reload();await expect(snapshot(page)).not.toContainText('9223372036854775807 bytes');expect(params(page).fileSnapshotRun).toBe(receipt.runId);
 await panel(page).getByRole('button',{name:'Clear file snapshot selection',exact:true}).click();await expect(snapshot(page)).toContainText('9223372036854775807 bytes');expect(params(page).fileSnapshotRun).toBeUndefined();
});
test('invalid duplicated snapshot identity remains explicit after a shared time change',async({page})=>{
 await page.goto(href({fileSnapshotRun:exact().runId,fileSnapshotRevision:'1'})+'&fileSnapshotRevision=1');await expect(snapshot(page)).toContainText('identity is invalid');
 const context=contexts.get(page);expect(context.reads.some(url=>/\/history\//.test(url.pathname))).toBe(false);
 await page.getByRole('combobox',{name:'Time range',exact:true}).selectOption('1h');await expect(snapshot(page)).toContainText('identity is invalid');expect(context.reads.some(url=>/\/history\//.test(url.pathname))).toBe(false);
});
test('denied refresh clears all received facts and retry restores the same pinned source',async({page})=>{
 const receipt=exact();await open(page,{fileSnapshotRun:receipt.runId,fileSnapshotRevision:'1'});const context=contexts.get(page);let denied=true;
 context.handler=async(route,url)=>{if(!denied||!url.pathname.endsWith(receipt.runId))return false;await json(route,{detail:'SECRET_DENIAL'},403);return true;};
 await panel(page).getByRole('button',{name:'Refresh file snapshot',exact:true}).click();await expect(panel(page)).toContainText('access was denied');await expect(panel(page)).not.toContainText('9223372036854775807');await expect(panel(page)).not.toContainText('SECRET_DENIAL');
 denied=false;await panel(page).getByRole('button',{name:'Retry database file access',exact:true}).click();await expect(snapshot(page)).toContainText('9223372036854775807 bytes');expect(params(page).fileSnapshotRun).toBe(receipt.runId);
});
test('a bounded response refusal offers smaller pages of the same exact source',async({page})=>{
 const context=contexts.get(page);context.backend.settings.fileCount=101;const receipt=exact();
 context.handler=async(route,url)=>{if(!url.pathname.endsWith(receipt.runId)||url.searchParams.get('limit')!=='100')return false;await json(route,{detail:'SECRET_RESPONSE_OVERFLOW'},422);return true;};
 await page.goto(href({fileSnapshotRun:receipt.runId,fileSnapshotRevision:'1'}));await expect(snapshot(page).getByRole('alert')).toBeVisible();await expect(snapshot(page)).not.toContainText('SECRET_RESPONSE_OVERFLOW');
 await snapshot(page).getByRole('button',{name:'Use smaller file pages',exact:true}).click();await expect(snapshot(page)).toContainText('Snapshot page 1; 50 received file rows');
 expect(params(page).fileSnapshotRun).toBe(receipt.runId);await snapshot(page).getByRole('button',{name:'Next file snapshot page',exact:true}).click();await expect(snapshot(page)).toContainText('Snapshot page 2; 100 received file rows');
 expect(context.reads.filter(url=>url.searchParams.has('cursor')&&url.pathname.endsWith(receipt.runId)).every(url=>url.searchParams.get('limit')==='50')).toBe(true);
});
test('a held previous-target file response is fenced after server navigation',async({page})=>{
 await open(page);const context=contexts.get(page);let start,release,end;const started=new Promise(r=>{start=r;}),gate=new Promise(r=>{release=r;}),finished=new Promise(r=>{end=r;});
 context.handler=async(route,url)=>{if(!url.pathname.includes(alpha)||! /\/history\//.test(url.pathname))return false;const p=url.searchParams,binding={targetId:alpha,targetRevision:1,fromUtc:p.get('fromUtc'),toUtc:p.get('toUtc')};const body=context.backend.snapshot(binding,{runId:url.pathname.split('/').at(-1),sampleTargetRevision:1});start();await gate;try{await json(route,body);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{end();}return true;};
 await panel(page).getByRole('button',{name:'Refresh file snapshot',exact:true}).click();await started;await page.getByRole('combobox',{name:'Target server',exact:true}).selectOption(beta);await expect(snapshot(page)).toContainText('SYNTHETIC_exact_data_02');
 release();await finished;await expect(snapshot(page)).not.toContainText('Exact run '+exact().runId);expect(params(page).target).toBe(beta);
});
