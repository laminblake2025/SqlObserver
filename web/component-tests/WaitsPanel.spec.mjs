import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { activityFixture, alpha, beta, wait, waitHistoryItem } from './activityFixture.mjs';

const sessions = new WeakMap();
const region = page => page.getByRole('region', { name: 'Current server waits', exact: true });
const json = (route, body) => route.fulfill({ status: 200, contentType: 'application/json',
  headers: { 'Cache-Control': 'no-store' }, body: JSON.stringify(body) });
const nextCursor = 'retained-wait-snapshot-page-2';
const historyCursor = 'retained-history+/=page-2';
const historyRegion = page => page.getByRole('region', { name: 'Server wait history', exact: true });

test.beforeEach(async ({ page }) => {
  const first = activityFixture(alpha).waits;
  const session = { unexpected: [], waits: [], auxiliary: [], histories: [], failed: [], intercept: undefined,
    history: undefined, historyNext: undefined,
    latest: { ...first, nextCursor },
    next: { ...first, items: [wait({ waitType: 'PAGEIOLATCH_SH' })], nextCursor: null } };
  sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(error.message));
  page.on('requestfailed', request => session.failed.push(request.url()));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = () => { session.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); };
    if (url.origin !== 'http://127.0.0.1:4328') return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') return reject();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(activity\/waits|activity\/waits\/history|activity\/waits\/trend|activity\/live|query-performance\/waits\/top)$/u.exec(url.pathname);
    if (!match || ![alpha, beta].includes(match[1])) return reject();
    if (match[2] !== 'activity/waits') {
      session.auxiliary.push(url);
      if (match[2] === 'activity/waits/history' && session.history) {
        if (url.searchParams.get('limit') !== '25' || !url.searchParams.has('fromUtc') || !url.searchParams.has('toUtc') ||
            [...url.searchParams.keys()].some(key => !['limit', 'fromUtc', 'toUtc', 'cursor'].includes(key)) ||
            url.searchParams.has('cursor') && url.searchParams.get('cursor') !== historyCursor) return reject();
        session.histories.push(url);
        if (session.intercept && await session.intercept(route, url)) return;
        const value = url.searchParams.has('cursor') ? session.historyNext : session.history;
        return json(route, { ...value, instanceId: match[1],
          fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'),
          ...(match[1] === beta ? { items: [waitHistoryItem({ waitType: 'RESOURCE_SEMAPHORE', observedAtUtc: '2026-09-30T13:45:00.000Z' })], nextCursor: null } : {}) });
      }
      // Registered secondary sources fail safely and locally; no request reaches a proxy.
      return route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
    }
    if (url.searchParams.get('limit') !== '25' || [...url.searchParams.keys()].some(key => !['limit', 'cursor'].includes(key))) return reject();
    const cursor = url.searchParams.get('cursor');
    if (cursor !== null && cursor !== nextCursor) return reject();
    session.waits.push(url);
    if (session.intercept && await session.intercept(route, url)) return;
    const value = cursor ? session.next : session.latest;
    return json(route, { ...value, instanceId: match[1],
      ...(match[1] === beta ? { items: [wait({ waitType: 'RESOURCE_SEMAPHORE' })], nextCursor: null } : {}) });
  });
  await page.addInitScript(() => {
    localStorage.setItem('sqlobserver.timeDisplayMode', 'utc');
    Date.now = () => Date.parse('2026-09-30T14:00:00.000Z');
  });
});

test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Unexpected API/external traffic was aborted before proxy/backend').toEqual([]);
});

async function open(page) {
  await page.goto('/component-tests/waits.html');
  await expect(region(page)).toContainText('LCK_M_X');
}
async function poll(page) {
  const session = sessions.get(page), before = session.auxiliary.length;
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
  await expect.poll(() => session.auxiliary.length).toBeGreaterThan(before);
}

test('keeps native snapshot pages across polls and returns to the retained first page until explicitly requesting latest', async ({ page }) => {
  await open(page);
  const panel = region(page), session = sessions.get(page);
  await panel.getByRole('button', { name: 'Next wait page', exact: true }).click();
  await expect(panel).toContainText('Page 2');
  session.latest = { ...session.latest, items: [wait({ waitType: 'WRITELOG' })], nextCursor: null };
  await poll(page);
  await expect(panel).toContainText('PAGEIOLATCH_SH');
  await expect(panel).toContainText('Browsing retained snapshot');
  expect(session.waits).toHaveLength(2);
  await panel.getByRole('button', { name: 'Previous wait page', exact: true }).click();
  await expect(panel).toContainText('Page 1');
  await expect(panel).toContainText('LCK_M_X');
  expect(session.waits).toHaveLength(2);
  await poll(page);
  await expect(panel).toContainText('LCK_M_X');
  await panel.getByRole('button', { name: 'Next wait page', exact: true }).click();
  await expect(panel).toContainText('Page 2');
  await panel.getByRole('button', { name: 'First wait page', exact: true }).click();
  await expect(panel).toContainText('LCK_M_X');
  expect(session.waits).toHaveLength(3);
  await panel.getByRole('button', { name: 'Return to latest wait snapshot', exact: true }).click();
  await expect(panel).toContainText('WRITELOG');
  await expect(panel).toContainText('Page 1');
  await expect(panel).not.toContainText('Browsing retained snapshot');
  expect(session.waits).toHaveLength(4);
  expect(session.waits.at(-1).searchParams.has('cursor')).toBe(false);
  await poll(page);
  await expect.poll(() => session.waits.length).toBe(5);
});

