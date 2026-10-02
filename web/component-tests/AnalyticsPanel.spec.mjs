import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const alpha = '11111111-1111-4111-8111-111111111111';
const beta = '22222222-2222-4222-8222-222222222222';
const firstId = '33333333-3333-4333-8333-333333333333';
const secondId = '44444444-4444-4444-8444-444444444444';
const next = 'opaque/page|2';
const sessions = new WeakMap();
const panel = page => page.locator('section.analytics-surface');
const body = (url, options = {}) => ({ targetId: url.pathname.split('/')[4], surface: url.pathname.split('/').at(-1),
  fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'), state: 'complete',
    items: [{ jobId: firstId, jobKind: url.pathname.endsWith('/backfill') ? 'backfill' : 'rollup', status: 'queued', requestedAtUtc: '2026-09-08T01:00:00Z',
    startedAtUtc: null, completedAtUtc: null, attempt: 1, targetRevision: 1 }], nextCursor: null,
  cutoffUtc: '2026-09-09T00:00:00Z', snapshotUtc: '2026-09-09T00:00:00Z', generation: 1, targetRevision: 1, ...options });
const json = (route, value) => route.fulfill({ status: 200, contentType: 'application/json',
  headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
const job = id => ({ jobId: id, jobKind: 'rollup', status: 'running', requestedAtUtc: '2026-09-08T01:00:00Z',
  startedAtUtc: null, completedAtUtc: null, attempt: 1, targetRevision: 1 });

test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], requests: [], cancelled: [], handler: undefined };
  sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(error.message));
  page.on('requestfailed', request => session.cancelled.push(request.url()));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== 'http://127.0.0.1:4328' || (/^\/api(?:\/|$)/.test(url.pathname) &&
      (request.method() !== 'GET' || !new RegExp(`^/api/v1/observation-targets/(${alpha}|${beta})/analytics/(jobs|backfill)$`).test(url.pathname)))) {
      session.unexpected.push(`${request.method()} ${url.href}`); return route.abort();
    }
    if (!url.pathname.startsWith('/api/')) return route.continue();
    expect(url.searchParams.get('limit')).toBe('100');
    session.requests.push(url);
    if (session.handler && await session.handler(route, url)) return;
    return json(route, body(url));
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});
test.afterEach(async ({ page }) => expect(sessions.get(page).unexpected).toEqual([]));
async function open(page) { await page.goto('/component-tests/analytics.html'); }
async function paged(page) {
  sessions.get(page).handler = async (route, url) => {
    await json(route, body(url, url.searchParams.has('cursor') ? { items: [job(secondId)] } : { nextCursor: next }));
    return true;
  };
  await open(page);
  await panel(page).getByRole('button', { name: 'Next evidence page', exact: true }).click();
  await expect(panel(page).getByRole('cell', { name: secondId, exact: true })).toBeVisible();
}

test('sanitizes an initial service failure and retries through the rendered action', async ({ page }) => {
  let fail = true;
  sessions.get(page).handler = async route => {
    if (!fail) return false;
    await route.fulfill({ status: 503, body: '<script>window.analyticsInjected=true</script> provider-secret' }); return true;
  };
  await open(page);
  await expect(panel(page).getByRole('alert')).toHaveText('The analytics service is temporarily unavailable.');
  await expect(page.locator('body')).not.toContainText('provider-secret');
  fail = false;
  await panel(page).getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  expect(await page.evaluate(() => window.analyticsInjected)).toBeUndefined();
});

