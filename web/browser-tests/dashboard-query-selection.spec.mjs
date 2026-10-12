import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4337';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const queryA = 'a'.repeat(64), queryB = 'b'.repeat(64), queryC = 'c'.repeat(64), planA = 'd'.repeat(64);
const run = '99999999-9999-4999-8999-000000000001', cursor = 'cXVlcnk6bmF0aXZlOnAy';
const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
const secret = '<img src="https://unexpected.invalid/steal" onerror="window.queryInjected=true"> SYNTHETIC protected SQL';
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
  await page.clock.setFixedTime(new Date('2026-10-02T12:00:00Z'));
  const context = { requests: [], unexpected: [], firstQuery: queryB, firstValue: 1001, handler: undefined, canReadText: true }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') return reject();
    if (url.pathname === '/api/v1/me') {
      const targetId = url.searchParams.get('targetId'); if (targetId !== null && ![alpha, beta].includes(targetId)) return reject();
      const roles = ['Viewer', ...(context.canReadText ? ['QueryTextReader'] : [])];
      return json(route, { active: true, targetId, grantedRoles: roles, allTargetRoles: [], targetRoles: targetId ? roles : [] });
    }
    // These finite routes belong to the existing loopback synthetic overview server.
    if (url.pathname === '/api/v1/overview' || url.pathname === '/api/v1/observation-targets'
      || /^\/api\/v1\/observation-targets\/(11111111-1111-4111-8111-00000000000[12])(?:\/health\/databases)?$/.test(url.pathname)) return route.continue();
    const item = registered(url); if (!item) return reject(); context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    return json(route, nativeValue(item, context));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only registered local synthetic APIs may run; no external reads or mutations').toEqual([]));
async function open(page, options = {}) { await page.goto('/' + href(options)); await expectSelected(page); }
async function expectSelected(page, query = queryA) { await expect(panel(page).getByText(query, { exact: true })).toBeVisible(); await expect(history(page)).toBeVisible(); }
async function openHistory(page) { await history(page).click(); await expect(panel(page)).toContainText('CPU 7201'); }
const reads = (page, kind) => contexts.get(page).requests.filter(item => item.kind === kind);
const refresh = page => page.getByRole('button', { name: '↻ Refresh', exact: true });
const exactScope = page => Object.fromEntries(hashParams(page));

test('the App reloads an off-page pure query URL and preserves exact inspection through ranking paging and browser history', async ({ page }) => {
  await open(page); await expect(ranking(page).locator('tbody > tr')).toHaveCount(200); await openHistory(page); await panel(page).getByRole('button', { name: 'Open text for this run', exact: true }).click();
  const text = panel(page).getByLabel('Captured Query Store text', { exact: true }); await expect(text).toHaveText(secret);
  const original = exactScope(page), historyReads = reads(page, 'history').length;
  await next(page).click(); await expect(ranking(page)).toContainText('cccccccccccc'); await expectSelected(page); await expect(text).toHaveText(secret);
  expect(exactScope(page)).toEqual(original); expect(reads(page, 'history')).toHaveLength(historyReads);
  expect(reads(page, 'top')[1]).toMatchObject({ cursor, window: { fromUtc: from, toUtc: to } });
  await panel(page).getByRole('combobox', { name: 'Source', exact: true }).selectOption('plan_cache');
  await expect(ranking(page)).toContainText('No plan cache observations'); await expect(text).toHaveText(secret);
  expect(hashParams(page).get('source')).toBe('query_store'); expect(hashParams(page).get('querySource')).toBe('plan_cache');
  await page.goBack(); await expect(panel(page).getByRole('combobox', { name: 'Source', exact: true })).toHaveValue('mixed'); await expectSelected(page);
  await page.goForward(); await expect(panel(page).getByRole('combobox', { name: 'Source', exact: true })).toHaveValue('plan_cache'); await expectSelected(page);
  await page.reload(); await expectSelected(page); expect(exactScope(page)).toEqual({ ...original, querySource: 'plan_cache' });
  const request = reads(page, 'history').at(-1); expect(request).toMatchObject({ id: alpha, databaseId: 5, query: queryA, window: { fromUtc: from, toUtc: to } });
  expect(await page.evaluate(() => window.queryInjected)).toBeUndefined();
});

