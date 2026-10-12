import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

import { historyBinding, volumeHistoryBackend } from '../tools/sql-volume-history-fixture.mjs';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const run = 'abababababab4bab8bab000000000001', newerRun = 'abababababab4bab8bab000000000002';
const from = '2026-10-02T08:00:00.000Z', to = '2026-10-02T12:00:00.000Z';
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'Database file I/O', exact: true });
const table = page => panel(page).getByRole('table');
const button = (page, name) => panel(page).getByRole('button', { name, exact: true });
const next = page => button(page, 'Next file page');
const previous = page => button(page, 'Previous file page');
const first = page => button(page, 'First file page');
const latest = page => button(page, 'Return to latest file snapshot');
function cursor(id, revision, index, snapshot = run) {
  return Buffer.from(['f', id.replaceAll('-', ''), snapshot, String(revision), '5', String(index * 25)].join('\n')).toString('base64url');
}
function collector() {
  return { collectorId: 'database.files', manifestVersion: 1, outputSchemaVersion: 1, state: 'current', reason: 'none', lastExecutionOutcome: 'succeeded',
    circuitState: 'closed', durationMilliseconds: 1, retryCount: 0, sourceRows: 75, outputRows: 75, insertedRows: 75, duplicateRows: 0, rejectedRows: 0,
    responseBytes: 5000, persistedBytes: 5000, sampleLossKind: null, minimumLostItems: null, lossCountIsExact: null, minimumLostBytes: null,
    hasVisibilityGap: false, scheduledAtUtc: from, lastAttemptAtUtc: from, lastSuccessAtUtc: from, nextDueAtUtc: to };
}
function nativePage(id, revision, index, snapshot = run) {
  return { instanceId: id, repositoryTimeUtc: `2026-10-02T10:00:0${index}.123456Z`, collector: collector(),
    nextCursor: index < 2 ? cursor(id, revision, index + 1, snapshot) : null,
    items: Array.from({ length: index === 2 ? 3 : 25 }, (_, offset) => ({ databaseId: 5, fileId: index * 25 + offset + 1,
      logicalName: `${id === alpha ? 'Alpha' : 'Beta'}-${snapshot === run ? 'captured' : 'newest'}-file-${index * 25 + offset + 1}`,
      fileType: 'rows', state: 'online', sizeBytes: '1073741824', maximumSizeBytes: null, growthBytes: '8388608', growthPercent: 0,
      readCount: '100', writeCount: '50', bytesRead: '819200', bytesWritten: '409600', ioStallMilliseconds: '600',
      readStallMilliseconds: '400', writeStallMilliseconds: '200', observedAtUtc: '2026-10-02T10:00:00.123456Z', collector: collector() })) };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function hold(index) {
  let started, release, ended;
  const start = new Promise(resolve => { started = resolve; }), gate = new Promise(resolve => { release = resolve; }), end = new Promise(resolve => { ended = resolve; });
  return { start, async finish() { release(); await end; }, async handle(route, item, context) {
    if (item.index !== index) return false;
    const value = nativePage(item.id, context.revision, item.index, item.snapshot); started(route.request()); await gate;
    try { await json(route, value); } catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { ended(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { requests: [], unexpected: [], revision: 1, snapshot: run, handler: undefined, expired: false };
  contexts.set(page, context); page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  const history = volumeHistoryBackend({ mode: 'unavailable' });
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (url.pathname === '/api/v1/overview') {
      if (request.method() !== 'GET') return reject();
      return json(route, { refreshedAtUtc: to, fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'),
        targetId: url.searchParams.get('targetId'), targets: [], excludedTargets: 0, evidence: [] });
    }
    if (/\/resources\/volumes\/history$/u.test(url.pathname)) {
      try { const binding = historyBinding(url); if (request.method() !== 'GET' || ![alpha, beta].includes(binding.instanceId)) return reject();
        return json(route, history.read(binding, url.searchParams.get('cursor'))); } catch { return reject(); }
    }
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/health\/files$/u.exec(url.pathname);
    if (!match || ![alpha, beta].includes(match[1]) || request.method() !== 'GET' || url.searchParams.get('limit') !== '25'
      || [...url.searchParams.keys()].some(key => !['limit', 'cursor'].includes(key))) return reject();
    const encoded = url.searchParams.get('cursor') ?? undefined;
    let index = 0, snapshot = context.snapshot;
    if (encoded) { const fields = Buffer.from(encoded, 'base64url').toString().split('\n'); index = Number(fields[5]) / 25; snapshot = fields[2]; }
    if (![0, 1, 2].includes(index)) return reject();
    const item = { id: match[1], index, cursor: encoded, snapshot }; context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    if (context.expired && encoded) return json(route, { detail: 'SECRET_EXPIRED_CURSOR' }, 409);
    return json(route, nativePage(item.id, context.revision, index, snapshot));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only registered native local API reads may run').toEqual([]));
async function mount(page, extra = '') {
  await page.goto(`/component-tests/retained-resource-files.html#resources?target=${alpha}&range=custom&from=${from}&to=${to}${extra}`);
  await expectPage(page, 0);
}
async function expectPage(page, index, name = 'Alpha-captured') {
  await expect(panel(page)).toContainText(`Page ${index + 1} · ${index === 2 ? 3 : 25} loaded files`);
  await expect(table(page).locator('tbody > tr')).toHaveCount(index === 2 ? 3 : 25);
  await expect(table(page).locator('tbody > tr').first()).toContainText(`${name}-file-${index * 25 + 1}`);
}

test('ordinary polling updates uninvestigated first page and preserves a browsed page and its focus', async ({ page }) => {
  await mount(page); const context = contexts.get(page); context.snapshot = newerRun;
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
  await expectPage(page, 0, 'Alpha-newest'); expect(context.requests).toHaveLength(2);
  await next(page).click(); await expectPage(page, 1, 'Alpha-newest');
  await next(page).focus(); const original = await next(page).elementHandle(); const count = context.requests.length;
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).evaluate(button => button.click());
  await expectPage(page, 1, 'Alpha-newest'); await expect(next(page)).toBeFocused();
  expect(await original.evaluate(element => element.isConnected)).toBe(true); expect(context.requests).toHaveLength(count);
  await expect(table(page).locator('caption')).toHaveText('Retained SQL Server file counters');
});

test('all visited pages restore locally after native expiry and preserve independent repository check times', async ({ page }) => {
  await mount(page); await next(page).click(); await expectPage(page, 1); await next(page).click(); await expectPage(page, 2);
  const context = contexts.get(page); context.expired = true; context.snapshot = newerRun;
  await previous(page).click(); await expectPage(page, 1); await first(page).click(); await expectPage(page, 0);
  await next(page).click(); await expectPage(page, 1); await next(page).click(); await expectPage(page, 2);
  expect(context.requests).toHaveLength(3); await expect(panel(page)).toContainText('Repository check times may differ between pages');
  await expect(next(page)).toBeDisabled();
});

test('selected first-page file retains its name and identity through polls, and an absent-page file is never relabelled', async ({ page }) => {
  await mount(page); await panel(page).getByRole('button', { name: 'View read and write latency trend for database 5, file Alpha-captured-file-1', exact: true }).click();
  await expect(page).toHaveURL(/resourceDatabaseId=5.*resourceFileId=1/u);
  const context = contexts.get(page); context.snapshot = newerRun;
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
  await expectPage(page, 0); expect(context.requests).toHaveLength(1);
  await next(page).click(); await expectPage(page, 1);
  await expect(panel(page)).toContainText('File ID 1 (name unavailable in the loaded snapshot)');
  await expect(panel(page)).toContainText('Its identity is retained; no other file has been selected');
  await first(page).click(); await expect(panel(page)).toContainText('Selected file: database ID 5, file ID 1 · Alpha-captured-file-1');
});

test('a held continuation survives workspace polling without duplicate dispatch', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold(1); context.handler = held.handle;
  await next(page).click(); await held.start;
  await expect(next(page)).toBeDisabled(); await expect(latest(page)).toBeDisabled();
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
  expect(context.requests).toHaveLength(2); await expectPage(page, 0);
  await held.finish(); await expectPage(page, 1); expect(context.requests).toHaveLength(2);
});

test('selecting a displayed file cancels an already-pending ordinary first-page poll before it can replace the selected name', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold(0); context.snapshot = newerRun; context.handler = held.handle;
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click(); const request = await held.start;
  await panel(page).getByRole('button', { name: 'View read and write latency trend for database 5, file Alpha-captured-file-1', exact: true }).click();
  await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED'); await held.finish();
  await expectPage(page, 0); await expect(panel(page)).toContainText('Selected file: database ID 5, file ID 1 · Alpha-captured-file-1');
  await expect(latest(page)).toBeEnabled(); expect(context.requests).toHaveLength(2);
});

test('failed explicit latest keeps the displayed page, selection and cursor; successful retry replaces the chain', async ({ page }) => {
  await mount(page); await next(page).click(); await expectPage(page, 1); const context = contexts.get(page);
  let failed = false;
  context.handler = async (route, item) => { if (item.cursor || failed) return false; failed = true; await json(route, { detail: 'SECRET_LATEST_FAILURE' }, 503); return true; };
  await latest(page).click(); await expect(panel(page).getByRole('alert')).toContainText('The health request failed safely.');
  await expectPage(page, 1); await expect(previous(page)).toBeEnabled(); await expect(panel(page)).not.toContainText('SECRET_LATEST_FAILURE');
  context.snapshot = newerRun; await latest(page).click(); await expectPage(page, 0, 'Alpha-newest');
  await expect(first(page)).toBeDisabled(); await expect(latest(page)).toHaveCount(0);
  await next(page).click(); await expectPage(page, 1, 'Alpha-newest'); expect(context.requests.at(-1).cursor).toBe(cursor(alpha, 1, 1, newerRun));
});

for (const change of ['server', 'revision', 'window', 'database']) test(`${change} change hides captured rows and errors in the first committed render and aborts a held page`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold(1); context.handler = held.handle;
  await next(page).click(); const request = await held.start;
  const count = await page.evaluate(() => window.resourceFileCommits.length);
  context.handler = undefined;
  if (change === 'server') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  if (change === 'revision') { context.revision = 2; await page.getByRole('combobox', { name: 'Target revision', exact: true }).selectOption('2'); }
  if (change === 'window') await page.getByRole('button', { name: 'Change shared window', exact: true }).click();
  if (change === 'database') await page.getByRole('combobox', { name: 'Database scope', exact: true }).selectOption('6');
  const commit = await page.evaluate(offset => window.resourceFileCommits.slice(offset)[0], count);
  expect(commit.text).not.toContain('Alpha-captured-file-'); expect(commit.text).not.toContain('Page 2');
  await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED');
  await held.finish();
  if (change === 'database') await expect(panel(page)).toContainText('No file rows for the selected database');
  else await expectPage(page, 0, change === 'server' ? 'Beta-captured' : 'Alpha-captured');
  expect(context.requests.filter(item => item.cursor)).toHaveLength(1);
});

test('unmount aborts a held continuation and remount starts with a fresh first page', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold(1); context.handler = held.handle;
  await next(page).click(); const request = await held.start;
  await page.getByRole('button', { name: 'Unmount resources', exact: true }).click(); await expect(panel(page)).toHaveCount(0);
  await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED'); await held.finish();
  context.handler = undefined; await page.getByRole('button', { name: 'Mount resources', exact: true }).click(); await expectPage(page, 0);
  expect(context.requests.at(-1).cursor).toBeUndefined();
});

test('invalid continuation anchors, run changes and cursor loops refuse advancement while retaining valid evidence', async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  for (const mutation of [value => { value.items[0].fileId = 25; }, value => { value.nextCursor = cursor(alpha, 1, 2, newerRun); },
    value => { value.nextCursor = cursor(alpha, 1, 1); }, value => { value.nextCursor = cursor(beta, 1, 2); },
    value => { value.nextCursor = cursor(alpha, 2, 2); }, value => { value.nextCursor = cursor(alpha, 1, 1, run); value.items = []; }]) {
    context.handler = async (route, item) => { if (item.index !== 1) return false; const value = nativePage(alpha, 1, 1); mutation(value); await json(route, value); return true; };
    await next(page).click(); await expect(panel(page).getByRole('alert')).toContainText('File evidence changed or returned an invalid continuation');
    await expectPage(page, 0); await expect(next(page)).toBeEnabled();
  }
  context.handler = undefined; await next(page).click(); await expectPage(page, 1);
});

