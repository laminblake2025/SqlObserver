import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {resolve} from 'node:path';
import {volumeObservedUtc} from '../tools/sql-volume-fixture.mjs';
import {installSqlVolumePlanningFixture,sqlVolumePlanningJson,sqlVolumePlanningHeld} from '../component-tests/sqlVolumePlanningFixture.mjs';

const origin='http://127.0.0.1:4324';
const first='11111111-1111-4111-8111-000000000001',second='11111111-1111-4111-8111-000000000002';
const from='2026-09-26T08:00:00.123456Z',to='2026-09-26T12:00:00.654321Z';
const href=(page,target=first,rewind=false)=>'/#/'+page+'?'+new URLSearchParams({target,range:rewind?'custom':'1h',...(rewind?{from,to}:{})});
const scope=page=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
async function fixture(page, mode = 'current') {
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'sqlVolumeFixture', value: '1', url: origin },
    { name: 'sqlVolumeMode', value: mode, url: origin }, { name: 'resourceInspectionFixture', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
}
const volume=page=>page.getByRole('region',{name:'SQL volume capacity planning',exact:true}),selected=page=>volume(page).getByRole('region',{name:'Selected volume planning receipt',exact:true});
const action=(page,name)=>volume(page).getByRole('button',{name,exact:true});
async function planningFixture(page,settings={}){await fixture(page);await page.clock.setFixedTime(new Date('2026-10-04T12:00:00Z'));const session=await installSqlVolumePlanningFixture(page,{origin,allowOtherApi:true});Object.assign(session.backend.settings,settings);return session;}
async function capacities(page){const summary=selected(page).locator('summary').filter({hasText:/^Browse volume capacities/iu});if(!await summary.evaluate(node=>node.parentElement.open))await summary.press('Enter');return selected(page).getByRole('region',{name:'Received volume capacities from selected receipt',exact:true}).getByRole('table');}
function response(session,read){return read.selection?session.backend.snapshot(read.binding,read.selection,read.cursor,read.limit):session.backend.read(read.binding,read.cursor,read.limit);}
function traffic(session){expect(session.errors,'Finite registered local API routes only').toEqual([]);}
test.beforeEach(async({},testInfo)=>{await testInfo.attach('evidence-context',{body:JSON.stringify({source:'Synthetic native DTO fixture; no connected VM evidence',executedAtUtc:new Date().toISOString(),observedAtUtc:volumeObservedUtc},null,2),contentType:'application/json'});});

test('SQL-target planning preserves exact large bytes qualified estimates unknown and full states across captured pages',async({page})=>{
 const session=await planningFixture(page,{volumeCount:101});await page.goto(href('resources',first,true));let table=await capacities(page);await expect(table).toContainText('9223372036854775807');await expect(table).toContainText('9007199254740993');await expect(table).toContainText(/Estimated days until full:\s*3/iu);await expect(table).toContainText(/not depleting|Insufficient daily history/iu);await expect(volume(page)).not.toContainText('C:\\');
 await action(page,'Next volume capacities page').press('Enter');await expect(table.locator('tbody>tr')).toHaveCount(1);const cursor=session.reads.at(-1).cursor;expect(cursor).toBeTruthy();const count=session.reads.length;await action(page,'Previous volume capacities page').press('Enter');await expect(table.locator('tbody>tr')).toHaveCount(100);expect(session.reads).toHaveLength(count);expect(scope(page).get('range')).toBe('custom');
 session.backend.settings.forecastState='full';await action(page,'Refresh selected volume planning receipt').press('Enter');await expect(table).toContainText('Full at selected receipt');await expect(table).toContainText('0');
 session.backend.settings.forecastState='unstable_trend';await action(page,'Refresh selected volume planning receipt').press('Enter');await expect(table).toContainText('Unstable daily trend');await expect(table).not.toContainText('Full at selected receipt');
 session.backend.settings.unknownCapacity=true;await action(page,'Refresh selected volume planning receipt').press('Enter');await expect(table).toContainText(/unknown|unavailable/iu);await expect(table).not.toContainText(/Estimated days until full|Full at selected receipt/iu);traffic(session);
});

