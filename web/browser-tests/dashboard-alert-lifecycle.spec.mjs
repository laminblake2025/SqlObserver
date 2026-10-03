import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {resolve} from 'node:path';
import {installAlertLifecycleHistoryFixture,alertLifecycleHistoryBackend,lifecycleFixtureEvent,lifecycleAlpha,lifecycleBeta,lifecycleFromUtc,lifecycleToUtc} from '../component-tests/alertLifecycleHistoryFixture.mjs';
const origin='http://127.0.0.1:4327',binding={targetId:lifecycleAlpha,targetRevision:1,fromUtc:lifecycleFromUtc,toUtc:lifecycleToUtc};
const href=(extra={})=>'/#/health?'+new URLSearchParams({target:lifecycleAlpha,range:'custom',from:lifecycleFromUtc,to:lifecycleToUtc,healthSection:'summary',...extra});
const identity=e=>`alert-history:${e.eventId}:${e.alertId}:${e.episodeId??'legacy'}`;
const selectedHref=e=>href({timelineEvent:identity(e),timelineAt:e.eventAtUtc});
const panel=p=>p.getByRole('region',{name:'Alert lifecycle history',exact:true}),detail=p=>panel(p).getByRole('region',{name:'Selected alert lifecycle event',exact:true}),timeline=p=>p.getByRole('region',{name:'Investigation timeline',exact:true});
const button=(p,name)=>panel(p).getByRole('button',{name:name.startsWith('Inspect alert event ')?new RegExp('^'+name+': Alert '):name,exact:true}),query=p=>new URLSearchParams(new URL(p.url()).hash.split('?')[1]);
const sessions=new WeakMap();
async function browser(p){const summary=panel(p).getByText('Browse alert lifecycle events',{exact:true});if(!await summary.locator('xpath=parent::details').evaluate(n=>n.open))await summary.click();}
const timelineBrowser=p=>timeline(p).getByText(/^Browse loaded timeline events \(\d+\)$/).locator('xpath=parent::details');
async function browseTimeline(p){const browser=timelineBrowser(p);if(!await browser.evaluate(n=>n.open))await browser.locator('summary').click();await expect(timeline(p).getByRole('region',{name:'Loaded timeline event list',exact:true})).toBeVisible();}
async function setup(p,count=16,liveClock=false){if(liveClock)await p.clock.install({time:new Date('2026-10-03T12:00:01Z')});else await p.clock.setFixedTime(new Date('2026-10-03T12:00:01Z'));await p.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));await p.context().addCookies([{name:'dashboardRefactor',value:'1',url:origin},{name:'chartReferenceRole',value:'viewer',url:origin},{name:'databaseScopeFixture',value:'1',url:origin}]);const session=await installAlertLifecycleHistoryFixture(p,{origin,backend:alertLifecycleHistoryBackend({count})});sessions.set(p,session);p.on('pageerror',error=>session.errors.push(error.message));return session;}
test.afterEach(async({page})=>{const session=sessions.get(page);if(session)expect(session.errors).toEqual([]);});

test('all native lifecycle verbs and resolved cycles remain evidence independent of current inbox',async({page})=>{
 const session=await setup(page);await page.goto(href({databaseId:'6',compare:'1'}));await browser(page);await expect(panel(page)).toContainText('16 received of 16');
 for(const kind of ['fired','acknowledged','resolved','snoozed','unsnoozed','assigned','reassigned','unassigned'])await expect(panel(page).getByRole('table')).toContainText('Alert '+kind);
 const event=lifecycleFixtureEvent(7,binding);await button(page,'Inspect alert event '+event.eventId).click();await expect(detail(page)).toContainText('Alert resolved');
 expect(query(page).get('timelineEvent')).toBe(identity(event));expect(query(page).get('databaseId')).toBe('6');expect(query(page).get('compare')).toBe('1');
 await page.reload();await expect(detail(page)).toContainText(event.alertId);await expect(detail(page)).toContainText(event.episodeId);await expect(detail(page)).toContainText('supplies no current mutation');
 await expect(timelineBrowser(page)).not.toHaveAttribute('open','');await expect(timeline(page).getByRole('link',{name:'Open selected event evidence',exact:true})).toBeVisible();
 const refire=lifecycleFixtureEvent(8,binding);await browser(page);await button(page,'Inspect alert event '+refire.eventId).click();await expect(detail(page)).toContainText(refire.alertId);expect(refire.alertId).not.toBe(event.alertId);expect(refire.episodeId).not.toBe(event.episodeId);
 await page.goBack();await expect(detail(page)).toContainText(event.alertId);await page.goForward();await expect(detail(page)).toContainText(refire.alertId);
 expect(session.reads.every(r=>r.binding.targetId===lifecycleAlpha&&r.binding.fromUtc===lifecycleFromUtc&&r.binding.toUtc===lifecycleToUtc)).toBe(true);
 expect(session.requests.every(r=>r.method()==='GET')).toBe(true);
});

