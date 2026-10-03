import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000001';
const alertId = '44444444-4444-4444-8444-444444444444';
const episodeId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
const now = '2026-10-03T15:00:00.123456Z';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const path = `/api/v1/observation-targets/${target}/alerts/${alertId}`;
const candidates = [{ sid: 'S-1-5-21-100-200-300-1101', displayName: 'SYNTHETIC primary DBA' },
  { sid: 'S-1-5-21-100-200-300-1102', displayName: 'SYNTHETIC on-call DBA' }];
const inbox = page => page.locator('.alerts-screen');
const assignment = page => page.getByRole('region', { name: 'Alert assignment', exact: true });
const row = { alertId, ruleId: '55555555-5555-4555-8555-555555555555', ruleName: 'SYNTHETIC ownership investigation',
  state: 'firing', firstObservedUtc: '2026-09-30T09:00:00Z', firedUtc: '2026-09-30T09:01:00Z',
  value: 12, reason: '12 distinct sessions blocked', deliverySuppressed: false, episodeId, revision: 5,
  snoozed: false, snoozedUntilUtc: null, snoozeReason: null, snoozedBy: null, snoozedAtUtc: null };
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
async function fixture(page, { fleet = true, allowed = true, empty = false } = {}) {
  const session = { gets: 0, posts: [], errors: [], failedReads: 0, uncertain: false, history: [], historyReads: [], operations: new Map(),
    current: { targetId: target, alertId, episodeId, revision: 5, state: 'firing', serverNowUtc: now,
      assigneeSid: null, assigneeName: null, assignmentReason: null, assignedBy: null, assignedAtUtc: null,
      eligibleAssignees: allowed && !empty ? candidates : [], canAssign: allowed } };
  page.on('pageerror', error => session.errors.push(error.message));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin },
    ...(allowed ? [{ name: 'alertOperatorFixture', value: '1', url: origin }] : [])]);
  await page.route('**/api/v1/**', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.pathname === '/api/v1/alerts/active' || url.pathname === `/api/v1/observation-targets/${target}/alerts/active`) {
      return json(route, { ...(fleet ? {} : { targetId: target }), snapshotUtc: now,
        items: [{ ...row, ...(fleet ? { targetId: target, targetName: 'SYNTHETIC SQL 01' } : {}) }], nextCursor: null });
    }
    if (url.pathname === `${path}/snooze` && request.method() === 'GET') {
      return json(route, { targetId: target, alertId, episodeId, revision: session.current.revision, state: 'firing', serverNowUtc: now,
        snoozed: false, deliverySuppressed: false, snoozedUntilUtc: null, snoozeReason: null, snoozedBy: null, snoozedAtUtc: null });
    }
    if (url.pathname === `${path}/assignment` && request.method() === 'GET') {
      session.gets++;
      if (session.failedReads > 0) { session.failedReads--; return json(route, { detail: 'SECRET assignment read failure' }, 503); }
      return json(route, session.current);
    }
    if (url.pathname === `${path}/assignment/history` && request.method() === 'GET') {
      session.historyReads.push(Object.fromEntries(url.searchParams));
      const before = url.searchParams.get('beforeOperationId');
      const start = before ? session.history.findIndex(item => item.operationId === before) + 1 : 0;
      if (before && start === 0) return json(route, {}, 400);
      const limit = Number(url.searchParams.get('limit') ?? 50), items = session.history.slice(start, start + limit);
      return json(route, { targetId: target, alertId, items,
        nextBeforeOperationId: start + items.length < session.history.length ? items.at(-1).operationId : null });
    }
    if (url.pathname === `${path}/assignment` && request.method() === 'POST') {
      const command = request.postDataJSON(), raw = request.postData(); session.posts.push(command);
      const existing = session.operations.get(command.operationId);
      if (existing) return json(route, existing.raw === raw ? existing.receipt : {}, existing.raw === raw ? 200 : 409);
      if (command.expectedEpisodeId !== session.current.episodeId || command.expectedRevision !== session.current.revision) return json(route, {}, 409);
      const candidate = session.current.eligibleAssignees.find(item => item.sid === command.assigneeSid);
      if (command.assigneeSid !== null && !candidate) return json(route, {}, 409);
      const previous = session.current;
      session.current = { ...previous, revision: previous.revision + 1, assigneeSid: command.assigneeSid,
        assigneeName: candidate?.displayName ?? null, assignmentReason: command.assigneeSid === null ? null : command.reason,
        assignedBy: command.assigneeSid === null ? null : 'S-1-5-21-100-200-300-1000', assignedAtUtc: command.assigneeSid === null ? null : now };
      session.history.unshift({ operationId: command.operationId, episodeId, revision: session.current.revision,
        assigneeSid: command.assigneeSid, assigneeName: candidate?.displayName ?? null,
        previousAssigneeSid: previous.assigneeSid, previousAssigneeName: previous.assigneeName,
        reason: command.reason, actorSid: 'S-1-5-21-100-200-300-1000', appliedAtUtc: now });
      const receipt = { operationId: command.operationId, auditId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
        recordedAtUtc: now, revision: session.current.revision, assigneeSid: command.assigneeSid, appliedAtUtc: now };
      session.operations.set(command.operationId, { raw, receipt });
      return json(route, session.uncertain && session.posts.length === 1 ? {} : receipt,
        session.uncertain && session.posts.length === 1 ? 503 : 200);
    }
    return route.continue();
  });
  await page.goto(`/#/alerts?${new URLSearchParams({ target: fleet ? '' : target, range: 'custom', from, to, compare: '1' })}`);
  await expect(inbox(page).getByRole('complementary', { name: row.ruleName, exact: true })).toBeVisible();
  if (allowed) await expect(assignment(page).getByRole('combobox', { name: 'Approved assignee', exact: true })).toBeVisible();
  else await expect(assignment(page)).toBeVisible();
  return session;
}