test('manual App Refresh in Rewind cancels an older ranking continuation and retains the exact query and plan URL', async ({ page }) => {
  await open(page, { planFingerprint: planA }); const context = contexts.get(page), pending = held(item => item.kind === 'top' && item.cursor === cursor); context.handler = pending.handle;
  const original = exactScope(page); await next(page).click(); const request = await pending.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  context.firstValue = 4321; await refresh(page).click(); await cancelled;
  await expect(ranking(page)).toContainText('4321'); await expectSelected(page); await expect(panel(page)).toContainText(`Plan: ${planA}`);
  await pending.finish(); await expect(ranking(page)).not.toContainText('cccccccccccc'); await expect(next(page)).toBeEnabled();
  expect(exactScope(page)).toEqual(original); expect(reads(page, 'top').at(-1)).toMatchObject({ cursor: undefined, window: { fromUtc: from, toUtc: to } });
  expect(reads(page, 'history').at(-1).window).toEqual({ fromUtc: from, toUtc: to });
  const beforeDisplay = reads(page, 'history').length; await page.getByRole('button', { name: 'Show local time', exact: true }).click();
  await expectSelected(page); expect(exactScope(page)).toEqual(original); expect(reads(page, 'history')).toHaveLength(beforeDisplay);
});

test('invalid duplicate, orphan and contradictory exact URL selections refuse history reads and offer ranked repair', async ({ page }) => {
  const invalid = [href() + '&queryFingerprint=' + queryB, href() + '&source=plan_cache', href({ source: 'plan_cache', planFingerprint: planA }),
    '#/queries?' + new URLSearchParams({ target: alpha, range: 'custom', from, to, planFingerprint: planA })];
  for (const hash of invalid) {
    const count = reads(page, 'history').length; await page.goto('/' + hash);
    await expect(panel(page).getByRole('alert')).toHaveText('The query selection in this URL is invalid. Select a query from the ranking.');
    await expect(ranking(page)).toContainText('bbbbbbbbbbbb'); await expect(history(page)).toHaveCount(0);
    expect(reads(page, 'history')).toHaveLength(count); expect(new URL(page.url()).hash).toBe(hash);
    await ranking(page).getByRole('button', { name: 'SYNTHETIC Orders / bbbbbbbbbbbb…', exact: true }).click(); await expectSelected(page, queryB);
    await expect(panel(page).getByRole('alert')).toHaveCount(0); expect(hashParams(page).getAll('queryFingerprint')).toEqual([queryB]);
    expect(hashParams(page).getAll('source')).toEqual(['query_store']); expect(hashParams(page).get('target')).toBe(alpha);
    expect(hashParams(page).get('from')).toBe(from); expect(hashParams(page).get('to')).toBe(to);
  }
});

test('refreshed native Viewer access aborts protected receipts and leaves unprotected exact history readable', async ({ page }) => {
  await open(page); await openHistory(page); const context = contexts.get(page), pending = held(item => item.kind === 'text'); context.handler = pending.handle;
  await panel(page).getByRole('button', { name: 'Open text for this run', exact: true }).click(); const request = await pending.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  context.canReadText = false; await refresh(page).click(); await cancelled; await pending.finish(); await expectSelected(page);
  await history(page).click(); await expect(panel(page)).toContainText('CPU 7201');
  await expect(panel(page).getByRole('button', { name: 'Open text for this run', exact: true })).toHaveCount(0);
  await expect(panel(page).getByLabel('Captured Query Store text', { exact: true })).toHaveCount(0); expect(reads(page, 'text')).toHaveLength(1);
  expect(hashParams(page).get('queryFingerprint')).toBe(queryA); expect(hashParams(page).get('from')).toBe(from);
});

for (const width of [320, 390, 768, 1440]) test(`off-page exact query inspection is dark and keyboard accessible in the App at ${width}px`, async ({ page }, info) => {
  contexts.get(page).small = true; await page.setViewportSize({ width, height: 1000 }); await open(page);
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
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`query-app-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-query-selection/screenshots', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; }); window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0); await page.screenshot({ path: `${directory}/query-selection-workspace-${width}.png`, fullPage: true });
  expect(hashParams(page).get('queryFingerprint')).toBe(queryA); expect(hashParams(page).get('from')).toBe(from); expect(hashParams(page).get('to')).toBe(to);
});
