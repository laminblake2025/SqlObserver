import { expect, test } from '@playwright/test';
import { origin, now, sourceOf, nativeResponse, json } from './waitsAccessFixture.mjs';
const sessions = new WeakMap();
const secret = 'SYNTHETIC protected executing SQL';
const live = page => page.getByRole('region', { name: 'Live sessions', exact: true });
const history = page => page.getByRole('region', { name: 'Session history', exact: true });
const poll = page => page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
const denied = page => page.getByRole('alert').filter({ hasText: 'Activity access was denied.' });

test.beforeEach(async ({ page }) => {
  const session = { reads: [], unexpected: [], handler: undefined, queryStatus: 200 }; sessions.set(page, session);
  await page.clock.setFixedTime(new Date(now));
  page.on('pageerror', error => session.unexpected.push(error.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    const reject = () => { session.unexpected.push(route.request().method() + ' ' + url.href); return route.abort(); };
    if (url.origin !== origin) return reject();
    if (!url.pathname.startsWith('/api/')) return route.continue();
    if (route.request().method() !== 'GET') return reject();
    if (url.pathname.endsWith('/activity/live/query')) {
      if (url.searchParams.get('identity') !== 'c'.repeat(64) || url.searchParams.get('snapshot') !== '55555555-5555-4555-8555-555555555555') return reject();
      session.reads.push({ source: 'activity/live/query', url: url.href });
      return json(route, session.queryStatus === 200 ? { state: 'available', text: secret } : {}, session.queryStatus);
    }
    let item, value;
    try { item = { ...sourceOf(url), url: url.href }; value = nativeResponse(url); } catch { return reject(); }
    if (item.source === 'activity/live') value = { ...value, rows: value.rows.map(row => ({ ...row, queryId: '77777777-7777-4777-8777-777777777777', queryState: 'available' })) };
    if (item.source === 'activity/live/history') value = value.map(row => ({ ...row, deadlockEventId: '88888888-8888-4888-8888-888888888888' }));
    session.reads.push(item);
    if (session.handler && await session.handler(route, item, value)) return;
    await json(route, value);
  });
});
test.afterEach(async ({ page }) => expect(sessions.get(page).unexpected, 'Only finite registered synthetic reads and no page errors').toEqual([]));
async function open(page) {
  await page.goto('/component-tests/live-sessions-access.html');
  await expect(live(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  await page.getByText('Open supporting waits, blocking, and history evidence', { exact: true }).click();
  await expect(page.getByRole('region', { name: 'Server wait history', exact: true }).getByRole('table')).toBeVisible();
  await live(page).getByRole('button', { name: 'Inspect 71/0', exact: true }).click();
  await live(page).getByRole('button', { name: 'Load captured SQL', exact: true }).click();
  await expect(page.locator('pre.query-text')).toContainText(secret);
}
async function removed(page) {
  await expect(denied(page)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Inspect 71/0', exact: true })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Loaded session observations', exact: true }).getByRole('table')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Session details', exact: true })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Server wait history', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText(secret);
}
for (const status of [401, 403]) for (const source of ['activity/live', 'activity/live/history'])
  test(`${source} HTTP${status} removes retained metadata, SQL and supporting siblings until explicit recovery`, async ({ page }) => {
    await open(page); const session = sessions.get(page);
    session.handler = async (route, item) => item.source === source ? (await json(route, {}, status), true) : false;
    if (source.endsWith('/history')) await live(page).getByRole('button', { name: 'History', exact: true }).click(); else await poll(page);
    await removed(page); const count = session.reads.length; await poll(page); await poll(page); expect(session.reads).toHaveLength(count);
    session.handler = undefined;
    await page.getByRole('button', { name: 'Retry session access', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Server wait history', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'Retry activity evidence', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Server wait history', exact: true }).getByRole('table')).toBeVisible();
    await expect(page.getByRole('region', { name: 'Session details', exact: true })).toHaveCount(0);
    await expect(page.locator('body')).not.toContainText(secret);
  });