test('partial absent and Rewind observations preserve real retained-source boundaries without fabricated forecasts',async({page})=>{
 const session=await planningFixture(page,{partial:true});await page.goto(href('resources',first,true));await expect(selected(page)).toContainText(/collection loss|partial/iu);
 const summary=selected(page).getByText('Volume planning receipt provenance',{exact:true});await summary.press('Enter');const provenance=summary.locator('..');await expect(provenance).toContainText('Collection outcome: partial. Loss: source row limit; minimum lost: 1.');await expect(provenance).toContainText('3 stored / 3 expected source volumes.');
 let table=await capacities(page);await expect(table).toContainText('No qualified estimate');await expect(table).not.toContainText('Estimated days until full');const partialFolder=resolve('../artifacts/dashboard-volume-planning-2026-10-04/screenshots');await mkdir(partialFolder,{recursive:true});await page.screenshot({path:resolve(partialFolder,'planning-partial-selected-receipt-provenance.png'),fullPage:true});
 session.backend.settings.count=0;await page.reload();await expect(volume(page)).toContainText(/No completed volume|no_completed_runs/iu);await expect(selected(page).getByRole('table')).toHaveCount(0);
 session.backend.settings.count=3;session.backend.settings.partial=false;await page.goto(href('resources',first,true));await page.reload();table=await capacities(page);await expect(table).toContainText(/Estimated days until full:\s*3/iu);expect(session.reads.some(read=>read.selection)).toBe(true);expect(scope(page).get('from')).toBe(from);expect(scope(page).get('to')).toBe(to);await expect(selected(page)).toContainText(/SQL observation time|Observed/iu);traffic(session);
});

test('malformed foreign revision and incompatible planning facts fail visibly without crashing Resources',async({page})=>{
 const session=await planningFixture(page);for(const mode of ['malformed','foreign','revision','invalid-capacity','invalid-trend','oversized']){
  session.handler=async(route,read)=>{if(!read.selection)return false;const body=response(session,read);if(mode==='malformed')delete body.lookback;if(mode==='foreign')body.targetId=second;if(mode==='revision')body.receipt.sampleTargetRevision=2;if(mode==='invalid-capacity')body.items[0].availableBytes='9223372036854775808';if(mode==='invalid-trend')body.items.find(row=>row.forecast.state==='estimated').forecast.daysUntilFull=-1;if(mode==='oversized')body.unapprovedRawPayload='X'.repeat(600000);await sqlVolumePlanningJson(route,body);return true;};await page.goto(href('resources',first,true));await page.reload();await expect(action(page,'Retry selected volume planning receipt read')).toBeVisible();await expect(selected(page).getByRole('table')).toHaveCount(0);await expect(page.getByRole('heading',{name:'Resources for SYNTHETIC SQL 01',exact:true})).toBeVisible();
 }
 session.handler=undefined;await action(page,'Retry selected volume planning receipt read').press('Enter');await expect(await capacities(page)).toContainText(/Estimated days until full:\s*3/iu);traffic(session);
});

test('superseded or mismatched planning continuation retains verified prior facts and requires explicit source recovery',async({page})=>{
 const session=await planningFixture(page,{volumeCount:101});await page.goto(href('resources',first,true));const table=await capacities(page),original=session.reads.find(read=>read.selection).selection;
 session.backend.settings.expired=true;await action(page,'Next volume capacities page').press('Enter');await expect(action(page,'Retry selected volume planning receipt read')).toBeVisible();await expect(table.locator('tbody>tr')).toHaveCount(100);await expect(table).toContainText('9223372036854775807');expect(session.reads.at(-1).selection).toEqual(original);
 session.backend.settings.expired=false;await action(page,'Refresh selected volume planning receipt').press('Enter');await expect(action(page,'Retry selected volume planning receipt read')).toHaveCount(0);
 session.handler=async(route,read)=>{if(!read.cursor||!read.selection)return false;const body=response(session,read);body.runId='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';await sqlVolumePlanningJson(route,body);return true;};await action(page,'Next volume capacities page').press('Enter');await expect(action(page,'Retry selected volume planning receipt read')).toBeVisible();await expect(table.locator('tbody>tr')).toHaveCount(100);expect(session.reads.at(-1).selection).toEqual(original);traffic(session);
});

