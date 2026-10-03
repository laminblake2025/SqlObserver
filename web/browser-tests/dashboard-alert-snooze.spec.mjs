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
const inbox = page => page.locator('.alerts-screen');
const notifications = page => page.getByRole('region', { name: 'Alert notifications', exact: true });
const row = { alertId, ruleId: '55555555-5555-4555-8555-555555555555', ruleName: 'SYNTHETIC notification investigation',
  state: 'firing', firstObservedUtc: '2026-09-30T09:00:00Z', firedUtc: '2026-09-30T09:01:00Z',
  value: 12, reason: '12 distinct sessions blocked', deliverySuppressed: false, episodeId, revision: 5,
  snoozed: false, snoozedUntilUtc: null, snoozeReason: null, snoozedBy: null, snoozedAtUtc: null };
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
async function fixture(page, { fleet = true, allowed = true } = {}) {
  const session = { gets: 0, posts: [], errors: [], failedReads: 0, uncertain: false, conflict: false,
    current: { targetId: target, alertId, episodeId, revision: 5, state: 'firing', serverNowUtc: now,
      snoozed: false, deliverySuppressed: false, snoozedUntilUtc: null, snoozeReason: null, snoozedBy: null, snoozedAtUtc: null } };
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
      session.gets++;
      if (session.failedReads > 0) { session.failedReads--; return json(route, { detail: 'SECRET read failure' }, 503); }
      return json(route, session.current);
    }
    if ([`${path}/snooze`, `${path}/unsnooze`].includes(url.pathname) && request.method() === 'POST') {
      const command = request.postDataJSON(); session.posts.push({ path: url.pathname, command });
      if (session.conflict) return json(route, {}, 409);
      if (session.uncertain && session.posts.length === 1) return json(route, {}, 503);
      const snoozed = url.pathname.endsWith('/snooze');
      session.current = { ...session.current, revision: command.expectedRevision + 1, snoozed, deliverySuppressed: snoozed,
        snoozedUntilUtc: command.endsAtUtc ?? null, snoozeReason: snoozed ? command.reason : null,
        snoozedBy: snoozed ? 'SYNTHETIC operator' : null, snoozedAtUtc: snoozed ? now : null };
      return json(route, { operationId: command.operationId, auditId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
        recordedAtUtc: now, revision: session.current.revision, endsAtUtc: command.endsAtUtc ?? null, appliedAtUtc: now });
    }
    return route.continue();
  });
  await page.goto(`/#/alerts?${new URLSearchParams({ target: fleet ? '' : target, range: 'custom', from, to, compare: '1' })}`);
  await expect(inbox(page).getByRole('complementary', { name: row.ruleName, exact: true })).toBeVisible();
  return session;
}

for (const width of [320, 390, 768, 1440]) test(`Built dashboard snooze and unsnooze preserves visible firing evidence at ${width}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1100 });
  const session = await fixture(page), region = notifications(page);
  await expect(region).toContainText('No active notification snooze');
  await region.getByRole('combobox', { name: 'Snooze duration', exact: true }).selectOption('60');
  await region.getByRole('textbox', { name: 'Notification reason', exact: true }).fill('SYNTHETIC investigating the head blocker');
  const snooze = region.getByRole('button', { name: 'Snooze notifications', exact: true });
  await snooze.focus(); await page.keyboard.press('Enter');
  await expect(region).toContainText('Notifications snoozed');
  expect(session.posts).toHaveLength(1);
  expect(session.posts[0].command).toMatchObject({ expectedEpisodeId: episodeId, expectedRevision: 5,
    endsAtUtc: '2026-10-03T16:00:00.123456Z', reason: 'SYNTHETIC investigating the head blocker' });
  await expect(inbox(page).getByRole('table')).toContainText('firing');
  await expect(inbox(page)).toContainText('12 distinct sessions blocked');
  expect(Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1])))
    .toMatchObject({ range: 'custom', from, to, compare: '1' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await mkdir('../artifacts/dashboard-alert-snooze/screenshots', { recursive: true });
  const screen = inbox(page), box = await screen.boundingBox();
  await page.setViewportSize({ width, height: Math.ceil(box.height) + 900 });
  await screen.scrollIntoViewIfNeeded(); await screen.screenshot({ path: `../artifacts/dashboard-alert-snooze/screenshots/workspace-snooze-${width}.png` });
  await region.getByRole('textbox', { name: 'Notification reason', exact: true }).fill('SYNTHETIC resume notifications');
  await region.getByRole('button', { name: 'Unsnooze notifications', exact: true }).click();
  await expect(region).toContainText('No active notification snooze');
  expect(session.posts).toHaveLength(2);
  expect(session.posts[1].command).toMatchObject({ expectedEpisodeId: episodeId, expectedRevision: 6, reason: 'SYNTHETIC resume notifications' });
  expect(session.posts[1].command.endsAtUtc).toBeUndefined();
  expect(session.posts[1].command.operationId).not.toBe(session.posts[0].command.operationId);
  expect(session.errors).toEqual([]);
});

test('Built server alerts retry confirmed readback using GET without replaying POST', async ({ page }) => {
  const session = await fixture(page, { fleet: false }), region = notifications(page);
  await expect(region).toContainText('No active notification snooze');
  session.failedReads = 1;
  await region.getByRole('textbox', { name: 'Notification reason', exact: true }).fill('Investigating');
  await region.getByRole('button', { name: 'Snooze notifications', exact: true }).click();
  await expect(region.getByRole('button', { name: 'Retry notification status', exact: true })).toBeVisible();
  expect(session.posts).toHaveLength(1);
  await region.getByRole('button', { name: 'Retry notification status', exact: true }).click();
  await expect(region).toContainText('Notifications snoozed');
  expect(session.posts).toHaveLength(1); expect(session.gets).toBe(3);
  await expect(page.locator('body')).not.toContainText('SECRET'); expect(session.errors).toEqual([]);
});

test('Built dashboard uncertain mutation keeps its original command after time advances', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-03T15:00:00Z') });
  const session = await fixture(page), region = notifications(page);
  await expect(region).toContainText('No active notification snooze'); session.uncertain = true;
  await region.getByRole('textbox', { name: 'Notification reason', exact: true }).fill('Investigating');
  await region.getByRole('button', { name: 'Snooze notifications', exact: true }).click();
  await expect(region.getByRole('button', { name: 'Retry notification operation', exact: true })).toBeVisible();
  const original = session.posts[0]; await page.clock.runFor(120_000);
  await region.getByRole('button', { name: 'Retry notification operation', exact: true }).click();
  await expect(region).toContainText('Notifications snoozed');
  expect(session.posts).toHaveLength(2); expect(session.posts[1]).toEqual(original); expect(session.errors).toEqual([]);
});

test('Built dashboard viewer can read retained alert evidence and has no notification mutation controls', async ({ page }) => {
  const session = await fixture(page, { allowed: false });
  await expect(inbox(page)).toContainText('12 distinct sessions blocked');
  await expect(inbox(page).getByRole('button', { name: 'Snooze notifications', exact: true })).toHaveCount(0);
  await expect(inbox(page).getByRole('button', { name: 'Unsnooze notifications', exact: true })).toHaveCount(0);
  expect(session.posts).toEqual([]); expect(session.errors).toEqual([]);
});