test('live metadata denial aborts a held supporting success that cannot republish evidence', async ({ page }) => {
  await open(page); const session = sessions.get(page); let release, started; const began = new Promise(resolve => { started = resolve; }); const gate = new Promise(resolve => { release = resolve; }); let request;
  session.handler = async (route, item, value) => {
    if (item.source === 'activity/waits/history') { request = route.request(); started(); await gate; try { await json(route, value); } catch { expect(request.failure()?.errorText).toContain('ERR_ABORTED'); } return true; }
    if (item.source === 'activity/live') { await began; await json(route, {}, 403); return true; } return false;
  };
  await poll(page); await began; await removed(page); await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED'); release(); await removed(page);
});
test('protected SQL-only403 retains authorized metadata and supporting evidence', async ({ page }) => {
  await open(page); const session = sessions.get(page); session.queryStatus = 403;
  await live(page).getByRole('button', { name: 'Load captured SQL', exact: true }).click();
  await expect(live(page).getByRole('alert')).toBeVisible(); await expect(live(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Server wait history', exact: true }).getByRole('table')).toBeVisible();
  await expect(denied(page)).toHaveCount(0); await expect(page.locator('body')).not.toContainText(secret);
});
test('captured SQL authentication401 clears the whole activity boundary', async ({ page }) => {
  await open(page); sessions.get(page).queryStatus = 401;
  await live(page).getByRole('button', { name: 'Load captured SQL', exact: true }).click();
  await removed(page); const count = sessions.get(page).reads.length; await poll(page); expect(sessions.get(page).reads).toHaveLength(count);
});
test('missing linked deadlock never automatically substitutes another event capture from the same minute', async ({ page }) => {
  await open(page); const session = sessions.get(page), before = session.reads.filter(item => item.source === 'activity/live').length;
  await page.getByRole('button', { name: 'Open missing deadlock', exact: true }).click();
  await expect(history(page)).toContainText('No retained capture is associated with the requested deadlock event');
  await expect(history(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toHaveCount(0);
  expect(session.reads.filter(item => item.source === 'activity/live')).toHaveLength(before);
});

test('historical metadata failure requires an explicit retry of the same captured snapshot', async ({ page }) => {
  await open(page);
  await live(page).getByRole('button', { name: 'History', exact: true }).click();
  await expect(history(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  const session = sessions.get(page);
  session.handler = async (route, item) => item.source === 'activity/live' ? (await json(route, {}, 503), true) : false;
  await history(page).getByRole('button', { name: 'Refresh now', exact: true }).click();
  await expect(history(page).getByRole('alert')).toHaveText('Refresh failed for the current request. Automatic refresh is paused. Select Refresh now to retry this captured snapshot.');
  await expect(history(page)).toContainText('Historical evidence · automatic refresh paused');
  await expect(history(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  const failed = session.reads.filter(item => item.source === 'activity/live').at(-1);
  expect(new URL(failed.url).searchParams.get('snapshot')).toBe('55555555-5555-4555-8555-555555555555');
  session.handler = undefined;
  const retry = history(page).getByRole('button', { name: 'Refresh now', exact: true });
  await retry.focus();
  await retry.press('Enter');
  await expect(history(page).getByRole('alert')).toHaveCount(0);
  await expect(history(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  expect(session.reads.filter(item => item.source === 'activity/live').at(-1).url).toBe(failed.url);
});

test('live failure discloses backoff only while polling is active and paused recovery stays explicit', async ({ page }) => {
  await open(page);
  const session = sessions.get(page);
  session.handler = async (route, item) => item.source === 'activity/live' ? (await json(route, {}, 503), true) : false;
  await poll(page);
  await expect(live(page).getByRole('alert')).toHaveText('Refresh failed for the current request. Automatic live retries use backoff.');
  const count = session.reads.filter(item => item.source === 'activity/live').length;
  await live(page).getByRole('button', { name: 'Pause', exact: true }).click();
  await expect(live(page).getByRole('alert')).toHaveText('Refresh failed for the current request. Automatic refresh is paused. Select Refresh now to retry this request.');
  expect(session.reads.filter(item => item.source === 'activity/live')).toHaveLength(count);
  await expect(live(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  session.handler = undefined;
  await live(page).getByRole('button', { name: 'Refresh now', exact: true }).click();
  await expect(live(page).getByRole('alert')).toHaveCount(0);
  await expect(live(page).getByRole('button', { name: 'Resume', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(live(page).getByRole('button', { name: 'Inspect 71/0', exact: true })).toBeVisible();
  expect(session.reads.filter(item => item.source === 'activity/live')).toHaveLength(count + 1);
});