for (const width of [320, 390, 768, 1440]) test(`Built dashboard assignment, reassignment and unassignment preserve alert evidence at ${width}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1100 });
  const session = await fixture(page), region = assignment(page);
  await region.getByRole('combobox', { name: 'Approved assignee', exact: true }).selectOption(candidates[0].sid);
  await region.getByRole('textbox', { name: 'Assignment reason', exact: true }).fill('SYNTHETIC investigating the head blocker');
  const assign = region.getByRole('button', { name: 'Assign alert', exact: true });
  await assign.focus(); await page.keyboard.press('Enter');
  await expect(region).toContainText(candidates[0].displayName);
  await expect(region.getByRole('button', { name: 'Reassign alert', exact: true })).toBeVisible();
  expect(session.posts).toHaveLength(1);
  expect(session.posts[0]).toMatchObject({ expectedEpisodeId: episodeId, expectedRevision: 5,
    assigneeSid: candidates[0].sid, reason: 'SYNTHETIC investigating the head blocker' });
  await region.getByRole('combobox', { name: 'Approved assignee', exact: true }).selectOption(candidates[1].sid);
  await region.getByRole('textbox', { name: 'Assignment reason', exact: true }).fill('SYNTHETIC handover to the on-call DBA');
  await region.getByRole('button', { name: 'Reassign alert', exact: true }).click();
  await expect(region).toContainText('SYNTHETIC handover to the on-call DBA');
  expect(session.posts).toHaveLength(2);
  expect(session.posts[1]).toMatchObject({ expectedRevision: 6, assigneeSid: candidates[1].sid });
  await expect(inbox(page).getByRole('table').first()).toContainText('firing');
  await expect(inbox(page)).toContainText('12 distinct sessions blocked');
  expect(Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1])))
    .toMatchObject({ range: 'custom', from, to, compare: '1' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  await region.getByRole('button', { name: 'Load assignment history', exact: true }).click();
  await expect(region).toContainText('SYNTHETIC investigating the head blocker');
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await mkdir('../artifacts/dashboard-alert-assignment/screenshots', { recursive: true });
  const screen = inbox(page), box = await screen.boundingBox();
  await page.setViewportSize({ width, height: Math.ceil(box.height) + 900 });
  await screen.scrollIntoViewIfNeeded();
  await screen.screenshot({ path: `../artifacts/dashboard-alert-assignment/screenshots/workspace-assignment-${width}.png` });
  await region.getByRole('textbox', { name: 'Assignment reason', exact: true }).fill('SYNTHETIC return to the shared inbox');
  await region.getByRole('button', { name: 'Unassign alert', exact: true }).click();
  await expect(region.getByRole('button', { name: 'Assign alert', exact: true })).toBeVisible();
  expect(session.posts).toHaveLength(3);
  expect(session.posts[2]).toMatchObject({ expectedRevision: 7, assigneeSid: null });
  expect(new Set(session.posts.map(command => command.operationId)).size).toBe(3);
  expect(session.errors).toEqual([]);
});

test('Built server assignment confirmed readback retries GET without replaying POST', async ({ page }) => {
  const session = await fixture(page, { fleet: false }), region = assignment(page);
  session.failedReads = 1;
  await region.getByRole('combobox', { name: 'Approved assignee', exact: true }).selectOption(candidates[0].sid);
  await region.getByRole('textbox', { name: 'Assignment reason', exact: true }).fill('Investigating');
  await region.getByRole('button', { name: 'Assign alert', exact: true }).click();
  await expect(region.getByRole('button', { name: 'Retry assignment status', exact: true })).toBeVisible();
  expect(session.posts).toHaveLength(1);
  await region.getByRole('button', { name: 'Retry assignment status', exact: true }).click();
  await expect(region.getByRole('button', { name: 'Reassign alert', exact: true })).toBeVisible();
  expect(session.posts).toHaveLength(1); expect(session.gets).toBe(3);
  await expect(page.locator('body')).not.toContainText('SECRET'); expect(session.errors).toEqual([]);
});

test('Built dashboard uncertain assignment retains its exact command through workspace ticks and roster revocation', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-03T15:00:00Z') });
  const session = await fixture(page), region = assignment(page); session.uncertain = true;
  await region.getByRole('combobox', { name: 'Approved assignee', exact: true }).selectOption(candidates[0].sid);
  await region.getByRole('textbox', { name: 'Assignment reason', exact: true }).fill('Investigating');
  await region.getByRole('button', { name: 'Assign alert', exact: true }).click();
  await expect(region.getByRole('button', { name: 'Retry assignment operation', exact: true })).toBeVisible();
  const original = session.posts[0]; session.current.eligibleAssignees = [];
  await page.clock.runFor(120_000);
  await region.getByRole('button', { name: 'Retry assignment operation', exact: true }).click();
  await expect(region).toContainText('SYNTHETIC primary DBA');
  expect(session.posts).toHaveLength(2); expect(session.posts[1]).toEqual(original);
  expect(session.operations.size).toBe(1); expect(session.errors).toEqual([]);
});

test('Built dashboard viewer sees ownership without recipient enumeration or mutation controls', async ({ page }) => {
  const session = await fixture(page, { allowed: false });
  const region = assignment(page); await expect(region).toBeVisible();
  await expect(inbox(page)).toContainText('12 distinct sessions blocked');
  await expect(region.getByRole('combobox', { name: 'Approved assignee', exact: true })).toHaveCount(0);
  await expect(region.getByRole('button', { name: 'Assign alert', exact: true })).toHaveCount(0);
  await expect(region.getByRole('button', { name: 'Unassign alert', exact: true })).toHaveCount(0);
  await expect(region).not.toContainText('SYNTHETIC primary DBA');
  expect(session.posts).toEqual([]); expect(session.errors).toEqual([]);
});

test('Built assignment history follows its opaque native anchor across equal timestamps without missing changes', async ({ page }) => {
  const session = await fixture(page), region = assignment(page);
  session.history = Array.from({ length: 51 }, (_, index) => ({
    operationId: `77777777-7777-4777-8777-${String(51-index).padStart(12, '0')}`,
    episodeId: index < 20 ? episodeId : 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', revision: 56-index,
    assigneeSid: candidates[index % 2].sid, assigneeName: candidates[index % 2].displayName,
    previousAssigneeSid: null, previousAssigneeName: null, reason: `SYNTHETIC recorded assignment ${51-index}`,
    actorSid: 'S-1-5-21-100-200-300-1000', appliedAtUtc: now,
  }));
  await region.getByRole('button', { name: 'Load assignment history', exact: true }).click();
  await expect(region).toContainText('SYNTHETIC recorded assignment 51');
  await expect(region.getByRole('cell', { name: session.history[50].reason + ' ' + session.history[50].actorSid, exact: true })).toHaveCount(0);
  await region.getByRole('button', { name: 'Load older assignment changes', exact: true }).click();
  await expect(region.getByRole('cell', { name: session.history[50].reason + ' ' + session.history[50].actorSid, exact: true })).toBeVisible();
  expect(session.historyReads).toEqual([{ limit: '50' }, { limit: '50', beforeOperationId: session.history[49].operationId }]);
  for (const item of session.history) await expect(region.getByRole('cell', { name: item.reason + ' ' + item.actorSid, exact: true })).toHaveCount(1);
  expect(await region.locator('tbody tr td:nth-child(3)').evaluateAll(cells => cells.map(cell => cell.firstChild.textContent))).toEqual(session.history.map(item => item.reason));
  expect(session.posts).toEqual([]); expect(session.errors).toEqual([]);
});
