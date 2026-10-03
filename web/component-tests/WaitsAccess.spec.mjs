import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { origin, alpha, beta, now, sourceOf, nativeResponse, nativeCurrent, json } from './waitsAccessFixture.mjs';
const sessions = new WeakMap();
const current = page => page.getByRole('region', { name: 'Current server waits', exact: true });
const history = page => page.getByRole('region', { name: 'Server wait history', exact: true });
const trend = page => page.getByRole('region', { name: 'Wait categories over time', exact: true });
const ranking = page => page.getByRole('region', { name: 'Query Store plans by wait category', exact: true });
const captures = page => page.getByRole('region', { name: 'Captured sessions by server wait category', exact: true });
const poll = page => page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
const reads = (page, source) => sessions.get(page).reads.filter(item => item.source === source);
test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], reads: [], failed: [], handler: undefined }; sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(error.message));
  page.on('requestfailed', request => session.failed.push(request.url()));
  await page.addInitScript(value => { localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'); Date.now = () => Date.parse(value); }, now);
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    const refuse = () => { session.unexpected.push(`${route.request().method()} ${url.href}`); return route.abort(); };
    if (url.origin !== origin) return refuse();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (route.request().method() !== 'GET') return refuse();
    let item, value; try { item = { ...sourceOf(url), url: url.href }; value = nativeResponse(url); } catch { return refuse(); }
    session.reads.push(item);
    if (session.handler && await session.handler(route, url, item, value)) return;
    return json(route, value);
  });
});
test.afterEach(async ({ page }) => { expect(sessions.get(page).unexpected, 'Unregistered API/external requests and page errors fail').toEqual([]); });
async function open(page) {
  await page.goto('/component-tests/waits-access.html');
  await expect(current(page).getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible();
  await expect(history(page).getByRole('cell', { name: 'LCK_M_X', exact: true }).first()).toBeVisible();
  await expect(trend(page).getByRole('img')).toBeVisible();
  await expect(ranking(page).getByRole('table')).toBeVisible();
  await expect(captures(page).getByRole('table')).toBeVisible();
}
async function removed(page) {
  await expect(page.getByRole('alert').filter({ hasText: 'Waits access was denied.' })).toBeVisible();
  await expect(current(page).getByRole('table')).toHaveCount(0); await expect(history(page)).toHaveCount(0);
  await expect(trend(page).getByRole('img')).toHaveCount(0); await expect(ranking(page).getByRole('table')).toHaveCount(0);
  await expect(captures(page).getByRole('table')).toHaveCount(0);
  await expect(current(page).getByRole('button', { name: 'First wait page' })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('PROVIDER_SECRET');
}
function held(session, source) {
  let start, release, done; const started = new Promise(resolve => start = resolve), gate = new Promise(resolve => release = resolve), completed = new Promise(resolve => done = resolve);
  session.handler = async (route, _url, item, value) => {
    if (item.source !== source) return false;
    start(item); await gate;
    try { await json(route, value); } catch { /* Late fixture delivery can fail after the owned request is observed cancelled. */ }
    finally { done(); }
    return true;
  };
  return { started, async release() { release(); await completed; } };
}

for (const status of [401, 403]) for (const source of ['activity/waits', 'activity/waits/history', 'activity/waits/trend', 'query-performance/waits/top', 'activity/live'])
  test(`${source} HTTP${status} clears sibling receipts and stops automatic denial loops`, async ({ page }) => {
    await open(page); const session = sessions.get(page);
    session.handler = async (route, _url, item) => {
      if (item.source !== source) return false;
      await route.fulfill({ status, contentType: 'text/plain', body: 'PROVIDER_SECRET' }); return true;
    };
    await poll(page); await removed(page);
    const before = session.reads.length; await poll(page); await poll(page); expect(session.reads).toHaveLength(before);
  });

test('retained current and history continuation denial clears local First caches and unread cursors', async ({ page }) => {
  await open(page); await current(page).getByRole('button', { name: 'Next wait page', exact: true }).click();
  await expect(current(page)).toContainText('XE_TIMER_EVENT');
  await history(page).getByRole('button', { name: 'Older waits', exact: true }).click(); await expect(history(page)).toContainText('PAGEIOLATCH_SH');
  sessions.get(page).handler = async (route, _url, item) => {
    if (item.source !== 'activity/waits/history') return false; await json(route, {}, 403); return true;
  };
  await history(page).getByRole('button', { name: 'Return to latest wait history', exact: true }).click(); await removed(page);
  sessions.get(page).handler = undefined;
  await page.getByRole('button', { name: 'Retry wait history', exact: true }).click();
  await expect(history(page).getByRole('cell', { name: 'LCK_M_X', exact: true }).first()).toBeVisible();
  await expect(current(page).getByRole('table')).toHaveCount(0); await expect(ranking(page).getByRole('table')).toHaveCount(0);
  const count = reads(page, 'activity/waits').length; await poll(page); expect(reads(page, 'activity/waits')).toHaveLength(count);
  await current(page).getByRole('button', { name: 'Return to latest wait snapshot', exact: true }).click();
  await expect(current(page).getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible(); await expect(current(page)).toContainText('Page 1');
});

test('source-only validated recovery does not resurrect old trend, ranking or captured metadata', async ({ page }) => {
  await open(page); const session = sessions.get(page);
  session.handler = async (route, _url, item) => { if (item.source !== 'activity/waits/trend') return false; await json(route, {}, 403); return true; };
  await poll(page); await removed(page); session.handler = undefined;
  await page.getByRole('button', { name: 'Retry wait trend', exact: true }).click(); await expect(trend(page).getByRole('img')).toBeVisible();
  await expect(ranking(page).getByRole('table')).toHaveCount(0); await expect(captures(page).getByRole('table')).toHaveCount(0);
  const before = session.reads.length; await poll(page);
  await expect.poll(() => session.reads.length).toBeGreaterThan(before);
  expect(session.reads.slice(before).every(item => item.source === 'activity/waits/trend')).toBe(true);
  await page.getByRole('button', { name: 'Retry Query Store wait groups', exact: true }).click(); await expect(ranking(page).getByRole('table')).toBeVisible();
  await page.getByRole('button', { name: 'Retry captured sessions', exact: true }).click(); await expect(captures(page).getByRole('table')).toBeVisible();
});

test('parallel late200 cannot mask a sibling definitive denial in the same refresh', async ({ page }) => {
  await open(page); const session = sessions.get(page), hold = held(session, 'activity/waits/history');
  const original = session.handler;
  session.handler = async (route, url, item, value) => {
    if (item.source === 'activity/waits/trend') { await hold.started; await json(route, {}, 401); return true; }
    return original(route, url, item, value);
  };
  await poll(page); const owner = await hold.started; await removed(page);
  await expect.poll(() => session.failed.includes(owner.url)).toBe(true); await hold.release(); await removed(page);
});

test('explicit recovery failure keeps access closed and pending recovery survives workspace polls', async ({ page }) => {
  await open(page); const session = sessions.get(page);
  session.handler = async (route, _url, item) => { if (item.source !== 'activity/waits/trend') return false; await json(route, {}, 403); return true; };
  await poll(page); await removed(page);
  session.handler = async (route, _url, item) => { if (item.source !== 'activity/waits/trend') return false; await json(route, {}, 503); return true; };
  await page.getByRole('button', { name: 'Retry wait trend', exact: true }).click(); await expect(trend(page).getByRole('alert')).toBeVisible(); await expect(trend(page).getByRole('img')).toHaveCount(0);
  const hold = held(session, 'activity/waits/trend'); await page.getByRole('button', { name: 'Retry wait trend', exact: true }).click(); const owner = await hold.started;
  const count = session.reads.length; await poll(page); expect(session.reads).toHaveLength(count); expect(session.failed).not.toContain(owner.url);
  await hold.release(); await expect(trend(page).getByRole('img')).toBeVisible(); await expect(current(page).getByRole('table')).toHaveCount(0);
});

test('ordinary503retains every received source with explicit failure disclosure', async ({ page }) => {
  await open(page); sessions.get(page).handler = async (route) => { await json(route, {}, 503); return true; };
  await poll(page); await expect(trend(page).getByRole('alert')).toContainText('Previously received trend remains displayed');
  await expect(current(page).getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible();
  await expect(history(page).getByRole('cell', { name: 'LCK_M_X', exact: true }).first()).toBeVisible();
  await expect(trend(page).getByRole('img')).toBeVisible(); await expect(ranking(page).getByRole('table')).toBeVisible(); await expect(captures(page).getByRole('table')).toBeVisible();
  await expect(page.getByRole('alert').filter({ hasText: 'Waits access was denied.' })).toHaveCount(0);
});

test('collector200permission-denied coverage remains source evidence and does not revoke caller access', async ({ page }) => {
  await open(page); sessions.get(page).handler = async (route, _url, item) => {
    if (item.source !== 'activity/waits') return false;
    const value = nativeCurrent(item.target); value.items = []; value.nextCursor = null; value.evidence = { ...value.evidence, outcome: 'permission_denied', reason: 'permission_denied', isPartial: false }; await json(route, value); return true;
  };
  await poll(page); await expect(current(page)).toContainText('permission_denied');
  await expect(history(page).getByRole('table')).toBeVisible(); await expect(trend(page).getByRole('img')).toBeVisible();
  await expect(page.getByRole('alert').filter({ hasText: 'Waits access was denied.' })).toHaveCount(0);
});

for (const control of ['Change target revision', 'Target ABA', 'Use Rewind window', 'Remove diagnostic screen'])
  test(`${control} aborts and fences late explicit recovery`, async ({ page }) => {
    await open(page); const session = sessions.get(page), hold = held(session, 'activity/waits');
    await current(page).getByRole('button', { name: 'Next wait page', exact: true }).click(); const owner = await hold.started;
    session.handler = undefined; await page.getByRole('button', { name: control, exact: true }).click();
    await expect.poll(() => session.failed.includes(owner.url)).toBe(true); await hold.release();
    if (control === 'Remove diagnostic screen') await expect(page.getByText('Diagnostic screen removed', { exact: true })).toBeVisible();
    else if (control === 'Use Rewind window') await expect(current(page)).toHaveCount(0);
    else { await expect(current(page).getByRole('cell', { name: 'LCK_M_X', exact: true })).toBeVisible(); await expect(current(page)).not.toContainText('XE_TIMER_EVENT'); }
  });

for (const status of [401, 403]) test(`shared Activity history HTTP${status} clears aggregate siblings and requires explicit recovery`, async ({ page }) => {
  await open(page); await page.getByRole('combobox', { name: 'Diagnostic screen', exact: true }).selectOption('activity');
  await page.getByText('Open supporting waits, blocking, and history evidence', { exact: true }).click();
  await expect(history(page).getByRole('table')).toBeVisible();
  const session = sessions.get(page); session.handler = async (route, _url, item) => {
    if (item.source !== 'activity/waits/history') return false; await json(route, {}, status); return true;
  };
  await poll(page); await expect(page.getByRole('alert').filter({ hasText: 'Activity access was denied.' })).toBeVisible();
  await expect(history(page)).toHaveCount(0); await expect(page.getByRole('region', { name: 'Blocking history', exact: true })).toHaveCount(0);
  const count = session.reads.length; await poll(page); expect(session.reads).toHaveLength(count);
  session.handler = async route => { await json(route, {}, 503); return true; };
  await page.getByRole('button', { name: 'Retry activity evidence', exact: true }).click(); await expect(page.getByRole('alert').filter({ hasText: 'Supporting activity evidence is unavailable.' })).toBeVisible();
  await expect(history(page)).toHaveCount(0); session.handler = undefined;
  await page.getByRole('button', { name: 'Retry activity evidence', exact: true }).click(); await expect(history(page).getByRole('table')).toBeVisible();
});

test('Activity partial recovery displays its validated history without reopening failed supporting sources', async ({ page }) => {
  await open(page); await page.getByRole('combobox', { name: 'Diagnostic screen', exact: true }).selectOption('activity');
  await page.getByText('Open supporting waits, blocking, and history evidence', { exact: true }).click();
  await expect(history(page).getByRole('table')).toBeVisible();
  const session = sessions.get(page); session.handler = async (route, _url, item) => {
    if (item.source !== 'activity/waits/history') return false; await json(route, {}, 403); return true;
  };
  await poll(page); await expect(page.getByRole('alert').filter({ hasText: 'Activity access was denied.' })).toBeVisible();
  session.handler = async (route, _url, item) => {
    if (item.source === 'activity/waits/history') return false; await json(route, {}, 503); return true;
  };
  await page.getByRole('button', { name: 'Retry activity evidence', exact: true }).click();
  await expect(history(page).getByRole('table')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Blocking history', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Sessions', exact: true })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Live sessions', exact: true })).toHaveCount(0);
  const count = session.reads.length; await poll(page); expect(session.reads).toHaveLength(count);
  await expect(history(page).getByRole('table')).toBeVisible();
});

for (const width of [320, 390, 768, 1440]) test(`denied Waits recovery stays keyboard accessible in dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await open(page); sessions.get(page).handler = async (route, _url, item) => { if (item.source !== 'activity/waits/trend') return false; await json(route, {}, 403); return true; };
  await poll(page); await removed(page);
  const button = page.getByRole('button', { name: 'Retry wait trend', exact: true });
  for (let step = 0; step < 50 && !await button.evaluate(element => document.activeElement === element); step++)
    await page.keyboard.press('Tab');
  await expect(button).toBeFocused();
  expect(await button.evaluate(element => element.matches(':focus-visible'))).toBe(true);
  const focus = await button.evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`waits-access-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-access-navigation/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' })); await page.screenshot({ path: `${directory}/waits-access-${width}.png`, fullPage: true });
  sessions.get(page).handler = undefined; await page.keyboard.press('Enter'); await expect(trend(page).getByRole('img')).toBeVisible();
});
