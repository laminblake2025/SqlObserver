import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { target, otherTarget, runId, fromUtc, toUtc, observedAtUtc, cursor, lastCursor, providerText,
  requestPath, routeHash, operational, tempFile, availability, replica, fixture } from './operationsFixture.mjs';

const sessions = new WeakMap();
const origin = 'http://127.0.0.1:4328';
const panel = page => page.getByRole('region', { name: 'Health checks', exact: true });
const card = (page, label) => panel(page).getByRole('article', { name: label, exact: true });
const files = page => card(page, 'TempDB files');
const namedFiles = page => files(page).getByRole('table', { name: /^tempdb-files named evidence/ });
const tab = (page, label) => panel(page).getByRole('tab', { name: label, exact: true });
function key(path) {
  const url = new URL(path, origin);
  return `${url.pathname}?${new URLSearchParams([...url.searchParams].sort(([a], [b]) => a.localeCompare(b)))}`;
}
function register(page, kind, body, options = {}) {
  sessions.get(page).registry.set(key(requestPath(kind, options.id ?? target, options.cursor)), {
    body, status: options.status ?? 200, handler: options.handler,
  });
}
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}
function held(body) {
  const started = deferred(), released = deferred(), completed = deferred();
  return {
    started: started.promise,
    release: async () => { released.resolve(); await completed.promise; },
    handle: async route => {
      started.resolve(route.request());
      await released.promise;
      try { await json(route, body); }
      catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
      finally { completed.resolve(); }
    },
  };
}

test.beforeEach(async ({ page }) => {
  const session = { registry: new Map(), requests: [], unexpected: [] };
  sessions.set(page, session);
  for (const id of [target, otherTarget]) {
    for (const [kind, body] of Object.entries(fixture(id))) register(page, kind, body, { id });
  }
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) {
      session.unexpected.push(`External request: ${request.method()} ${url.href}`);
      return route.abort();
    }
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const registered = request.method() === 'GET' && session.registry.get(key(url.href));
    if (!registered) {
      session.unexpected.push(`Unregistered API: ${request.method()} ${url.href}`);
      return route.abort();
    }
    session.requests.push(request);
    if (registered.handler) await registered.handler(route);
    else await json(route, registered.body, registered.status);
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});
test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Unexpected API/external traffic is aborted before the proxy/backend; page errors fail the suite').toEqual([]);
});
async function mount(page, selectedTab = 'backups') {
  await page.goto(`/component-tests/operations.html${routeHash(target, selectedTab)}`);
  await expect(page.getByRole('heading', { name: 'Operations panel harness' })).toBeVisible();
  await expect(tab(page, selectedTab === 'tempdb' ? 'TempDB' : selectedTab === 'agent' ? 'SQL Agent' : selectedTab === 'availability' ? 'Availability groups' : 'Backups')).toHaveAttribute('aria-selected', 'true');
}
async function supporting(page, label) {
  const summary = panel(page).locator('summary').filter({ hasText: label });
  if (!await summary.evaluate(element => element.parentElement.open)) await summary.click();
}
async function expectContextLinks(page, id = target) {
  for (const [label, section] of [['Configuration drift', 'summary'], ['Database options', 'databases']]) {
    const href = await panel(page).getByRole('link', { name: label, exact: true }).getAttribute('href');
    const url = new URL(href, origin);
    expect(url.hash.split('?')[0]).toBe('#/health');
    expect(Object.fromEntries(new URLSearchParams(url.hash.split('?')[1]))).toEqual({
      target: id, databaseId: '7', range: 'custom', from: fromUtc, to: toUtc, compare: '1', healthSection: section,
    });
  }
}

test('the grouped tabs expose six distinct sources, current Agent history, and shared context links', async ({ page }) => {
  register(page, 'tempdb-files', operational('tempdb-files', target, { items: [tempFile(1, { state: providerText })] }));
  await mount(page);
  await expect(panel(page).getByRole('region', { name: 'Backup freshness assessments' })).toContainText('no database verdicts are available for this received assessment');
  await supporting(page, 'Earlier backup observations');
  await expect(card(page, 'Backups').getByRole('table', { name: /^backups named evidence/ })).toContainText('Full');
  await tab(page, 'SQL Agent').click();
  const agent = panel(page).getByRole('region', { name: 'SQL Agent failures', exact: true });
  await expect(agent.getByRole('heading', { name: 'SQL Agent failures' })).toBeVisible();
  await expect(agent).toContainText('1 job-outcome failures in this page');
  await expect(agent.getByRole('cell', { name: '2026-08-25T07:58:00', exact: true })).toBeVisible();
  await expect(agent).toContainText('timezone is unknown');
  await tab(page, 'TempDB').click();
  await expect(card(page, 'TempDB summary').locator('.operation-metrics > div')
    .filter({ has: page.getByText('Total bytes', { exact: true }) }).locator('strong')).toHaveText('1024');
  await expect(namedFiles(page)).toContainText(providerText);
  await expect(page.locator('img, [onerror]')).toHaveCount(0);
  expect(await page.evaluate(() => window.operationsInjected)).toBeUndefined();
  await tab(page, 'Availability groups').click();
  await expect(panel(page).getByRole('region', { name: 'Per-replica availability evidence' })).toContainText('No v2 run was published');
  await supporting(page, 'Earlier availability summary');
  await expect(card(page, 'Availability Group replicas').getByRole('table', { name: /^ag-replicas named evidence/ })).toContainText('PRIMARY');
  await expect(card(page, 'Availability Group databases').getByRole('table', { name: /^ag-databases named evidence/ })).toContainText('SYNCHRONIZED');
  await expectContextLinks(page);
  const actual = new Set(sessions.get(page).requests.map(request => key(request.url())));
  for (const kind of ['backups', 'agent', 'tempdb-summary', 'tempdb-files', 'ag-replicas', 'ag-databases', 'backup-assessment', 'availability-replicas', 'availability-databases']) {
    expect(actual.has(key(requestPath(kind))), `Native source ${kind} was exercised`).toBe(true);
  }
});

