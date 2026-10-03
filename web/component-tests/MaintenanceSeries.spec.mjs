import { test,expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import {alpha,beta,seriesId,now,auditId,definition,preview,status,seriesBackend,windowId,nativeUtc} from './maintenanceSeriesFixture.mjs';
const origin='http://127.0.0.1:4328',sessions=new WeakMap();
const panel=page=>page.locator('.alerts-screen'),region=page=>panel(page).getByRole('region',{name:'Recurring maintenance',exact:true});
const button=(page,name)=>region(page).getByRole('button',{name,exact:true});
const field=(page,name)=>['Recurrence frequency','Repeated local time'].includes(name)
 ?region(page).getByRole('combobox',{name,exact:true}):['Series reason','Series cancellation reason'].includes(name)
  ?region(page).getByRole('textbox',{name,exact:true}):region(page).getByLabel(name,{exact:true});
const current=page=>region(page).getByRole('region',{name:'Current maintenance series',exact:true});
const occurrences=page=>region(page).getByRole('region',{name:'Series occurrences',exact:true});
async function json(route,body,code=200){await route.fulfill({status:code,contentType:'application/json',headers:{'cache-control':'no-store'},body:JSON.stringify(body)});}
function held(){let start,unlock,finish;const started=new Promise(resolve=>start=resolve),gate=new Promise(resolve=>unlock=resolve),done=new Promise(resolve=>finish=resolve);
 return{started,async release(){unlock();await done;},async handle(route,body,code=200){start(route.request());await gate;try{await json(route,body,code);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{finish();}return true;}};}
test.beforeEach(async({page})=>{
 const session={backend:seriesBackend(),requests:[],posts:[],oneOffPosts:[],previews:[],reads:[],occurrences:[],lists:[],windows:[],unexpected:[],handler:undefined};sessions.set(page,session);
 await page.clock.setFixedTime(new Date(now));await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
 page.on('pageerror',error=>session.unexpected.push(error.message));
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url()),match=/^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/maintenance(.*)$/u.exec(url.pathname);
  const reject=async()=>{session.unexpected.push('Unregistered '+request.method()+' '+url.href);await route.abort();};
  if(url.origin!==origin)return reject();if(!/^\/api(?:\/|$)/u.test(url.pathname))return route.continue();
  if(!match||![alpha,beta].includes(match[1]))return reject();session.requests.push(request);
  const target=match[1],parts=match[2].split('/').filter(Boolean),isSeries=parts[0]==='series',id=isSeries?parts[1]:parts[0],resource=isSeries?parts[2]:parts[1];
  let item;
  if(!isSeries&&!id&&request.method()==='GET'&&url.search==='?limit=100')item={type:'windows',target};
  else if(!isSeries&&['POST','PUT'].includes(request.method())&&!url.search){const body=request.postDataJSON(),kind=resource==='cancel'?'oneoff-cancel':request.method()==='PUT'?'oneoff-edit':'oneoff-create';
   item={type:'oneoff',target,id:id??body.windowId,kind,body};session.oneOffPosts.push(item);}
  else if(isSeries&&id==='preview'&&request.method()==='POST'&&!url.search){item={type:'preview',target,body:request.postDataJSON()};session.previews.push(item);}
  else if(isSeries&&!id&&request.method()==='GET'&&url.searchParams.get('limit')==='50'){item={type:'list',target,before:url.searchParams.get('beforeSeriesId')??undefined};session.lists.push(item);}
  else if(isSeries&&resource==='occurrences'&&request.method()==='GET'&&url.searchParams.get('limit')==='50'){item={type:'occurrences',target,id,before:url.searchParams.get('beforeWindowId')??undefined};session.occurrences.push(item);}
  else if(isSeries&&id&&request.method()==='GET'&&!url.search){item={type:'status',target,id};session.reads.push(item);}
  else if(isSeries&&['POST','PUT'].includes(request.method())&&!url.search){const kind=resource==='cancel'?'cancel':request.method()==='PUT'?'edit':'create',body=request.postDataJSON();
   item={type:'mutation',target,id:kind==='create'?body.seriesId:id,command:kind==='create'?{kind,body}:{kind,seriesId:id,body},raw:request.postData()};
   session.posts.push(item);expect(request.headers()['content-type']).toBe('application/json');
  }else return reject();
  if(item.type==='windows')session.windows.push(item);
  if(session.handler&&await session.handler(route,item))return;
  try{
   if(item.type==='windows')return json(route,session.backend.oneOff(target));
   if(item.type==='preview')return json(route,{...preview(item.body.seriesId,item.body.definition,item.body.effectiveFromDate),targetId:target});
   if(item.type==='list')return json(route,session.backend.list(target,item.before));
   if(item.type==='status')return json(route,session.backend.read(target,id));
   if(item.type==='occurrences')return json(route,session.backend.occurrences(target,id,item.before));
   if(item.type==='oneoff')return json(route,session.backend.changeWindow(target,item.kind,item.id,item.body));
   return json(route,session.backend.change(target,item.command,item.raw));
  }catch(error){return json(route,{detail:'PRIVATE_PROVIDER_DETAIL'},error.status??503);}
 });
});
test.afterEach(async({page})=>expect(sessions.get(page).unexpected,'Native registered routes only, no page errors/external traffic').toEqual([]));
async function mount(page,query=''){await page.goto('/component-tests/maintenance-series.html#/maintenance?target='+alpha+'&range=1h'+query);
 await expect(page.getByRole('heading',{name:'Maintenance series harness',exact:true})).toBeVisible();await expect(region(page)).toBeVisible();}