test('a previous file error is absent in the first committed render of a different server', async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => { if (item.index !== 1) return false; await json(route, {}, 409); return true; };
  await next(page).click(); await expect(panel(page).getByRole('alert')).toContainText('Health evidence changed');
  const count = await page.evaluate(() => window.resourceFileCommits.length);
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  const commit = await page.evaluate(offset => window.resourceFileCommits.slice(offset)[0], count);
  expect(commit.text).not.toContain('Health evidence changed'); expect(commit.text).not.toContain('Alpha-captured-file-');
  await expectPage(page, 0, 'Beta-captured');
});

for (const status of [401, 403]) test(`HTTP ${status} clears protected retained file evidence and permits an explicit fresh read`, async ({ page }) => {
  await mount(page); await next(page).click(); await expectPage(page, 1); const context = contexts.get(page);
  context.handler = async (route, item) => { if (item.index !== 2) return false; await json(route, { detail: 'SECRET_DENIAL' }, status); return true; };
  await next(page).click(); await expect(table(page)).toHaveCount(0); await expect(panel(page).getByRole('alert')).toBeVisible();
  await expect(panel(page)).not.toContainText('Alpha-captured-file-'); await expect(panel(page)).not.toContainText('SECRET_DENIAL');
  context.handler = undefined; await latest(page).click(); await expectPage(page, 0); expect(context.requests.at(-1).cursor).toBeUndefined();
});