test('NoData, Unsupported, PermissionDenied and Partial remain truthful about missing or incomplete evidence', async ({ page }) => {
  register(page, 'tempdb-summary', operational('tempdb-summary', target, { state: 'NoData', runId: null }));
  register(page, 'tempdb-files', operational('tempdb-files', target, { state: 'Unsupported' }));
  register(page, 'backups', operational('backups', target, { state: 'PermissionDenied' }));
  register(page, 'ag-replicas', operational('ag-replicas', target, {
    state: 'Partial', truncated: false, items: fixture()['ag-replicas'].items,
  }));
  await mount(page, 'tempdb');
  const summary = card(page, 'TempDB summary');
  await expect(summary.getByRole('status')).toContainText('NoData');
  await expect(summary).toContainText('No observation is available for this target.');
  await expect(summary.locator('.operation-metrics strong')).toHaveText(['Unavailable', 'Unavailable', 'Unavailable', 'Unavailable']);
  await expect(files(page).getByRole('status')).toContainText('Unsupported');
  await expect(files(page)).toContainText('unsupported by the target');
  await tab(page, 'Backups').click();
  await supporting(page, 'Earlier backup observations');
  await expect(card(page, 'Backups').getByRole('status')).toContainText('PermissionDenied');
  await expect(card(page, 'Backups')).toContainText('could not read this evidence');
  await tab(page, 'Availability groups').click();
  await supporting(page, 'Earlier availability summary');
  await expect(card(page, 'Availability Group replicas').getByRole('status')).toContainText('Partial');
  await expect(card(page, 'Availability Group replicas')).toContainText('Partial source observation · source coverage is incomplete');
  await expect(card(page, 'Availability Group replicas').getByRole('table', { name: /^ag-replicas named evidence/ })).toBeVisible();
});

test('cancelling pending evidence aborts its request and retry obtains a fresh bounded snapshot', async ({ page }) => {
  const old = held(operational('tempdb-files', target, { items: [tempFile(77)] }));
  register(page, 'tempdb-files', undefined, { handler: old.handle });
  await mount(page, 'tempdb');
  const request = await old.started;
  await expect(files(page)).toContainText('Loading bounded evidence…');
  const cancelled = page.waitForEvent('requestfailed', { predicate: failed => failed === request });
  await files(page).getByRole('button', { name: 'Cancel TempDB files read', exact: true }).click();
  await cancelled;
  await expect(files(page).getByRole('alert')).toContainText('cancelled');
  await expect(files(page).getByRole('button', { name: 'Cancel TempDB files read', exact: true })).toHaveCount(0);
  await old.release();
  register(page, 'tempdb-files', operational('tempdb-files', target, { items: [tempFile(9)] }));
  await files(page).getByRole('button', { name: 'Retry TempDB files read', exact: true }).click();
  await expect(namedFiles(page).getByRole('row')).toHaveCount(2);
  await expect(namedFiles(page).getByRole('row').nth(1).getByRole('cell').first()).toHaveText('9');
  await expect(files(page).getByRole('alert')).toHaveCount(0);
  expect(sessions.get(page).requests.filter(request => key(request.url()) === key(requestPath('tempdb-files')))).toHaveLength(2);
});