test('exact restored event absent from first page resolves independently and pins chart alignment',async({page})=>{
 const session=await setup(page,201),event=lifecycleFixtureEvent(200,binding,{count:201});await page.goto(selectedHref(event));
 await expect(detail(page)).toContainText(event.eventId);await expect(detail(page)).toContainText('Alert reassigned');await expect(panel(page)).toContainText('100 received of 201');
 await expect(timeline(page)).toContainText('validated exact selected event');
 await expect(timelineBrowser(page)).not.toHaveAttribute('open','');await expect(timeline(page).getByRole('link',{name:'Open selected event evidence',exact:true})).toBeVisible();
 await timeline(page).getByRole('button',{name:'Show selected event in timeline list',exact:true}).press('Enter');
 const pressed=timeline(page).locator('button.dashboard-timeline-align[aria-pressed="true"]');await expect(pressed).toHaveCount(1);
 await expect(pressed).toBeFocused();
 expect(session.reads.filter(r=>r.selected)).toHaveLength(1);expect(session.reads.filter(r=>!r.selected)).toHaveLength(1);expect(session.reads.some(r=>r.cursor)).toBe(false);
 await page.reload();await expect(detail(page)).toContainText(event.eventId);await expect(timelineBrowser(page)).not.toHaveAttribute('open','');await expect(timeline(page).getByRole('link',{name:'Open selected event evidence',exact:true})).toBeVisible();await browseTimeline(page);await expect(pressed).toHaveCount(1);
});

test('an older selected event remains pinned after all received pages exceed the 120-marker summary',async({page})=>{
 await setup(page,201);const event=lifecycleFixtureEvent(0,binding,{count:201});await page.goto(selectedHref(event));await expect(detail(page)).toContainText(event.eventId);await browser(page);
 await button(page,'Next alert history page').click();await expect(panel(page)).toContainText('200 received of 201');await button(page,'Next alert history page').click();await expect(panel(page)).toContainText('201 received of 201');
 await browseTimeline(page);
 await expect(timeline(page).locator('button.dashboard-timeline-align')).toHaveCount(120);await expect(timeline(page).locator('button.dashboard-timeline-align[aria-pressed="true"]')).toHaveCount(1);await expect(timeline(page)).toContainText('119 other latest loaded events');
 expect(query(page).get('timelineEvent')).toBe(identity(event));await expect(detail(page)).toContainText(event.eventId);
});

test('list overflow cannot prevent independently resolving an exact selected event',async({page})=>{
 const session=await setup(page,10001),event=lifecycleFixtureEvent(0,binding,{count:10001});await page.goto(selectedHref(event));
 await expect(panel(page).getByText('Browse alert lifecycle events',{exact:true}).locator('xpath=parent::details')).not.toHaveAttribute('open','');
 await expect(button(page,'Retry alert history read')).toBeVisible();await expect(detail(page)).toContainText(event.eventId);await expect(detail(page)).toContainText('does not establish complete window coverage');
 expect(session.reads.filter(r=>r.selected)).toHaveLength(1);expect(session.reads.filter(r=>!r.selected)).toHaveLength(1);
});

test('missing mismatched and malformed exact selections never choose another event or cycle',async({page})=>{
 const session=await setup(page),event=lifecycleFixtureEvent(7,binding);const wrong={...event,episodeId:lifecycleFixtureEvent(8,binding).episodeId};await page.goto(selectedHref(wrong));
 await expect(detail(page)).toContainText('This exact event is not retained');await expect(detail(page).getByRole('link',{name:/Open current alert status/})).toHaveCount(0);
 await expect(timeline(page).getByRole('link',{name:'Open selected event evidence',exact:true})).toHaveCount(0);
 await page.getByRole('button',{name:'Review selected alert event evidence',exact:true}).press('Enter');await expect(page.getByRole('region',{name:'Alert history evidence',exact:true})).toBeFocused();
 const before=session.reads.filter(r=>r.selected).length;await page.goto(selectedHref(event)+'&timelineEvent='+encodeURIComponent(identity(event)));await expect(panel(page)).toContainText('requested alert event and cycle are invalid');expect(session.reads.filter(r=>r.selected)).toHaveLength(before);
 await page.goto(selectedHref({...event,eventAtUtc:lifecycleToUtc}));await expect(panel(page)).toContainText('requested alert event and cycle are invalid');expect(session.reads.filter(r=>r.selected)).toHaveLength(before);
});

