import { expect, test } from '@playwright/test';
import { revealHealthSupport } from './redesign-controls.mjs';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4432';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const from = '2026-10-02T08:00:00.123456Z', to = '2026-10-02T12:00:00.123456Z', at = '2026-10-02T10:00:00.654321Z';
const query = '000000001000' + '0'.repeat(52), plan = '000000002000' + '0'.repeat(52);
const runs = new WeakMap(), labels = { queries: 'Queries', waits: 'Waits' };
const parameters = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const controls = (page, surface) => page.getByRole('region', { name: `${labels[surface]} chart alignment`, exact: true });
const input = (page, surface) => controls(page, surface).getByRole('textbox', { name: `${labels[surface]} inspection time (UTC)`, exact: true });
const apply = (page, surface) => controls(page, surface).getByRole('button', { name: `Align ${surface} charts`, exact: true });
const clear = (page, surface) => controls(page, surface).getByRole('button', { name: `Clear ${surface} alignment`, exact: true });
const screen = (page, surface) => page.getByRole('region', { name: new RegExp(`^${labels[surface]} for `, 'u') });
const plots = (page, surface) => screen(page, surface).locator('.chart-plot-scroll svg');
const baseSelection = surface => surface === 'queries'
  ? { queryFingerprint: query, planFingerprint: plan, source: 'query_store', queryMetric: 'cpu', planWaitCategory: '3' }
  : { waitCategory: '3', sessionWaitCategory: 'Memory' };
const href = (surface, extra = {}) => '/#/' + surface + '?' + new URLSearchParams({ target: alpha, range: 'custom', from, to,
  databaseId: '5', compare: '1', ...baseSelection(surface), ...extra });
const sourcePath = surface => surface === 'queries' ? 'query-performance/top' : 'activity/waits/trend';