async function draft(page,changes={}){
 const d=definition(changes);await field(page,'Recurrence frequency').selectOption(d.frequency);await field(page,'Recurrence interval').fill(String(d.interval));
 await field(page,'Series start date').fill(d.startDate);await field(page,'Series end date').fill(d.endDate);await field(page,'Local start time').fill(d.localStartTime);
 await field(page,'Elapsed duration (minutes)').fill(String(d.durationMinutes));await field(page,'IANA time zone').fill(d.timeZoneId);await field(page,'Repeated local time').selectOption(d.foldPolicy);
 await field(page,'Series reason').fill(d.reason);if(d.frequency==='monthly')await field(page,'Day of month').fill(String(d.dayOfMonth));
}
async function ready(page,changes={}){await draft(page,changes);await button(page,'Preview maintenance series').click();await expect(region(page).getByRole('region',{name:'Server maintenance preview',exact:true})).toBeVisible();await expect(button(page,'Create maintenance series')).toBeEnabled();}
async function create(page){await ready(page);await button(page,'Create maintenance series').click();await expect(current(page)).toContainText('revision 1');}
test('server preview precedes creation; daily fold policy and exact native commands/readback remain separate',async({page})=>{
 const session=sessions.get(page);await mount(page);await draft(page);await expect(button(page,'Create maintenance series')).toBeDisabled();
 await button(page,'Preview maintenance series').click();await expect(region(page)).toContainText('2026-11-01T05:30:00.000000Z');await expect(region(page)).toContainText('2026-11-02T06:30:00.000000Z');
 expect(session.posts).toHaveLength(0);await button(page,'Create maintenance series').click();await expect(current(page)).toContainText('revision 1');
 await expect(region(page)).toContainText('2 created, 0 updated');expect(session.posts[0].command.body.definition.dayOfMonth).toBeNull();
 expect(session.previews[0].body.effectiveFromDate).toBeNull();expect(session.reads).toHaveLength(1);
 expect(new URL(page.url()).hash).toContain('seriesId='+session.posts[0].id);await expect(current(page)).toContainText('separate from retained preview');
});
test('weekly weekdays and later fold preview are sent as explicit civil choices without client expansion',async({page})=>{
 const session=sessions.get(page);await mount(page);await draft(page,{frequency:'weekly',weekdays:[0],foldPolicy:'later'});
 await field(page,'Monday').check();await button(page,'Preview maintenance series').click();await expect(region(page)).toContainText('2026-11-01T06:30:00.000000Z');
 expect(session.previews[0].body.definition.weekdays).toEqual([0,1]);await button(page,'Create maintenance series').click();await expect(current(page)).toBeVisible();
 expect(session.posts[0].command.body.definition.foldPolicy).toBe('later');
});
test('monthly missing civil day and DST gap are displayed as skips, never synthetic occurrence identities',async({page})=>{
 await mount(page);await ready(page,{frequency:'monthly',dayOfMonth:31,startDate:'2027-02-01',endDate:'2027-03-31'});
 await expect(region(page)).toContainText('Requested 2027-02-31: day does not exist in this month');await expect(region(page)).toContainText('2027-03-31');
 await button(page,'New series draft').click();await ready(page,{startDate:'2027-03-14',endDate:'2027-03-15',localStartTime:'02:30'});
 await expect(region(page)).toContainText('Requested 2027-03-14: local time does not exist');await expect(region(page)).toContainText('2027-03-15T06:30:00.000000Z');
});
test('changing draft invalidates its received preview before any mutation',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);await field(page,'Series reason').fill('Changed after preview');
 await expect(button(page,'Create maintenance series')).toBeDisabled();await expect(region(page).getByRole('region',{name:'Server maintenance preview',exact:true})).toHaveCount(0);
 expect(session.posts).toHaveLength(0);
});
test('future edit updates stable window IDs and reports real updated count; cancel confirmation retains exceptions and irreversible fence',async({page})=>{
 const session=sessions.get(page);await mount(page);await create(page);const id=session.posts[0].id,first=windowId(id,'2026-11-01'),last=windowId(id,'2026-11-02');
 session.backend.windows.get(first).exception=true;session.backend.windows.get(first).reason='Individual exception';
 await button(page,'Edit future occurrences').click();await field(page,'Effective from date').fill('2026-11-01');await field(page,'Local start time').fill('02:30');
 await button(page,'Preview maintenance series').click();await button(page,'Save future changes').click();await expect(current(page)).toContainText('revision 2');
 await expect(region(page)).toContainText('0 created, 1 updated, 0 cancelled, 1 exceptions preserved');expect(session.backend.windows.has(last)).toBe(true);
 await field(page,'Cancellation effective from date').fill('2026-11-02');await field(page,'Series cancellation reason').fill('Stop remaining future dates');
 await button(page,'Cancel future occurrences').click();expect(session.posts).toHaveLength(2);
 await expect(region(page).getByRole('region',{name:'Future cancellation confirmation',exact:true})).toContainText('2026-11-02');
 await button(page,'Confirm future cancellation').click();await expect(current(page)).toContainText('Cancelled from 2026-11-02');await expect(current(page)).toContainText('there is no reactivation action');
 await button(page,'Load series occurrences').click();await expect(occurrences(page)).toContainText('Individual exception');await expect(occurrences(page)).toContainText('Cancelled');
 expect(session.backend.windows.get(first).cancelledAtUtc).toBeNull();await expect(occurrences(page)).toContainText('original schedule offset and source revision');
});
test('confirmed POST with unavailable current readback retries only GET and preserves immutable counts',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);
 session.handler=async(route,item)=>item.type==='status'&&session.reads.length===1?(await json(route,{},503),true):false;
 await button(page,'Create maintenance series').click();await expect(button(page,'Retry series status')).toBeVisible();
 await expect(region(page)).toContainText('Series operation recorded, but current readback is unavailable');await expect(current(page)).toHaveCount(0);
 await button(page,'Retry series status').click();await expect(current(page)).toContainText('revision 1');expect(session.posts).toHaveLength(1);expect(session.reads).toHaveLength(2);
});
test('uncertain future change survives polls/draft changes and replays its complete exact command',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);
 session.handler=async(route,item)=>{if(item.type!=='mutation'||session.posts.length!==1)return false;session.backend.change(item.target,item.command,item.raw);await route.abort('failed');return true;};
 await button(page,'Create maintenance series').click();await expect(button(page,'Retry series operation')).toBeVisible();
 await field(page,'Series reason').fill('Changed draft');await field(page,'Series end date').fill('2026-11-03');await page.getByRole('button',{name:'Workspace tick',exact:true}).click();
 await button(page,'Retry series operation').click();await expect(current(page)).toContainText('revision 1');
 expect(session.posts[1].raw).toBe(session.posts[0].raw);expect(session.backend.operations.size).toBe(1);
});
test('failed and cancelled explicit status refresh retain the original uncertain future operation',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);
 session.handler=async(route,item)=>item.type==='mutation'?(await json(route,{},503),true):false;
 await button(page,'Create maintenance series').click();await expect(button(page,'Retry series operation')).toBeVisible();
 session.handler=async(route,item)=>item.type==='status'?(await json(route,{},503),true):false;
 await button(page,'Refresh series status').click();await expect(button(page,'Retry series status')).toBeVisible();await expect(button(page,'Retry series operation')).toBeVisible();
 const pending=held();session.handler=(route,item)=>item.type==='status'?pending.handle(route,{}):false;
 await button(page,'Retry series status').click();const request=await pending.started;await button(page,'Cancel series request').click();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();
 await expect(button(page,'Retry series operation')).toBeVisible();session.handler=undefined;await button(page,'Retry series operation').click();await expect(current(page)).toBeVisible();
 expect(session.posts[1].raw).toBe(session.posts[0].raw);
});
test('revision conflict requires new current read, explicit edit draft and new server preview',async({page})=>{
 const session=sessions.get(page);await mount(page);await create(page);const id=session.posts[0].id;
 await button(page,'Edit future occurrences').click();await field(page,'Effective from date').fill('2026-11-01');await button(page,'Preview maintenance series').click();
 session.backend.series.get(alpha+':'+id).revision=2;await button(page,'Save future changes').click();await expect(region(page)).toContainText('server refused this future change');
 await expect(button(page,'Retry series operation')).toHaveCount(0);await button(page,'Refresh series status').click();await expect(current(page)).toContainText('revision 2');
 await expect(button(page,'Save future changes')).toBeDisabled();await button(page,'Edit future occurrences').click();await field(page,'Effective from date').fill('2026-11-01');
 await button(page,'Preview maintenance series').click();await button(page,'Save future changes').click();await expect(current(page)).toContainText('revision 3');
 expect(session.posts[2].command.body.expectedRevision).toBe(2);expect(session.posts[2].command.body.operationId).not.toBe(session.posts[1].command.body.operationId);
});
test('shareable current series reload and Back/Forward preserve explicit identity without default substitution',async({page})=>{
 const session=sessions.get(page);session.backend.change(alpha,{kind:'create',body:{operationId:auditId,seriesId,definition:definition()}});
 await mount(page,'&seriesId='+seriesId);await expect(current(page)).toContainText('revision 1');await page.reload();await expect(current(page)).toContainText('revision 1');
 const other='99999999-9999-4999-8999-999999999999';await page.evaluate(value=>{const q=new URLSearchParams(location.hash.split('?')[1]);q.set('seriesId',value);location.hash='#/maintenance?'+q;},other);
 await expect(button(page,'Retry series status')).toBeVisible();await expect(current(page)).toHaveCount(0);
 await page.goBack();await expect(current(page)).toContainText('revision 1');await page.goForward();await expect(current(page)).toHaveCount(0);
 expect(session.posts).toHaveLength(0);
});
test('invalid explicit series URL cannot select the first received series',async({page})=>{
 const session=sessions.get(page);session.backend.change(alpha,{kind:'create',body:{operationId:auditId,seriesId,definition:definition()}});
 await mount(page,'&seriesId=bad');await expect(region(page)).toContainText('series URL selection is invalid');await expect(current(page)).toHaveCount(0);expect(session.reads).toHaveLength(0);
});
test('selecting another window during a held initial administrative read starts a correctly owned replacement',async({page})=>{
 const session=sessions.get(page);session.backend.change(alpha,{kind:'create',body:{operationId:auditId,seriesId,definition:definition()}});
 const first=windowId(seriesId,'2026-11-01'),second=windowId(seriesId,'2026-11-02'),pending=held();
 session.handler=(route,item)=>item.type==='windows'&&session.windows.length===1?pending.handle(route,session.backend.oneOff(alpha)):false;
 await mount(page,'&seriesId='+seriesId+'&windowId='+first);const request=await pending.started;
 await page.evaluate(id=>{const q=new URLSearchParams(location.hash.split('?')[1]);q.set('windowId',id);location.hash='#/maintenance?'+q;},second);
 await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();
 await expect(panel(page).getByRole('complementary',{name:'Maintenance window',exact:true})).toContainText(second);
 expect(session.windows).toHaveLength(2);await expect(panel(page)).not.toContainText('This maintenance read no longer owns the context');
 expect(new URL(page.url()).hash).toContain('windowId='+second);expect(session.posts).toHaveLength(0);
});
test('selecting another series cancels a held listing refresh without latching its controls',async({page})=>{
 const session=sessions.get(page),other='99999999-9999-4999-8999-999999999999';
 session.backend.change(alpha,{kind:'create',body:{operationId:auditId,seriesId,definition:definition()}});
 session.backend.change(alpha,{kind:'create',body:{operationId:beta,seriesId:other,definition:definition({reason:'Other received series'})}});
 await mount(page,'&seriesId='+seriesId);await expect(current(page)).toContainText('revision 1');
 const pending=held();session.handler=(route,item)=>item.type==='list'?pending.handle(route,session.backend.list(alpha)):false;
 await button(page,'Refresh maintenance series').click();const request=await pending.started;
 await button(page,'Select series '+other).click();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();
 await expect(current(page)).toContainText('Other received series');await expect(button(page,'Refresh maintenance series')).toBeEnabled();
 await expect(button(page,'Retry maintenance series list')).toBeVisible();session.handler=undefined;
 await button(page,'Retry maintenance series list').click();expect(session.lists.at(-1).before).toBeUndefined();
 await expect(region(page).getByRole('region',{name:'Maintenance series listing',exact:true})).toContainText('2 received series');
});
test('bounded series listing retains native full pages and exact retry anchor until a verified refresh',async({page})=>{
 const session=sessions.get(page);for(let i=1;i<=51;i++){const id='77777777-7777-4777-8777-'+String(i).padStart(12,'0');session.backend.series.set(alpha+':'+id,status(id,definition({reason:'Recorded series '+i})));}
 await mount(page);const listing=region(page).getByRole('region',{name:'Maintenance series listing',exact:true});await expect(listing).toContainText('50 received series');
 session.handler=async(route,item)=>item.type==='list'&&item.before?(await json(route,{},503),true):false;
 await button(page,'Load more maintenance series').click();await expect(button(page,'Retry maintenance series list')).toBeVisible();const anchor=session.lists.at(-1).before;
 await expect(listing).toContainText('50 received series');session.handler=undefined;await button(page,'Retry maintenance series list').click();await expect(listing).toContainText('51 received series');expect(session.lists.at(-1).before).toBe(anchor);
 session.backend.series.delete(alpha+':77777777-7777-4777-8777-000000000051');session.handler=async(route,item)=>item.type==='list'?(await json(route,{},503),true):false;
 await button(page,'Refresh maintenance series').click();await expect(button(page,'Retry maintenance series list')).toBeVisible();await expect(listing).toContainText('51 received series');
 session.handler=undefined;await button(page,'Retry maintenance series list').click();await expect(listing).toContainText('50 received series');expect(session.lists.at(-1).before).toBeUndefined();
});
test('validated empty edit preview cancels obsolete ordinary future windows and preserves earlier dates',async({page})=>{
 const session=sessions.get(page);await mount(page);await create(page);const id=session.posts[0].id;
 await button(page,'Edit future occurrences').click();await field(page,'Effective from date').fill('2026-11-02');await field(page,'Series end date').fill('2026-11-01');
 await button(page,'Preview maintenance series').click();await expect(region(page)).toContainText('0 proposed future occurrences');
 await expect(region(page)).toContainText('Only the applied receipt reports actual cancellations');await expect(button(page,'Save future changes')).toBeEnabled();
 await button(page,'Save future changes').click();await expect(current(page)).toContainText('revision 2');await expect(region(page)).toContainText('0 created, 0 updated, 1 cancelled');
 expect(session.backend.windows.get(windowId(id,'2026-11-01')).cancelledAtUtc).toBeNull();expect(session.backend.windows.get(windowId(id,'2026-11-02')).cancelledAtUtc).toBe(now);
});
test('uncertain creation B reconciles B, never selected A, and B404 preserves its complete retry',async({page})=>{
 const session=sessions.get(page);session.backend.change(alpha,{kind:'create',body:{operationId:auditId,seriesId,definition:definition({reason:'Received series A'})}});
 await mount(page,'&seriesId='+seriesId);await expect(current(page)).toContainText('Received series A');await ready(page,{reason:'New series B'});
 session.handler=async(route,item)=>item.type==='mutation'?(await json(route,{},503),true):false;
 await button(page,'Create maintenance series').click();await expect(button(page,'Retry series operation')).toBeVisible();const uncertain=session.posts[0];
 await button(page,'Refresh series status').click();await expect(button(page,'Retry series status')).toBeVisible();expect(session.reads.at(-1).id).toBe(uncertain.id);
 await expect(current(page)).toContainText('Received series A');await expect(button(page,'Retry series operation')).toBeVisible();
 session.handler=undefined;await button(page,'Retry series operation').click();await expect(current(page)).toContainText('New series B');expect(session.posts[1].raw).toBe(uncertain.raw);
});
for(const source of ['preview','mutation','status','occurrences','windows'])for(const code of [401,403])test('HTTP '+code+' '+source+' clears both protected maintenance sources and fences polling recovery',async({page})=>{
 const session=sessions.get(page);await mount(page);await create(page);
 session.handler=async(route,item)=>item.type===source?(await json(route,{detail:'PRIVATE_PROVIDER_DETAIL'},code),true):false;
 if(source==='preview'){await button(page,'New series draft').click();await draft(page);await button(page,'Preview maintenance series').click();}
 else if(source==='mutation'){await field(page,'Cancellation effective from date').fill('2026-11-02');await field(page,'Series cancellation reason').fill('Stop future');await button(page,'Cancel future occurrences').click();await button(page,'Confirm future cancellation').click();}
 else if(source==='status')await button(page,'Refresh series status').click();
 else if(source==='occurrences')await button(page,'Load series occurrences').click();
 else await page.getByRole('button',{name:'Workspace tick',exact:true}).click();
 await expect(panel(page).getByRole('alert').filter({hasText:/^Maintenance access was denied\. Protected windows and series were removed\./u})).toBeVisible();await expect(current(page)).toHaveCount(0);
 await expect(panel(page).getByRole('complementary',{name:'Maintenance window',exact:true})).toHaveCount(0);await expect(button(page,'Create maintenance series')).toHaveCount(0);
 await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_DETAIL');const count=session.requests.length;await page.getByRole('button',{name:'Workspace tick',exact:true}).click();expect(session.requests).toHaveLength(count);
});
for(const context of ['target','window','role','unmount'])test('held series mutation aborts and fences late success across '+context,async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);const pending=held();
 session.handler=(route,item)=>item.type==='mutation'?pending.handle(route,session.backend.change(item.target,item.command,item.raw)):false;
 await button(page,'Create maintenance series').click();const request=await pending.started,aborted=page.waitForEvent('requestfailed',{predicate:r=>r===request});
 if(context==='target')await page.getByRole('combobox',{name:'Server context',exact:true}).selectOption(beta);
 else if(context==='window')await page.getByRole('combobox',{name:'Window context',exact:true}).selectOption('custom');
 else if(context==='role')await page.getByRole('checkbox',{name:'Target Administrator permission',exact:true}).uncheck();
 else await page.getByRole('button',{name:'Unmount maintenance',exact:true}).click();
 await aborted;await pending.release();
 if(context==='unmount')await expect(panel(page)).toHaveCount(0);else await expect(region(page)).not.toContainText('Immutable series receipt');
 expect(session.posts).toHaveLength(1);
});
test('target ABA does not let the old response publish an immutable receipt',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);const pending=held();
 session.handler=(route,item)=>item.type==='mutation'?pending.handle(route,session.backend.change(item.target,item.command,item.raw)):false;
 await button(page,'Create maintenance series').click();const request=await pending.started,aborted=page.waitForEvent('requestfailed',{predicate:r=>r===request});
 await page.getByRole('combobox',{name:'Server context',exact:true}).selectOption(beta);await aborted;await page.getByRole('combobox',{name:'Server context',exact:true}).selectOption(alpha);await pending.release();
 await expect(region(page)).not.toContainText('Immutable series receipt');expect(session.posts).toHaveLength(1);
});
test('occurrence paging retains exact native anchor on failure/cancel/retry and selection opens the legacy editor',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page,{startDate:'2026-11-01',endDate:'2026-12-21',timeZoneId:'UTC'});await button(page,'Create maintenance series').click();await expect(current(page)).toBeVisible();
 await button(page,'Load series occurrences').click();await expect(occurrences(page)).toContainText('50 received occurrences');const pending=held();
 session.handler=(route,item)=>item.type==='occurrences'&&item.before?pending.handle(route,session.backend.occurrences(item.target,item.id,item.before),503):false;
 await button(page,'Load older series occurrences').click();const request=await pending.started;await button(page,'Cancel occurrence read').click();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();
 await expect(occurrences(page)).toContainText('50 received occurrences');const anchor=session.occurrences.at(-1).before;session.handler=undefined;
 await button(page,'Retry series occurrences').click();await expect(occurrences(page)).toContainText('51 received occurrences');expect(session.occurrences.at(-1).before).toBe(anchor);
 const id=session.backend.occurrences(alpha,session.posts[0].id).items[0].windowId;await button(page,'Select occurrence '+id).click();
 await expect(panel(page).getByRole('complementary',{name:'Maintenance window',exact:true})).toContainText(id);expect(new URL(page.url()).hash).toContain('windowId='+id);
 await expect(panel(page).getByRole('button',{name:'Edit window',exact:true})).toBeEnabled();
});
test('received occurrences remain unchanged until explicit refresh after a future mutation',async({page})=>{
 await mount(page);await create(page);await button(page,'Load series occurrences').click();await expect(occurrences(page)).toContainText('2026-11-02T06:30:00.000000Z');
 await button(page,'Edit future occurrences').click();await field(page,'Effective from date').fill('2026-11-01');await field(page,'Local start time').fill('02:30');
 await button(page,'Preview maintenance series').click();await button(page,'Save future changes').click();await expect(current(page)).toContainText('revision 2');
 await expect(occurrences(page)).toContainText('2026-11-02T06:30:00.000000Z');await button(page,'Refresh series occurrences').click();await expect(occurrences(page)).toContainText('2026-11-02T07:30:00.000000Z');
});
test('series POST synchronously excludes one-off scheduling including same-batch submission',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);
 await page.getByLabel('Starts (local time)',{exact:true}).fill('2026-11-03T02:00');await page.getByLabel('Ends (local time)',{exact:true}).fill('2026-11-03T03:00');await page.getByLabel('Reason',{exact:true}).fill('One-off draft');
 const pending=held();session.handler=(route,item)=>item.type==='mutation'?pending.handle(route,session.backend.change(item.target,item.command,item.raw)):false;
 await button(page,'Create maintenance series').evaluate(node=>{node.click();Array.from(document.querySelectorAll('button')).find(item=>item.textContent==='Schedule window')?.click();});
 await pending.started;await expect(panel(page).getByRole('button',{name:'Schedule window',exact:true})).toBeDisabled();expect(session.requests.filter(r=>r.method()==='POST'&&!r.url().includes('/series'))).toHaveLength(0);
 await pending.release();await expect(current(page)).toBeVisible();
});
test('one-off POST synchronously excludes series creation until the owner settles',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);
 await page.getByLabel('Starts (local time)',{exact:true}).fill('2026-11-03T02:00');await page.getByLabel('Ends (local time)',{exact:true}).fill('2026-11-03T03:00');await page.getByLabel('Reason',{exact:true}).fill('One-off owner');
 const pending=held();session.handler=(route,item)=>item.type==='oneoff'?pending.handle(route,session.backend.changeWindow(item.target,item.kind,item.id,item.body)):false;
 await panel(page).getByRole('button',{name:'Schedule window',exact:true}).evaluate(node=>{node.click();Array.from(document.querySelectorAll('button')).find(item=>item.textContent==='Create maintenance series')?.click();});
 await pending.started;await expect(button(page,'Create maintenance series')).toBeDisabled();expect(session.posts).toHaveLength(0);
 await pending.release();await expect(panel(page).getByRole('complementary',{name:'Maintenance window',exact:true})).toContainText('One-off owner');
});
test('selected generated windows use native individual edit and cancellation without rewriting original provenance',async({page})=>{
 const session=sessions.get(page);await mount(page);await create(page);const id=session.posts[0].id,wid=windowId(id,'2026-11-01');
 await button(page,'Load series occurrences').click();await button(page,'Select occurrence '+wid).click();
 await panel(page).getByRole('button',{name:'Edit window',exact:true}).click();await page.getByRole('form',{name:'Edit maintenance window',exact:true}).getByRole('textbox',{name:'Edit reason',exact:true}).fill('Individual reason override');
 await panel(page).getByRole('button',{name:'Save window changes',exact:true}).click();await expect(panel(page).getByRole('complementary',{name:'Maintenance window',exact:true})).toContainText('Individual reason override');
 expect(session.oneOffPosts[0].body.expectedRevision).toBe(1);expect(session.oneOffPosts[0].body.startsAtUtc).toBe(session.backend.windows.get(wid).startsAtUtc);
 await button(page,'Load series occurrences').click();await expect(occurrences(page)).toContainText('Individual exception');await expect(occurrences(page)).toContainText('Individual reason override');
 expect(session.backend.windows.get(wid).seriesRevision).toBe(1);expect(session.backend.windows.get(wid).utcOffsetMinutes).toBe(-240);
 await panel(page).getByRole('button',{name:'Cancel window',exact:true}).click();await expect(panel(page).getByRole('complementary',{name:'Maintenance window',exact:true})).toContainText('Cancelled');
 expect(session.oneOffPosts[1].body.expectedRevision).toBe(2);expect(session.backend.windows.get(wid).exception).toBe(true);
 await button(page,'Refresh series occurrences').click();await expect(occurrences(page)).toContainText('Cancelled');
});
test('role loss and regrant keep a private uncertain command for an explicit exact retry',async({page})=>{
 const session=sessions.get(page);await mount(page);await ready(page);const pending=held();
 session.handler=(route,item)=>item.type==='mutation'?pending.handle(route,{},503):false;
 await button(page,'Create maintenance series').click();const request=await pending.started;
 await page.getByRole('checkbox',{name:'Target Administrator permission',exact:true}).uncheck();await expect.poll(()=>request.failure()?.errorText).toContain('ERR_ABORTED');await pending.release();
 await expect(region(page)).not.toContainText('Synthetic overnight maintenance');await expect(button(page,'Retry series operation')).toHaveCount(0);
 session.handler=undefined;await page.getByRole('checkbox',{name:'Target Administrator permission',exact:true}).check();await expect(button(page,'Retry series operation')).toBeVisible();
 await button(page,'Retry series operation').click();await expect(current(page)).toContainText('revision 1');expect(session.posts[1].raw).toBe(session.posts[0].raw);
});
for(const width of [320,390,768,1440])test('dark recurring maintenance keyboard/accessibility/responsive workflow at '+width+'px',async({page},info)=>{
 await page.setViewportSize({width,height:1200});await mount(page);await ready(page);
 await button(page,'Create maintenance series').focus();await page.keyboard.press('Enter');await expect(current(page)).toContainText('revision 1');
 await button(page,'Load series occurrences').click();await expect(occurrences(page)).toContainText('2 received occurrences');
 await field(page,'Series reason').focus();await page.keyboard.press('Tab');await expect(button(page,'Preview maintenance series')).toBeFocused();
 const focus=await button(page,'Preview maintenance series').evaluate(node=>({style:getComputedStyle(node).outlineStyle,width:parseFloat(getComputedStyle(node).outlineWidth)}));expect(focus.style).not.toBe('none');expect(focus.width).toBeGreaterThanOrEqual(2);
 expect(await page.evaluate(()=>getComputedStyle(document.documentElement).colorScheme)).toBe('dark');expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const violations=(await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','best-practice']).analyze()).violations;await info.attach('series-'+width+'-axe',{body:JSON.stringify(violations,null,2),contentType:'application/json'});expect(violations).toEqual([]);
 const directory=fileURLToPath(new URL('../../artifacts/dashboard-maintenance-series/screenshots/',import.meta.url));await mkdir(directory,{recursive:true});
 await page.evaluate(()=>scrollTo({top:0,left:0,behavior:'instant'}));await page.screenshot({path:directory+'/series-'+width+'.png',fullPage:true});
});