test('delayed old-server planning facts cannot republish and failed current-source refresh preserves only current receipt',async({page})=>{
 const session=await planningFixture(page),held=sqlVolumePlanningHeld();session.handler=(route,read)=>read.selection&&read.binding.targetId===first?held.handle(route,response(session,read)):false;await page.goto(href('resources',first,true));const request=await held.started;session.handler=undefined;await page.goto(href('resources',second,true));await expect(selected(page)).toContainText('9007199254740993');const current=session.reads.filter(read=>read.selection&&read.binding.targetId===second).at(-1);await expect(selected(page)).toContainText(current.selection.runId);await expect.poll(()=>request.failure()?.errorText??'').toContain('ERR_ABORTED');await held.finish();await expect(selected(page)).not.toContainText(session.reads.find(read=>read.selection&&read.binding.targetId===first).selection.runId);
 session.handler=async(route,read)=>read.selection?(await sqlVolumePlanningJson(route,{detail:'SECRET'},503),true):false;await action(page,'Refresh selected volume planning receipt').press('Enter');await expect(action(page,'Retry selected volume planning receipt read')).toBeVisible();await expect(selected(page)).toContainText(current.selection.runId);await expect(selected(page)).toContainText('9007199254740993');await expect(volume(page)).not.toContainText('SECRET');session.handler=undefined;await action(page,'Retry selected volume planning receipt read').press('Enter');await expect(action(page,'Retry selected volume planning receipt read')).toHaveCount(0);traffic(session);
});