test('workspace polling does not cancel a held snapshot page; returning to latest aborts it and refuses its late result', async ({ page }) => {
  await open(page);
  const session = sessions.get(page), panel = region(page);
  let release, started, finished;
  const gate = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  const delivered = new Promise(resolve => { finished = resolve; });
  session.intercept = async (route, url) => {
    if (!url.searchParams.has('cursor')) return false;
    started(); await gate;
    try { await json(route, session.next); } catch (error) {
      if (!session.failed.some(value => value.includes(encodeURIComponent(nextCursor)))) throw error;
    } finally { finished(); }
    return true;
  };
  await panel.getByRole('button', { name: 'Next wait page', exact: true }).click();
  await seen;
  await poll(page);
  expect(session.waits).toHaveLength(2);
  expect(session.failed.filter(url => url.includes(nextCursor))).toEqual([]);
  session.latest = { ...session.latest, items: [wait({ waitType: 'WRITELOG' })], nextCursor: null };
  await panel.getByRole('button', { name: 'Return to latest wait snapshot', exact: true }).click();
  await expect(panel).toContainText('WRITELOG');
  await expect.poll(() => session.failed.some(url => url.includes(nextCursor))).toBe(true);
  release(); await delivered;
  await expect(panel).not.toContainText('PAGEIOLATCH_SH');
});

test('expired snapshot paging keeps retained evidence and offers explicit latest recovery without automatic substitution', async ({ page }) => {
  await open(page);
  const session = sessions.get(page), panel = region(page);
  session.intercept = async (route, url) => {
    if (!url.searchParams.has('cursor')) return false;
    await route.fulfill({ status: 410, body: 'private source detail' }); return true;
  };
  await panel.getByRole('button', { name: 'Next wait page', exact: true }).click();
  await expect(panel.getByRole('alert')).toBeVisible();
  await expect(panel).toContainText('LCK_M_X');
  await expect(panel).not.toContainText('private source detail');
  await poll(page);
  expect(session.waits).toHaveLength(2);
  session.latest = { ...session.latest, items: [wait({ waitType: 'WRITELOG' })], nextCursor: null };
  await panel.getByRole('button', { name: 'Return to latest wait snapshot', exact: true }).click();
  await expect(panel).toContainText('WRITELOG');
  await expect(panel.getByRole('alert')).toHaveCount(0);
});

test('target change clears the retained snapshot and its cursors', async ({ page }) => {
  await open(page);
  await region(page).getByRole('button', { name: 'Next wait page', exact: true }).click();
  await expect(region(page)).toContainText('PAGEIOLATCH_SH');
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  await expect(region(page)).toContainText('RESOURCE_SEMAPHORE');
  await expect(region(page)).toContainText('Page 1');
  await expect(region(page)).not.toContainText('PAGEIOLATCH_SH');
  const betaRequests = sessions.get(page).waits.filter(url => url.pathname.includes(beta));
  expect(betaRequests).toHaveLength(1);
  expect(betaRequests[0].searchParams.has('cursor')).toBe(false);
});