// Finite APIs implemented by the existing synthetic built-assets fixture. No
// external traffic, native database connection or unregistered mutation may run.
function registered(url) {
  const params = url.searchParams;
  if ([...new Set(params.keys())].some(key => params.getAll(key).length !== 1)) return false;
  const keys = allowed => [...params.keys()].every(key => allowed.includes(key));
  if (url.pathname === '/api/v1/me') return keys(['targetId']) && (!params.has('targetId') || [alpha, beta].includes(params.get('targetId')));
  if (url.pathname === '/api/v1/overview') return keys(['fromUtc', 'toUtc', 'useLatestStatus', 'targetId']);
  if (url.pathname === '/api/v1/observation-targets') return keys(['limit', 'cursor']);
  const match = /^\/api\/v1\/observation-targets\/([^/]+)(?:\/(.+))?$/u.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1])) return false;
  const suffix = match[2];
  if (!suffix) return keys([]);
  if (suffix === 'health/databases') return keys(['limit', 'cursor']);
  if (suffix === 'health') return keys([]);
  if (suffix === 'health/files') return keys(['limit', 'cursor']);
  if (suffix === 'health/server-configuration-snapshot') return keys(['limit', 'cursor']);
  if (['health/database-option-changes', 'health/server-configuration-changes'].includes(suffix)) return keys(['fromUtc', 'toUtc', 'limit', 'cursor']);
  if (suffix === 'activity/blocking/current') return keys(['limit']) && params.get('limit') === '256';
  if (suffix === 'deadlocks') return keys(['fromUtc', 'toUtc', 'limit', 'cursor']);
  if (['alerts/history', 'lifecycle/history'].includes(suffix)) return keys(['targetRevision', 'fromUtc', 'toUtc', 'limit', 'cursor']);
  if (suffix === 'sql-agent/failures') return keys(['fromUtc', 'toUtc', 'limit', 'cursor']);
  if (suffix === 'analytics/baselines') return keys(['metricKey', 'fromUtc', 'toUtc', 'limit']);
  if (suffix === 'analytics/sql-baselines') return keys(['targetRevision', 'metricKey', 'fromUtc', 'toUtc', 'catalogVersion', 'generation']);
  if (suffix === 'activity/waits/history') return keys(['limit', 'fromUtc', 'toUtc', 'cursor']);
  if (suffix === 'activity/waits') return keys(['limit', 'cursor']) && params.get('limit') === '25';
  if (suffix === 'activity/waits/trend') return keys(['fromUtc', 'toUtc']);
  if (suffix === 'activity/live') return keys(['includeIdle', 'includeSystem', 'sort', 'descending', 'databaseId', 'snapshot', 'cursor']);
  if (suffix === 'activity/live/history') return keys(['from', 'to']);
  if (suffix === 'query-performance/status') return keys(['fromUtc', 'toUtc']);
  if (suffix === 'query-performance/top') return keys(['fromUtc', 'toUtc', 'metric', 'limit', 'cursor']);
  if (suffix === 'query-performance/regressions') return keys(['fromUtc', 'toUtc', 'limit']);
  if (suffix === 'query-performance/waits/top') return keys(['fromUtc', 'toUtc', 'limit', 'category']);
  if (/^query-performance\/databases\/[56]\/plans\/[0-9a-f]{64}$/u.test(suffix)) return keys(['queryFingerprint']);
  if (/^query-performance\/databases\/[56]\/history\/[0-9a-f]{64}$/u.test(suffix)) return keys(['fromUtc', 'toUtc', 'limit', 'cursor']);
  if (/^query-performance\/databases\/[56]\/history\/[0-9a-f]{64}\/plans\/[0-9a-f]{64}\/trend$/u.test(suffix)) return keys(['fromUtc', 'toUtc']);
  return false;
}
test.beforeEach(async ({ page }) => {
  const run = { requests: [], pending: new Set(), errors: [], denial: undefined, held: undefined }; runs.set(page, run);
  await page.clock.setFixedTime(new Date('2026-10-03T12:01:00Z'));
  await page.context().addCookies(['dashboardRefactor', 'nativeInsightsFixture', 'queryRowTrendFixture', 'databaseScopeFixture'].map(name => ({ name, value: '1', url: origin }))
    .concat([{ name: 'chartReferenceRole', value: 'viewer', url: origin }]));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  page.on('pageerror', error => run.errors.push(error.message));
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) { run.requests.push({ method: request.method(), url: request.url() }); run.pending.add(request); } });
  page.on('requestfinished', request => run.pending.delete(request)); page.on('requestfailed', request => run.pending.delete(request));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin === origin && !/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (url.origin !== origin || request.method() !== 'GET' || !registered(url)) {
      run.errors.push(`Unregistered ${request.method()} ${url.href}`); return route.abort();
    }
    if (run.held && await run.held(route, url)) return;
    if (run.denial && url.pathname === `/api/v1/observation-targets/${alpha}/${sourcePath(run.denial.surface)}`)
      return route.fulfill({ status: run.denial.status, contentType: 'application/json', json: { detail: 'SYNTHETIC_SECRET_NOT_FOR_DISPLAY' } });
    return route.continue();
  });
});
test.afterEach(async ({ page }) => {
  expect(runs.get(page).errors).toEqual([]);
  expect(runs.get(page).requests.every(request => request.method === 'GET'), 'Inspection must remain read-only').toBe(true);
});
async function settled(page) {
  await expect.poll(() => runs.get(page).pending.size).toBe(0);
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
}
async function open(page, surface, extra = {}) {
  await page.goto(href(surface, extra)); await expect(input(page, surface)).toBeVisible();
  await expect(plots(page, surface).first()).toBeVisible(); await settled(page);
  if (surface === 'queries') await expect(plots(page, surface)).toHaveCount(3);
}
function assertScope(page, surface, overrides = {}) {
  expect(Object.fromEntries(parameters(page))).toMatchObject({ target: alpha, range: 'custom', from, to, databaseId: '5', compare: '1', ...baseSelection(surface), ...overrides });
}
async function aligned(page, surface, value = at) {
  await expect(controls(page, surface).locator('time')).toHaveAttribute('datetime', value);
  expect(parameters(page).get('chartAt')).toBe(value); assertScope(page, surface);
  expect(await plots(page, surface).count()).toBeGreaterThanOrEqual(surface === 'queries' ? 3 : 1);
  for (const plot of await plots(page, surface).all()) await expect(plot.locator('.chart-crosshair line')).toHaveCount(1);
}
async function align(page, surface) {
  await input(page, surface).fill(at); await input(page, surface).press('Tab'); await expect(apply(page, surface)).toBeFocused();
  await page.keyboard.press('Enter'); await expect(apply(page, surface)).toBeFocused(); await aligned(page, surface); await settled(page);
}

