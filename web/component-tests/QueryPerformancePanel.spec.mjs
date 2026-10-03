import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const queryA = 'a'.repeat(64), queryB = 'b'.repeat(64), queryC = 'c'.repeat(64), planA = 'd'.repeat(64);
const run = '99999999-9999-4999-8999-000000000001', cursor = 'cXVlcnk6bmF0aXZlOnAy';
const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
const secret = '<img src="https://unexpected.invalid/steal" onerror="window.queryInjected=true"> SYNTHETIC protected SQL';
const cacheIncarnation = '9'.repeat(64), cacheEntry = 'f'.repeat(64), cacheCursor = 'Y2FjaGU.cDI';
const cacheRun = index => '88888888-8888-4888-8888-' + (101-index).toString(16).padStart(12,'0');
const cacheSelection = { queryView: 'cache', cacheDatabaseId: '5', cacheIncarnation, cacheQuery: queryA, cachePlan: planA };
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: /^Queries for / });
const next = page => panel(page).getByRole('button', { name: 'Next ranking page', exact: true });
const ranking = page => panel(page).getByRole('region', { name: 'Ranked query observations', exact: true });
const history = page => panel(page).getByText('Bounded history observations', { exact: true });
const hashParams = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const href = (options = {}) => '#/queries?' + new URLSearchParams({ target: alpha, range: 'custom', from, to, databaseId: '5',
  queryFingerprint: queryA, source: 'query_store', ...options });