for (const width of [320, 390, 768, 1440]) test(`dark retained file table remains accessible, keyboard scrollable and bounded at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page); await next(page).click(); await expectPage(page, 1);
  const scroller = panel(page).getByRole('region', { name: 'Loaded database file counters', exact: true });
  await scroller.focus(); await expect(scroller).toBeFocused(); await page.keyboard.press('ArrowRight');
  await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(13, 20, 30)');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const geometry = await panel(page).evaluate(element => ({ width: element.getBoundingClientRect().width, scroll: element.scrollWidth }));
  expect(geometry.scroll).toBeLessThanOrEqual(Math.ceil(geometry.width) + 1);
  const audit = await new AxeBuilder({ page }).include('[aria-label="Database file I/O"]').withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
  expect(audit.violations).toEqual([]);
  await previous(page).focus(); await expect(previous(page)).toBeFocused(); await expect(previous(page)).toHaveCSS('outline-style', 'solid');
  await page.keyboard.press('Enter'); await expectPage(page, 0);
  await next(page).focus(); await page.keyboard.press('Enter'); await expectPage(page, 1);
  await page.evaluate(() => { document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; element.scrollTop = 0; });
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-retained-pagers/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await panel(page).screenshot({ path: `${directory}/resource-files-${width}.png`, animations: 'disabled' });
});
