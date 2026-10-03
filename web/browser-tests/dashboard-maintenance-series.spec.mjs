import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { createHash } from 'node:crypto';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000001';
const now = '2026-10-03T15:00:00.123456Z';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const prefix = `/api/v1/observation-targets/${target}/alerts/maintenance`;
const region = page => page.getByRole('region', { name: 'Recurring maintenance', exact: true });
const preview = page => page.getByRole('region', { name: 'Server maintenance preview', exact: true });
const occurrences = page => page.getByRole('region', { name: 'Series occurrences', exact: true });
const button = (page, name) => region(page).getByRole('button', { name, exact: true });
const digest = d => createHash('sha256').update([d.frequency,d.interval,d.startDate,d.endDate,d.localStartTime,d.durationMinutes,d.timeZoneId,d.weekdays.join(','),d.dayOfMonth??'<null>',d.gapPolicy,d.foldPolicy,d.reason].join('|')).digest('hex');
const windowId = (id, date) => {
  const hex = createHash('sha256').update(id+'|'+date).digest('hex').slice(0,32);
  return [hex.slice(0,8),hex.slice(8,12),hex.slice(12,16),hex.slice(16,20),hex.slice(20)].join('-');
};

// Deliberately bounded native-shaped synthetic dates. Real timezone and native
// expansion/canonical validation are qualified separately by the owning backend.
function expand(id, d) {
  const rows = [], skippedDates = [];
  for (let date = new Date(d.startDate+'T00:00:00Z'); date.toISOString().slice(0,10) <= d.endDate; date.setUTCDate(date.getUTCDate()+1)) {
    const day = date.toISOString().slice(0,10);
    if (d.frequency === 'monthly' && date.getUTCDate() !== d.dayOfMonth) continue;
    if (d.frequency === 'weekly' && !d.weekdays.includes(date.getUTCDay())) continue;
    let utcOffsetMinutes = 0, ambiguous = false;
    if (d.timeZoneId === 'America/New_York') {
      ambiguous = day === '2026-11-01' && d.localStartTime >= '01:00' && d.localStartTime < '02:00';
      utcOffsetMinutes = day < '2026-11-01' || day === '2026-11-01' && d.localStartTime < '02:00' && (!ambiguous || d.foldPolicy === 'earlier') ? -240 : -300;
    }
    const start = new Date(Date.parse(day+'T'+d.localStartTime+':00Z')-utcOffsetMinutes*60000);
    rows.push({ windowId:windowId(id,day),localDate:day,startsAtUtc:start.toISOString(),endsAtUtc:new Date(start.getTime()+d.durationMinutes*60000).toISOString(),utcOffsetMinutes,ambiguous });
  }
  if (d.frequency === 'monthly' && d.dayOfMonth === 31 && d.startDate <= '2027-02-01' && d.endDate >= '2027-02-28')
    skippedDates.push({localDate:'2027-02-31',reason:'missing_month_day'});
  return { rows, skippedDates };
}
async function json(route, body, status=200) { await route.fulfill({status,contentType:'application/json',headers:{'cache-control':'no-store'},body:JSON.stringify(body)}); }
async function fixture(page, { viewer=false }={}) {
  const session = { series:new Map(), rows:new Map(), operations:new Map(), posts:[], oneOffPosts:[], gets:[], previews:[], errors:[], failReads:0, uncertain:false };
  page.on('pageerror', e=>session.errors.push(e.message));
  await page.context().addCookies([
    {name:'dashboardRefactor',value:'1',url:origin}, {name:'maintenanceCalendarFixture',value:'1',url:origin},
    {name:'maintenanceCalendarRole',value:viewer?'viewer':'administrator',url:origin}, {name:'chartReferenceRole',value:'viewer',url:origin},
  ]);
  await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
  await page.route('**/api/v1/**', async route=> {
    const req = route.request(), url = new URL(req.url());
    if (url.pathname === prefix && req.method()==='GET') {
      const items=[...session.rows.values()].flat().map(({windowId,startsAtUtc,endsAtUtc,reason,revision,cancelledAtUtc})=>({windowId,startsAtUtc,endsAtUtc,reason,revision,cancelledAtUtc}))
        .sort((a,b)=>b.startsAtUtc.localeCompare(a.startsAtUtc)||b.windowId.localeCompare(a.windowId));
      return json(route,{targetId:target,listingStartedUtc:now,items,hasMore:false,nextCursor:null});
    }
    const oneOff=/^\/([^/]+)(\/cancel)?$/.exec(url.pathname.slice(prefix.length));
    if (url.pathname.startsWith(prefix+'/') && oneOff && oneOff[1]!=='series' && req.method()!=='GET') {
      const body=req.postDataJSON();session.oneOffPosts.push({path:url.pathname,method:req.method(),body});
      const row=[...session.rows.values()].flat().find(item=>item.windowId===oneOff[1]);
      if (!row||row.cancelledAtUtc||row.revision!==body.expectedRevision) return json(route,{},409);
      if (oneOff[2]) row.cancelledAtUtc=now;
      else Object.assign(row,{startsAtUtc:body.startsAtUtc,endsAtUtc:body.endsAtUtc,reason:body.reason});
      row.exception=true;row.revision++;
      return json(route,{auditId:'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',recordedAtUtc:now});
    }
    if (!url.pathname.startsWith(prefix+'/series')) return route.continue();
    const suffix = url.pathname.slice((prefix+'/series').length);
    if (viewer) return json(route,{},403);
    if (req.method()==='GET') {
      session.gets.push(url.pathname+url.search);
      if (suffix==='') return json(route,{targetId:target,serverNowUtc:now,items:[...session.series.values()],nextBeforeSeriesId:null});
      const match = /^\/([^/]+)(\/occurrences)?$/.exec(suffix), status = match && session.series.get(match[1]);
      if (!status) return json(route,{},404);
      if (match[2]) {
        const all = [...session.rows.get(match[1])].sort((a,b)=>b.localDate.localeCompare(a.localDate));
        const before = url.searchParams.get('beforeWindowId'), start = before ? all.findIndex(r=>r.windowId===before)+1 : 0;
        const items = all.slice(start,start+50);
        return json(route,{targetId:target,seriesId:match[1],serverNowUtc:now,items,nextBeforeWindowId:start+items.length<all.length?items.at(-1).windowId:null});
      }
      if (session.failReads>0) { session.failReads--; return json(route,{detail:'PRIVATE series provider failure'},503); }
      return json(route,status);
    }
    const body = req.postDataJSON();
    if (suffix==='/preview') {
      session.previews.push(body);
      const expansion = expand(body.seriesId,body.definition);
      return json(route,{targetId:target,seriesId:body.seriesId,serverNowUtc:now,definitionDigest:digest(body.definition),expansionVersion:'civil-v1',
        occurrences:expansion.rows.filter(row=>!body.effectiveFromDate||row.localDate>=body.effectiveFromDate),skippedDates:expansion.skippedDates});
    }
    session.posts.push({path:url.pathname,method:req.method(),body,raw:req.postData()});
    const replay = session.operations.get(body.operationId);
    if (replay) return json(route,replay.raw===req.postData()&&replay.path===url.pathname?replay.receipt:{},replay.raw===req.postData()&&replay.path===url.pathname?200:409);
    const id = suffix==='' ? body.seriesId : suffix.split('/')[1], current = session.series.get(id);
    if (suffix!=='' && (!current||body.expectedRevision!==current.revision)) return json(route,{},409);
    let created=0,updated=0,cancelled=0;
    if (suffix==='' || req.method()==='PUT') {
      const definition = body.definition, expanded = expand(id,definition).rows;
      const next = current ? structuredClone(session.rows.get(id)) : [];
      for (const item of expanded) {
        if (body.effectiveFromDate && item.localDate<body.effectiveFromDate || current?.cancelledFromDate&&item.localDate>=current.cancelledFromDate) continue;
        const old = next.find(row=>row.localDate===item.localDate);
        if (old?.cancelledAtUtc || old?.exception) continue;
        const value = {...item,reason:definition.reason,revision:(old?.revision??0)+1,cancelledAtUtc:null,seriesRevision:(current?.revision??0)+1,exception:false,expansionVersion:'civil-v1'};
        if (old) { Object.assign(old,value); updated++; } else { next.push(value); created++; }
      }
      session.rows.set(id,next);
      session.series.set(id,{targetId:target,seriesId:id,revision:(current?.revision??0)+1,definition,cancelledFromDate:current?.cancelledFromDate??null,
        createdAtUtc:current?.createdAtUtc??now,updatedAtUtc:now,expansionVersion:'civil-v1',serverNowUtc:now,occurrenceCount:next.length});
    } else {
      const cutoff = current.cancelledFromDate && current.cancelledFromDate<body.effectiveFromDate ? current.cancelledFromDate : body.effectiveFromDate;
      for (const row of session.rows.get(id)) if (row.localDate>=cutoff&&!row.cancelledAtUtc&&!row.exception) { row.cancelledAtUtc=now; row.revision++; cancelled++; }
      session.series.set(id,{...current,revision:current.revision+1,cancelledFromDate:cutoff,updatedAtUtc:now});
    }
    const receipt = {operationId:body.operationId,auditId:'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',recordedAtUtc:now,seriesId:id,revision:session.series.get(id).revision,
      appliedAtUtc:now,createdOccurrences:created,updatedOccurrences:updated,cancelledOccurrences:cancelled,preservedExceptions:session.rows.get(id).filter(row=>row.exception).length};
    session.operations.set(body.operationId,{path:url.pathname,raw:req.postData(),receipt});
    return json(route,session.uncertain&&session.posts.length===1?{}:receipt,session.uncertain&&session.posts.length===1?503:200);
  });
  await page.goto('/#/maintenance?'+new URLSearchParams({target,range:'custom',from,to,compare:'1'}));
  return session;
}
async function draft(page, change={}) {
  const r=region(page);
  await r.getByRole('combobox',{name:'Recurrence frequency',exact:true}).selectOption(change.frequency??'daily');
  await r.getByLabel('Recurrence interval',{exact:true}).fill('1');
  await r.getByLabel('Series start date',{exact:true}).fill(change.startDate??'2026-11-01');
  await r.getByLabel('Series end date',{exact:true}).fill(change.endDate??'2026-11-02');
  await r.getByLabel('Local start time',{exact:true}).fill('01:30');
  await r.getByLabel('Elapsed duration (minutes)',{exact:true}).fill('60');
  await r.getByLabel('IANA time zone',{exact:true}).fill(change.timeZoneId??'America/New_York');
  await r.getByRole('combobox',{name:'Repeated local time',exact:true}).selectOption('earlier');
  if (change.frequency==='monthly') await r.getByLabel('Day of month',{exact:true}).fill('31');
  await r.getByLabel('Series reason',{exact:true}).fill('SYNTHETIC recurring DBA maintenance');
}
async function create(page, session) {
  await draft(page); await button(page,'Preview maintenance series').click(); await expect(preview(page)).toBeVisible();
  expect(session.previews.at(-1).definition).toMatchObject({frequency:'daily',timeZoneId:'America/New_York',foldPolicy:'earlier',gapPolicy:'skip'});
  await button(page,'Create maintenance series').click();
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('revision 1');
  const id=session.posts.at(-1).body.seriesId;
  await expect.poll(()=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('seriesId')).toBe(id);
  return id;
}
for (const width of [320,390,768,1440]) test(`Built recurring maintenance preview/create/future-edit/cancel remains accessible at ${width}`,async({page},info)=> {
  await page.setViewportSize({width,height:1100}); const session=await fixture(page); const id=await create(page,session);
  expect(session.rows.get(id).map(row=>row.startsAtUtc)).toEqual(['2026-11-01T05:30:00.000Z','2026-11-02T06:30:00.000Z']);
  expect(session.rows.get(id).map(row=>row.ambiguous)).toEqual([true,false]);
  await button(page,'Edit future occurrences').click(); await region(page).getByLabel('Effective from date',{exact:true}).fill('2026-11-01');
  await region(page).getByLabel('Local start time',{exact:true}).fill('02:30');
  await button(page,'Preview maintenance series').click(); await expect(preview(page)).toBeVisible();
  await button(page,'Save future changes').click(); await expect.poll(()=>session.posts.length).toBe(2);
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('revision 2');
  await region(page).getByLabel('Cancellation effective from date',{exact:true}).fill('2026-11-02');
  await region(page).getByLabel('Series cancellation reason',{exact:true}).fill('SYNTHETIC later maintenance cancelled');
  await button(page,'Cancel future occurrences').click();
  // The cancellation action is a separate explicit form confirmation.
  await region(page).getByRole('button',{name:'Confirm future cancellation',exact:true}).click();
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('revision 3');
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('Cancelled from 2026-11-02');
  await button(page,'Load series occurrences').click(); await expect(occurrences(page)).toContainText('2026-11-02');
  expect(session.posts).toHaveLength(3); expect(session.operations.size).toBe(3);
  expect(session.posts[1].body).toMatchObject({expectedRevision:1,effectiveFromDate:'2026-11-01',definition:{localStartTime:'02:30'}});
  expect(session.posts[2].body).toMatchObject({expectedRevision:2,effectiveFromDate:'2026-11-02',reason:'SYNTHETIC later maintenance cancelled'});
  const p=new URLSearchParams(new URL(page.url()).hash.split('?')[1]); expect(p.get('from')).toBe(from); expect(p.get('to')).toBe(to);
  await button(page,'Refresh series status').focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
  await expect(button(page,'Refresh series status')).toBeFocused();
  const focus=await button(page,'Refresh series status').evaluate(el=>({style:getComputedStyle(el).outlineStyle,width:parseFloat(getComputedStyle(el).outlineWidth)}));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
  const violations=(await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa','best-practice']).analyze()).violations;
  await info.attach('series-axe',{body:JSON.stringify(violations,null,2),contentType:'application/json'}); expect(violations).toEqual([]);
  const folder=fileURLToPath(new URL('../../artifacts/dashboard-maintenance-series/screenshots/',import.meta.url)); await mkdir(folder,{recursive:true});
  await page.evaluate(()=>scrollTo({top:0,left:0,behavior:'instant'}));
  await page.screenshot({path:folder+`workspace-series-${width}.png`,fullPage:true}); expect(session.errors).toEqual([]);
});
test('Built recurring series confirmed write retries only failed current read and restores URL on reload',async({page})=> {
  const session=await fixture(page); await draft(page); await button(page,'Preview maintenance series').click(); await expect(preview(page)).toBeVisible();
  session.failReads=1; await button(page,'Create maintenance series').click(); await expect(button(page,'Retry series status')).toBeVisible();
  expect(session.posts).toHaveLength(1); await button(page,'Retry series status').click(); await expect(button(page,'Edit future occurrences')).toBeVisible();
  expect(session.posts).toHaveLength(1); const id=session.posts[0].body.seriesId; await page.reload();
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('SYNTHETIC recurring DBA maintenance');
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('revision 1');
  await expect(button(page,'Select series '+id)).toHaveAttribute('aria-pressed','true');
  expect(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('seriesId')).toBe(id);
  expect(session.errors).toEqual([]);
});
test('Built recurring series uncertain create replays the complete original command once',async({page})=> {
  const session=await fixture(page); session.uncertain=true; await draft(page);
  await button(page,'Preview maintenance series').click(); await expect(preview(page)).toBeVisible(); await button(page,'Create maintenance series').click();
  await expect(button(page,'Retry series operation')).toBeVisible(); await button(page,'Retry series operation').click();
  await expect(button(page,'Edit future occurrences')).toBeVisible(); expect(session.posts).toHaveLength(2);
  expect(session.posts[1]).toEqual(session.posts[0]); expect(session.operations.size).toBe(1); expect(session.series.size).toBe(1); expect(session.errors).toEqual([]);
});
test('Built monthly preview discloses skipped dates and Viewer cannot invoke recurring administration',async({page})=> {
  const session=await fixture(page); await draft(page,{frequency:'monthly',startDate:'2027-01-01',endDate:'2027-03-31',timeZoneId:'UTC'});
  await button(page,'Preview maintenance series').click(); await expect(preview(page)).toContainText('2027-02-31');
  expect(session.previews.at(-1).definition.dayOfMonth).toBe(31); expect(session.posts).toEqual([]);
  await page.context().addCookies([{name:'maintenanceCalendarRole',value:'viewer',url:origin}]); await page.reload();
  await expect(button(page,'Create maintenance series')).toHaveCount(0); await expect(button(page,'Preview maintenance series')).toHaveCount(0); expect(session.posts).toEqual([]); expect(session.errors).toEqual([]);
});
test('Built series occurrence opens exact legacy editor and preserves an individual exception through a future change',async({page})=> {
  const session=await fixture(page),id=await create(page,session);
  await button(page,'Load series occurrences').click(); const first=session.rows.get(id)[0];
  await occurrences(page).getByRole('button',{name:'Select occurrence '+first.windowId,exact:true}).click();
  const scope=new URLSearchParams(new URL(page.url()).hash.split('?')[1]); expect(scope.get('seriesId')).toBe(id); expect(scope.get('windowId')).toBe(first.windowId);
  await page.getByRole('button',{name:'Edit window',exact:true}).click(); const editor=page.getByRole('form',{name:'Edit maintenance window',exact:true});
  await editor.getByRole('textbox',{name:'Edit reason',exact:true}).fill('SYNTHETIC individual maintenance exception');
  await editor.getByRole('button',{name:'Save window changes',exact:true}).click(); await expect(editor).toHaveCount(0);
  expect(session.oneOffPosts).toHaveLength(1); expect(session.oneOffPosts[0].body).toMatchObject({windowId:first.windowId,expectedRevision:1,startsAtUtc:first.startsAtUtc,endsAtUtc:first.endsAtUtc,reason:'SYNTHETIC individual maintenance exception'});
  await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('revision 1');
  await button(page,'Edit future occurrences').click(); await region(page).getByLabel('Effective from date',{exact:true}).fill('2026-11-01');
  await region(page).getByLabel('Local start time',{exact:true}).fill('02:30'); await button(page,'Preview maintenance series').click(); await expect(preview(page)).toBeVisible();
  await button(page,'Save future changes').click(); await expect(region(page).getByRole('region',{name:'Current maintenance series',exact:true})).toContainText('revision 2');
  expect(session.rows.get(id)[0]).toMatchObject({windowId:first.windowId,reason:'SYNTHETIC individual maintenance exception',exception:true,startsAtUtc:'2026-11-01T05:30:00.000Z'});
  await button(page,'Load series occurrences').click(); await expect(occurrences(page)).toContainText('SYNTHETIC individual maintenance exception'); expect(session.errors).toEqual([]);
});