async function json(route, value, status = 200) { await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) }); }
function observation(query, window, metric = 'cpu', value = 1001) {
  return { databaseId: 5, queryFingerprint: query, planFingerprint: planA, source: 'query_store', sourceState: 'read_write',
    semantics: 'query_store_interval', metric, value, intervalStartUtc: window.fromUtc, intervalEndUtc: window.toUtc,
    coverage: 'complete', fresh: true, truncated: false, contentAvailable: false, collectionRunId: run, observationKey: query.slice(0, 32) };
}
function nativeTop(item, context) {
  const leading = observation(item.cursor ? queryC : context.firstQuery, item.window, item.metric, item.cursor ? 501 : context.firstValue);
  const remaining = item.cursor || context.small ? [] : Array.from({ length: 199 }, (_, index) => ({
    ...observation('e'.repeat(52) + (index + 1).toString(16).padStart(12, '0'), item.window, item.metric, context.firstValue - index - 1),
    planFingerprint: null, source: 'plan_cache', sourceState: 'disabled', semantics: 'plan_cache_cumulative',
    observationKey: (index + 1).toString(16).padStart(32, '0'),
  }));
  return { targetId: item.id, metric: item.metric, snapshotUtc: item.window.toUtc, ...item.window,
    nextCursor: item.cursor || context.small ? null : cursor, items: [leading, ...remaining] };
}
function nativeValue(item, context) {
  const common = { targetId: item.id, ...item.window };
  if(item.kind==='cache_top')return {...common,metric:item.metric,semantics:'plan_cache_delta',coverage:'no_comparable_rows',sourceFrames:101,incompleteFrames:0,comparableRows:0,unavailableRows:101,boundaryRows:0,hasMore:false,items:[]};
  if(item.kind==='cache_history')return {...common,databaseId:5,databaseIncarnation:cacheIncarnation,queryFingerprint:queryA,planFingerprint:planA,snapshotUtc:item.window.toUtc,revision:1,totalRows:101,nextCursor:item.cursor?null:cacheCursor,semantics:'plan_cache_delta',coverage:'observed_entries_only',
    items:Array.from({length:item.cursor?1:100},(_,offset)=>{const index=item.cursor?100:offset;return{collectionRunId:cacheRun(index),cacheEntryFingerprint:index===0?cacheEntry:'e'.repeat(52)+index.toString(16).padStart(12,'0'),sourceEpoch:'8'.repeat(64),observedAtUtc:new Date(Date.parse('2026-10-02T10:50:00Z')-index*1000).toISOString(),committedAtUtc:new Date(Date.parse('2026-10-02T10:50:00Z')-index*1000+1).toISOString(),captureComplete:true,transition:'baseline_unavailable',intervalFromUtc:null,intervalToUtc:null,cpuMilliseconds:null,durationMilliseconds:null,executions:null,logicalReads:null,writes:null,rows:null};})};
  if(item.kind==='cache_content')return{targetId:item.id,databaseId:5,databaseIncarnation:cacheIncarnation,queryFingerprint:queryA,planFingerprint:planA,collectionRunId:cacheRun(0),cacheEntryFingerprint:cacheEntry,payloadKind:item.payloadKind,status:'available',reason:'available',observedAtUtc:'2026-10-02T10:50:00.000Z',text:item.payloadKind==='text'?secret:null,xml:item.payloadKind==='plan'?'<ShowPlanXML> SYNTHETIC protected cache plan </ShowPlanXML>':null};
  if (item.kind === 'top') return nativeTop(item, context);
  if (item.kind === 'status') return { ...common, snapshotUtc: item.window.toUtc, source: 'query_store', sourceState: 'read_write', coverage: 'complete', fresh: true,
    truncated: false, contentAvailable: false, databaseStatuses: [], databaseCatalog: [{ databaseId: 5, databaseName: 'SYNTHETIC Orders' }, { databaseId: 6, databaseName: 'SYNTHETIC Warehouse' }] };
  if (item.kind === 'waits') return { ...common, category: null, semantics: 'query_store_group_wait_delta', coverage: 'no_comparable_groups', items: [] };
  if (item.kind === 'regressions') {
    const micros = value => BigInt(Date.parse(value.slice(0, 19) + 'Z')) * 1000000n / 1000n + BigInt((value.split('.')[1]?.replace('Z', '') ?? '').padEnd(6, '0'));
    const value = micros(item.window.fromUtc) * 2n - micros(item.window.toUtc);
    return { ...common, comparisonFromUtc: new Date(Number(value / 1000n)).toISOString().slice(0, 19) + '.' + String(value % 1000000n).padStart(6, '0') + 'Z',
      semantics: 'query_store_group_plan_comparison', coverage: 'no_qualifying_changes', items: [] };
  }
  if (item.kind === 'trend') return { ...common, databaseId: item.databaseId, queryFingerprint: item.query, planFingerprint: planA,
    semantics: 'query_store_group_delta', revisionScope: 'current', coverage: 'observed_groups_only', bucketSeconds: 3600, points: [] };
  if (item.kind === 'history') return { ...common, databaseId: item.databaseId, queryFingerprint: item.query, snapshotUtc: item.window.toUtc, nextCursor: null,
    items: [{ ...observation(item.query, item.window), databaseId: item.databaseId, cpuMilliseconds: 7201, durationMilliseconds: 8201, executions: 10, logicalReads: 1234 }] };
  if (item.kind === 'metadata') return { targetId: item.id, databaseId: item.databaseId, queryFingerprint: item.query, planFingerprint: planA,
    source: 'query_store', observedAtUtc: to, coverage: 'complete', contentAvailable: true };
  if (item.kind === 'text') return { targetId: item.id, databaseId: item.databaseId, queryFingerprint: item.query, collectionRunId: run, status: 'available', text: secret };
  if (item.kind === 'content') return { targetId: item.id, databaseId: item.databaseId, queryFingerprint: item.query, planFingerprint: planA, collectionRunId: run, status: 'available', xml: '<ShowPlanXML> SYNTHETIC protected plan </ShowPlanXML>' };
  throw new Error(`Unregistered DTO ${item.kind}`);
}
function registered(url) {
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/query-performance\/(.+)$/.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1])) return undefined;
  const id = match[1], suffix = match[2], params = url.searchParams;
  const window = { fromUtc: params.get('fromUtc'), toUtc: params.get('toUtc') };
  let item, keys;
  if (suffix === 'top') { item = { kind: 'top', metric: params.get('metric'), cursor: params.get('cursor') ?? undefined }; keys = ['metric', 'limit', 'fromUtc', 'toUtc', 'cursor'];
    if (!['cpu', 'duration', 'executions', 'logical_reads', 'writes', 'rows'].includes(item.metric) || params.get('limit') !== '200' || item.cursor && item.cursor !== cursor) return undefined; }
  else if (suffix === 'status') { item = { kind: 'status' }; keys = ['fromUtc', 'toUtc']; }
  else if (suffix === 'waits/top' || suffix === 'regressions') { item = { kind: suffix === 'regressions' ? 'regressions' : 'waits' }; keys = ['fromUtc', 'toUtc', 'limit']; if (params.get('limit') !== '25') return undefined; }
  else if(suffix==='cache/top'){item={kind:'cache_top',metric:params.get('metric')};keys=['fromUtc','toUtc','metric','limit'];if(params.get('limit')!=='25'||!['cpu','duration','executions','logical_reads','writes','rows'].includes(item.metric))return undefined;}
  else if(suffix===`cache/databases/5/history/${queryA}/plans/${planA}`){item={kind:'cache_history',cursor:params.get('cursor')??undefined};keys=['databaseIncarnation','fromUtc','toUtc','limit','cursor'];if(params.get('databaseIncarnation')!==cacheIncarnation||params.get('limit')!=='100'||item.cursor&&item.cursor!==cacheCursor)return undefined;}
  else if(suffix.startsWith(`cache/databases/5/history/${queryA}/plans/${planA}/runs/${cacheRun(0)}/entries/${cacheEntry}/content/`)){const kind=suffix.split('/').at(-1);if(!['text','plan'].includes(kind)||params.get('databaseIncarnation')!==cacheIncarnation)return undefined;item={kind:'cache_content',payloadKind:kind};keys=['databaseIncarnation'];}
  else {
    const selected = /^databases\/(5|6)\/history\/([a-c]{64})(?:\/plans\/([d]{64})\/trend|\/runs\/(99999999-9999-4999-8999-000000000001)\/(text|plans\/([d]{64})\/content))?$/.exec(suffix);
    const metadata = /^databases\/(5|6)\/plans\/([d]{64})$/.exec(suffix);
    if (selected && [queryA, queryB, queryC].includes(selected[2])) { item = { databaseId: Number(selected[1]), query: selected[2], kind: selected[3] ? 'trend' : selected[5] ? selected[5] === 'text' ? 'text' : 'content' : 'history' };
      keys = item.kind === 'history' ? ['fromUtc', 'toUtc', 'limit'] : item.kind === 'trend' ? ['fromUtc', 'toUtc'] : [];
      if (item.kind === 'history' && params.get('limit') !== '200') return undefined; }
    else if (metadata && [queryA, queryB, queryC].includes(params.get('queryFingerprint'))) { item = { kind: 'metadata', databaseId: Number(metadata[1]), query: params.get('queryFingerprint') }; keys = ['queryFingerprint']; }
    else return undefined;
  }
  if ([...params.keys()].some(key => !keys.includes(key)) || [...new Set(params.keys())].some(key => params.getAll(key).length !== 1)) return undefined;
  if (keys.includes('fromUtc') && (!['2026-10-02T10:00:00.123456Z', '2026-10-02T09:00:00.123456Z'].includes(window.fromUtc)
    || !['2026-10-02T11:00:00.123456Z', '2026-10-02T10:00:00.123456Z'].includes(window.toUtc))) return undefined;
  return { id, url, window, ...item };
}
function held(predicate) {
  let start, release, complete;
  const started = new Promise(resolve => { start = resolve; }), gate = new Promise(resolve => { release = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    if (!predicate(item)) return false;
    const value = nativeValue(item, context); start(route.request()); await gate;
    try { await json(route, value); } catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { requests: [], unexpected: [], firstQuery: queryB, firstValue: 1001, handler: undefined }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const item = registered(url); if (request.method() !== 'GET' || !item) return reject(); context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    return json(route, nativeValue(item, context));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only registered native query reads may run; no provider HTML or external traffic').toEqual([]));
async function mount(page, options = {}) { await page.goto('/component-tests/queries.html' + href(options)); await expectSelected(page); }
async function expectSelected(page, query = queryA) { await expect(panel(page).getByText(query, { exact: true })).toBeVisible(); await expect(history(page)).toBeVisible(); }
async function openHistory(page) { await history(page).click(); await expect(panel(page)).toContainText('CPU 7201'); }
const reads = (page, kind) => contexts.get(page).requests.filter(item => item.kind === kind);

test('an exact pure-query URL owns history outside both ranking pages and preserves opened run content', async ({ page }) => {
  await mount(page); await expect(ranking(page)).toContainText('bbbbbbbbbbbb'); await expect(ranking(page).locator('tbody > tr')).toHaveCount(200); await openHistory(page);
  await panel(page).getByRole('button', { name: 'Open text for this run', exact: true }).click();
  const text = panel(page).getByLabel('Captured Query Store text', { exact: true }); await expect(text).toHaveText(secret);
  const params = hashParams(page).toString(), historyReads = reads(page, 'history').length;
  await next(page).click(); await expect(ranking(page)).toContainText('cccccccccccc'); await expectSelected(page); await expect(text).toHaveText(secret);
  await expect(panel(page)).toContainText('outside this loaded ranking page; its exact history remains selected');
  expect(hashParams(page).toString()).toBe(params); expect(reads(page, 'history')).toHaveLength(historyReads);
  expect(reads(page, 'top')[1]).toMatchObject({ cursor, window: { fromUtc: from, toUtc: to } });
  expect(await page.evaluate(() => window.queryInjected)).toBeUndefined(); await expect(text.locator('img')).toHaveCount(0);
});

test('changed-window and refresh commits hide old ranking, status, waits, exact history and plan before new receipts arrive', async ({ page }) => {
  await mount(page, { planFingerprint: planA }); await expect(panel(page)).toContainText(`Plan: ${planA}`);
  await expect(panel(page)).toContainText('No comparable Query Store wait groups'); await expect(ranking(page).locator('tbody > tr')).toHaveCount(200);
  const context = contexts.get(page), newFrom = '2026-10-02T09:00:00.123456Z';
  const pending = held(item => item.window.fromUtc === newFrom || item.kind === 'metadata'); context.handler = pending.handle;
  await page.getByRole('button', { name: 'Change bounded window', exact: true }).click(); await pending.started;
  const projection = page.getByLabel('Context commit evidence projection', { exact: true });
  await expect(projection).toContainText(newFrom);
  expect(JSON.parse(await projection.textContent())).toMatchObject({ rankingRows: 0, historyVisible: false, selectedQueryVisible: false, planVisible: false, statusReady: false, waitsReady: false });
  await expect(ranking(page)).toHaveCount(0); await expect(history(page)).toHaveCount(0); await expect(panel(page)).not.toContainText(`Plan: ${planA}`);
  await pending.finish(); await expectSelected(page); await expect(ranking(page).locator('tbody > tr')).toHaveCount(200); await expect(panel(page)).toContainText(`Plan: ${planA}`);
  expect(reads(page, 'history').at(-1).window).toEqual({ fromUtc: newFrom, toUtc: '2026-10-02T10:00:00.123456Z' });
  await expect(panel(page)).toContainText('No comparable Query Store wait groups');
  const refreshed = held(item => item.window.fromUtc === newFrom || item.kind === 'metadata'); context.handler = refreshed.handle;
  await page.getByRole('button', { name: 'Refresh query workspace', exact: true }).click(); await refreshed.started;
  await expect.poll(async () => JSON.parse((await projection.textContent()) ?? '{}').context).toContain(',1]');
  expect(JSON.parse(await projection.textContent())).toMatchObject({ rankingRows: 0, historyVisible: false, selectedQueryVisible: false, planVisible: false, statusReady: false, waitsReady: false });
  await refreshed.finish(); await expectSelected(page); await expect(ranking(page).locator('tbody > tr')).toHaveCount(200);
});

test('exact plan metadata and history survive independent rank and source edits without duplicate history reads', async ({ page }) => {
  await mount(page, { planFingerprint: planA }); await expect(panel(page)).toContainText(`Plan: ${planA}`);
  await expect(panel(page).getByRole('region', { name: 'Selected plan delta trend', exact: true })).toBeVisible(); const historyReads = reads(page, 'history').length;
  await panel(page).getByRole('combobox', { name: 'Rank by', exact: true }).selectOption('duration'); await expect(ranking(page)).toContainText('1001');
  await panel(page).getByRole('combobox', { name: 'Source', exact: true }).selectOption('plan_cache');
  await expect(ranking(page).locator('tbody > tr')).toHaveCount(199); await expect(ranking(page)).toContainText('plan_cache_cumulative');
  await expect(ranking(page).getByRole('button', { name: 'SYNTHETIC Orders / bbbbbbbbbbbb…', exact: true })).toHaveCount(0);
  await expectSelected(page); await expect(panel(page)).toContainText(`Plan: ${planA}`);
  expect(reads(page, 'history')).toHaveLength(historyReads); expect(hashParams(page).get('source')).toBe('query_store');
  expect(hashParams(page).get('querySource')).toBe('plan_cache'); expect(hashParams(page).get('planFingerprint')).toBe(planA);
});

test('a ranking failure cannot hide authorized exact-query history and per-run inspection', async ({ page }) => {
  contexts.get(page).handler = async (route, item) => { if (item.kind !== 'top') return false; await json(route, { detail: '<script>SECRET_RANKING</script>' }, 503); return true; };
  await mount(page); await expect(panel(page).getByRole('alert')).toHaveText('Query performance request failed.');
  await expect(ranking(page)).toHaveCount(0); await openHistory(page); await panel(page).getByRole('button', { name: 'Open plan XML for this run', exact: true }).click();
  await expect(panel(page).getByLabel('Captured Query Store plan XML', { exact: true })).toHaveText('<ShowPlanXML> SYNTHETIC protected plan </ShowPlanXML>');
  await expect(panel(page)).not.toContainText('SECRET_RANKING');
});

for (const change of ['refresh', 'metric', 'source', 'database', 'window', 'target', 'unmount']) test(`a held ranking continuation is canceled and fenced on ${change}`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => item.kind === 'top' && item.cursor === cursor); context.handler = pending.handle;
  await next(page).click(); const request = await pending.started; await expect(next(page)).toBeDisabled();
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  if (change === 'refresh') { context.firstValue = 4321; await page.getByRole('button', { name: 'Refresh query workspace', exact: true }).click(); }
  if (change === 'metric') await panel(page).getByRole('combobox', { name: 'Rank by', exact: true }).selectOption('duration');
  if (change === 'source') await panel(page).getByRole('combobox', { name: 'Source', exact: true }).selectOption('plan_cache');
  if (change === 'database') await panel(page).getByRole('combobox', { name: 'Database', exact: true }).selectOption('6');
  if (change === 'window') await page.getByRole('button', { name: 'Change bounded window', exact: true }).click();
  if (change === 'target') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  if (change === 'unmount') await page.getByRole('button', { name: 'Unmount query panel', exact: true }).click();
  await aborted; await pending.finish();
  if (change === 'unmount') { await expect(panel(page)).toHaveCount(0); return; }
  await expect(ranking(page)).not.toContainText('cccccccccccc'); await expect(next(page)).toBeEnabled();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  if (change === 'refresh') { await expect(ranking(page)).toContainText('4321'); await expectSelected(page); expect(reads(page, 'top').at(-1).window).toEqual({ fromUtc: from, toUtc: to }); }
  if (change === 'window') { await expectSelected(page); expect(reads(page, 'history').at(-1).window).toEqual({ fromUtc: '2026-10-02T09:00:00.123456Z', toUtc: '2026-10-02T10:00:00.123456Z' }); }
  if (change === 'target') { await expectSelected(page); expect(reads(page, 'history').at(-1).id).toBe(beta); }
  if (change === 'database') { await expect(history(page)).toHaveCount(0); await expect(panel(page)).toContainText('No query selected'); }
});

test('ranking paging does not cancel an independently pending exact history response', async ({ page }) => {
  const pending = held(item => item.kind === 'history'); contexts.get(page).handler = pending.handle;
  await page.goto('/component-tests/queries.html' + href()); await pending.started; await expect(next(page)).toBeEnabled();
  await next(page).click(); await expect(ranking(page)).toContainText('cccccccccccc'); await pending.finish(); await expectSelected(page);
  await openHistory(page); expect(reads(page, 'history')).toHaveLength(1);
});

test('invalid exact selection clears prior inspection and only an explicit ranked selection repairs it', async ({ page }) => {
  await mount(page); const count = reads(page, 'history').length;
  await page.getByRole('button', { name: 'Invalidate exact selection', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toHaveText('The query selection in this URL is invalid. Select a query from the ranking.');
  await expect(panel(page).getByText(queryA, { exact: true })).toHaveCount(0); await expect(history(page)).toHaveCount(0); expect(reads(page, 'history')).toHaveLength(count);
  await ranking(page).getByRole('button', { name: 'SYNTHETIC Orders / bbbbbbbbbbbb…', exact: true }).click(); await expectSelected(page, queryB);
  await expect(panel(page).getByRole('alert')).toHaveCount(0); expect(hashParams(page).get('source')).toBe('query_store'); expect(hashParams(page).get('queryFingerprint')).toBe(queryB);
});

for (const kind of ['text', 'content']) test(`late protected ${kind} is aborted on read-role loss and restoration requires a fresh explicit read`, async ({ page }) => {
  await mount(page); await openHistory(page); const pending = held(item => item.kind === kind); contexts.get(page).handler = pending.handle;
  const button = panel(page).getByRole('button', { name: kind === 'text' ? 'Open text for this run' : 'Open plan XML for this run', exact: true }); await button.click(); const request = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  await page.getByRole('combobox', { name: 'Protected content access', exact: true }).selectOption('viewer'); await aborted; await pending.finish();
  await expect(panel(page).getByLabel(kind === 'text' ? 'Captured Query Store text' : 'Captured Query Store plan XML', { exact: true })).toHaveCount(0); await expect(button).toHaveCount(0);
  const count = reads(page, kind).length; contexts.get(page).handler = undefined;
  await page.getByRole('combobox', { name: 'Protected content access', exact: true }).selectOption('reader'); await expect(button).toBeVisible(); expect(reads(page, kind)).toHaveLength(count);
  await button.click(); await expect(panel(page).getByLabel(kind === 'text' ? 'Captured Query Store text' : 'Captured Query Store plan XML', { exact: true })).toBeVisible(); expect(reads(page, kind)).toHaveLength(count + 1);
});

test('a continuation outside the native window fails safely while verified exact history stays selected', async ({ page }) => {
  await mount(page); contexts.get(page).handler = async (route, item, context) => { if (item.kind !== 'top' || !item.cursor) return false;
    await json(route, { ...nativeTop(item, context), fromUtc: '2026-10-02T10:00:00.123457Z', detail: 'SECRET_FOREIGN_WINDOW' }); return true; };
  await next(page).click(); await expect(panel(page).getByRole('alert')).toHaveText('Query performance top response was outside its bounds.');
  await expect(ranking(page)).toContainText('bbbbbbbbbbbb'); await expectSelected(page); await expect(panel(page)).not.toContainText('SECRET_FOREIGN_WINDOW');
});

for(const status of [401,403])for(const kind of ['text','content'])test(`ranking continuation HTTP${status} clears received ${kind}, fences sibling200 and requires explicit access recovery`,async({page})=>{
 const context=contexts.get(page),pending=held(item=>item.kind==='regressions');context.handler=async(route,item,state)=>{if(item.kind==='top'&&item.cursor){await pending.started;await json(route,{detail:'PRIVATE_DENIAL_BODY'},status);return true;}return pending.handle(route,item,state);};
 await mount(page);const sibling=await pending.started;await openHistory(page);await panel(page).getByRole('button',{name:kind==='text'?'Open text for this run':'Open plan XML for this run',exact:true}).click();
 await expect(panel(page).getByLabel(kind==='text'?'Captured Query Store text':'Captured Query Store plan XML',{exact:true})).toBeVisible();
 await next(page).click();const denied=page.getByRole('region',{name:'Query evidence access',exact:true});await expect(denied).toContainText('Retained rankings, history, waits, trends, and protected content were removed');await expect.poll(()=>sibling.failure()?.errorText).toContain('ERR_ABORTED');await pending.finish();
 await expect(ranking(page)).toHaveCount(0);await expect(history(page)).toHaveCount(0);await expect(page.getByLabel('Captured Query Store text',{exact:true})).toHaveCount(0);await expect(page.getByLabel('Captured Query Store plan XML',{exact:true})).toHaveCount(0);await expect(denied).not.toContainText('PRIVATE_DENIAL_BODY');
 const count=context.requests.length;await page.getByRole('button',{name:'Refresh query workspace',exact:true}).click();await page.getByRole('button',{name:'Change bounded window',exact:true}).click();await expect(denied).toBeVisible();expect(context.requests).toHaveLength(count);
 context.handler=undefined;await denied.getByRole('button',{name:'Retry query access',exact:true}).click();await expectSelected(page);await expect(ranking(page)).toContainText('bbbbbbbbbbbb');await expect(page.getByLabel('Captured Query Store text',{exact:true})).toHaveCount(0);await expect(page.getByLabel('Captured Query Store plan XML',{exact:true})).toHaveCount(0);
 await openHistory(page);await panel(page).getByRole('button',{name:kind==='text'?'Open text for this run':'Open plan XML for this run',exact:true}).click();await expect(panel(page).getByLabel(kind==='text'?'Captured Query Store text':'Captured Query Store plan XML',{exact:true})).toBeVisible();
});

for(const status of [401,403])test(`cache history continuation HTTP${status} purges captured content and blocks a late Query Store sibling`,async({page})=>{
 const context=contexts.get(page),pending=held(item=>item.kind==='regressions');context.handler=async(route,item,state)=>{if(item.kind==='cache_history'&&item.cursor){await pending.started;await json(route,{detail:'PRIVATE_CACHE_DENIAL'},status);return true;}return pending.handle(route,item,state);};
 await page.goto('/component-tests/queries.html'+href(cacheSelection));const sibling=await pending.started;const cache=panel(page).getByRole('region',{name:'Cache query interval history',exact:true});await expect(cache).toContainText('100 of 101 observed samples');await cache.getByRole('button',{name:'Open text',exact:true}).first().click();await expect(cache.getByLabel('Captured query text',{exact:true})).toHaveText(secret);
 await cache.getByRole('button',{name:'Load earlier cache samples',exact:true}).click();const denied=page.getByRole('region',{name:'Query evidence access',exact:true});await expect(denied).toContainText('protected content were removed');await expect.poll(()=>sibling.failure()?.errorText).toContain('ERR_ABORTED');await pending.finish();
 await expect(page.getByRole('region',{name:'Cache query interval history',exact:true})).toHaveCount(0);await expect(page.getByLabel('Captured query text',{exact:true})).toHaveCount(0);await expect(history(page)).toHaveCount(0);await expect(denied).not.toContainText('PRIVATE_CACHE_DENIAL');const count=context.requests.length;
 await page.getByRole('button',{name:'Refresh query workspace',exact:true}).click();await expect(denied).toBeVisible();expect(context.requests).toHaveLength(count);
 context.handler=undefined;await denied.getByRole('button',{name:'Retry query access',exact:true}).click();await expect(cache).toContainText('100 of 101 observed samples');await expect(cache.getByLabel('Captured query text',{exact:true})).toHaveCount(0);
 await cache.getByRole('button',{name:'Load earlier cache samples',exact:true}).click();await expect(cache).toContainText('101 of 101 observed samples');expect(reads(page,'cache_history').at(-1).cursor).toBe(cacheCursor);await cache.getByRole('button',{name:'Open text',exact:true}).first().click();await expect(cache.getByLabel('Captured query text',{exact:true})).toHaveText(secret);
});

test('ordinary cache continuation503 retains received history and protected content without inventing access denial',async({page})=>{
 await page.goto('/component-tests/queries.html'+href(cacheSelection));const cache=panel(page).getByRole('region',{name:'Cache query interval history',exact:true});await expect(cache).toContainText('100 of 101 observed samples');await cache.getByRole('button',{name:'Open text',exact:true}).first().click();await expect(cache.getByLabel('Captured query text',{exact:true})).toHaveText(secret);
 contexts.get(page).handler=async(route,item)=>item.kind==='cache_history'&&item.cursor?(await json(route,{detail:'PRIVATE_TRANSIENT_ERROR'},503),true):false;
 await cache.getByRole('button',{name:'Load earlier cache samples',exact:true}).click();await expect(cache.getByRole('alert')).toContainText('More history could not be loaded');await expect(cache).toContainText('100 of 101 observed samples');await expect(cache.getByLabel('Captured query text',{exact:true})).toHaveText(secret);await expect(page.getByRole('region',{name:'Query evidence access',exact:true})).toHaveCount(0);await expect(cache).not.toContainText('PRIVATE_TRANSIENT_ERROR');
 contexts.get(page).handler=undefined;await cache.getByRole('button',{name:'Load earlier cache samples',exact:true}).click();await expect(cache).toContainText('101 of 101 observed samples');
});

test('ordinary ranking continuation503 retains protected SQL and exact history while explicit retry uses the same cursor',async({page})=>{
 await mount(page);await openHistory(page);await panel(page).getByRole('button',{name:'Open text for this run',exact:true}).click();const text=panel(page).getByLabel('Captured Query Store text',{exact:true});await expect(text).toHaveText(secret);
 contexts.get(page).handler=async(route,item)=>item.kind==='top'&&item.cursor?(await json(route,{detail:'PRIVATE_TRANSIENT_RANKING'},503),true):false;
 await next(page).click();await expect(panel(page).getByRole('alert')).toHaveText('Query performance request failed.');await expect(ranking(page)).toContainText('bbbbbbbbbbbb');await expectSelected(page);await expect(text).toHaveText(secret);await expect(page.getByRole('region',{name:'Query evidence access',exact:true})).toHaveCount(0);await expect(panel(page)).not.toContainText('PRIVATE_TRANSIENT_RANKING');
 contexts.get(page).handler=undefined;await next(page).click();await expect(ranking(page)).toContainText('cccccccccccc');expect(reads(page,'top').at(-1).cursor).toBe(cursor);await expect(text).toHaveText(secret);
});

test('protected-text403 removes that content while independently authorized query metadata and history remain',async({page})=>{
 await mount(page);await openHistory(page);const open=panel(page).getByRole('button',{name:'Open text for this run',exact:true});await open.click();await expect(panel(page).getByLabel('Captured Query Store text',{exact:true})).toHaveText(secret);
 contexts.get(page).handler=async(route,item)=>item.kind==='text'?(await json(route,{detail:'PRIVATE_TEXT_DENIAL'},403),true):false;await open.click();await expect(panel(page)).toContainText('Query performance request failed.');await expect(panel(page).getByLabel('Captured Query Store text',{exact:true})).toHaveCount(0);await expectSelected(page);await expect(ranking(page)).toContainText('bbbbbbbbbbbb');await expect(panel(page)).toContainText('CPU 7201');await expect(page.getByRole('region',{name:'Query evidence access',exact:true})).toHaveCount(0);await expect(panel(page)).not.toContainText('PRIVATE_TEXT_DENIAL');
 contexts.get(page).handler=undefined;await open.click();await expect(panel(page).getByLabel('Captured Query Store text',{exact:true})).toHaveText(secret);
});

test('protected-plan401 is authentication loss and clears the shared query receipt until explicit access recovery',async({page})=>{
 await mount(page);await openHistory(page);const open=panel(page).getByRole('button',{name:'Open plan XML for this run',exact:true});await open.click();await expect(panel(page).getByLabel('Captured Query Store plan XML',{exact:true})).toBeVisible();
 contexts.get(page).handler=async(route,item)=>item.kind==='content'?(await json(route,{detail:'PRIVATE_AUTHENTICATION_DENIAL'},401),true):false;await open.click();const denied=page.getByRole('region',{name:'Query evidence access',exact:true});await expect(denied).toContainText('protected content were removed');await expect(ranking(page)).toHaveCount(0);await expect(history(page)).toHaveCount(0);await expect(page.getByLabel('Captured Query Store plan XML',{exact:true})).toHaveCount(0);await expect(denied).not.toContainText('PRIVATE_AUTHENTICATION_DENIAL');
 contexts.get(page).handler=undefined;await denied.getByRole('button',{name:'Retry query access',exact:true}).click();await expectSelected(page);await expect(panel(page).getByLabel('Captured Query Store plan XML',{exact:true})).toHaveCount(0);await openHistory(page);await panel(page).getByRole('button',{name:'Open plan XML for this run',exact:true}).click();await expect(panel(page).getByLabel('Captured Query Store plan XML',{exact:true})).toBeVisible();
});

for (const change of ['target', 'window', 'unmount']) test(`a protected run receipt cannot follow a changed ${change} inspection context`, async ({ page }) => {
  await mount(page); await openHistory(page); const pending = held(item => item.kind === 'text'); contexts.get(page).handler = pending.handle;
  await panel(page).getByRole('button', { name: 'Open text for this run', exact: true }).click(); const request = await pending.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  if (change === 'target') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  if (change === 'window') await page.getByRole('button', { name: 'Change bounded window', exact: true }).click();
  if (change === 'unmount') await page.getByRole('button', { name: 'Unmount query panel', exact: true }).click();
  await cancelled; await pending.finish();
  await expect(page.getByLabel('Captured Query Store text', { exact: true })).toHaveCount(0); expect(reads(page, 'text')).toHaveLength(1);
  if (change === 'unmount') await expect(panel(page)).toHaveCount(0);
  else { await expectSelected(page); await expect(panel(page)).not.toContainText('SYNTHETIC protected SQL'); }
});

for (const width of [320, 390, 768, 1440]) test(`off-page query history remains dark, readable and keyboard accessible at ${width}px`, async ({ page }, info) => {
  contexts.get(page).small = true; await page.setViewportSize({ width, height: 1000 }); await mount(page);
  await expect(ranking(page).locator('tbody > tr')).toHaveCount(1); await expect(next(page)).toBeDisabled();
  await expect(panel(page)).toContainText('outside this loaded ranking page; its exact history remains selected');
  await history(page).focus(); await page.keyboard.press('Enter'); await expect(panel(page)).toContainText('CPU 7201');
  await ranking(page).focus(); await expect(ranking(page)).toBeFocused();
  const focus = await ranking(page).evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  if (await ranking(page).evaluate(element => element.scrollWidth > element.clientWidth)) { await page.keyboard.press('ArrowRight'); await expect.poll(() => ranking(page).evaluate(element => element.scrollLeft)).toBeGreaterThan(0); }
  for (const control of [panel(page).getByRole('combobox', { name: 'Rank by', exact: true }), panel(page).getByRole('combobox', { name: 'Source', exact: true }),
    panel(page).getByRole('button', { name: 'Open text for this run', exact: true })]) {
    await control.focus(); await expect(control).toBeFocused(); const box = await control.boundingBox(); expect(box.height).toBeGreaterThanOrEqual(40);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`query-mounted-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-query-selection/screenshots', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; }); window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0); await page.screenshot({ path: `${directory}/query-selection-mounted-${width}.png`, fullPage: true });
});
