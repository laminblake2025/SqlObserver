import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, alertId, episodeId, serverNowUtc, access, notificationBackend, snoozeInbox } from './alertSnoozeFixture.mjs';
const sessions = new WeakMap(), origin = 'http://127.0.0.1:4328';
const panel = page => page.locator('.alerts-screen');
const notifications = page => panel(page).getByRole('region', { name: 'Alert notifications', exact: true });
const snooze = page => notifications(page).getByRole('button', { name: 'Snooze notifications', exact: true });
const reason = page => notifications(page).getByRole('textbox', { name: 'Notification reason', exact: true });
const retryOperation = page => notifications(page).getByRole('button', { name: 'Retry notification operation', exact: true });
const retryStatus = page => notifications(page).getByRole('button', { name: 'Retry notification status', exact: true });
const refreshStatus = page => notifications(page).getByRole('button', { name: 'Refresh notification status', exact: true });
async function json(route, value, status = 200) { await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) }); }
function held() {
  let announce, unlock, complete;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { unlock = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async release() { unlock(); await finished; }, async handle(route, value, status = 200) {
    announce(route.request()); await gate; try { await json(route, value, status); }
    catch (failure) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure; } finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const session = { backend: notificationBackend(), allowed: true, legacy: false, state: 'firing', total: 1,
    inboxReads: [], statusReads: [], posts: [], acknowledgements: [], unexpected: [], handler: undefined };
  sessions.set(page, session); await page.clock.setFixedTime(new Date(serverNowUtc));
  page.on('pageerror', failure => session.unexpected.push(`Page error: ${failure.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async reason => { session.unexpected.push(`${reason}: ${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject('External request');
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (request.method() === 'GET' && url.pathname === '/api/v1/me' && [alpha, beta].includes(url.searchParams.get('targetId')))
      return json(route, access(url.searchParams.get('targetId'), session.allowed));
    const acknowledgement = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/acknowledge$/u.exec(url.pathname);
    if (acknowledgement && request.method() === 'POST' && [alpha, beta].includes(acknowledgement[1]) && !url.search) {
      session.acknowledgements.push(request.postDataJSON());
      return session.acknowledgements.length === 1 ? json(route, {}, 503) : route.fulfill({ status: 204 });
    }
    const notification = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/(snooze|unsnooze)$/u.exec(url.pathname);
    if (notification && [alpha, beta].includes(notification[1]) && !url.search) {
      const item = { type: request.method() === 'GET' ? 'status' : 'mutation', targetId: notification[1], alertId: notification[2], action: notification[3] };
      if (item.type === 'status' && item.action !== 'snooze') return reject('Unsupported notification read');
      if (item.type === 'mutation') {
        if (request.method() !== 'POST') return reject('Unsupported notification method');
        item.command = request.postDataJSON(); item.rawBody = request.postData();
        expect(Object.keys(item.command).sort()).toEqual(item.action === 'snooze'
          ? ['endsAtUtc', 'expectedEpisodeId', 'expectedRevision', 'operationId', 'reason']
          : ['expectedEpisodeId', 'expectedRevision', 'operationId', 'reason']);
        expect(request.headers()['content-type']).toBe('application/json'); session.posts.push(item);
      } else session.statusReads.push(item);
      if (session.handler && await session.handler(route, item)) return;
      try { return json(route, item.type === 'status' ? session.backend.read(item.targetId, item.alertId)
        : session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)); }
      catch (failure) { return json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, failure.status ?? 503); }
    }
    const target = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/active$/u.exec(url.pathname);
    if (request.method() === 'GET' && (url.pathname === '/api/v1/alerts/active' || target && [alpha, beta].includes(target[1]))
      && url.searchParams.get('limit') === '100' && !url.searchParams.has('cursor')) {
      const item = { type: 'inbox', kind: target ? 'target' : 'fleet', targetId: target?.[1] ?? alpha }; session.inboxReads.push(item);
      if (session.handler && await session.handler(route, item)) return;
      return json(route, snoozeInbox(item.kind, item.targetId, session));
    }
    return reject('Unregistered native API');
  });
});
test.afterEach(async ({ page }) => { expect(sessions.get(page).unexpected, 'Registered native routes only; no external requests or page errors').toEqual([]); });
async function mount(page, kind = 'target') {
  await page.goto(`/component-tests/alert-snooze.html#/alerts?${kind === 'target' ? `target=${alpha}&` : ''}range=1h`);
  await expect(page.getByRole('heading', { name: 'Alert notification harness', exact: true })).toBeVisible();
  await expect(panel(page).getByRole('table')).toContainText('Alert 001 g1');
}
async function ready(page) { await expect(notifications(page)).toContainText('No active notification snooze at server read'); await reason(page).fill('DBA investigation'); }
async function unchangedInbox(page, state = 'firing') {
  await expect(panel(page).getByRole('table')).toContainText('Alert 001 g1');
  await expect(panel(page).getByRole('table').getByRole('row').last()).toContainText(state);
}
for (const kind of ['target', 'fleet']) {
  test(`${kind}: auto-read fences legacy rows, snooze/unsnooze preserve visible state and render exact current provenance`, async ({ page }) => {
    const session = sessions.get(page); session.legacy = true; await mount(page, kind); await ready(page);
    await notifications(page).getByRole('combobox', { name: 'Snooze duration', exact: true }).selectOption('15');
    await reason(page).fill('<img src=x onerror=alert(1)> DBA investigation'); await snooze(page).click();
    await expect(notifications(page)).toContainText('Notifications snoozed at server read');
    await expect(panel(page).getByRole('complementary').locator('.evidence-status')).toContainText('Received row: delivery enabled');
    await expect(panel(page).getByRole('complementary').locator('.evidence-status')).toContainText('firing');
    expect(session.posts[0].command).toMatchObject({ expectedEpisodeId: episodeId, expectedRevision: 7,
      endsAtUtc: '2026-09-26T13:15:00.123456Z', reason: '<img src=x onerror=alert(1)> DBA investigation' });
    await expect(notifications(page).locator('time').first()).toHaveAttribute('datetime', '2026-09-26T13:15:00.123456Z');
    await expect(notifications(page)).toContainText('Server as of'); await expect(notifications(page)).toContainText('separate from the retained inbox cutoff');
    await expect(notifications(page).locator('img, script')).toHaveCount(0); await unchangedInbox(page);
    await expect(panel(page)).toContainText('Blocking evidence is stale');
    await reason(page).fill('Investigation complete'); await notifications(page).getByRole('button', { name: 'Unsnooze notifications', exact: true }).click();
    await expect(notifications(page)).toContainText('No active notification snooze at server read'); await unchangedInbox(page);
    expect(session.posts[1].command).toMatchObject({ expectedRevision: 8, expectedEpisodeId: episodeId, reason: 'Investigation complete' });
    expect(session.posts[1].command).not.toHaveProperty('endsAtUtc'); expect(session.posts[1].command.operationId).not.toBe(session.posts[0].command.operationId);
    expect(session.statusReads).toHaveLength(3); expect(session.inboxReads).toHaveLength(1);
  });
  test(`${kind}: confirmed mutation with failed readback retries GET only and never locally marks suppression`, async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => item.type === 'status' && session.statusReads.length === 2
      ? (await json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, 503), true) : false;
    await snooze(page).click(); await expect(retryStatus(page)).toBeVisible();
    await expect(notifications(page)).toContainText('operation was recorded'); await expect(notifications(page)).not.toContainText('Notifications snoozed at server read');
    await expect(notifications(page)).not.toContainText('PRIVATE_PROVIDER_DETAIL'); await unchangedInbox(page);
    await retryStatus(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
    expect(session.posts).toHaveLength(1); expect(session.backend.operations.size).toBe(1); expect(session.statusReads).toHaveLength(3);
  });
  test(`${kind}: uncertain replay after expiry keeps the exact immutable payload, then fresh intent uses current server clock`, async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    await notifications(page).getByRole('combobox', { name: 'Snooze duration', exact: true }).selectOption('15');
    session.handler = async (route, item) => {
      if (item.type !== 'mutation' || session.posts.length !== 1) return false;
      session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody); await route.abort('failed'); return true;
    };
    await snooze(page).click(); await expect(retryOperation(page)).toBeVisible();
    await reason(page).fill('Changed draft must not alter retry');
    session.backend.setNow('2026-09-26T13:16:00.123456Z'); await page.clock.setFixedTime(new Date('2026-09-26T13:16:00.123Z'));
    await page.getByRole('button', { name: 'Workspace tick', exact: true }).click(); await retryOperation(page).click();
    await expect(notifications(page)).toContainText('No active notification snooze at server read');
    expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody); expect(session.backend.operations.size).toBe(1);
    await snooze(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
    expect(session.posts[2].command.operationId).not.toBe(session.posts[0].command.operationId);
    expect(session.posts[2].command).toMatchObject({ expectedRevision: 8, endsAtUtc: '2026-09-26T13:31:00.123456Z', reason: 'Changed draft must not alter retry' });
    await unchangedInbox(page);
  });
  test(`${kind}: failed explicit refresh preserves uncertain operation until a validated fresh status read`, async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => item.type === 'mutation' || item.type === 'status'
      ? (await json(route, {}, 503), true) : false;
    await snooze(page).click(); await expect(retryOperation(page)).toBeVisible();
    await refreshStatus(page).click(); await expect(retryStatus(page)).toBeVisible(); await expect(retryOperation(page)).toBeVisible();
    session.handler = undefined; await retryOperation(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
    expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody);
  });
  test(`${kind}: failed latest and failed current status preserve the exact uncertain snooze retry`, async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => item.type === 'mutation' || item.type === 'inbox' || item.type === 'status'
      ? (await json(route, {}, 503), true) : false;
    await snooze(page).click(); await expect(retryOperation(page)).toBeVisible();
    await panel(page).getByRole('button', { name: /^Return (inbox|server alerts) to latest$/u }).click();
    await expect(panel(page).getByRole('button', { name: 'Retry alert read', exact: true })).toBeVisible();
    await unchangedInbox(page); await expect(retryOperation(page)).toBeVisible();
    await refreshStatus(page).click(); await expect(retryStatus(page)).toBeVisible(); await expect(retryOperation(page)).toBeVisible();
    session.handler = undefined; await retryOperation(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
    expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody);
  });
  test(`${kind}: revision conflict requires explicit refresh and new intent rather than retargeting the failed operation`, async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => {
      if (item.type !== 'mutation' || session.posts.length !== 1) return false;
      session.backend.statuses.set(`${alpha}:${alertId(0)}`, { ...session.backend.read(alpha, alertId(0)), revision: 8 });
      await json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, 409); return true;
    };
    await snooze(page).click(); await expect(notifications(page)).toContainText('episode or revision changed'); await expect(retryOperation(page)).toHaveCount(0);
    await expect(snooze(page)).toBeDisabled(); await refreshStatus(page).click(); await expect(snooze(page)).toBeEnabled();
    await snooze(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
    expect(session.posts[1].command.expectedRevision).toBe(8); expect(session.posts[1].command.operationId).not.toBe(session.posts[0].command.operationId);
  });
  test(`${kind}: transient initial status failure keeps inbox evidence and offers GET-only retry`, async ({ page }) => {
    const session = sessions.get(page); session.handler = async (route, item) => item.type === 'status' && session.statusReads.length === 1
      ? (await json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, 503), true) : false;
    await mount(page, kind); await expect(retryStatus(page)).toBeVisible(); await unchangedInbox(page);
    await reason(page).fill('Investigation'); await expect(snooze(page)).toBeDisabled(); await retryStatus(page).click(); await expect(snooze(page)).toBeEnabled();
    expect(session.posts).toHaveLength(0); expect(session.statusReads).toHaveLength(2);
  });
  for (const status of [401, 403]) for (const source of ['mutation', 'readback']) test(`${kind}: definitive ${source} HTTP ${status} clears protected rows, details and pending action ownership`, async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => item.type === (source === 'mutation' ? 'mutation' : 'status')
      ? (await json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, status), true) : false;
    await snooze(page).click(); await expect(panel(page).getByRole('alert')).toContainText('Alert access was denied');
    await expect(panel(page).getByRole('table')).not.toContainText('Alert 001 g1'); await expect(notifications(page)).toHaveCount(0);
    await page.getByRole('button', { name: 'Workspace tick', exact: true }).click(); expect(session.inboxReads).toHaveLength(1);
    await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_DETAIL');
  });
}
for (const context of ['target', 'revision', 'window', 'unmount']) test(`target ${context} change cancels held mutation and excludes its late success`, async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page); const pending = held();
  session.handler = async (route, item) => item.type === 'mutation'
    ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)) : false;
  await snooze(page).click(); const request = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  if (context === 'target') await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(beta);
  if (context === 'revision') await page.getByRole('button', { name: 'Advance revision', exact: true }).click();
  if (context === 'window') await page.getByRole('combobox', { name: 'Window context', exact: true }).selectOption('custom');
  if (context === 'unmount') await page.getByRole('button', { name: 'Unmount alerts', exact: true }).click();
  await aborted; await pending.release();
  if (context === 'unmount') {
    await expect(panel(page)).toHaveCount(0); await expect(notifications(page)).toHaveCount(0);
  } else {
    await expect(panel(page)).not.toContainText('Immutable operation receipt:');
    const commit = JSON.parse(await page.getByRole('status', { name: 'First committed notification context', exact: true }).textContent());
    expect(commit.text).not.toContain('Immutable operation receipt:');
  }
});
test('target authority removal stops held mutation, and restored authority retains the exact retry identity', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page); const pending = held();
  session.handler = async (route, item) => item.type === 'mutation' && session.posts.length === 1
    ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)) : false;
  await snooze(page).click(); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).uncheck(); await aborted; await pending.release();
  await expect(snooze(page)).toHaveCount(0); await expect(notifications(page)).toContainText('Operator or Target Administrator');
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).check(); await expect(retryOperation(page)).toBeVisible();
  await retryOperation(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
  expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody);
});
test('fleet held mutation owns duplicate clicks and ticks; cancellation retries the same durable operation', async ({ page }) => {
  const session = sessions.get(page); await mount(page, 'fleet'); await ready(page); const pending = held();
  session.handler = async (route, item) => item.type === 'mutation' && session.posts.length === 1
    ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)) : false;
  await snooze(page).evaluate(button => { button.click(); button.click(); }); const request = await pending.started;
  await page.getByRole('button', { name: 'Workspace tick', exact: true }).click(); expect(request.failure()).toBeNull(); expect(session.posts).toHaveLength(1);
  await expect(panel(page).getByRole('button', { name: 'Acknowledge', exact: true })).toBeDisabled();
  await expect(panel(page).getByRole('button', { name: 'Return inbox to latest', exact: true })).toBeDisabled();
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await notifications(page).getByRole('button', { name: 'Cancel notification request', exact: true }).click(); await aborted; await pending.release();
  await retryOperation(page).click(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
  expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody); expect(session.backend.operations.size).toBe(1); expect(session.inboxReads).toHaveLength(1);
});
test('target ABA fences a late receipt; returning displays only independently read current metadata', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page); const pending = held();
  session.handler = async (route, item) => item.type === 'mutation'
    ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)) : false;
  await snooze(page).click(); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(beta); await aborted;
  await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(alpha);
  await expect(notifications(page)).toContainText('Notifications snoozed at server read'); await pending.release();
  await expect(notifications(page)).not.toContainText('Immutable operation receipt:'); await expect(retryOperation(page)).toHaveCount(0);
  expect(session.posts).toHaveLength(1); await unchangedInbox(page);
});
test('changing selected alert cancels a held status read and never substitutes its response', async ({ page }) => {
  const session = sessions.get(page); session.total = 2; const pending = held();
  session.handler = async (route, item) => item.type === 'status' && item.alertId === alertId(0)
    ? pending.handle(route, { ...session.backend.read(alpha, alertId(0)), snoozeReason: 'OLD_SELECTED_PRIVATE_REASON' }) : false;
  await mount(page); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await panel(page).getByRole('button', { name: 'Alert 002 g1', exact: true }).click(); await aborted; await pending.release();
  await expect(panel(page).getByRole('complementary', { name: 'Alert 002 g1', exact: true })).toBeVisible();
  await expect(notifications(page)).toContainText('No active notification snooze at server read');
  await expect(panel(page)).not.toContainText('OLD_SELECTED_PRIVATE_REASON'); expect(session.posts).toHaveLength(0);
});
test('a different current episode requires returning the inbox to latest and cannot retarget a received row', async ({ page }) => {
  const session = sessions.get(page); session.handler = async (route, item) => item.type === 'status'
    ? (await json(route, { ...session.backend.read(item.targetId, item.alertId), episodeId: beta, revision: 9 }), true) : false;
  await mount(page); await expect(notifications(page).getByRole('alert')).toContainText('episode differs from this received row');
  await reason(page).fill('Investigation'); await expect(snooze(page)).toBeDisabled(); expect(session.posts).toHaveLength(0); await unchangedInbox(page);
});
test('Viewer sees received snooze metadata but has no status reads or write actions', async ({ page }) => {
  const session = sessions.get(page); session.allowed = false;
  session.projectionOverrides = { snoozedUntilUtc: '2026-09-26T14:00:00.123456Z', snoozeReason: 'DBA investigation', snoozed: true, deliverySuppressed: true };
  await page.goto(`/component-tests/alert-snooze.html#/alerts?range=1h`);
  await expect(notifications(page)).toContainText('Operator or Target Administrator');
  await expect(notifications(page)).toContainText('Received inbox snooze expiry'); await expect(notifications(page)).toContainText('DBA investigation');
  await expect(panel(page).getByRole('complementary').locator('.evidence-status')).toContainText('Received row: delivery suppressed');
  await expect(snooze(page)).toHaveCount(0); expect(session.statusReads).toHaveLength(0); expect(session.posts).toHaveLength(0);
});
for (const kind of ['target', 'fleet']) test(`${kind}: an uncertain acknowledgement retry cannot dispatch while snooze owns a pending mutation`, async ({ page }) => {
  const session = sessions.get(page); await mount(page, kind); await ready(page);
  await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click();
  const retryAck = panel(page).getByRole('button', { name: 'Retry acknowledgement', exact: true }); await expect(retryAck).toBeVisible();
  const pending = held(); session.handler = async (route, item) => item.type === 'mutation'
    ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)) : false;
  await snooze(page).click(); await pending.started; await expect(retryAck).toBeDisabled();
  await retryAck.evaluate(button => button.click()); expect(session.acknowledgements).toHaveLength(1);
  await pending.release(); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
  await expect(retryAck).toBeEnabled(); await retryAck.click(); expect(session.acknowledgements).toHaveLength(2);
  expect(session.acknowledgements[1]).toEqual(session.acknowledgements[0]);
});
for (const width of [320, 390, 768, 1440]) test(`snooze controls remain readable, keyboard accessible and dark at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1200 }); await mount(page, 'fleet'); await ready(page);
  await notifications(page).getByRole('combobox', { name: 'Snooze duration', exact: true }).selectOption('15');
  await snooze(page).focus(); await page.keyboard.press('Enter'); await expect(notifications(page)).toContainText('Notifications snoozed at server read');
  await unchangedInbox(page); await expect(notifications(page).getByRole('button', { name: 'Unsnooze notifications', exact: true })).toBeEnabled();
  await refreshStatus(page).focus(); await expect(refreshStatus(page)).toBeFocused();
  const focus = await refreshStatus(page).evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`snooze-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-alert-snooze/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await page.screenshot({ path: `${directory}/snooze-${width}.png`, fullPage: true });
});