test('paging replaces bounded rows, preserves the investigated page on failure, and retries the same native cursor safely', async ({ page }) => {
  register(page, 'tempdb-files', operational('tempdb-files', target, { items: Array.from({ length: 50 }, (_, i) => tempFile(i + 1)), hasMore: true, nextCursor: cursor }));
  const next = held(operational('tempdb-files', target, { items: Array.from({ length: 50 }, (_, i) => tempFile(i + 51)), hasMore: true, nextCursor: lastCursor }));
  register(page, 'tempdb-files', undefined, { cursor, handler: next.handle });
  register(page, 'tempdb-files', { providerDetail: 'OPERATIONS_PROVIDER_SECRET' }, { cursor: lastCursor, status: 503 });
  await mount(page, 'tempdb');
  await expect(namedFiles(page).getByRole('row')).toHaveCount(51);
  await files(page).getByRole('button', { name: 'Next TempDB files page', exact: true }).click();
  await next.started;
  await expect(files(page).getByRole('button', { name: 'Next TempDB files page', exact: true })).toBeDisabled();
  await expect(namedFiles(page).getByRole('row')).toHaveCount(51);
  await next.release();
  await expect(namedFiles(page).getByRole('row')).toHaveCount(51);
  await expect(namedFiles(page).getByRole('row').nth(1).getByRole('cell').first()).toHaveText('51');
  await files(page).getByRole('button', { name: 'Next TempDB files page', exact: true }).click();
  await expect(files(page).getByRole('alert')).toHaveText('Operational health is temporarily unavailable.');
  await expect(namedFiles(page).getByRole('row')).toHaveCount(51);
  await expect(namedFiles(page).getByRole('row').nth(1).getByRole('cell').first()).toHaveText('51');
  await expect(panel(page)).not.toContainText('OPERATIONS_PROVIDER_SECRET');
  register(page, 'tempdb-files', operational('tempdb-files', target, { items: [tempFile(101)] }), { cursor: lastCursor });
  await files(page).getByRole('button', { name: 'Retry TempDB files read', exact: true }).click();
  await expect(namedFiles(page).getByRole('row')).toHaveCount(2);
  await expect(namedFiles(page).getByRole('row').nth(1).getByRole('cell').first()).toHaveText('101');
  await expect(files(page).getByRole('alert')).toHaveCount(0);
  await expect(files(page).getByRole('button', { name: 'Next TempDB files page', exact: true })).toBeDisabled();
  const failedAndRetried = sessions.get(page).requests.filter(request => key(request.url()) === key(requestPath('tempdb-files', target, lastCursor)));
  expect(failedAndRetried).toHaveLength(2);
});

test('a server change cancels pending evidence and its late response cannot replace the new target or links', async ({ page }) => {
  const old = held(operational('tempdb-files', target, { items: [tempFile(77)] }));
  register(page, 'tempdb-files', undefined, { handler: old.handle });
  register(page, 'tempdb-files', operational('tempdb-files', otherTarget, { items: [tempFile(88)] }), { id: otherTarget });
  await mount(page, 'tempdb');
  const request = await old.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: failed => failed === request });
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
  await cancelled;
  await expect(namedFiles(page).getByRole('row').nth(1).getByRole('cell').first()).toHaveText('88');
  await old.release();
  await expect(namedFiles(page).getByRole('row')).toHaveCount(2);
  await expect(namedFiles(page).getByRole('row').nth(1).getByRole('cell').first()).toHaveText('88');
  await expect(files(page).getByRole('alert')).toHaveCount(0);
  await expectContextLinks(page, otherTarget);
});

test('modern AG evidence labels unknown local state and fences correlation until both streams share a run', async ({ page }) => {
  const retained = availability(target, { runId, completedAtUtc: observedAtUtc, sourceObservedAtUtc: observedAtUtc,
    state: 'Partial', evidenceStatus: 'retained', sourceRows: 1, outputItems: 1, retainedItems: 1,
    lossKind: 'visibility_incomplete', items: [replica()] });
  register(page, 'availability-replicas', retained);
  register(page, 'availability-databases', { ...retained, runId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', items: [] });
  await mount(page, 'availability');
  const replicas = card(page, 'AG replicas');
  await expect(replicas.getByRole('table')).toContainText('Unknown');
  await expect(replicas).toContainText('Secondary: local state only');
  await expect(replicas).toContainText('Captured rows have incomplete coverage.');
  await expect(panel(page).getByRole('alert')).toContainText('different cutoffs, runs or target revisions');
  register(page, 'availability-databases', { ...retained, items: [] });
  await panel(page).getByRole('button', { name: 'Return both AG streams to latest', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await expect(replicas.getByRole('table')).toBeVisible();
  await expect(card(page, 'AG database replicas')).toContainText('1 retained / 1 captured / 1 source rows');
});

test('Operations remains keyboard accessible without page overflow at narrow and desktop widths', async ({ page }, info) => {
  await mount(page, 'tempdb');
  await expect(namedFiles(page)).toBeVisible();
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-operations-projections/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await tab(page, 'TempDB').focus();
    await tab(page, 'TempDB').press('ArrowLeft');
    await expect(tab(page, 'SQL Agent')).toBeFocused();
    await expect(tab(page, 'SQL Agent')).toHaveAttribute('aria-selected', 'true');
    await tab(page, 'SQL Agent').press('ArrowRight');
    await expect(tab(page, 'TempDB')).toBeFocused();
    await expect(tab(page, 'TempDB')).toHaveAttribute('aria-selected', 'true');
    await expect(namedFiles(page)).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`operations-panel-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}operations-panel-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`operations-panel-${width}`, { path, contentType: 'image/png' });
  }
});
