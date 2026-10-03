import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, firstWindow, nativePle, nativeFileTrend, nativeOverview } from './diagnosticWorkflowFixture.mjs';

const origin = 'http://127.0.0.1:4328';
const contexts = new WeakMap();
const ple = page => page.getByRole('region', { name: 'Per-NUMA page life expectancy', exact: true });
const file = page => page.getByRole('region', { name: 'Selected file latency history', exact: true });
const overview = page => page.getByRole('region', { name: 'Overview revision evidence', exact: true });
const selector = page => ple(page).getByRole('combobox', { name: 'NUMA node', exact: true });
const refresh = page => page.getByRole('button', { name: 'Refresh diagnostics', exact: true });
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function receipt(item, value) {
  return item.kind === 'ple' ? nativePle(item, value, item.target === beta ? [2, 3] : [0, 1])
    : item.kind === 'file' ? nativeFileTrend(item, value) : nativeOverview(item, value);
}
function hold() {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, value = 111) {
    announce(route.request()); await gate;
    try { await json(route, receipt(item, value)); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { reads: [], unexpected: [], value: 111, handler: undefined }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') return reject();
    let kind, target, expectedKeys;
    if (url.pathname === '/api/v1/overview') { kind = 'overview'; target = url.searchParams.get('targetId'); expectedKeys = ['fromUtc', 'toUtc', 'targetId', 'useLatestStatus'];
      if (url.searchParams.get('useLatestStatus') !== 'true') return reject(); }
    else {
      const match = /^\/api\/v1\/observation-targets\/([^/]+)\/resources\/(buffer-nodes\/ple|files\/7\/1\/trend)$/.exec(url.pathname);
      if (!match) return reject();
      target = match[1]; kind = match[2] === 'buffer-nodes/ple' ? 'ple' : 'file'; expectedKeys = ['fromUtc', 'toUtc'];
      if (kind === 'ple' && url.searchParams.has('nodeId')) expectedKeys.push('nodeId');
    }
    if (![alpha, beta].includes(target) || [...url.searchParams.keys()].sort().join('|') !== expectedKeys.sort().join('|')) return reject();
    const item = { kind, target, fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'),
      nodeId: url.searchParams.has('nodeId') ? Number(url.searchParams.get('nodeId')) : null };
    if (!Number.isFinite(Date.parse(item.fromUtc)) || Date.parse(item.fromUtc) >= Date.parse(item.toUtc) ||
        item.nodeId !== null && (!Number.isInteger(item.nodeId) || item.nodeId < 0 || item.nodeId > 1023)) return reject();
    context.reads.push(item);
    if (context.handler && await context.handler(route, item)) return;
    await json(route, receipt(item, context.value));
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Only registered native local APIs and no runtime errors').toEqual([]);
});
async function mount(page) {
  await page.goto('/component-tests/diagnostic-workflow.html');
  await expect(selector(page)).toHaveValue('0'); await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2);
  await expect(file(page).locator('svg.overview-chart')).toHaveCount(1);
  await expect(page.getByRole('status', { name: 'Overview sample', exact: true })).toHaveText('111');
}
async function firstCommit(page, offset) { return page.evaluate(value => window.diagnosticWorkflowCommits.slice(value)[0], offset); }
async function commitCount(page) { return page.evaluate(() => window.diagnosticWorkflowCommits.length); }
async function heldAll(page) {
  const gates = { overview: hold(), ple: hold(), file: hold() };
  contexts.get(page).handler = (route, item) => gates[item.kind].handle(route, item);
  await refresh(page).click();
  const requests = await Promise.all(Object.values(gates).map(gate => gate.started));
  return { gates, requests, async finish() { await Promise.all(Object.values(gates).map(gate => gate.finish())); } };
}

test('keyboard NUMA changes keep the exact select element and focus while selected-node evidence reloads', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold();
  context.handler = (route, item) => item.kind === 'ple' && item.nodeId === 1 ? held.handle(route, item, 222) : false;
  const element = await selector(page).elementHandle(); await selector(page).focus();
  await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter'); await held.started;
  await expect(selector(page)).toHaveValue('1'); await expect(selector(page)).toBeFocused();
  expect(await selector(page).evaluate((next, previous) => next === previous, element)).toBe(true);
  await expect(ple(page)).toContainText('NUMA options are retained from a previous read');
  await expect(ple(page).locator('svg')).toHaveCount(0); await expect(selector(page).locator('option')).toHaveCount(2);
  await held.finish(); await expect(selector(page)).toBeFocused(); await expect(ple(page)).toContainText('Buffer Node 1 PLE');
  await expect(ple(page)).not.toContainText('NUMA options are retained');
});