for (const surface of ['queries', 'waits']) test(`${labels[surface]} keyboard alignment restores exact microseconds and preserves window and native selections`, async ({ page }) => {
  await open(page, surface); const count = runs.get(page).requests.length;
  await align(page, surface); expect(runs.get(page).requests).toHaveLength(count);
  await page.reload(); await expect(input(page, surface)).toHaveValue(at); await aligned(page, surface); await settled(page);
  const restoredReads = runs.get(page).requests.length;
  await clear(page, surface).focus(); await page.keyboard.press('Enter');
  await expect(controls(page, surface).locator('time')).toHaveCount(0); await expect(screen(page, surface).locator('.chart-crosshair')).toHaveCount(0);
  expect(parameters(page).has('chartAt')).toBe(false); assertScope(page, surface); await settled(page);
  expect(runs.get(page).requests).toHaveLength(restoredReads);
  await page.goBack(); await aligned(page, surface); await settled(page); expect(runs.get(page).requests).toHaveLength(restoredReads);
  await page.goForward(); await expect(controls(page, surface).locator('time')).toHaveCount(0); assertScope(page, surface);
});

for (const surface of ['queries', 'waits']) test(`${labels[surface]} invalid, repeated and exclusive-end selections stay explicit until repaired`, async ({ page }) => {
  await open(page, surface, { chartAt: at }); await aligned(page, surface); const count = runs.get(page).requests.length;
  for (const invalid of [to, '2026-10-02T10:00:00.1234567Z', '2026-10-02T10:00:00+00:00', 'invalid']) {
    await input(page, surface).fill(invalid); await input(page, surface).press('Enter');
    await expect(controls(page, surface).getByRole('alert')).toBeVisible(); await aligned(page, surface);
    await expect(input(page, surface)).toHaveAttribute('aria-invalid', 'true');
  }
  await page.evaluate(next => { location.hash = next; }, href(surface, { chartAt: at }).slice(1) + '&chartAt=bad');
  await expect(controls(page, surface)).toContainText('invalid or repeated');
  await expect(screen(page, surface).locator('.chart-crosshair')).toHaveCount(0);
  expect(parameters(page).getAll('chartAt')).toEqual([at, 'bad']); assertScope(page, surface);
  await clear(page, surface).click(); await expect(controls(page, surface).getByRole('alert')).toHaveCount(0);
  await page.evaluate(next => { location.hash = next; }, href(surface, { chartAt: to }).slice(1));
  await expect(input(page, surface)).toHaveValue(to); await expect(controls(page, surface)).toContainText('outside the loaded window');
  await expect(screen(page, surface).locator('.chart-crosshair')).toHaveCount(0);
  await align(page, surface); expect(runs.get(page).requests).toHaveLength(count);
});

for (const surface of ['queries', 'waits']) test(`${labels[surface]} pointer inspection aligns owning charts temporarily and target changes cancel it`, async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 }); await open(page, surface, { chartAt: at });
  const plot = plots(page, surface).first(); await plot.scrollIntoViewIfNeeded(); const box = await plot.boundingBox();
  const before = page.url(); await page.mouse.move(box.x + box.width * .7, box.y + box.height * .4);
  await expect(controls(page, surface).locator('time')).not.toHaveAttribute('datetime', at);
  const hover = await controls(page, surface).locator('time').getAttribute('datetime'); expect(hover).toBeTruthy();
  for (const chart of await plots(page, surface).all()) await expect(chart.locator('.chart-crosshair line')).toHaveCount(1);
  expect(page.url()).toBe(before);
  await page.mouse.move(0, 0); await aligned(page, surface);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(beta);
  await expect(input(page, surface)).toBeVisible(); await expect(controls(page, surface).locator('time')).toHaveCount(0);
  expect(parameters(page).get('target')).toBe(beta); expect(parameters(page).has('chartAt')).toBe(false);
  expect(parameters(page).get('from')).toBe(from); expect(parameters(page).get('to')).toBe(to);
  await expect(screen(page, surface).locator('.chart-crosshair')).toHaveCount(0);
});

