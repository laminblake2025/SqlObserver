import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, alertId, episodeId, serverNowUtc, responders, assignmentBackend, assignmentInbox, assignmentStatus,
  historyPage, operation, access } from './alertAssignmentFixture.mjs';
import { notificationBackend } from './alertSnoozeFixture.mjs';
const origin = 'http://127.0.0.1:4328', sessions = new WeakMap();
const panel = page => page.locator('.alerts-screen');
const inbox = page => panel(page).locator('.alert-list');
const assignment = page => panel(page).getByRole('region', { name: 'Alert assignment', exact: true });
const notifications = page => panel(page).getByRole('region', { name: 'Alert notifications', exact: true });
const history = page => assignment(page).getByRole('region', { name: 'Assignment history', exact: true });
const button = (page, name) => assignment(page).getByRole('button', { name, exact: true });
const choose = page => assignment(page).getByRole('combobox', { name: 'Approved assignee', exact: true });
const reason = page => assignment(page).getByRole('textbox', { name: 'Assignment reason', exact: true });
const current = (page, name) => expect(assignment(page)).toContainText(name ? 'Assigned to ' + name + ' at server read' : 'Unassigned at server read');
async function json(route, body, status = 200) { await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) }); }
function held() {
  let announce, unlock, complete;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { unlock = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async release() { unlock(); await finished; }, async handle(route, body, status = 200) {
    announce(route.request()); await gate; try { await json(route, body, status); }
    catch (failure) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure; } finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const session = { backend: assignmentBackend(), notifications: notificationBackend(), allowed: true, legacy: false, total: 1,
    reads: [], histories: [], posts: [], acknowledgements: [], notificationPosts: [], inboxReads: [], unexpected: [], handler: undefined };
  sessions.set(page, session); await page.clock.setFixedTime(new Date(serverNowUtc));
  page.on('pageerror', failure => session.unexpected.push('Page error: ' + failure.message));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async text => { session.unexpected.push(text + ': ' + request.method() + ' ' + url.href); await route.abort(); };
    if (url.origin !== origin) return reject('External request');
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (url.pathname === '/api/v1/me' && request.method() === 'GET' && [alpha, beta].includes(url.searchParams.get('targetId')))
      return json(route, access(url.searchParams.get('targetId'), session.allowed));
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/assignment(\/history)?$/u.exec(url.pathname);
    if (match && [alpha, beta].includes(match[1])) {
      const item = { type: match[3] ? 'history' : request.method() === 'POST' ? 'mutation' : 'status', targetId: match[1], alertId: match[2] };
      if (item.type === 'history') {
        if (request.method() !== 'GET' || url.searchParams.get('limit') !== '50' ||
          [...url.searchParams.keys()].sort().join('|') !== (url.searchParams.has('beforeOperationId') ? 'beforeOperationId|limit' : 'limit')) return reject('Invalid native history route');
        item.before = url.searchParams.get('beforeOperationId') ?? undefined; session.histories.push(item);
      } else {
        if (url.search || !['GET', 'POST'].includes(request.method())) return reject('Invalid native assignment route');
        if (item.type === 'mutation') {
          item.command = request.postDataJSON(); item.rawBody = request.postData(); session.posts.push(item);
          expect(Object.keys(item.command).sort()).toEqual(['assigneeSid', 'expectedEpisodeId', 'expectedRevision', 'operationId', 'reason']);
          expect(request.headers()['content-type']).toBe('application/json');
        } else session.reads.push(item);
      }
      if (session.handler && await session.handler(route, item)) return;
      try { return json(route, item.type === 'status' ? session.backend.read(item.targetId, item.alertId, session.allowed)
        : item.type === 'history' ? session.backend.history(item.targetId, item.alertId, item.before)
          : session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)); }
      catch (failure) { return json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, failure.status ?? 503); }
    }
    const ack = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/acknowledge$/u.exec(url.pathname);
    if (ack && request.method() === 'POST' && !url.search) {
      const item = { type: 'ack', targetId: ack[1], alertId: ack[2], command: request.postDataJSON() }; session.acknowledgements.push(item);
      if (session.handler && await session.handler(route, item)) return; return route.fulfill({ status: 204 });
    }
    const notification = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/(snooze|unsnooze)$/u.exec(url.pathname);
    if (notification && !url.search) {
      const item = { type: request.method() === 'POST' ? 'notificationMutation' : 'notificationStatus', targetId: notification[1], alertId: notification[2], action: notification[3] };
      if (item.type === 'notificationMutation') { item.command = request.postDataJSON(); item.rawBody = request.postData(); session.notificationPosts.push(item); }
      if (session.handler && await session.handler(route, item)) return;
      try { return json(route, item.type === 'notificationStatus' ? session.notifications.read(item.targetId, item.alertId)
        : session.notifications.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)); }
      catch (failure) { return json(route, {}, failure.status ?? 503); }
    }
    const target = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/active$/u.exec(url.pathname);
    if (request.method() === 'GET' && (url.pathname === '/api/v1/alerts/active' || target && [alpha, beta].includes(target[1])) && url.search === '?limit=100') {
      const item = { type: 'inbox', kind: target ? 'target' : 'fleet', targetId: target?.[1] ?? alpha }; session.inboxReads.push(item);
      if (session.handler && await session.handler(route, item)) return;
      return json(route, assignmentInbox(item.kind, item.targetId, session));
    }
    return reject('Unregistered native API');
  });
});
test.afterEach(async ({ page }) => { expect(sessions.get(page).unexpected, 'Registered native APIs only; no external requests or page errors').toEqual([]); });
async function mount(page, kind = 'target') {
  await page.goto('/component-tests/alert-assignment.html#/alerts?' + (kind === 'target' ? 'target=' + alpha + '&' : '') + 'range=1h');
  await expect(page.getByRole('heading', { name: 'Alert assignment harness', exact: true })).toBeVisible();
  await expect(inbox(page).getByRole('table')).toContainText('Alert 001 g1');
}
async function ready(page, sid = responders[0].sid) {
  await current(page); await choose(page).selectOption(sid); await reason(page).fill('DBA investigation'); await expect(button(page, 'Assign alert')).toBeEnabled();
}
async function unchanged(page) {
  await expect(inbox(page).getByRole('table')).toContainText('Alert 001 g1');
  await expect(inbox(page).getByRole('table').getByRole('row').last()).toContainText('firing');
}
for (const kind of ['target', 'fleet']) {
  test(kind + ': native assign/reassign/null unassign preserve state and read current owner separately from retained evidence', async ({ page }) => {
    const session = sessions.get(page); session.legacy = true; await mount(page, kind); await ready(page); const initialReads = session.reads.length;
    await reason(page).fill('<img src=x onerror=alert(1)> investigation'); await button(page, 'Assign alert').click(); await current(page, responders[0].displayName);
    expect(session.posts[0].command).toMatchObject({ expectedEpisodeId: episodeId, expectedRevision: 7, assigneeSid: responders[0].sid });
    await expect(assignment(page)).toContainText('Assignment server as of'); await expect(assignment(page)).toContainText('separate from retained inbox evidence');
    await expect(assignment(page).locator('img,script')).toHaveCount(0); await unchanged(page);
    await choose(page).selectOption(responders[1].sid); await reason(page).fill('Hand investigation to Morgan'); await button(page, 'Reassign alert').click();
    await current(page, responders[1].displayName); expect(session.posts[1].command.expectedRevision).toBe(8);
    await reason(page).fill('Release ownership'); await button(page, 'Unassign alert').click(); await current(page);
    expect(session.posts[2].command.assigneeSid).toBeNull(); expect(session.posts[2].rawBody).toContain('"assigneeSid":null');
    expect(session.posts[2].command.operationId).not.toBe(session.posts[1].command.operationId);
    await expect(assignment(page)).not.toContainText('Assignment reason: Release ownership');
    await button(page, 'Load assignment history').click(); await expect(history(page).getByRole('table')).toContainText('Morgan Chen → Unassigned');
    await expect(history(page).getByRole('table')).toContainText('Release ownership');
    expect(session.histories).toHaveLength(1); expect(session.reads).toHaveLength(initialReads + 3); expect(session.inboxReads).toHaveLength(1); await unchanged(page);
  });
  test(kind + ': failed committed readback retries GET only without locally changing owner', async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page); const initialReads = session.reads.length;
    session.handler = async (route, item) => item.type === 'status' && session.reads.length === initialReads + 1 ? (await json(route, {}, 503), true) : false;
    await button(page, 'Assign alert').click(); await expect(button(page, 'Retry assignment status')).toBeVisible(); await current(page);
    await expect(assignment(page)).toContainText('Immutable assignment receipt'); await expect(assignment(page)).not.toContainText('PRIVATE_PROVIDER_DETAIL');
    await button(page, 'Retry assignment status').click(); await current(page, responders[0].displayName);
    expect(session.posts).toHaveLength(1); expect(session.reads).toHaveLength(initialReads + 2); await unchanged(page);
  });
  test(kind + ': uncertain operation survives failed latest/status, changed draft and revoked roster with identical replay body', async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => {
      if (item.type !== 'mutation' || session.posts.length !== 1) return false;
      session.backend.change(item.targetId, item.alertId, item.command, item.rawBody); await route.abort('failed'); return true;
    };
    await button(page, 'Assign alert').click(); await expect(button(page, 'Retry assignment operation')).toBeVisible();
    await reason(page).fill('New draft'); await choose(page).selectOption(responders[1].sid); session.backend.setRoster([]);
    session.handler = async (route, item) => ['status', 'inbox'].includes(item.type) ? (await json(route, {}, 503), true) : false;
    await inbox(page).getByRole('button', { name: /^Return (inbox|server alerts) to latest$/u }).click();
    await expect(inbox(page).getByRole('button', { name: 'Retry alert read', exact: true })).toBeVisible();
    await button(page, 'Refresh assignment status').click(); await expect(button(page, 'Retry assignment status')).toBeVisible();
    await expect(button(page, 'Retry assignment operation')).toBeVisible(); session.handler = undefined;
    await button(page, 'Retry assignment operation').click(); await current(page, responders[0].displayName);
    expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody); expect(session.backend.operations.size).toBe(1);
    await expect(assignment(page)).toContainText('No approved responders'); await unchanged(page);
  });
  test(kind + ': uncertain null unassignment retry preserves null and original reason despite changed draft', async ({ page }) => {
    const session = sessions.get(page); session.backend.change(alpha, alertId(0), { operationId: operation(0), expectedEpisodeId: episodeId, expectedRevision: 7, assigneeSid: responders[0].sid, reason: 'Initial owner' });
    await mount(page, kind); await current(page, responders[0].displayName); await reason(page).fill('Release ownership');
    session.handler = async (route, item) => item.type === 'mutation' && session.posts.length === 1 ? (await json(route, {}, 503), true) : false;
    await button(page, 'Unassign alert').click(); await expect(button(page, 'Retry assignment operation')).toBeVisible();
    await choose(page).selectOption(responders[1].sid); await reason(page).fill('Changed draft'); await button(page, 'Retry assignment operation').click(); await current(page);
    expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody); expect(session.posts[1].command.assigneeSid).toBeNull();
  });
  test(kind + ': conflict requires validated explicit status refresh and a new operation', async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.backend.statuses.set(alpha + ':' + alertId(0), assignmentStatus({ revision: 8 }));
    await button(page, 'Assign alert').click(); await expect(assignment(page)).toContainText('episode, revision or approved responder changed');
    await expect(button(page, 'Retry assignment operation')).toHaveCount(0); await expect(button(page, 'Assign alert')).toBeDisabled();
    await button(page, 'Refresh assignment status').click(); await expect(button(page, 'Assign alert')).toBeEnabled();
    await button(page, 'Assign alert').click(); await current(page, responders[0].displayName);
    expect(session.posts[1].command.expectedRevision).toBe(8); expect(session.posts[1].command.operationId).not.toBe(session.posts[0].command.operationId);
  });
  test(kind + ': pending assignment excludes duplicate writes, acknowledgement retry, snooze and latest', async ({ page }) => {
    const session = sessions.get(page); await mount(page, kind); await ready(page);
    session.handler = async (route, item) => item.type === 'ack' ? (await json(route, {}, 503), true) : false;
    await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click();
    const retryAck = panel(page).getByRole('button', { name: 'Retry acknowledgement', exact: true }); await expect(retryAck).toBeVisible();
    await notifications(page).getByRole('textbox', { name: 'Notification reason', exact: true }).fill('DBA investigation');
    const pending = held(); session.handler = (route, item) => item.type === 'mutation'
      ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)) : false;
    await button(page, 'Assign alert').evaluate(node => {
      node.click(); node.click();
      // The second handler runs before React can commit disabled state.
      Array.from(document.querySelectorAll('button')).find(item => /^Return (inbox|server alerts) to latest$/u.test(item.textContent ?? ''))?.click();
    }); await pending.started;
    await expect(retryAck).toBeDisabled(); await expect(notifications(page).getByRole('button', { name: 'Snooze notifications', exact: true })).toBeDisabled();
    await expect(inbox(page).getByRole('button', { name: /^Return (inbox|server alerts) to latest$/u })).toBeDisabled();
    await retryAck.evaluate(node => node.click()); expect(session.acknowledgements).toHaveLength(1);
    await page.getByRole('button', { name: 'Workspace tick', exact: true }).click(); expect(session.posts).toHaveLength(1); expect(session.inboxReads).toHaveLength(1);
    await pending.release(); await current(page, responders[0].displayName); await expect(retryAck).toBeEnabled();
    await retryAck.click(); expect(session.acknowledgements).toHaveLength(2); expect(session.acknowledgements[1].command).toEqual(session.acknowledgements[0].command);
  });
}
test('empty roster gives administrator recovery; role loss hides candidates while current owner/history stay readable', async ({ page }) => {
  const session = sessions.get(page); session.backend.setRoster([]); await mount(page); await current(page);
  await expect(assignment(page)).toContainText('Ask the application administrator'); await reason(page).fill('Investigate'); await expect(button(page, 'Assign alert')).toBeDisabled();
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).uncheck();
  await expect(choose(page)).toHaveCount(0); await expect(assignment(page)).toContainText('Operator or Target Administrator');
  await button(page, 'Load assignment history').click(); await expect(history(page)).toContainText('0 assignment changes received'); expect(session.posts).toHaveLength(0);
});
test('Viewer fleet current owner is readable while candidate roster and assignment actions are absent', async ({ page }) => {
  const session = sessions.get(page); session.allowed = false;
  session.backend.statuses.set(alpha + ':' + alertId(0), assignmentStatus({ assigneeSid: responders[0].sid, assigneeName: responders[0].displayName,
    assignmentReason: 'On call', assignedBy: 'S-1-5-21-303', assignedAtUtc: serverNowUtc }));
  await mount(page, 'fleet'); await current(page, responders[0].displayName); await expect(choose(page)).toHaveCount(0);
  await expect(button(page, 'Assign alert')).toHaveCount(0); expect(session.reads).toHaveLength(1); expect(session.posts).toHaveLength(0);
});
test('legacy absent episode plus native 404 never fabricates an assignment precondition', async ({ page }) => {
  const session = sessions.get(page); session.legacy = true; session.handler = async (route, item) => item.type === 'status' ? (await json(route, {}, 404), true) : false;
  await mount(page); await expect(button(page, 'Retry assignment status')).toBeVisible(); await reason(page).fill('Investigate');
  await expect(button(page, 'Assign alert')).toBeDisabled(); expect(session.posts).toHaveLength(0); await unchanged(page);
});
test('changed episode and invalid target identity cannot replace selected alert evidence', async ({ page }) => {
  const session = sessions.get(page); session.handler = async (route, item) => item.type === 'status' ? (await json(route, assignmentStatus({ episodeId: beta })), true) : false;
  await mount(page); await expect(assignment(page).getByRole('alert')).toContainText('episode differs from this received row');
  await reason(page).fill('Investigate'); await choose(page).selectOption(responders[0].sid); await expect(button(page, 'Assign alert')).toBeDisabled();
  session.handler = async (route, item) => item.type === 'status' ? (await json(route, assignmentStatus({ targetId: beta })), true) : false;
  await button(page, 'Refresh assignment status').click(); await expect(button(page, 'Retry assignment status')).toBeVisible(); expect(session.posts).toHaveLength(0); await unchanged(page);
});
for (const status of [401, 403]) for (const source of ['status', 'mutation', 'history']) test('HTTP ' + status + ' from ' + source + ' purges protected receipts and stops polling recovery', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page);
  session.handler = async (route, item) => item.type === source ? (await json(route, { detail: 'PRIVATE_PROVIDER_DETAIL' }, status), true) : false;
  await button(page, source === 'status' ? 'Refresh assignment status' : source === 'mutation' ? 'Assign alert' : 'Load assignment history').click();
  await expect(inbox(page).getByRole('alert')).toContainText('Alert access was denied');
  await expect(assignment(page)).toHaveCount(0); await expect(inbox(page).getByRole('table')).not.toContainText('Alert 001 g1');
  await page.getByRole('button', { name: 'Workspace tick', exact: true }).click(); expect(session.inboxReads).toHaveLength(1); await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_DETAIL');
});
for (const context of ['target', 'revision', 'window', 'selection', 'unmount']) test('held assignment aborts and fences late receipt across ' + context, async ({ page }) => {
  const session = sessions.get(page); session.total = 2; await mount(page); await ready(page); const pending = held();
  session.handler = (route, item) => item.type === 'mutation' ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)) : false;
  await button(page, 'Assign alert').click(); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  if (context === 'target') await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(beta);
  else if (context === 'window') await page.getByRole('combobox', { name: 'Window context', exact: true }).selectOption('custom');
  else if (context === 'selection') await inbox(page).getByRole('button', { name: 'Alert 002 g1', exact: true }).click();
  else await page.getByRole('button', { name: context === 'revision' ? 'Advance revision' : 'Unmount alerts', exact: true }).click();
  await aborted; await pending.release();
  if (context === 'unmount') { await expect(panel(page)).toHaveCount(0); await expect(assignment(page)).toHaveCount(0); }
  else { await expect(assignment(page)).toBeVisible(); await expect(panel(page)).not.toContainText('Immutable assignment receipt'); }
  expect(session.posts).toHaveLength(1);
});
test('target ABA permits new authoritative read but excludes the old aborted receipt', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page); const pending = held();
  session.handler = (route, item) => item.type === 'mutation' ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)) : false;
  await button(page, 'Assign alert').click(); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(beta); await aborted;
  await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(alpha); await current(page, responders[0].displayName);
  await pending.release(); await expect(assignment(page)).not.toContainText('Immutable assignment receipt'); expect(session.posts).toHaveLength(1);
});
test('authority loss aborts owned assignment; authorized retry preserves its original body', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page); const pending = held();
  session.handler = (route, item) => item.type === 'mutation' ? pending.handle(route, session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)) : false;
  await button(page, 'Assign alert').click(); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).uncheck(); await aborted; await pending.release();
  await expect(button(page, 'Retry assignment operation')).toHaveCount(0); await expect(choose(page)).toHaveCount(0); session.handler = undefined;
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).check(); await expect(button(page, 'Retry assignment operation')).toBeVisible();
  await button(page, 'Retry assignment operation').click(); await current(page, responders[0].displayName); expect(session.posts[1].rawBody).toBe(session.posts[0].rawBody);
});
test('history retains full native pages; cancellation retry preserves exact operation anchor', async ({ page }) => {
  const session = sessions.get(page); await mount(page); const pending = held();
  session.handler = (route, item) => item.type !== 'history' ? false : item.before ? pending.handle(route, historyPage({ index: 50 })) : json(route, historyPage()).then(() => true);
  await button(page, 'Load assignment history').click(); await expect(history(page)).toContainText('50 assignment changes received');
  await button(page, 'Load older assignment changes').click(); const request = await pending.started;
  await button(page, 'Cancel assignment history read').click(); await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
  await expect(history(page)).toContainText('50 assignment changes received'); const anchor = session.histories.at(-1).before;
  session.handler = (route, item) => item.type === 'history' ? json(route, historyPage({ index: 50 })).then(() => true) : false;
  await button(page, 'Retry assignment history').click(); await expect(history(page)).toContainText('51 assignment changes received');
  expect(session.histories.at(-1).before).toBe(anchor); expect(anchor).toBe(operation(49)); await expect(button(page, 'Load older assignment changes')).toBeDisabled(); expect(session.posts).toHaveLength(0);
});
test('duplicate history continuation is rejected while retained received changes remain visible', async ({ page }) => {
  const session = sessions.get(page); await mount(page);
  session.handler = (route, item) => item.type === 'history' ? json(route, item.before ? { ...historyPage({ index: 50 }), items: historyPage({ total: 1 }).items } : historyPage()).then(() => true) : false;
  await button(page, 'Load assignment history').click(); await expect(history(page)).toContainText('50 assignment changes received');
  await button(page, 'Load older assignment changes').click(); await expect(history(page).getByRole('alert')).toContainText('history is unavailable');
  await expect(history(page)).toContainText('50 assignment changes received'); expect(session.histories).toHaveLength(2);
});
test('history refresh retains received pages on failure/cancel and replaces only after validated GET success', async ({ page }) => {
  const session = sessions.get(page); await mount(page);
  session.handler = (route, item) => item.type === 'history' ? json(route, historyPage()).then(() => true) : false;
  await button(page, 'Load assignment history').click(); await expect(history(page)).toContainText('50 assignment changes received');
  session.handler = (route, item) => item.type === 'history' ? json(route, {}, 503).then(() => true) : false;
  await button(page, 'Refresh assignment history').click(); await expect(history(page).getByRole('alert')).toContainText('history is unavailable');
  await expect(history(page)).toContainText('50 assignment changes received'); expect(session.histories.at(-1).before).toBeUndefined();
  const pending = held(); session.handler = (route, item) => item.type === 'history' ? pending.handle(route, historyPage({ total: 1 })) : false;
  await button(page, 'Retry assignment history').click(); const request = await pending.started;
  await button(page, 'Cancel assignment history read').click(); await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
  await expect(history(page)).toContainText('50 assignment changes received');
  session.handler = (route, item) => item.type === 'history' ? json(route, historyPage({ total: 1 })).then(() => true) : false;
  await button(page, 'Retry assignment history').click(); await expect(history(page)).toContainText('1 assignment changes received');
  expect(session.histories.slice(1).every(item => item.before === undefined)).toBe(true);
});
test('history refresh after a new mutation reads server ledger and does not append guessed changes', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page);
  await button(page, 'Load assignment history').click(); await expect(history(page)).toContainText('0 assignment changes received');
  await button(page, 'Assign alert').click(); await current(page, responders[0].displayName);
  await expect(history(page)).toContainText('0 assignment changes received');
  await button(page, 'Refresh assignment history').click(); await expect(history(page)).toContainText('1 assignment changes received');
  await expect(history(page).getByRole('table')).toContainText('Unassigned → Alex Rivera'); expect(session.histories).toHaveLength(2);
});
test('native422 approval refusal discloses non-application and requires refreshed approved roster', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page);
  session.handler = async (route, item) => item.type === 'mutation' ? (await json(route, {}, 422), true) : false;
  await button(page, 'Assign alert').click(); await expect(assignment(page)).toContainText('This assignment was not applied');
  await expect(button(page, 'Retry assignment operation')).toHaveCount(0); await expect(button(page, 'Assign alert')).toBeDisabled();
  session.backend.setRoster([]); session.handler = undefined; await button(page, 'Refresh assignment status').click();
  await expect(assignment(page)).toContainText('No approved responders'); expect(session.backend.operations.size).toBe(0);
});
test('inactive current state explains disabled mutation despite a retained firing inbox row', async ({ page }) => {
  const session = sessions.get(page); session.backend.statuses.set(alpha + ':' + alertId(0), assignmentStatus({ state: 'resolved' }));
  await mount(page); await current(page); await choose(page).selectOption(responders[0].sid); await reason(page).fill('Investigate');
  await expect(assignment(page)).toContainText('Current alert state: resolved'); await expect(assignment(page)).toContainText('Only firing or acknowledged alerts can change assignment');
  await expect(button(page, 'Assign alert')).toBeDisabled(); await unchanged(page); expect(session.posts).toHaveLength(0);
});
test('same-name approved responders are distinguishable without assuming unique labels', async ({ page }) => {
  const session = sessions.get(page); session.backend.setRoster(responders.map(item => ({ ...item, displayName: 'Alex Rivera' })));
  await mount(page); await current(page);
  await expect(choose(page).locator('option')).toHaveText(['Choose an approved responder', 'Alex Rivera · ' + responders[0].sid, 'Alex Rivera · ' + responders[1].sid]);
  await choose(page).selectOption(responders[1].sid); await reason(page).fill('Second configured responder'); await button(page, 'Assign alert').click();
  await current(page, 'Alex Rivera'); await expect(assignment(page)).toContainText('Assigned responder identifier: ' + responders[1].sid);
  expect(session.posts[0].command.assigneeSid).toBe(responders[1].sid);
});
test('retained history cap stops after twenty native fifty-row pages', async ({ page }) => {
  const session = sessions.get(page); await mount(page);
  session.handler = (route, item) => item.type === 'history' ? json(route, historyPage({ total: 1001, index: item.before ? Number(item.before.split('-').at(-1)) : 0 })).then(() => true) : false;
  await button(page, 'Load assignment history').click();
  for (let i = 1; i < 20; i++) { await expect(history(page)).toContainText(i * 50 + ' assignment changes received'); await button(page, 'Load older assignment changes').click(); }
  await expect(history(page)).toContainText('1000 assignment changes received'); await expect(button(page, 'Load older assignment changes')).toBeDisabled();
  await expect(history(page)).toContainText('20 pages / 1,000 retained changes'); expect(session.histories).toHaveLength(20);
  session.handler = (route, item) => item.type === 'history' ? json(route, historyPage({ total: 1 })).then(() => true) : false;
  await button(page, 'Refresh assignment history').click(); await expect(history(page)).toContainText('1 assignment changes received');
  expect(session.histories).toHaveLength(21); expect(session.histories.at(-1).before).toBeUndefined();
});
test('history denial fences a parallel latest200 and cannot restore protected receipts', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await current(page); const pending = held();
  session.handler = (route, item) => item.type === 'inbox' ? pending.handle(route, assignmentInbox(item.kind, item.targetId, session))
    : item.type === 'history' ? json(route, {}, 403).then(() => true) : false;
  await inbox(page).getByRole('button', { name: 'Return server alerts to latest', exact: true }).click(); await pending.started;
  await button(page, 'Load assignment history').click(); await expect(inbox(page).getByRole('alert')).toContainText('Alert access was denied');
  await pending.release(); await expect(assignment(page)).toHaveCount(0); await expect(inbox(page).getByRole('table')).not.toContainText('Alert 001 g1');
  await page.getByRole('button', { name: 'Workspace tick', exact: true }).click(); expect(session.inboxReads).toHaveLength(2);
});
test('failed latest retry cannot replace selected evidence while assignment owns a pending write', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page);
  session.handler = (route, item) => item.type === 'inbox' ? json(route, {}, 503).then(() => true) : false;
  await inbox(page).getByRole('button', { name: 'Return server alerts to latest', exact: true }).click();
  const retry = inbox(page).getByRole('button', { name: 'Retry alert read', exact: true }); await expect(retry).toBeVisible();
  const pending = held(); session.handler = (route, item) => item.type === 'mutation' ? pending.handle(route,
    session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)) : false;
  await button(page, 'Assign alert').evaluate(node => { node.click();
    Array.from(document.querySelectorAll('button')).find(item => item.textContent === 'Retry alert read')?.click(); });
  await pending.started; await expect(retry).toBeDisabled(); await retry.evaluate(node => node.click());
  expect(session.inboxReads).toHaveLength(2); expect(session.posts).toHaveLength(1); await unchanged(page);
  await pending.release(); await current(page, responders[0].displayName); await expect(retry).toBeEnabled();
});
test('starting assignment aborts a previously pending latest replacement and retains its retry intent', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page); const latest = held(), mutation = held();
  session.handler = (route, item) => item.type === 'inbox' ? latest.handle(route, assignmentInbox(item.kind, item.targetId, session))
    : item.type === 'mutation' ? mutation.handle(route, session.backend.change(item.targetId, item.alertId, item.command, item.rawBody)) : false;
  await inbox(page).getByRole('button', { name: 'Return server alerts to latest', exact: true }).click();
  const request = await latest.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await button(page, 'Assign alert').click(); await mutation.started; await aborted; await latest.release();
  await unchanged(page); await expect(inbox(page).getByRole('button', { name: 'Retry alert read', exact: true })).toBeDisabled();
  await mutation.release(); await current(page, responders[0].displayName); await expect(inbox(page).getByRole('button', { name: 'Retry alert read', exact: true })).toBeEnabled();
  expect(session.posts).toHaveLength(1); expect(session.inboxReads).toHaveLength(2);
});
test('selected-alert change aborts a held history read and excludes its private late evidence', async ({ page }) => {
  const session = sessions.get(page); session.total = 2; await mount(page); const pending = held();
  session.handler = (route, item) => item.type === 'history' ? pending.handle(route, { ...historyPage({ total: 1 }),
    items: historyPage({ total: 1 }).items.map(value => ({ ...value, reason: 'OLD_HISTORY_PRIVATE_REASON' })) }) : false;
  await button(page, 'Load assignment history').click(); const request = await pending.started, aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await inbox(page).getByRole('button', { name: 'Alert 002 g1', exact: true }).click(); await aborted; await pending.release();
  await expect(assignment(page)).toBeVisible(); await expect(assignment(page)).not.toContainText('OLD_HISTORY_PRIVATE_REASON'); await expect(history(page).getByRole('table')).toHaveCount(0);
});
for (const peer of ['ack', 'notificationMutation']) test('assignment retries cannot dispatch while ' + peer + ' owns the shared lane', async ({ page }) => {
  const session = sessions.get(page); await mount(page); await ready(page);
  session.handler = async (route, item) => item.type === 'mutation' ? (await json(route, {}, 503), true) : false;
  await button(page, 'Assign alert').click(); await expect(button(page, 'Retry assignment operation')).toBeVisible();
  const pending = held(); session.handler = (route, item) => item.type === peer ? pending.handle(route, peer === 'ack' ? {} : session.notifications.change(item.targetId, item.alertId, item.action, item.command, item.rawBody)) : false;
  if (peer === 'ack') await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click();
  else { await notifications(page).getByRole('textbox', { name: 'Notification reason', exact: true }).fill('Investigate'); await notifications(page).getByRole('button', { name: 'Snooze notifications', exact: true }).click(); }
  await pending.started; await expect(button(page, 'Retry assignment operation')).toBeDisabled(); await button(page, 'Retry assignment operation').evaluate(node => node.click());
  expect(session.posts).toHaveLength(1); await pending.release(); await expect(button(page, 'Retry assignment operation')).toBeEnabled();
});
for (const width of [320, 390, 768, 1440]) test('dark, keyboard accessible assignment layout at ' + width + 'px', async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1200 }); await mount(page, 'fleet'); await ready(page);
  await button(page, 'Assign alert').focus(); await page.keyboard.press('Enter'); await current(page, responders[0].displayName);
  await choose(page).selectOption(responders[1].sid); await reason(page).fill('Shift handoff'); await button(page, 'Reassign alert').focus(); await page.keyboard.press('Enter');
  await current(page, responders[1].displayName); await button(page, 'Load assignment history').click(); await expect(history(page)).toContainText('2 assignment changes received'); await unchanged(page);
  await reason(page).focus(); await page.keyboard.press('Tab'); await expect(button(page, 'Unassign alert')).toBeFocused();
  await page.keyboard.press('Tab'); await expect(button(page, 'Refresh assignment status')).toBeFocused();
  const focus = await button(page, 'Refresh assignment status').evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach('assignment-' + width + '-axe', { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-alert-assignment/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => scrollTo({ top: 0, left: 0, behavior: 'instant' })); await page.screenshot({ path: directory + '/assignment-' + width + '.png', fullPage: true });
});
