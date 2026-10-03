import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const contexts = new WeakMap();
const answer = page => page.getByRole('region', { name: 'What needs attention', exact: true });
const graph = page => page.getByRole('region', { name: 'Blocking chains', exact: true });
const payload = target => ({ instanceId: target, repositoryTimeUtc: '2026-01-15T13:01:00Z',
  evidence: { runId: '33333333-3333-4333-8333-000000000001', targetRevision: '7', collectorId: 'blocking.current',
    freshness: 'current', outcome: 'succeeded', reason: 'completed', isPartial: false, completedAtUtc: '2026-01-15T13:00:01Z' },
  items: [61, 62, 62].map((session, index) => ({ blockedSessionId: session, blockerKind: 'session', blockerSessionId: index === 1 ? 61 : 50,
    rootBlockerSessionId: target === alpha ? 50 : 80, waitType: index ? 'LCK_M_S' : 'LCK_M_X', waitingTaskCount: '1',
    waitDurationMilliseconds: String(1000 + index * 1000), chainDepth: index === 1 ? 2 : 1, chainState: 'resolved', observedAtUtc: '2026-01-15T13:00:00.123456Z' })),
  maximumChainDepth: 32, maximumGraphNodes: 256 });
async function json(route, value, status = 200) { await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) }); }
test.beforeEach(async ({ page }) => {
  const context = { reads: [], unexpected: [], handler: undefined }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(error.message));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'http://127.0.0.1:4328') { context.unexpected.push(url.href); return route.abort(); }
    if (!url.pathname.startsWith('/api/')) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/activity\/blocking\/current$/.exec(url.pathname);
    if (!match || ![alpha, beta].includes(match[1]) || route.request().method() !== 'GET' || url.search !== '?limit=256') {
      context.unexpected.push(url.href); return route.abort();
    }
    context.reads.push(match[1]);
    if (context.handler) return context.handler(route, match[1]);
    await json(route, payload(match[1]));
  });
});
test.afterEach(async ({ page }) => { expect(contexts.get(page).unexpected).toEqual([]); });
async function mount(page) { await page.goto('/component-tests/head-blocker.html'); await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50' })).toBeVisible(); }
function hold() {
  let release, announce; const gate = new Promise(resolve => release = resolve), started = new Promise(resolve => announce = resolve);
  return { started, release, handler: async (route, target) => { announce(route.request()); await gate; await json(route, payload(target)).catch(() => {}); } };
}
test('one native graph feeds direct root and keyboard inspection without changing rewind context', async ({ page }) => {
  await mount(page); expect(contexts.get(page).reads).toEqual([alpha]);
  await expect(answer(page)).toContainText('2 distinct blocked sessions');
  await expect(answer(page)).toContainText('independent of the selected history window');
  const hash = await page.evaluate(() => location.hash);
  await answer(page).getByRole('button', { name: 'Inspect head blocker 50' }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Blocking chains', exact: true })).toBeFocused();
  await expect(graph(page)).toContainText('Selected head-blocker evidence');
  expect(await page.evaluate(() => location.hash)).toBe(hash); expect(contexts.get(page).reads).toHaveLength(1);
});
test('same-target pending and failed refresh disclose retained graph; forbidden drops it', async ({ page }) => {
  await mount(page); const pending = hold(); contexts.get(page).handler = pending.handler;
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click(); await pending.started;
  await expect(answer(page)).toContainText('Previously received snapshot'); await expect(answer(page)).toContainText('Refresh pending');
  pending.release(); await expect(answer(page)).toContainText('Received current snapshot');
  contexts.get(page).handler = route => json(route, { detail: 'SECRET_FAILURE' }, 503);
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await expect(answer(page)).toContainText('Previously received snapshot'); await expect(answer(page)).not.toContainText('SECRET_FAILURE');
  contexts.get(page).handler = route => json(route, { detail: 'SECRET_DENIED' }, 403);
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50' })).toHaveCount(0);
  await expect(graph(page)).not.toContainText('Session 61'); await expect(answer(page)).toContainText('not authorized');
});
test('target changes fence old graph and ignore a delayed previous response', async ({ page }) => {
  await mount(page); const pending = hold(); contexts.get(page).handler = pending.handler;
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click(); const request = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  contexts.get(page).handler = undefined; await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  await aborted; pending.release();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 80' })).toBeVisible();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50' })).toHaveCount(0);
});
test('revision, cycles and partial pages do not promote an unconfirmed all-clear', async ({ page }) => {
  await mount(page); await page.getByRole('button', { name: 'Change revision', exact: true }).click();
  await expect(answer(page)).toContainText('No confirmed head blocker'); await expect(answer(page)).toContainText('not an all-clear');
  contexts.get(page).handler = (route, target) => { const value = payload(target); value.evidence.targetRevision = '8'; value.items.forEach(edge => edge.chainState = 'cycle'); return json(route, value); };
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await expect(answer(page)).toContainText('3 unresolved'); await expect(answer(page)).toContainText('not an all-clear');
});
for (const width of [320, 390, 768, 1440]) test(`dark head blocker is keyboard accessible and responsive at ${width}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 }); await mount(page);
  await expect(answer(page)).toContainText('longest observed wait 3000 ms');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  const button = answer(page).getByRole('button', { name: 'Inspect head blocker 50' });
  await page.keyboard.press('Tab'); await button.focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Blocking chains', exact: true })).toBeFocused();
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  expect(await page.evaluate(() => scrollY)).toBe(0);
  await mkdir('../artifacts/dashboard-retained-pagers/screenshots', { recursive: true });
  await page.screenshot({ path: `../artifacts/dashboard-retained-pagers/screenshots/head-blocker-${width}.png`, fullPage: true });
});


test('never-collected blocking labels repository check time without inventing a completed collection', async ({ page }) => {
  contexts.get(page).handler = (route, target) => json(route, { ...payload(target), evidence: null, items: [] });
  await page.goto('/component-tests/head-blocker.html');
  await expect(answer(page)).toContainText('collection time unavailable; repository checked');
  await expect(answer(page)).not.toContainText('collection completed');
  await expect(answer(page)).toContainText('not an all-clear');
  await expect(graph(page)).toContainText('Collection time unavailable; repository checked');
  await expect(graph(page)).not.toContainText('Collection completed');
});