for (const surface of ['queries', 'waits']) test(`${labels[surface]} same-window query or wait filters retain alignment while a new window drops it`, async ({ page }) => {
  await open(page, surface, { chartAt: at }); await aligned(page, surface);
  if (surface === 'queries') {
    await screen(page, surface).getByRole('combobox', { name: 'Source', exact: true }).selectOption('query_store');
    expect(parameters(page).get('querySource')).toBe('query_store');
    await page.getByRole('tab', { name: /^Regressed/u }).click();
    expect(parameters(page).get('queryView')).toBe('regressed');
  } else {
    await screen(page, surface).getByRole('combobox', { name: 'Query Store category', exact: true }).selectOption('6');
    await screen(page, surface).getByRole('combobox', { name: 'Session wait category', exact: true }).selectOption('Log');
    expect(parameters(page).get('waitCategory')).toBe('6'); expect(parameters(page).get('sessionWaitCategory')).toBe('Log');
  }
  await expect(controls(page, surface).locator('time')).toHaveAttribute('datetime', at); expect(parameters(page).get('chartAt')).toBe(at);
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('1h');
  await expect(input(page, surface)).toBeVisible(); await expect(controls(page, surface).locator('time')).toHaveCount(0);
  expect(parameters(page).has('chartAt')).toBe(false); expect(parameters(page).get('range')).not.toBe('custom');
});

test('Same-window navigation and a bookmarked Fleet view carry exact inspection across diagnostics and Dashboard tabs', async ({ page }) => {
  await open(page, 'queries', { chartAt: at }); await aligned(page, 'queries');
  const mainNavigation = page.locator('nav');
  await mainNavigation.getByRole('link', { name: 'Waits', exact: true }).click();
  await expect(input(page, 'waits')).toBeVisible(); await expect(controls(page, 'waits').locator('time')).toHaveAttribute('datetime', at);
  expect(parameters(page).get('chartAt')).toBe(at);
  await page.goto(href('queries', { chartAt: at }).replace('/#/queries?', '/#/overview?'));
  const fleet = page.getByRole('region', { name: 'Fleet chart alignment', exact: true });
  await expect(fleet.locator('time')).toHaveAttribute('datetime', at);
  const server = page.getByRole('region', { name: 'Server health grid', exact: true }).getByRole('link', { name: 'SYNTHETIC SQL 01', exact: true });
  await expect(server).toHaveCount(1); await server.click();
  await revealHealthSupport(page);
  const dashboard = page.getByRole('region', { name: 'Dashboard chart alignment', exact: true });
  await expect(dashboard.locator('time')).toHaveAttribute('datetime', at);
  const tabs = page.getByRole('tablist', { name: 'Server summary sections', exact: true });
  await tabs.getByRole('tab', { name: 'Databases', exact: true }).click();
  expect(parameters(page).get('chartAt')).toBe(at); await expect(dashboard).toBeHidden();
  await tabs.getByRole('tab', { name: 'Dashboard', exact: true }).click();
  await expect(dashboard.locator('time')).toHaveAttribute('datetime', at); expect(parameters(page).get('chartAt')).toBe(at);
  for (const key of ['target', 'range', 'from', 'to', 'databaseId', 'compare'])
    expect(parameters(page).get(key)).toBe(new URLSearchParams(href('queries').split('?')[1]).get(key));
  await mainNavigation.getByRole('link', { name: 'Queries', exact: true }).click();
  await expect(input(page, 'queries')).toBeVisible(); await expect(controls(page, 'queries').locator('time')).toHaveAttribute('datetime', at);
  await settled(page);
});