async function queryFixture(page) {
  await fixture(page); await page.context().addCookies([{ name: 'queryRowTrendFixture', value: '1', url: origin }]);
  await page.route('**/query-performance/top?*', async route => {
    const response = await route.fetch(), body = await response.json();
    body.items.push({ ...body.items[0], source: 'plan_cache', sourceState: 'disabled', semantics: 'plan_cache_cumulative',
      planFingerprint: null, queryFingerprint: 'd'.repeat(64), collectionRunId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', observationKey: 'f'.repeat(32) });
    await route.fulfill({ response, json: body });
  });
}
const sourceControl = page => page.getByRole('combobox', { name: 'Source', exact: true });
const ranking = page => page.getByRole('region', { name: 'Ranked query observations', exact: true });
const rankingRows = page => ranking(page).locator('tbody tr');

test('query source filter restores native rankings through reload back forward and independent query selection', async ({ page }) => {
  await queryFixture(page); await page.goto(href('queries', first, true) + '&querySource=plan_cache');
  await expect(sourceControl(page)).toHaveValue('plan_cache'); await expect(rankingRows(page)).toHaveCount(1);
  await expect(ranking(page)).toContainText('dddddddddddd');
  await sourceControl(page).selectOption('query_store');
  await expect.poll(() => scope(page).get('querySource')).toBe('query_store');
  await expect(rankingRows(page)).toHaveCount(3);
  await page.reload(); await expect(sourceControl(page)).toHaveValue('query_store'); await expect(rankingRows(page)).toHaveCount(3);
  await page.goBack(); await expect(sourceControl(page)).toHaveValue('plan_cache'); await expect(rankingRows(page)).toHaveCount(1);
  await page.goForward(); await expect(sourceControl(page)).toHaveValue('query_store');
  await ranking(page).getByRole('button', { name: /000000001000/ }).click();
  await expect.poll(() => scope(page).get('source')).toBe('query_store');
  expect(scope(page).get('querySource')).toBe('query_store');
  expect(scope(page).get('from')).toBe(from); expect(scope(page).get('to')).toBe(to);
});

test('invalid or duplicate query source URL values show no fallback ranking and valid controls repair the selection', async ({ page }) => {
  await queryFixture(page);
  for (const invalid of ['unknown', 'query_store&querySource=plan_cache']) {
    await page.goto(href('queries', first, true) + '&querySource=' + invalid);
    await expect(sourceControl(page)).toHaveValue('invalid');
    await expect(page.getByRole('alert').filter({ hasText: 'The shared URL contains an invalid or duplicate query source filter.' })).toBeVisible();
    await expect(sourceControl(page).getByRole('option', { name: 'Invalid source filter', exact: true })).toHaveCount(1);
    await expect(rankingRows(page)).toHaveCount(0);
    await sourceControl(page).selectOption('mixed');
    await expect(rankingRows(page)).toHaveCount(4);
    await expect.poll(() => scope(page).getAll('querySource').length).toBeLessThanOrEqual(1);
  }
});

test('query source filter survives metric view and database controls while delayed history cannot overwrite it', async ({ page }) => {
  await queryFixture(page); await page.goto(href('queries', first, true) + '&querySource=query_store');
  await expect(rankingRows(page)).toHaveCount(3);
  await page.getByRole('combobox', { name: 'Rank by', exact: true }).selectOption('duration');
  await expect.poll(() => scope(page).get('queryMetric')).toBe('duration'); expect(scope(page).get('querySource')).toBe('query_store');
  await page.getByRole('combobox', { name: 'Database', exact: true }).selectOption('5');
  await expect(rankingRows(page)).toHaveCount(2); expect(scope(page).get('querySource')).toBe('query_store');
  await page.getByRole('tab', { name: 'Cache interval deltas', exact: true }).click();
  await expect.poll(() => scope(page).get('queryView')).toBe('cache'); expect(scope(page).get('querySource')).toBe('query_store');
  await page.getByRole('tab', { name: 'Top queries', exact: true }).click(); await expect(sourceControl(page)).toHaveValue('query_store');
  let release, started = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/query-performance/databases/5/history/*', async route => { started = true; await gate; await route.continue().catch(() => {}); returned = true; });
  try {
    await ranking(page).getByRole('button', { name: /000000001000/ }).click(); await expect.poll(() => started).toBe(true);
    await sourceControl(page).selectOption('plan_cache'); await expect(rankingRows(page)).toHaveCount(1);
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  const history = page.getByText('Bounded history observations', { exact: true });
  await expect(history).toBeVisible(); await history.click();
  await expect(history.locator('xpath=parent::details')).toContainText('CPU 120');
  await expect(sourceControl(page)).toHaveValue('plan_cache'); expect(scope(page).get('querySource')).toBe('plan_cache');
});

test('protected query text is bound to the selected observation and rejects a late result after query and window changes', async ({ page }) => {
  await queryFixture(page);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '1', url: origin }]);
  const queryA = '000000001000' + '0'.repeat(52), queryB = '000000001002' + '0'.repeat(52);
  const textA = 'SELECT 101 AS synthetic_query_a_only;', textB = 'SELECT 202 AS synthetic_query_b_only;';
  const reads = [];
  let release, delayed = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/query-performance/databases/5/history/*/runs/*/text', async route => {
    const match = /\/history\/([0-9a-f]{64})\/runs\/([^/]+)\/text$/.exec(new URL(route.request().url()).pathname);
    reads.push(match[1]);
    if (match[1] === queryB) { delayed = true; await gate; }
    await route.fulfill({ status: 200, contentType: 'application/json', json: {
      targetId: first, databaseId: 5, queryFingerprint: match[1], collectionRunId: match[2],
      status: 'available', text: match[1] === queryA ? textA : textB,
    } }).catch(() => {});
    if (match[1] === queryB) returned = true;
  });
  await page.goto(href('queries', first, true) + '&querySource=query_store');
  const selected = page.getByRole('heading', { name: 'Selected query', exact: true }).locator('xpath=parent::section');
  const captured = page.locator('pre[aria-label="Captured Query Store text"]');
  const openText = page.getByRole('button', { name: 'Open query text for selected observation', exact: true });
  await ranking(page).getByRole('button', { name: /000000001000/ }).click();
  await expect(selected).toContainText(queryA); await expect(openText).toBeVisible();
  expect(reads).toEqual([]);
  await openText.click(); await expect(captured).toHaveText(textA);
  await sourceControl(page).selectOption('plan_cache');
  await expect(selected).toContainText('The inspected observation is hidden by the ranking source filter; its history remains selected.');
  await expect(captured).toHaveText(textA); expect(reads).toEqual([queryA]);
  expect(scope(page).get('queryFingerprint')).toBe(queryA); expect(scope(page).get('source')).toBe('query_store');
  await sourceControl(page).selectOption('query_store');
  const rowB = ranking(page).getByRole('button', { name: /000000001002/ });
  await rowB.click(); await expect(rowB).toHaveAttribute('aria-pressed', 'true');
  await expect(selected).toContainText(queryB); await expect(captured).toHaveCount(0);
  expect(reads).toEqual([queryA]);
  try {
    await openText.click(); await expect.poll(() => delayed).toBe(true);
    const next = scope(page);
    next.set('queryFingerprint', queryA); next.set('planFingerprint', '000000002000' + '0'.repeat(52));
    next.set('from', '2026-09-26T09:00:00.123456Z');
    await page.evaluate(hash => { location.hash = hash; }, '#/queries?' + next);
    await expect.poll(() => scope(page).get('from')).toBe('2026-09-26T09:00:00.123456Z');
    await expect(selected).toContainText(queryA); await expect(captured).toHaveCount(0);
    await expect(selected).not.toContainText('Opening query text');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(captured).toHaveCount(0); await expect(page.getByText(textB, { exact: true })).toHaveCount(0);
  await expect(sourceControl(page)).toHaveValue('query_store');
  expect(scope(page).get('source')).toBe('query_store'); expect(scope(page).get('querySource')).toBe('query_store');
  expect(reads).toEqual([queryA, queryB]);
});

for(const width of [320,390,768,1440])test(`populated retained SQL volume planning remains accessible at ${width}px`,async({page},testInfo)=>{
 await page.setViewportSize({width,height:1000});const session=await planningFixture(page);await page.goto(href('resources',first,true));const table=await capacities(page);await expect(table).toContainText(/Estimated days until full:\s*3/iu);const scroll=table.locator('..');await scroll.press('ArrowRight');await expect(scroll).toBeFocused();expect(await scroll.evaluate(node=>parseFloat(getComputedStyle(node).outlineWidth))).toBeGreaterThanOrEqual(2);
 if(width<960){expect(await scroll.evaluate(node=>node.scrollWidth>node.clientWidth)).toBe(true);await expect.poll(()=>scroll.evaluate(node=>node.scrollLeft)).toBeGreaterThan(0);}
 const box=await volume(page).boundingBox();expect(box.x).toBeGreaterThanOrEqual(-1);expect(box.x+box.width).toBeLessThanOrEqual(width+1);const bounds=await scroll.boundingBox();expect(bounds.x).toBeGreaterThanOrEqual(box.x);expect(bounds.x+bounds.width).toBeLessThanOrEqual(box.x+box.width+1);expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const results=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','best-practice']).analyze();await testInfo.attach('planning-evidence-axe',{body:JSON.stringify(results.violations,null,2),contentType:'application/json'});expect(results.violations).toEqual([]);traffic(session);
 const folder=resolve('../artifacts/dashboard-volume-planning-2026-10-04/screenshots');await mkdir(folder,{recursive:true});await page.screenshot({path:resolve(folder,`planning-evidence-contracts-${width}-focus.png`),fullPage:true});
});