function enableHistory(page) {
  const session = sessions.get(page), native = activityFixture(alpha)['waits/history'];
  session.history = { ...native, repositoryTimeUtc: '2026-09-30T14:00:00.000Z',
    items: [waitHistoryItem({ observedAtUtc: '2026-09-30T13:45:00.000Z' })], nextCursor: historyCursor };
  session.historyNext = { ...session.history,
    items: [waitHistoryItem({ waitType: 'PAGEIOLATCH_SH', observedAtUtc: '2026-09-30T13:30:00.000Z' })], nextCursor: null };
  return session;
}
async function nextHistory(page) {
  const history = historyRegion(page);
  await expect(history.getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible();
  await history.getByRole('button', { name: 'Older waits', exact: true }).click();
  await expect(history.getByRole('cell', { name: 'PAGEIOLATCH_SH', exact: true })).toBeVisible();
  return history;
}
async function advanceHistory(page) {
  const session = sessions.get(page);
  session.history = { ...session.history, repositoryTimeUtc: '2026-09-30T14:10:00.000Z',
    items: [waitHistoryItem({ waitType: 'WRITELOG', observedAtUtc: '2026-09-30T13:55:00.000Z' })], nextCursor: null };
  await page.evaluate(() => { Date.now = () => Date.parse('2026-09-30T14:10:00.000Z'); });
  const response = page.waitForResponse(value => value.url().includes('/activity/waits/history?') && !new URL(value.url()).searchParams.has('cursor'));
  await poll(page); await response;
}
function nativeWindow(url) {
  return { fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc') };
}
function holdHistory(page) {
  const session = sessions.get(page);
  let release, started, finished;
  const gate = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  const delivered = new Promise(resolve => { finished = resolve; });
  session.intercept = async (route, url) => {
    if (!url.pathname.endsWith('/waits/history') || !url.searchParams.has('cursor')) return false;
    started(url); await gate;
    try { await json(route, { ...session.historyNext, ...nativeWindow(url) }); }
    catch (error) {
      if (!session.failed.includes(url.href)) throw error;
    } finally { finished(); }
    return true;
  };
  return { seen, async finish() { release(); await delivered; } };
}

test('wait history retains paged evidence and original window across a moving live poll until latest is explicitly requested', async ({ page }) => {
  const session = enableHistory(page);
  await open(page); const history = await nextHistory(page), originalWindow = nativeWindow(session.histories[0]);
  await advanceHistory(page);
  await expect(history.getByRole('cell', { name: 'PAGEIOLATCH_SH', exact: true })).toBeVisible();
  await expect(history.getByRole('status').filter({ hasText: 'Page 2' })).toBeVisible();
  expect(session.histories).toHaveLength(3);
  expect(nativeWindow(session.histories[1])).toEqual(originalWindow);
  expect(nativeWindow(session.histories[2])).not.toEqual(originalWindow);
  await history.getByRole('button', { name: 'First wait history page', exact: true }).click();
  await expect(history.getByRole('cell', { name: 'WRITELOG', exact: true })).toBeVisible();
  // Mutable history is read again against the retained bounds; it is not an
  // immutable collector snapshot even while its navigation context is retained.
  expect(nativeWindow(session.histories.at(-1))).toEqual(originalWindow);
  await history.getByRole('button', { name: 'Return to latest wait history', exact: true }).click();
  await expect(history.getByRole('status').filter({ hasText: 'Page 1' })).toBeVisible();
  await expect(history.getByRole('button', { name: 'Return to latest wait history', exact: true })).toHaveCount(0);
  expect(nativeWindow(session.histories.at(-1))).toEqual(nativeWindow(session.histories[2]));
  expect(session.histories.at(-1).searchParams.has('cursor')).toBe(false);
  await expect(history).toContainText('14:10:00 UTC');
  await expect(history.getByRole('button', { name: 'Older waits', exact: true })).toBeDisabled();
});

test('moving workspace poll preserves held wait history continuation and latest aborts it without consuming late rows', async ({ page }) => {
  const session = enableHistory(page); await open(page);
  const history = historyRegion(page), held = holdHistory(page);
  await expect(history.getByRole('button', { name: 'Older waits', exact: true })).toBeEnabled();
  await history.getByRole('button', { name: 'Older waits', exact: true }).click();
  const continuation = await held.seen, originalWindow = nativeWindow(continuation);
  await advanceHistory(page);
  expect(session.failed.filter(url => url.includes('cursor='))).toEqual([]);
  await expect(history).toHaveAttribute('aria-busy', 'true');
  await expect(history.getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible();
  expect(nativeWindow(session.histories[0])).toEqual(originalWindow);
  await history.getByRole('button', { name: 'Return to latest wait history', exact: true }).click();
  await expect.poll(() => session.failed.includes(continuation.href)).toBe(true);
  await expect(history.getByRole('cell', { name: 'WRITELOG', exact: true })).toBeVisible();
  expect(nativeWindow(session.histories.at(-1))).not.toEqual(originalWindow);
  await held.finish();
  await expect(history).toHaveAttribute('aria-busy', 'false');
  await expect(history.getByRole('cell', { name: 'PAGEIOLATCH_SH', exact: true })).toHaveCount(0);
  await expect(history.getByRole('cell', { name: 'WRITELOG', exact: true })).toBeVisible();
  await expect(history.getByRole('alert')).toHaveCount(0);
});

test('failed latest history recovery retains the displayed page and offers safe explicit retry', async ({ page }) => {
  const session = enableHistory(page); await open(page); const history = await nextHistory(page);
  await advanceHistory(page);
  let fail = true;
  session.intercept = async (route, url) => {
    if (!url.pathname.endsWith('/waits/history') || url.searchParams.has('cursor') || !fail) return false;
    await route.fulfill({ status: 503, contentType: 'text/html', body: '<script>window.historyInjected=true</script> SECRET_HISTORY_PROVIDER' }); return true;
  };
  await history.getByRole('button', { name: 'Return to latest wait history', exact: true }).click();
  await expect(history.getByRole('alert')).toBeVisible();
  await expect(history.getByRole('cell', { name: 'PAGEIOLATCH_SH', exact: true })).toBeVisible();
  await expect(history.getByRole('status').filter({ hasText: 'Page 2' })).toBeVisible();
  await expect(history).not.toContainText('SECRET_HISTORY_PROVIDER');
  expect(await page.evaluate(() => window.historyInjected)).toBeUndefined();
  fail = false;
  await history.getByRole('button', { name: 'Return to latest wait history', exact: true }).click();
  await expect(history.getByRole('cell', { name: 'WRITELOG', exact: true })).toBeVisible();
  await expect(history.getByRole('alert')).toHaveCount(0);
  await expect(history.getByRole('status').filter({ hasText: 'Page 1' })).toBeVisible();
});

test('target change cancels held wait history, resets navigation and fences the old target response', async ({ page }) => {
  const session = enableHistory(page); await open(page); const held = holdHistory(page), history = historyRegion(page);
  await history.getByRole('button', { name: 'Older waits', exact: true }).click(); const continuation = await held.seen;
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  await expect(history.getByRole('cell', { name: 'RESOURCE_SEMAPHORE', exact: true })).toBeVisible();
  await expect.poll(() => session.failed.includes(continuation.href)).toBe(true);
  await held.finish();
  await expect(history.getByRole('cell', { name: 'PAGEIOLATCH_SH', exact: true })).toHaveCount(0);
  await expect(history.getByRole('cell', { name: 'RESOURCE_SEMAPHORE', exact: true })).toBeVisible();
  const requests = session.histories.filter(url => url.pathname.includes(beta));
  expect(requests).toHaveLength(1); expect(requests[0].searchParams.has('cursor')).toBe(false);
  await expect(history.getByRole('status').filter({ hasText: 'Page 1' })).toBeVisible();
});

for (const width of [320, 768, 1024, 1440]) test(`retained wait history exposes keyboard latest recovery and dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); enableHistory(page); await open(page);
  const history = await nextHistory(page), latest = history.getByRole('button', { name: 'Return to latest wait history', exact: true });
  await history.getByRole('button', { name: 'First wait history page', exact: true }).focus();
  await page.keyboard.press('Shift+Tab');
  await expect(latest).toBeFocused(); expect(await latest.evaluate(element => element.matches(':focus-visible'))).toBe(true);
  const box = await latest.boundingBox();
  expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`waits-retained-history-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const path = `${directory}/waits-retained-history-${width}.png`;
  await page.screenshot({ path, fullPage: true });
  await info.attach(`waits-retained-history-${width}`, { path, contentType: 'image/png' });
  await latest.focus(); await page.keyboard.press('Enter');
  await expect(history.getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible();
  await expect(latest).toHaveCount(0);
});

for (const width of [320, 390, 768, 1440]) {
  test(`retained Waits controls wrap, remain keyboard accessible and pass axe at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 });
    await open(page);
    const panel = region(page);
    await panel.getByRole('button', { name: 'Next wait page', exact: true }).click();
    await expect(panel).toContainText('Page 2');
    const latest = panel.getByRole('button', { name: 'Return to latest wait snapshot', exact: true });
    await expect(latest).toBeVisible();
    const geometry = await latest.boundingBox();
    expect(geometry.width).toBeGreaterThan(150);
    expect(geometry.height).toBeGreaterThanOrEqual(40);
    expect(geometry.x).toBeGreaterThanOrEqual(0);
    expect(geometry.x + geometry.width).toBeLessThanOrEqual(width + 1);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width + 1);
    await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).focus();
    for (let index = 0; index < 30; index++) {
      await page.keyboard.press('Tab');
      if (await latest.evaluate(node => node === document.activeElement)) break;
    }
    await expect(latest).toBeFocused();
    expect(await latest.evaluate(node => node.matches(':focus-visible'))).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`waits-retained-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
    await mkdir(directory, { recursive: true });
    const path = `${directory}/waits-retained-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`waits-retained-${width}`, { path, contentType: 'image/png' });
    await page.keyboard.press('Enter');
    await expect(panel).not.toContainText('Browsing retained snapshot');
    await expect(panel).toContainText('Page 1');
  });
}