test('Queries shared alignment takes precedence without erasing an independent plan-only inspection', async ({ page }) => {
  const planAt = '2026-10-02T10:30:00.111222Z';
  await open(page, 'queries', { chartAt: at, planInspectUtc: planAt }); await aligned(page, 'queries');
  const count = runs.get(page).requests.length;
  expect(parameters(page).get('planInspectUtc')).toBe(planAt);
  await clear(page, 'queries').focus(); await page.keyboard.press('Enter'); await settled(page);
  expect(parameters(page).has('chartAt')).toBe(false); expect(parameters(page).get('planInspectUtc')).toBe(planAt);
  await expect(controls(page, 'queries').locator('time')).toHaveCount(0);
  await expect(plots(page, 'queries').first().locator('.chart-crosshair')).toHaveCount(0);
  const planCharts = screen(page, 'queries').getByRole('region', { name: 'Selected plan delta trend', exact: true }).locator('.chart-plot-scroll svg');
  await expect(planCharts).toHaveCount(2);
  for (const chart of await planCharts.all()) await expect(chart.locator('.chart-crosshair line')).toHaveCount(1);
  expect(runs.get(page).requests).toHaveLength(count); assertScope(page, 'queries');
});

test('Queries ranking previews use the shared exact inspection and clear it without additional source reads', async ({ page }) => {
  await open(page, 'queries', { chartAt: at }); await aligned(page, 'queries');
  const preview = screen(page, 'queries').locator('.ranking-trend').first();
  await preview.scrollIntoViewIfNeeded(); await expect(preview.locator('svg')).toBeVisible(); await settled(page);
  await expect(preview.locator('.ranking-trend-crosshair line')).toHaveCount(1);
  await expect(preview.locator('.ranking-trend-crosshair title')).toHaveText(`Shared chart inspection: ${at}. Missing observations remain unavailable.`);
  const count = runs.get(page).requests.length;
  await clear(page, 'queries').focus(); await page.keyboard.press('Enter'); await settled(page);
  await expect(screen(page, 'queries').locator('.ranking-trend-crosshair')).toHaveCount(0);
  expect(parameters(page).has('chartAt')).toBe(false); assertScope(page, 'queries');
  expect(runs.get(page).requests).toHaveLength(count);
});