test('idle refresh preserves keyboard selector identity and safe inventory but hides the prior PLE charts', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold();
  context.handler = (route, item) => item.kind === 'ple' ? held.handle(route, item, 222) : false;
  const element = await selector(page).elementHandle(); await selector(page).focus();
  await refresh(page).evaluate(button => button.click()); await held.started;
  await expect(selector(page)).toBeFocused(); expect(await selector(page).evaluate((next, previous) => next === previous, element)).toBe(true);
  await expect(ple(page).locator('svg')).toHaveCount(0); await expect(selector(page).locator('option')).toHaveCount(2);
  await held.finish(); await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2); await expect(selector(page)).toBeFocused();
});

for (const change of ['revision', 'server', 'window']) test(`${change} first render hides old Overview, PLE and file receipts and aborts held reads`, async ({ page }) => {
  await mount(page); const pending = await heldAll(page), context = contexts.get(page);
  await expect(overview(page)).toContainText('Refreshing the same diagnostic context');
  const offset = await commitCount(page); context.handler = undefined; context.value = 222;
  if (change === 'revision') await page.getByRole('combobox', { name: 'Target revision', exact: true }).selectOption('2');
  if (change === 'server') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  if (change === 'window') await page.getByRole('button', { name: 'Change window', exact: true }).click();
  const commit = await firstCommit(page, offset); expect(commit.charts).toBe(0); expect(commit.options).toEqual(['Choose a NUMA node']); expect(commit.errors).toBe(0);
  for (const request of pending.requests) await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED');
  await pending.finish(); await expect(page.getByRole('status', { name: 'Overview sample', exact: true })).toHaveText('222');
  await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2); await expect(file(page).locator('svg.overview-chart')).toHaveCount(1);
  await expect(ple(page)).not.toContainText('111 s'); await expect(file(page)).not.toContainText('111 ms/op');
});

test('an A→B→A server round trip starts fresh receipts and cannot revive the prior inventory or held reads', async ({ page }) => {
  await mount(page); const pending = await heldAll(page), context = contexts.get(page);
  context.handler = undefined; context.value = 222;
  const target = page.getByRole('combobox', { name: 'Selected server', exact: true });
  await target.selectOption(beta); const offset = await commitCount(page); await target.selectOption(alpha);
  const commit = await firstCommit(page, offset); expect(JSON.parse(commit.context)[0]).toBe(alpha);
  expect(commit.charts).toBe(0); expect(commit.options).toEqual(['Choose a NUMA node']); expect(commit.errors).toBe(0);
  for (const request of pending.requests) await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED');
  await pending.finish(); await expect(page.getByRole('status', { name: 'Overview sample', exact: true })).toHaveText('222');
  await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2); await expect(file(page).locator('svg.overview-chart')).toHaveCount(1);
  await expect(ple(page)).not.toContainText('111 s'); await expect(file(page)).not.toContainText('111 ms/op');
});

test('a transient PLE failure keeps safe options without old charts, and revision change hides the old error immediately', async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => { if (item.kind !== 'ple') return false; await json(route, { detail: 'SECRET_PROVIDER' }, 503); return true; };
  await selector(page).focus(); await refresh(page).evaluate(button => button.click());
  await expect(ple(page).getByRole('alert')).toBeVisible(); await expect(selector(page)).toBeFocused();
  await expect(selector(page).locator('option')).toHaveCount(2); await expect(ple(page).locator('svg')).toHaveCount(0);
  await expect(selector(page)).toHaveAccessibleDescription(/Selected-node evidence is unavailable/);
  const offset = await commitCount(page); context.handler = undefined; context.value = 222;
  await page.getByRole('combobox', { name: 'Target revision', exact: true }).selectOption('2');
  const commit = await firstCommit(page, offset); expect(commit.errors).toBe(0); expect(commit.charts).toBe(0); expect(commit.options).toEqual(['Choose a NUMA node']);
  await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2);
});

test('a late previous-period read cannot refill a revised Overview with its old comparison', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = hold();
  context.handler = (route, item) => item.kind === 'overview' && Date.parse(item.fromUtc) < Date.parse(firstWindow.fromUtc)
    ? held.handle(route, item, 999) : false;
  await page.getByRole('checkbox', { name: 'Compare previous', exact: true }).check(); const request = await held.started;
  const offset = await commitCount(page); context.handler = undefined; context.value = 222;
  await page.getByRole('combobox', { name: 'Target revision', exact: true }).selectOption('2');
  expect((await firstCommit(page, offset)).charts).toBe(0);
  await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED'); await held.finish();
  await expect(page.getByRole('status', { name: 'Overview sample', exact: true })).toHaveText('222');
  await expect(page.getByRole('status', { name: 'Previous sample', exact: true })).toHaveText('222');
  await expect(overview(page)).not.toContainText('999');
});