test('history remains independently readable when core Overview analytics fails',async({page})=>{
 await setup(page);await page.route('**/api/v1/overview?*',async route=>{if(new URL(route.request().url()).searchParams.get('targetId')===lifecycleAlpha)return route.fulfill({status:503,contentType:'application/json',body:'{}'});return route.continue();});
 await page.goto(href());await expect(panel(page)).toContainText('16 received of 16');await browser(page);await expect(panel(page).getByRole('table')).toContainText('Alert resolved');
});

test('definitive history denial purges list and exact event; explicit access retry recovers',async({page})=>{
 const session=await setup(page),event=lifecycleFixtureEvent(7,binding);await page.goto(selectedHref(event));await expect(detail(page)).toContainText(event.eventId);await browser(page);await expect(panel(page)).toContainText('16 received of 16');
 session.handler=async(route)=>{await route.fulfill({status:403,contentType:'application/json',body:'{}'});return true;};await button(page,'Refresh selected alert event').click();await expect(panel(page)).toContainText('Protected event pages and selected detail were removed');await expect(detail(page)).toHaveCount(0);await expect(panel(page).getByRole('table')).toHaveCount(0);
 session.handler=undefined;await button(page,'Retry alert history access').click();await expect(detail(page)).toContainText(event.eventId);await browser(page);await expect(panel(page)).toContainText('16 received of 16');
});

test('opening history fixes the global Live window and keeps visited pages through real workspace polling',async({page})=>{
 const session=await setup(page,101,true);await page.goto(href({range:'1h',from:'',to:''}));await expect(panel(page)).toContainText('100 received of 101');
 await browser(page);await expect.poll(()=>query(page).get('range')).toBe('custom');const fixed={from:query(page).get('from'),to:query(page).get('to')};
 await button(page,'Next alert history page').click();await expect(panel(page)).toContainText('101 received of 101');const reads=session.reads.length,cursor=session.reads.at(-1).cursor;
 await page.clock.runFor(61_000);await expect(panel(page)).toContainText('page 2');expect(session.reads).toHaveLength(reads);expect(query(page).get('from')).toBe(fixed.from);expect(query(page).get('to')).toBe(fixed.to);expect(cursor).toBeTruthy();
 await button(page,'First alert history page').click();await button(page,'Next alert history page').click();expect(session.reads).toHaveLength(reads);
});

for(const width of [320,390,768,1440])test(`integrated alert history uses dark tokens keyboard and responsive evidence at ${width}px`,async({page},info)=>{
 await page.setViewportSize({width,height:1000});await setup(page);const event=lifecycleFixtureEvent(7,binding);await page.goto(selectedHref(event));await expect(detail(page)).toContainText(event.eventId);await browser(page);
 const scroll=panel(page).getByRole('region',{name:'Received alert lifecycle event table',exact:true});await scroll.focus();await expect(scroll).toBeFocused();if(width<=390){expect(await scroll.evaluate(n=>n.scrollWidth>n.clientWidth)).toBe(true);await page.keyboard.press('ArrowRight');await expect.poll(()=>scroll.evaluate(n=>n.scrollLeft)).toBeGreaterThan(0);}else{expect(await scroll.evaluate(n=>n.scrollWidth-n.clientWidth)).toBeLessThanOrEqual(1);}
 await button(page,'Refresh selected alert event').focus();await page.keyboard.press('Enter');await expect(detail(page)).toContainText(event.eventId);
 expect(await page.evaluate(()=>getComputedStyle(document.documentElement).colorScheme)).toBe('dark');expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const result=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','best-practice']).analyze();await info.attach('axe',{body:JSON.stringify(result.violations),contentType:'application/json'});expect(result.violations).toEqual([]);
 const folder=resolve('../artifacts/dashboard-alert-lifecycle/screenshots');await mkdir(folder,{recursive:true});await page.evaluate(()=>{document.activeElement?.blur();document.querySelectorAll('.table-scroll').forEach(n=>{n.scrollLeft=0;});scrollTo(0,0);});await mkdir(resolve(folder,'full-page'),{recursive:true});await page.screenshot({path:resolve(folder,'full-page',`workspace-alert-history-${width}.png`),fullPage:true});await panel(page).screenshot({path:resolve(folder,`workspace-alert-history-${width}.png`),style:'.sidebar, .fleet-strip, .skip-link { visibility: hidden !important; }'});
});