for (const surface of ['queries', 'waits']) test(`${labels[surface]} a cancelled previous-target denial cannot purge a newly aligned target`, async ({ page }) => {
  await open(page, surface, { chartAt: at }); await aligned(page, surface);
  const run = runs.get(page); let heldRoute;
  run.held = async (route, url) => {
    if (heldRoute || url.pathname !== `/api/v1/observation-targets/${alpha}/${sourcePath(surface)}`) return false;
    heldRoute = route; return true;
  };
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect.poll(() => Boolean(heldRoute)).toBe(true);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(beta);
  await expect(input(page, surface)).toBeVisible(); expect(parameters(page).get('target')).toBe(beta);
  run.held = undefined;
  try { await heldRoute.fulfill({ status: 403, contentType: 'application/json', json: { detail: 'SYNTHETIC_SECRET_NOT_FOR_DISPLAY' } }); }
  catch (failure) { expect(String(failure)).toMatch(/closed|disposed|cancelled|canceled|aborted|interception/u); }
  await input(page, surface).fill(at); await input(page, surface).press('Enter'); await settled(page);
  await expect(controls(page, surface).locator('time')).toHaveAttribute('datetime', at);
  await expect(page.getByRole('button', { name: surface === 'queries' ? 'Retry query access' : 'Retry wait trend', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('SYNTHETIC_SECRET_NOT_FOR_DISPLAY');
  expect(parameters(page).get('target')).toBe(beta); expect(parameters(page).get('chartAt')).toBe(at);
});

for (const [surface, status] of [['queries', 403], ['waits', 401]]) test(`${labels[surface]} ${status} clears alignment evidence until explicit native access retry`, async ({ page }) => {
  await open(page, surface, { chartAt: at }); await aligned(page, surface);
  runs.get(page).denial = { surface, status };
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  const retry = page.getByRole('button', { name: surface === 'queries' ? 'Retry query access' : 'Retry wait trend', exact: true });
  await expect(retry).toBeVisible(); await expect(controls(page, surface)).toHaveCount(0);
  await expect(page.locator('.chart-crosshair')).toHaveCount(0); await expect(page.locator('body')).not.toContainText('SYNTHETIC_SECRET_NOT_FOR_DISPLAY');
  expect(parameters(page).get('chartAt')).toBe(at); assertScope(page, surface);
  runs.get(page).denial = undefined; await retry.click(); await expect(input(page, surface)).toBeVisible(); await aligned(page, surface);
});

for (const surface of ['queries', 'waits']) for (const mode of ['dark', 'light-preference', 'forced'])
  test(`${labels[surface]} alignment remains accessible under ${mode} preference at mobile and desktop widths`, async ({ page }, info) => {
    const widths = mode === 'dark' ? [320, 390, 768, 1440] : [320, 1440];
    await page.emulateMedia({ forcedColors: mode === 'forced' ? 'active' : 'none', colorScheme: mode === 'light-preference' ? 'light' : 'dark' });
    const directory = fileURLToPath(new URL('../../artifacts/dashboard-diagnostic-alignment-2026-10-04/screenshots/', import.meta.url));
    await mkdir(directory, { recursive: true });
    for (const width of widths) {
      await page.setViewportSize({ width, height: 1000 }); await open(page, surface); await align(page, surface);
      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(0);
      // The product currently provides a dark palette. Light OS preference is
      // resilience coverage, not a claim that a separate light theme exists.
      // Forced colors computes color-scheme to light dark per the platform:
      // https://www.w3.org/TR/css-color-adjust-1/#forced-colors-properties
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe(mode === 'forced' ? 'light dark' : 'dark');
      for (const control of await controls(page, surface).locator('button,input').all()) {
        const box = await control.boundingBox(); expect(box.width).toBeGreaterThanOrEqual(44); expect(box.height).toBeGreaterThanOrEqual(44);
      }
      const focus = await apply(page, surface).evaluate(element => {
        const style = getComputedStyle(element), box = element.getBoundingClientRect(), owner = element.closest('.chart-alignment'), parent = owner.getBoundingClientRect();
        const width = parseFloat(style.outlineWidth), offset = parseFloat(style.outlineOffset), reach = width + offset;
        return { visible: element.matches(':focus-visible'), width, style: style.outlineStyle, color: style.outlineColor, fill: style.backgroundColor,
          paint: { left: box.left - reach, top: box.top - reach, right: box.right + reach, bottom: box.bottom + reach },
          owner: { left: parent.left + owner.clientLeft, top: parent.top + owner.clientTop,
            right: parent.left + owner.clientLeft + owner.clientWidth, bottom: parent.top + owner.clientTop + owner.clientHeight } };
      });
      expect(focus.visible).toBe(true); expect(focus.width).toBeGreaterThanOrEqual(2); expect(focus.style).not.toBe('none'); expect(focus.color).not.toBe(focus.fill);
      expect(focus.paint.left).toBeGreaterThanOrEqual(focus.owner.left); expect(focus.paint.right).toBeLessThanOrEqual(focus.owner.right);
      expect(focus.paint.top).toBeGreaterThanOrEqual(focus.owner.top); expect(focus.paint.bottom).toBeLessThanOrEqual(focus.owner.bottom);
      const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
      await info.attach(`${surface}-${mode}-${width}-axe`, { body: JSON.stringify(violations), contentType: 'application/json' }); expect(violations).toEqual([]);
      await info.attach(`${surface}-${mode}-${width}-focus`, { body: JSON.stringify(focus), contentType: 'application/json' });
      await page.evaluate(() => {
        document.querySelectorAll('.chart-plot-scroll').forEach(node => { node.scrollLeft = 0; });
        scrollTo({ top: 0, left: 0, behavior: 'instant' });
      });
      await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
      await expect(apply(page, surface)).toBeFocused();
      const path = `${directory}/${surface}-${mode}-${width}.png`; await page.screenshot({ path, fullPage: true, animations: 'disabled' });
      await info.attach(`${surface}-${mode}-${width}`, { path, contentType: 'image/png' });
    }
  });
