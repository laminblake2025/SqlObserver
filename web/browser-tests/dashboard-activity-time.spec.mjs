import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4338';
const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T11:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const state = new WeakMap();
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const sessions = page => page.locator('.live-sessions');
const href = extra => '/#/activity?' + new URLSearchParams({ target, range: 'custom', from, to, databaseId: '5', ...extra });

test.beforeEach(async ({ page }, info) => {
  const context = { unexpected: [], history: [] }; state.set(page, context);
  await page.clock.install({ time: new Date('2026-09-26T12:30:00Z') });
  await page.context().addCookies(['dashboardRefactor', 'sessionSelectionFixture', 'databaseScopeFixture'].map(name => ({ name, value: '1', url: origin })));
  await page.addInitScript(() => { localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'); Date.now = () => Date.parse('2026-09-26T12:30:00Z'); });
  page.on('pageerror', error => context.unexpected.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = () => { context.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') return reject();
    if (url.pathname.endsWith('/activity/live/history')) context.history.push(url);
    if (['/api/v1/me', '/api/v1/overview', '/api/v1/observation-targets'].includes(url.pathname) ||
      url.pathname === `/api/v1/observation-targets/${target}` ||
      url.pathname === `/api/v1/observation-targets/${target}/health/databases` && url.searchParams.toString() === 'limit=25' ||
      new RegExp(`^/api/v1/observation-targets/${target}/activity/(sessions|requests|waits|blocking/current|blocking/history|waits/history|live|live/history)$`, 'u').test(url.pathname)) return route.continue();
    return reject();
  });
  await info.attach('provenance', { body: 'Built App with finite loopback synthetic native activity data. No SQL or connected VM execution.', contentType: 'text/plain' });
});
test.afterEach(async ({ page }) => expect(state.get(page).unexpected).toEqual([]));

test('Activity Live and History change shared time context and restore it through reload and browser history', async ({ page }) => {
  await page.goto(href());
  await expect(sessions(page).getByRole('heading', { name: 'Session history', exact: true })).toBeVisible();
  await expect(page.locator('.topbar-time-mode')).toHaveText('Rewind · fixed');
  await sessions(page).getByRole('button', { name: 'Live', exact: true }).click();
  await expect.poll(() => scope(page).get('range')).toBe('1h');
  expect(scope(page).has('from')).toBe(false); expect(scope(page).has('to')).toBe(false);
  expect(scope(page).get('databaseId')).toBe('5');
  await page.reload();
  await expect(sessions(page).getByRole('heading', { name: 'Live sessions', exact: true })).toBeVisible();
  await sessions(page).getByRole('button', { name: 'History', exact: true }).click();
  await expect.poll(() => scope(page).get('range')).toBe('custom');
  const frozen = Object.fromEntries(scope(page));
  expect(frozen.from).toBe('2026-09-26T11:30:00.000Z'); expect(frozen.to).toBe('2026-09-26T12:30:00.000Z');
  await page.reload();
  await expect(sessions(page).getByRole('heading', { name: 'Session history', exact: true })).toBeVisible();
  expect(Object.fromEntries(scope(page))).toEqual(frozen);
  const latest = state.get(page).history.at(-1);
  expect(latest.searchParams.get('from')).toBe(frozen.from); expect(latest.searchParams.get('to')).toBe(frozen.to);
  await page.goBack();
  await expect.poll(() => scope(page).get('range')).toBe('1h');
  await expect(sessions(page).getByRole('heading', { name: 'Live sessions', exact: true })).toBeVisible();
  await page.goForward();
  await expect.poll(() => scope(page).get('range')).toBe('custom');
  await expect(sessions(page).getByRole('heading', { name: 'Session history', exact: true })).toBeVisible();
});

test('returning captured or event-linked Activity to Live clears the exact evidence link in the shared URL', async ({ page }) => {
  for (const extra of [
    { sessionSnapshot: '88888888-8888-4888-8888-888888888888', sessionObserved: '2026-09-26T11:30:00.123456Z' },
    { at: '2026-09-26T11:30:00.123456Z', event: '33333333-3333-4333-8333-333333333333' },
  ]) {
    await page.goto(href(extra));
    await expect(sessions(page)).toContainText(extra.sessionSnapshot ? 'This table is pinned' : 'Opened for the deadlock event');
    await sessions(page).getByRole('button', { name: 'Live', exact: true }).click();
    await expect.poll(() => scope(page).get('range')).toBe('1h');
    for (const key of ['sessionSnapshot', 'sessionObserved', 'sessionIdentity', 'at', 'event', 'from', 'to']) expect(scope(page).has(key)).toBe(false);
    await page.reload();
    await expect(sessions(page).getByRole('heading', { name: 'Live sessions', exact: true })).toBeVisible();
  }
});

for (const width of [320, 768, 1024, 1440]) test(`shared Activity time controls stay accessible in dark Rewind at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await page.goto(href());
  const history = sessions(page).getByRole('button', { name: 'History', exact: true });
  await expect(history).toHaveAttribute('aria-pressed', 'true');
  await history.focus(); await expect(history).toBeFocused();
  const geometry = await history.evaluate(node => { const box = node.getBoundingClientRect(); return { width: box.width, height: box.height, right: box.right }; });
  expect(geometry.width).toBeGreaterThanOrEqual(40); expect(geometry.height).toBeGreaterThanOrEqual(36); expect(geometry.right).toBeLessThanOrEqual(width);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await page.evaluate(() => { document.activeElement?.blur(); document.querySelectorAll('.table-scroll,.live-table-scroll').forEach(node => { node.scrollLeft = 0; }); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  const folder = resolve('../artifacts/dashboard-query-history/screenshots'); await mkdir(folder, { recursive: true });
  const path = resolve(folder, `activity-shared-time-${width}.png`); await page.screenshot({ path, fullPage: true });
  await info.attach(`activity-shared-time-${width}`, { path, contentType: 'image/png' });
});
