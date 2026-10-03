import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, alertId, inboxPage, decodeCursor, access, providerText } from './alertTriageFixture.mjs';
const origin = 'http://127.0.0.1:4328', sessions = new WeakMap();
const panel = page => page.locator('.alerts-screen');
const table = page => panel(page).getByRole('table');
const more = page => panel(page).getByRole('button', { name: 'Load more', exact: true });
const latest = page => panel(page).getByRole('button', { name: /^Return (inbox|server alerts) to latest$/u });
const tick = page => page.getByRole('button', { name: 'Workspace tick', exact: true });
const reads = page => sessions.get(page).reads;
const posts = page => sessions.get(page).posts;
async function json(route, body, status = 200) { await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) }); }
function held(status = 200, change = body => body) {
  let announce, unlock, complete;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { unlock = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async release() { unlock(); await finished; }, async handle(route, item) {
    announce({ request: route.request(), item }); await gate;
    try { await json(route, status === 200 ? change(item.type === 'read' ? inboxPage(item) : {}) : { detail: providerText }, status); }
    catch (failure) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure; }
    finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const session = { generation: 1, total: 210, allowed: true, reads: [], posts: [], accessReads: [], notificationReads: [], assignmentReads: [], unexpected: [], handler: undefined };
  sessions.set(page, session); page.on('pageerror', failure => session.unexpected.push(`Page error: ${failure.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async reason => { session.unexpected.push(`${reason}: ${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject('External request');
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const assignment = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/assignment$/u.exec(url.pathname);
    if (assignment && request.method() === 'GET' && [alpha, beta].includes(assignment[1]) && !url.search) {
      session.assignmentReads.push({ target: assignment[1], alertId: assignment[2] });
      return json(route, { targetId: assignment[1], alertId: assignment[2], episodeId: assignment[2], revision: 1, state: 'firing',
        serverNowUtc: '2026-03-12T13:00:00.1234560+00:00', assigneeSid: null, assigneeName: null, assignmentReason: null,
        assignedBy: null, assignedAtUtc: null, eligibleAssignees: [], canAssign: false });
    }
    const notification = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/snooze$/u.exec(url.pathname);
    if (notification && request.method() === 'GET' && [alpha, beta].includes(notification[1]) && !url.search) {
      const item = { type: 'notification', target: notification[1], alertId: notification[2] }; session.notificationReads.push(item);
      if (session.handler && await session.handler(route, item)) return;
      return json(route, { targetId: item.target, alertId: item.alertId, episodeId: item.alertId, revision: 1,
        state: 'firing', serverNowUtc: '2026-03-12T13:00:00.1234560+00:00', snoozedUntilUtc: null,
        snoozeReason: null, snoozedBy: null, snoozedAtUtc: null, snoozed: false, deliverySuppressed: false });
    }
    if (url.pathname === '/api/v1/me' && request.method() === 'GET' && [...url.searchParams.keys()].join('|') === 'targetId' && [alpha, beta].includes(url.searchParams.get('targetId'))) {
      const item = { type: 'access', target: url.searchParams.get('targetId') }; session.accessReads.push(item);
      if (session.handler && await session.handler(route, item)) return;
      return json(route, access(item.target, session.allowed));
    }
    const ack = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/([^/]+)\/acknowledge$/u.exec(url.pathname);
    if (ack && request.method() === 'POST' && [alpha, beta].includes(ack[1]) && !url.search) {
      const item = { type: 'ack', target: ack[1], alertId: ack[2], command: request.postDataJSON() };
      if (Object.keys(item.command).join('|') !== 'operationId' || !/^[0-9a-f-]{36}$/u.test(item.command.operationId)) return reject('Invalid acknowledgement command');
      session.posts.push(item); if (session.handler && await session.handler(route, item)) return; return json(route, {});
    }
    const targetMatch = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/active$/u.exec(url.pathname);
    const kind = url.pathname === '/api/v1/alerts/active' ? 'fleet' : targetMatch && [alpha, beta].includes(targetMatch[1]) ? 'target' : undefined;
    if (!kind || request.method() !== 'GET' || url.searchParams.get('limit') !== '100' || [...url.searchParams.keys()].sort().join('|') !== (url.searchParams.has('cursor') ? 'cursor|limit' : 'limit')) return reject('Unregistered native alert API');
    const cursor = url.searchParams.get('cursor');
    let item = { type: 'read', kind, target: targetMatch?.[1] ?? alpha, generation: session.generation, index: 0, total: session.total, cursor };
    if (cursor) { try { item = { ...item, ...decodeCursor(cursor, kind) }; } catch { return reject('Invalid native cursor'); } }
    session.reads.push(item); if (session.handler && await session.handler(route, item)) return;
    return json(route, inboxPage(item));
  });
});
test.afterEach(async ({ page }) => { expect(sessions.get(page).unexpected, 'Only registered native API traffic, no external traffic or page errors').toEqual([]); });
async function mount(page, kind = 'target', extra = '') {
  await page.goto(`/component-tests/alert-triage.html#/alerts?${kind === 'target' ? `target=${alpha}&` : ''}range=1h${extra}`);
  await expect(page.getByRole('heading', { name: 'Alert triage harness', exact: true })).toBeVisible();
  await expect(kind === 'target' ? table(page).getByRole('button', { name: 'Alert 001 g1', exact: true })
    : table(page).getByRole('cell', { name: 'Alert 001 g1', exact: true })).toBeVisible();
}
async function rows(page, count) { await expect(table(page).getByRole('row')).toHaveCount(count + 1 + (count > 0 && await page.getByRole('combobox', { name: 'Group by', exact: true }).count() ? 1 : 0)); }
async function select(page, index, kind) {
  const name = `Alert ${String(index + 1).padStart(3, '0')} g1`;
  await panel(page).getByRole('button', { name: kind === 'fleet' ? `Select ${name} on Alpha` : name, exact: true }).click();
  await expect(panel(page).getByRole('complementary', { name, exact: true })).toBeVisible();
}
for (const kind of ['target', 'fleet']) {
  test(`${kind}: held continuation owns duplicate clicks and workspace ticks, then preserves visited pages`, async ({ page }) => {
    await mount(page, kind); const session = sessions.get(page), pending = held();
    session.handler = (route, item) => item.type === 'read' && item.cursor ? pending.handle(route, item) : false;
    await more(page).evaluate(button => { button.click(); button.click(); }); const started = await pending.started;
    session.generation = 2; await tick(page).click(); expect(reads(page)).toHaveLength(2);
    expect(started.request.failure()).toBeNull(); await rows(page, 100); await pending.release(); await rows(page, 200);
    await expect(panel(page).locator('.alert-list .alert-triage-receipt')).toContainText('2 received page(s), 200 loaded rows');
    await tick(page).click(); expect(reads(page)).toHaveLength(2); await expect(table(page)).toContainText('Alert 101 g1');
  });
  test(`${kind}: cursor failure and cancellation retry the exact native cursor without discarding loaded rows`, async ({ page }) => {
    await mount(page, kind); const session = sessions.get(page), pending = held(503);
    session.handler = (route, item) => item.type === 'read' && item.cursor ? pending.handle(route, item) : false;
    await more(page).click(); await pending.started; await pending.release(); await expect(panel(page).getByRole('alert')).toBeVisible(); await rows(page, 100);
    const failedCursor = reads(page).at(-1).cursor; session.handler = undefined;
    await panel(page).getByRole('button', { name: 'Retry alert read', exact: true }).click(); await rows(page, 200); expect(reads(page).at(-1).cursor).toBe(failedCursor);
    const cancelled = held(); session.handler = (route, item) => item.type === 'read' && item.cursor ? cancelled.handle(route, item) : false;
    await more(page).click(); const started = await cancelled.started; await panel(page).getByRole('button', { name: 'Cancel alert read', exact: true }).click();
    await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await cancelled.release(); await rows(page, 200);
    session.handler = undefined; await panel(page).getByRole('button', { name: 'Retry alert read', exact: true }).click(); await rows(page, 210);
    expect(reads(page).at(-1).cursor).toBe(started.item.cursor);
  });
  test(`${kind}: explicit latest is success-only, failure/pending retain old pages and success resumes idle polling`, async ({ page }) => {
    await mount(page, kind); await more(page).click(); await rows(page, 200); const session = sessions.get(page), failed = held(503); session.generation = 2;
    session.handler = (route, item) => item.type === 'read' && !item.cursor ? failed.handle(route, item) : false;
    await latest(page).click(); await failed.started; await rows(page, 200); await tick(page).click(); expect(reads(page)).toHaveLength(3);
    await failed.release(); await rows(page, 200); await expect(panel(page).getByRole('alert')).toBeVisible();
    session.handler = undefined; await panel(page).getByRole('button', { name: 'Retry alert read', exact: true }).click(); await rows(page, 100);
    await expect(table(page)).toContainText('Alert 001 g2'); session.generation = 3; await tick(page).click(); await expect(table(page)).toContainText('Alert 001 g3');
  });
  for (const status of [401, 403]) test(`${kind}: HTTP ${status} removes denied receipt, details/actions; read200 cannot restore write authority`, async ({ page }) => {
    await mount(page, kind); await expect(panel(page).getByRole('button', { name: 'Acknowledge', exact: true })).toBeVisible();
    const session = sessions.get(page); session.allowed = false;
    session.handler = async (route, item) => { if (item.type !== 'read' || !item.cursor) return false; await json(route, { detail: providerText }, status); return true; };
    await more(page).click(); await expect(panel(page).getByRole('alert')).toContainText('Alert access was denied'); await rows(page, 0);
    await expect(panel(page).getByRole('complementary')).toHaveCount(0); await expect(panel(page).getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
    await expect(panel(page)).not.toContainText(providerText); session.handler = undefined; await latest(page).click(); await rows(page, 100);
    await expect(panel(page)).toContainText('Operator or Target Administrator'); await expect(panel(page).getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  });
  test(`${kind}: acknowledgement survives ticks and duplicate clicks; cancellation/uncertain retry preserve operation ID`, async ({ page }) => {
    await mount(page, kind); const session = sessions.get(page), pending = held();
    session.handler = (route, item) => item.type === 'ack' ? pending.handle(route, item) : false;
    const ack = panel(page).getByRole('button', { name: 'Acknowledge', exact: true }); await expect(ack).toBeVisible();
    await ack.evaluate(button => { button.click(); button.click(); }); const started = await pending.started;
    await tick(page).click(); expect(reads(page)).toHaveLength(1); expect(posts(page)).toHaveLength(1); expect(started.request.failure()).toBeNull();
    await panel(page).getByRole('button', { name: 'Cancel acknowledgement', exact: true }).click(); await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
    const failed = held(503); session.handler = (route, item) => item.type === 'ack' ? failed.handle(route, item) : false;
    await panel(page).getByRole('button', { name: 'Retry acknowledgement', exact: true }).click(); await failed.started; await failed.release();
    expect(posts(page)[1].command.operationId).toBe(posts(page)[0].command.operationId);
    session.handler = undefined; await panel(page).getByRole('button', { name: 'Retry acknowledgement', exact: true }).click();
    await expect(panel(page).getByRole('status').filter({ hasText: 'Acknowledgement recorded' })).toBeVisible();
    expect(posts(page)[2].command.operationId).toBe(posts(page)[0].command.operationId); await expect(panel(page).getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  });
  test(`${kind}: explicit latest/new first-observed cycle aborts old acknowledgement and never applies its late success`, async ({ page }) => {
    await mount(page, kind); const session = sessions.get(page), pending = held();
    session.handler = (route, item) => item.type === 'ack' ? pending.handle(route, item) : false;
    await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click(); const started = await pending.started;
    session.generation = 2; await latest(page).click(); await expect(table(page)).toContainText('Alert 001 g2');
    await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
    await expect(panel(page)).not.toContainText('Acknowledgement recorded'); session.handler = undefined;
    await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click(); await expect(panel(page)).toContainText('Acknowledgement recorded');
    expect(posts(page)[1].command.operationId).not.toBe(posts(page)[0].command.operationId);
  });
  test(`${kind}: source cutoff mismatch leaves loaded pages unjoined`, async ({ page }) => {
    await mount(page, kind); sessions.get(page).handler = async (route, item) => {
      if (item.type !== 'read' || !item.cursor) return false;
      await json(route, { ...inboxPage(item), snapshotUtc: '2026-03-12T13:00:00.123457Z' }); return true;
    };
    await more(page).click(); await expect(panel(page).getByRole('alert')).toContainText('could not be verified'); await rows(page, 100);
    await expect(table(page)).not.toContainText('Alert 101 g1');
  });
  test(`${kind}: exact URL selection remains selected across ticks with its loaded continuation trail`, async ({ page }) => {
    await mount(page, kind); await more(page).click(); await rows(page, 200); await select(page, 125, kind);
    await tick(page).click(); expect(reads(page)).toHaveLength(2);
    await expect(panel(page).getByRole('complementary', { name: 'Alert 126 g1', exact: true })).toBeVisible();
    expect(new URLSearchParams((await page.evaluate(() => location.hash)).split('?')[1]).get('alert')).toBe(alertId(125));
  });
  test(`${kind}: selection round trip after a held acknowledgement cannot resurrect an inert busy owner`, async ({ page }) => {
    await mount(page, kind); const session = sessions.get(page), pending = held();
    session.handler = (route, item) => item.type === 'ack' ? pending.handle(route, item) : false;
    await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click(); const started = await pending.started;
    await select(page, 1, kind); await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
    await select(page, 0, kind); await expect(panel(page).getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
    await expect(panel(page).getByRole('button', { name: 'Cancel acknowledgement', exact: true })).toHaveCount(0);
    session.handler = undefined; await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click();
    await expect(panel(page)).toContainText('Acknowledgement recorded'); expect(posts(page)[1].command.operationId).toBe(posts(page)[0].command.operationId);
  });
  test(`${kind}: definitive acknowledgement denial removes source rows, detail and action ownership`, async ({ page }) => {
    await mount(page, kind); sessions.get(page).handler = async (route, item) => {
      if (item.type !== 'ack') return false; await json(route, { detail: providerText }, 403); return true;
    };
    await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click();
    await expect(panel(page).getByRole('alert')).toContainText('Alert access was denied'); await rows(page, 0);
    await expect(panel(page).getByRole('complementary')).toHaveCount(0); await expect(panel(page).getByRole('button', { name: 'Cancel acknowledgement', exact: true })).toHaveCount(0);
  });
}
test('target Operator removal during a held acknowledgement cancels ownership with uncertainty and no stuck busy state', async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held(); session.handler = (route, item) => item.type === 'ack' ? pending.handle(route, item) : false;
  await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click(); const started = await pending.started;
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).uncheck();
  await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
  await expect(panel(page)).toContainText('server completion is uncertain'); await expect(panel(page).getByRole('button', { name: 'Cancel acknowledgement', exact: true })).toHaveCount(0);
  await page.getByRole('checkbox', { name: 'Operator permission', exact: true }).check(); session.handler = undefined;
  await panel(page).getByRole('button', { name: 'Retry acknowledgement', exact: true }).click(); await expect(panel(page)).toContainText('Acknowledgement recorded');
  expect(posts(page)[1].command.operationId).toBe(posts(page)[0].command.operationId);
});
for (const status of ['viewer', 'transient', 401, 403]) test(`fleet target /me ${status} on tick stops held acknowledgement truthfully`, async ({ page }) => {
  await mount(page, 'fleet'); const session = sessions.get(page), pending = held();
  session.handler = (route, item) => item.type === 'ack' ? pending.handle(route, item) : false;
  await panel(page).getByRole('button', { name: 'Acknowledge', exact: true }).click(); const started = await pending.started;
  session.allowed = false; session.handler = async (route, item) => {
    if (item.type === 'ack') return pending.handle(route, item);
    if (item.type === 'access' && status !== 'viewer') { await json(route, { detail: providerText }, status === 'transient' ? 503 : status); return true; }
    return false;
  };
  await tick(page).click(); await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
  await expect(panel(page).getByRole('button', { name: 'Cancel acknowledgement', exact: true })).toHaveCount(0);
  if (typeof status === 'number') { await rows(page, 0); await expect(panel(page).getByRole('complementary')).toHaveCount(0); }
  else { await rows(page, 100); await expect(panel(page)).toContainText('server completion is uncertain'); }
  await expect(panel(page)).not.toContainText(providerText); expect(reads(page)).toHaveLength(1);
});
for (const context of ['target', 'revision', 'window', 'database']) test(`target: ${context} context fences receipt in first commit and aborts owned continuation`, async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held();
  session.handler = (route, item) => item.type === 'read' && item.cursor ? pending.handle(route, item) : false;
  await more(page).click(); const started = await pending.started;
  if (context === 'target') await page.getByRole('combobox', { name: 'Inbox context', exact: true }).selectOption(beta);
  if (context === 'revision') await page.getByRole('button', { name: 'Advance revision', exact: true }).click();
  if (context === 'window') await page.getByRole('combobox', { name: 'Window context', exact: true }).selectOption('custom');
  if (context === 'database') await page.getByRole('combobox', { name: 'Database context', exact: true }).selectOption('7');
  const first = JSON.parse(await page.getByRole('status', { name: 'First committed alert context', exact: true }).textContent());
  expect(first.text).not.toContain('Alert 001 g1'); await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
  await rows(page, 100); await expect(table(page)).not.toContainText('Alert 101 g1');
});
test('unmount aborts read ownership and late response cannot publish into a remounted inbox', async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held(); session.handler = (route, item) => item.type === 'read' && item.cursor ? pending.handle(route, item) : false;
  await more(page).click(); const started = await pending.started; await page.getByRole('button', { name: 'Unmount alerts', exact: true }).click();
  await expect.poll(() => started.request.failure()?.errorText).toContain('ERR_ABORTED'); await pending.release();
  session.handler = undefined; session.generation = 2; await page.getByRole('button', { name: 'Mount alerts', exact: true }).click(); await expect(table(page)).toContainText('Alert 001 g2');
  await expect(table(page)).not.toContainText('Alert 101 g1');
});
test('initial exact URL lookup reads native pages until found and does not substitute the first row', async ({ page }) => {
  await mount(page, 'target', `&alert=${alertId(125)}`); await rows(page, 200);
  await expect(panel(page).getByRole('complementary', { name: 'Alert 126 g1', exact: true })).toBeVisible(); expect(reads(page)).toHaveLength(2);
});
test('finite retained bound refuses an unread twenty-first page before IO', async ({ page }) => {
  sessions.get(page).total = 2100; await mount(page);
  for (let index = 1; index < 20; index++) { await more(page).click(); await rows(page, (index + 1) * 100); }
  const count = reads(page).length; await more(page).click(); await expect(panel(page).getByRole('alert')).toContainText('20 pages / 2,000 rows'); expect(reads(page)).toHaveLength(count);
});
for (const width of [320, 390, 768, 1440]) test(`retained dark alert triage keyboard, responsive and accessibility at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await mount(page, 'fleet'); await more(page).click(); await rows(page, 200);
  await page.keyboard.press('Tab'); await page.getByRole('combobox', { name: 'Group by', exact: true }).focus(); await page.keyboard.press('End'); await page.keyboard.press('Enter');
  await expect(page.getByRole('combobox', { name: 'Group by', exact: true })).toHaveValue('none');
  await page.getByRole('textbox', { name: 'Filter loaded alerts', exact: true }).fill('Alert 126 g1');
  await more(page).focus(); await expect(more(page)).toBeFocused();
  const focus = await more(page).evaluate(element => ({ width: parseFloat(getComputedStyle(element).outlineWidth), style: getComputedStyle(element).outlineStyle }));
  expect(focus.width).toBeGreaterThanOrEqual(3); expect(focus.style).not.toBe('none');
  const scroller = panel(page).getByRole('region', { name: 'Fleet alert observations', exact: true }); await scroller.focus();
  if (await scroller.evaluate(element => element.scrollWidth > element.clientWidth)) { await page.keyboard.press('ArrowRight'); await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBeGreaterThan(0); }
  await scroller.evaluate(element => { element.scrollLeft = 0; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  for (const control of await panel(page).locator('button').all()) { const box = await control.boundingBox(); expect(box.x).toBeGreaterThanOrEqual(-1); expect(box.x + box.width).toBeLessThanOrEqual(width + 1); }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`alert-triage-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  await scroller.evaluate(element => { element.scrollLeft = 0; element.scrollTop = 0; element.blur(); });
  await page.evaluate(() => { document.activeElement instanceof HTMLElement && document.activeElement.blur(); window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-workspace-triage/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.screenshot({ path: `${directory}/alert-triage-${width}.png` });
});