test('restarts an invalid cursor with the exact selected window and no cursor', async ({ page }) => {
  sessions.get(page).handler = async (route, url) => {
    if (url.searchParams.has('cursor')) await route.fulfill({ status: 400, body: 'provider-secret' });
    else await json(route, body(url, { nextCursor: next }));
    return true;
  };
  await open(page);
  await panel(page).getByRole('button', { name: 'Next evidence page', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toHaveText('The page cursor is invalid; restart paging to load the first page.');
  await expect(panel(page).getByRole('button', { name: 'Retry', exact: true })).toHaveCount(0);
  await panel(page).getByRole('button', { name: 'Restart paging', exact: true }).click();
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
  const requests = sessions.get(page).requests;
  expect(requests.map(url => url.searchParams.get('cursor'))).toEqual([null, next, null]);
  expect(requests.every(url => url.searchParams.get('fromUtc') === '2026-09-08T00:00:00Z' && url.searchParams.get('toUtc') === '2026-09-09T00:00:00Z')).toBe(true);
});

test('renders permission, no-data and stale states without treating job inventory as time-filtered rows', async ({ page }) => {
  let state = 'permission_denied';
  sessions.get(page).handler = async (route, url) => { await json(route, body(url, { state, items: state === 'no_data' ? [] : [job(firstId)] })); return true; };
  await open(page);
  await expect(panel(page).getByRole('status')).toHaveText('You are not authorized to view this surface.');
  await expect(panel(page).getByRole('button')).toHaveCount(0);
  state = 'no_data'; await page.getByRole('button', { name: 'Refresh context' }).click();
  await expect(panel(page).getByRole('status')).toHaveText('No job inventory rows are available in this response.');
  state = 'stale'; await page.getByRole('button', { name: 'Refresh context' }).click();
  await expect(panel(page).getByRole('status')).toHaveText('This snapshot is stale; refresh to retry.');
  state = 'complete'; await panel(page).getByRole('button', { name: 'Refresh snapshot' }).click();
  await expect(panel(page)).toContainText('not filtered');
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
});

test('changing target discards another target cursor before its first native read', async ({ page }) => {
  await paged(page);
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(beta);
  await expect.poll(() => sessions.get(page).requests.filter(url => url.pathname.includes(beta)).length).toBeGreaterThan(0);
  expect(sessions.get(page).requests.filter(url => url.pathname.includes(beta)).every(url => !url.searchParams.has('cursor'))).toBe(true);
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
  await expect(panel(page).getByRole('button', { name: 'First page', exact: true })).toBeDisabled();
});

test('changing surface discards the old surface cursor before its first native read', async ({ page }) => {
  await paged(page);
  await page.getByRole('combobox', { name: 'Evidence surface', exact: true }).selectOption('backfill');
  await expect.poll(() => sessions.get(page).requests.filter(url => url.pathname.endsWith('/backfill')).length).toBeGreaterThan(0);
  expect(sessions.get(page).requests.filter(url => url.pathname.endsWith('/backfill')).every(url => !url.searchParams.has('cursor'))).toBe(true);
  await expect(panel(page).getByRole('heading', { name: 'Backfill status', exact: true })).toBeVisible();
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
});

test('changing window aborts pending continuation and cannot display its late rows', async ({ page }) => {
  let start, release, finish;
  const seen = new Promise(resolve => { start = resolve; });
  const held = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { finish = resolve; });
  const session = sessions.get(page);
  session.handler = async (route, url) => {
    if (!url.searchParams.has('cursor')) { await json(route, body(url, { nextCursor: next })); return true; }
    start(); await held;
    try { await json(route, body(url, { items: [job(secondId)] })); }
    catch (error) { if (!session.cancelled.some(value => value.includes('cursor='))) throw error; }
    finally { finish(); }
    return true;
  };
  await open(page); await panel(page).getByRole('button', { name: 'Next evidence page', exact: true }).click(); await seen;
  await expect(panel(page).getByRole('status')).toHaveText('Loading…');
  await page.getByRole('button', { name: 'Change window' }).click();
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
  await expect.poll(() => session.cancelled.filter(value => value.includes('cursor=')).length).toBe(1);
  release(); await finished;
  await expect(panel(page).getByRole('cell', { name: secondId, exact: true })).toHaveCount(0);
  expect(session.requests.at(-1).searchParams.get('fromUtc')).toBe('2026-09-07T00:00:00Z');
  expect(session.requests.at(-1).searchParams.has('cursor')).toBe(false);
});

test('keeps job evidence accessible and responsive at 320 and 1440 pixels', async ({ page }, info) => {
  await open(page);
  await expect(panel(page).getByRole('cell', { name: firstId, exact: true })).toBeVisible();
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    const scroll = panel(page).getByRole('region', { name: 'Job inventory scrollable evidence', exact: true });
    await scroll.focus();
    await expect(scroll).toBeFocused();
    await page.getByRole('button', { name: 'Refresh context' }).focus();
    await expect(page.getByRole('button', { name: 'Refresh context' })).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`analytics-panel-${width}-axe`, { body: JSON.stringify(violations), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}analytics-panel-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`analytics-panel-${width}`, { path, contentType: 'image/png' });
  }
});