for (const status of [401, 403]) test(`HTTP ${status} removes protected NUMA inventory, keeps a named selector and permits a fresh read`, async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => { if (item.kind !== 'ple') return false; await json(route, { detail: 'SECRET_DENIAL' }, status); return true; };
  await refresh(page).click(); await expect(ple(page).getByRole('alert')).toBeVisible();
  await expect(selector(page).locator('option')).toHaveCount(1); await expect(selector(page)).toHaveValue('');
  await expect(ple(page)).not.toContainText('2 samples'); await expect(ple(page)).not.toContainText('SECRET_DENIAL'); await expect(ple(page).locator('svg')).toHaveCount(0);
  context.handler = undefined; await refresh(page).click(); await expect(selector(page)).toHaveValue('0'); await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2);
});

test('missing and invalid NUMA selections do not display another node as the selected evidence', async ({ page }) => {
  await mount(page); await page.getByRole('button', { name: 'Request missing node', exact: true }).click();
  await expect(selector(page)).toHaveValue('9'); await expect(ple(page)).toContainText('Requested NUMA node 9 is not in the loaded node inventory');
  await expect(ple(page).locator('svg.overview-chart')).toHaveCount(1); await expect(ple(page)).not.toContainText('Buffer Node 0 PLE');
  await page.getByRole('button', { name: 'Request invalid node', exact: true }).click();
  await expect(selector(page)).toHaveValue(''); await expect(ple(page)).toContainText('Requested NUMA selection is invalid');
  await expect(ple(page).locator('svg.overview-chart')).toHaveCount(1); await expect(ple(page)).not.toContainText('Buffer Node 0 PLE');
  await selector(page).selectOption('1'); await expect(ple(page)).toContainText('Buffer Node 1 PLE');
});

test('unmount aborts held resource reads; remount starts fresh without publishing abandoned results', async ({ page }) => {
  await mount(page); const context = contexts.get(page), gates = { ple: hold(), file: hold() };
  context.handler = (route, item) => item.kind !== 'overview' ? gates[item.kind].handle(route, item, 999) : false;
  await refresh(page).click(); const requests = await Promise.all(Object.values(gates).map(gate => gate.started));
  await page.getByRole('button', { name: 'Unmount diagnostic panels', exact: true }).click();
  await expect(ple(page)).toHaveCount(0); for (const request of requests) await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED');
  await Promise.all(Object.values(gates).map(gate => gate.finish())); context.handler = undefined; context.value = 222;
  await page.getByRole('button', { name: 'Mount diagnostic panels', exact: true }).click();
  await expect(ple(page).locator('svg.overview-chart')).toHaveCount(2); await expect(file(page).locator('svg.overview-chart')).toHaveCount(1);
  await expect(ple(page)).not.toContainText('999 s'); await expect(file(page)).not.toContainText('999 ms/op');
});

for (const width of [320, 390, 768, 1440]) test(`dark diagnostic controls and evidence stay named, keyboard usable and bounded at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page);
  await selector(page).focus(); await expect(selector(page)).toBeFocused(); await expect(selector(page)).toHaveCSS('outline-width', '3px');
  await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter'); await expect(selector(page)).toHaveValue('1'); await expect(selector(page)).toBeFocused();
  await expect(ple(page)).toContainText('Buffer Node 1 PLE');
  const details = ple(page).locator('details.chart-values').first(); await details.locator('summary').focus(); await page.keyboard.press('Enter');
  const scroller = details.getByRole('region', { name: 'Chart observation values', exact: true }); await scroller.focus(); await expect(scroller).toBeFocused();
  const canScroll = await scroller.evaluate(element => element.scrollWidth > element.clientWidth);
  await page.keyboard.press('ArrowRight'); if (canScroll) await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  for (const component of [ple(page), file(page), overview(page)]) {
    const geometry = await component.evaluate(element => ({ width: element.getBoundingClientRect().width, scroll: element.scrollWidth }));
    expect(geometry.scroll).toBeLessThanOrEqual(Math.ceil(geometry.width) + 1);
  }
  await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(13, 20, 30)');
  const audit = await new AxeBuilder({ page }).include('main').withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze(); expect(audit.violations).toEqual([]);
  await page.evaluate(() => { document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; element.scrollTop = 0; });
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-workspace-triage/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.screenshot({ path: `${directory}/diagnostic-workflow-${width}.png`, animations: 'disabled', fullPage: true });
});
