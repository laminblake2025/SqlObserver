import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import {cpuCaptureFixtureBinding} from '../tools/cpu-capture-window-history-fixture.mjs';

const origin = 'http://127.0.0.1:4334';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const capturedRun = 'abababab-abab-4bab-8bab-000000000001', latestRun = 'abababab-abab-4bab-8bab-000000000002';
const cursors = [undefined, 'bmF0aXZlOnZvbHVtZXM6Mg==', 'bmF0aXZlOnZvbHVtZXM6Mw=='];
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'SQL-reported target-host volume capacity', exact: true });
const table = page => panel(page).getByRole('table');
const action = (page, name) => panel(page).getByRole('button', { name, exact: true });
const next = page => action(page, 'Next volume page');
const latest = page => action(page, 'Return to latest volume snapshot');
const scope = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const supporting = new Set(['health/databases', 'health/files', 'analytics/baselines', 'analytics/sql-baselines', 'resources/buffer-nodes/ple']);
const href = id => '/#/resources?' + new URLSearchParams({ target: id, range: '1h', databaseId: '5', compare: '1' });
function nativePage(id, run, index = 0) {
  return { instanceId: id, targetRevision: 1, snapshotRunId: run, state: 'current', reason: 'completed',
    completedAtUtc: '2026-10-02T10:00:01.123456Z', lossKind: 'none', minimumLostItems: 0, hasVisibilityGap: false,
    repositoryTimeUtc: '2026-10-02T10:00:02.123456Z', nextCursor: cursors[index + 1] ?? null,
    items: Array.from({ length: index === 2 ? 4 : 25 }, (_, offset) => ({
      volumeKey: id.slice(-8) + (index * 25 + offset + 1).toString(16).padStart(8, '0') + '0'.repeat(48),
      identityKind: 'volume_id', mappedFileCount: 1, totalBytes: '107374182400',
      availableBytes: run === latestRun ? '53687091200' : ['96636764160', '85899345920', '75161927680'][index],
      observedAtUtc: '2026-10-02T10:00:00.123456Z', trendState: index === 1 ? 'not_depleting' : 'estimated',
      trendSampleDays: 14, daysUntilFull: index === 1 ? null : index === 2 ? 8.2 : 3.2,
    })) };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
test.beforeEach(async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-02T12:00:00Z') });
  const context = { requests: [], supporting: [], unexpected: [], run: capturedRun, expired: false, handler: undefined };
  contexts.set(page, context); page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'resourceInspectionFixture', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') return reject();
    if (url.pathname === '/api/v1/me') {
      const targetId = url.searchParams.get('targetId');
      if (targetId !== null && ![alpha, beta].includes(targetId)) return reject();
      return json(route, { active: true, targetId, grantedRoles: ['Viewer'], allTargetRoles: [], targetRoles: targetId ? ['Viewer'] : [] });
    }
    if (url.pathname === '/api/v1/overview' || url.pathname === '/api/v1/observation-targets') {
      context.supporting.push(url); return route.continue();
    }
    const match = /^\/api\/v1\/observation-targets\/([^/]+)(?:\/(.+))?$/.exec(url.pathname);
    if (!match || ![alpha, beta].includes(match[1])) return reject();
    if (match[2] === 'resources/cpu/captures') {
      const cpu = cpuCaptureFixtureBinding(url);
      if (!cpu || cpu.selection || cpu.cursor || cpu.limit !== 100 || cpu.binding.targetRevision !== 1
          || cpu.binding.targetId !== match[1] || !cpu.binding.fromUtc || !cpu.binding.toUtc
          || [...url.searchParams.keys()].some(key => !['targetRevision', 'limit', 'fromUtc', 'toUtc'].includes(key)
            || url.searchParams.getAll(key).length !== 1)) return reject();
      context.supporting.push(url); return route.continue();
    }
    if (!match[2] || supporting.has(match[2]) || /^resources\/files\/history(?:\/[a-f0-9-]+)?$/.test(match[2])) { context.supporting.push(url); return route.continue(); }
    if (match[2] === 'resources/volumes/history') {
      if (url.searchParams.get('targetRevision') !== '1' || url.searchParams.get('limit') !== '25' ||
          !url.searchParams.get('fromUtc') || !url.searchParams.get('toUtc') ||
          [...url.searchParams.keys()].some(key => !['targetRevision', 'limit', 'fromUtc', 'toUtc'].includes(key))) return reject();
      context.supporting.push(url);
      return json(route, { instanceId: match[1], targetRevision: 1, fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'),
        cutoffUtc: '2026-10-03T12:00:00.123456Z', repositoryTimeUtc: '2026-10-03T12:00:00.123456Z', readEpoch: 'f'.repeat(64), state: 'unavailable', reason: 'no_completed_runs',
        coverage: { runCount: 0, completeRunCount: 0, gapRunCount: 0, retainedObservationCount: 0, retentionDays: 30, hasVisibilityGap: true }, items: [], nextCursor: null });
    }
    if (match[2] !== 'resources/volumes' || url.searchParams.get('limit') !== '25'
      || [...url.searchParams.keys()].some(key => !['limit', 'cursor'].includes(key))) return reject();
    const cursor = url.searchParams.get('cursor') ?? undefined, index = cursors.indexOf(cursor);
    if (index < 0) return reject();
    const item = { id: match[1], index, cursor, url }; context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    if (context.expired && cursor) return json(route, { detail: 'SECRET_EXPIRED_CURSOR' }, 400);
    return json(route, nativePage(item.id, context.run, index));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only finite synthetic local API routes may run; no mutations or external traffic').toEqual([]));
async function expectPage(page, index) {
  await expect(panel(page)).toContainText(`Page ${index + 1} · ${index === 2 ? 4 : 25} volumes`);
  await expect(table(page).locator('tbody > tr')).toHaveCount(index === 2 ? 4 : 25);
  await expect(table(page).locator('tbody > tr').first()).toContainText(['4 days', 'Not depleting', '9 days'][index]);
}
async function open(page) { await page.goto(href(alpha)); await expectPage(page, 0); }

test('real workspace polling retains browsed volume evidence and cached pages until explicit latest renewal', async ({ page }) => {
  await open(page); await next(page).click(); await expectPage(page, 1);
  const context = contexts.get(page); context.run = latestRun; context.expired = true;
  const nativeReads = context.requests.length, fileReads = context.supporting.filter(url => url.pathname.endsWith('/resources/files/history')).length;
  await next(page).focus(); const focused = await next(page).elementHandle();
  await page.clock.runFor(61_000);
  await expect.poll(() => context.supporting.filter(url => url.pathname.endsWith('/resources/files/history')).length).toBeGreaterThan(fileReads);
  await expectPage(page, 1); expect(context.requests).toHaveLength(nativeReads); await expect(next(page)).toBeFocused();
  await expect(table(page).locator('caption')).toHaveText('Retained SQL-reported volume capacity');
  await expect(panel(page)).toContainText('automatic volume refresh paused');
  expect(await focused.evaluate(element => element.isConnected)).toBe(true);
  await action(page, 'First volume page').click(); await expectPage(page, 0);
  await expect(table(page).locator('tbody > tr').first()).toContainText('10.00%');
  await next(page).click(); await expectPage(page, 1); expect(context.requests).toHaveLength(nativeReads);
  const preserved = scope(page); await latest(page).click(); await expectPage(page, 0);
  await expect(table(page).locator('tbody > tr').first()).toContainText('50.00%'); expect(context.requests.at(-1).cursor).toBeUndefined();
  await expect(table(page).locator('caption')).toHaveText('Latest SQL-reported volume capacity');
  await expect(latest(page)).toHaveCount(0);
  expect(scope(page)).toEqual(preserved);
  const beforePoll = context.requests.length; await page.clock.runFor(61_000);
  await expect.poll(() => context.requests.length).toBeGreaterThan(beforePoll); await expectPage(page, 0);
  await expect(table(page).locator('tbody > tr').first()).toContainText('50.00%');
});

test('a pending native volume continuation survives Refresh but Rewind cancels it and Live returns to a fresh first page', async ({ page }) => {
  await open(page); const context = contexts.get(page); let start, release, complete;
  const started = new Promise(resolve => { start = resolve; }), gate = new Promise(resolve => { release = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  context.handler = async (route, item) => {
    if (item.index !== 1) return false;
    const value = nativePage(item.id, capturedRun, 1); start(route.request()); await gate;
    try { await json(route, value); } catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); } return true;
  };
  await next(page).click(); const request = await started;
  await page.locator('.page-actions').getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(next(page)).toBeDisabled(); expect(context.requests).toHaveLength(2);
  const cancelled = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('custom'); await cancelled;
  await expect(panel(page)).toContainText('latest volume snapshot and forecast are separate from Rewind'); await expect(table(page)).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'SQL volume window history', exact: true })).toContainText('No completed volume collections were retained');
  const fixed = scope(page); expect(fixed).toMatchObject({ target: alpha, databaseId: '5', range: 'custom', compare: '1' });
  release(); await finished; await expect(table(page)).toHaveCount(0); expect(context.requests).toHaveLength(2);
  context.handler = undefined; context.run = latestRun;
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('1h'); await expectPage(page, 0);
  await expect(table(page).locator('tbody > tr').first()).toContainText('50.00%'); expect(context.requests.at(-1).cursor).toBeUndefined();
  expect(scope(page)).toMatchObject({ target: alpha, databaseId: '5', range: '1h', compare: '1' });
});

for (const width of [320, 390, 768, 1440]) test(`retained native volume pages are dark, readable and keyboard operable in the App at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await open(page);
  await next(page).focus(); await page.keyboard.press('Enter'); await expectPage(page, 1);
  const scroll = panel(page).getByRole('region', { name: 'SQL-reported volume capacity table', exact: true });
  await scroll.focus(); await expect(scroll).toBeFocused();
  const focus = await scroll.evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  if (await scroll.evaluate(element => element.scrollWidth > element.clientWidth)) {
    await page.keyboard.press('ArrowRight'); await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }
  for (const control of [action(page, 'First volume page'), action(page, 'Previous volume page'), next(page), latest(page)]) {
    await control.focus(); await expect(control).toBeFocused(); const box = await control.boundingBox();
    expect(box.height).toBeGreaterThanOrEqual(40); expect(box.width).toBeGreaterThanOrEqual(90);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`volume-app-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-volume-browsing/screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; });
    window.scrollTo({ top: 0, left: 0, behavior: 'instant' });
  });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: resolve(directory, `volume-browse-workspace-${width}.png`), fullPage: true });
  expect(scope(page)).toMatchObject({ target: alpha, databaseId: '5', range: '1h', compare: '1' });
});
