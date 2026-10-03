import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';

const origin = 'http://127.0.0.1:4322';
const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const run = '22222222-2222-4222-8222-222222222222';
const newerRun = '33333333-3333-4333-8333-333333333333';
const observedAtUtc = '2026-10-03T12:00:00.123456Z';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.123456Z';
const sessions = new WeakMap();
const card = page => page.getByRole('article', { name: 'TempDB files', exact: true });
const rows = page => card(page).getByRole('table', { name: /^tempdb-files named evidence/ }).locator('tbody tr');
const control = (page, label) => card(page).getByRole('button', { name: label, exact: true });
const scope = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const cursor = (target, runId = run) => Buffer.from(`${target}|2026-10-03T12:00:00.1234560+00:00|${Buffer.from(JSON.stringify({
  Kind: 'tempdb', RunId: runId, Revision: 1, From: null, To: null, A: '50', B: '', C: '', D: '',
})).toString('base64')}`).toString('base64');
function pageFor(target, kind, continuation, runId = run) {
  const base = { targetId: target, targetRevision: 1, runId, evidence: { observedAtUtc }, observedAtUtc,
    state: 'Complete', items: [], hasMore: false, nextCursor: null, truncated: false };
  if (kind === 'tempdb') return { ...base, totalBytes: 1024, usedBytes: 256, logTotalBytes: 512, logUsedBytes: 0 };
  if (kind !== 'tempdb/files') return { ...base, runId: null, evidence: null, state: 'NoData', visibilityScope: 'PrimaryAllKnown' };
  const firstId = continuation ? 51 : 1;
  return { ...base, items: Array.from({ length: continuation ? 1 : 50 }, (_, index) => ({ fileId: firstId + index,
    sizeBytes: 1024, usedBytes: 256, freeBytes: 768, state: 'Online' })),
    hasMore: !continuation, nextCursor: continuation ? null : cursor(target, runId) };
}
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
test.beforeEach(async ({ page }) => {
  sessions.set(page, { reads: [], errors: [], handler: undefined, runId: run });
  page.on('pageerror', error => sessions.get(page).errors.push(error.message));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin }]);
  await page.route(/\/api\/v1\/observation-targets\/[^/]+\/(?:backups|tempdb(?:\/files)?|availability-groups\/(?:replicas|databases))(?:\?|$)/, async route => {
    const url = new URL(route.request().url());
    const [, target, kind] = /^\/api\/v1\/observation-targets\/([^/]+)\/(.*)$/.exec(url.pathname);
    const session = sessions.get(page), continuation = url.searchParams.get('cursor');
    expect(route.request().method()).toBe('GET'); expect(url.searchParams.get('limit')).toBe('50');
    expect([first, second]).toContain(target);
    const receipt = { target, kind, cursor: continuation }; session.reads.push(receipt);
    if (session.handler && await session.handler(route, receipt)) return;
    if (continuation) expect(continuation).toBe(cursor(target, session.runId));
    await json(route, pageFor(target, kind, continuation, session.runId));
  });
});
test.afterEach(async ({ page }) => { expect(sessions.get(page).errors).toEqual([]); });
async function open(page) {
  await page.goto(`/#/operations?${new URLSearchParams({ target: first, databaseId: '7', range: 'custom', from, to, check: 'tempdb' })}`);
  await expect(rows(page)).toHaveCount(50);
}
async function contextUnchanged(page, target = first) {
  expect(scope(page)).toMatchObject({ target, range: 'custom', from, to, check: 'tempdb' });
  // Database IDs belong to one target; native App navigation clears them on server change.
  expect(scope(page).databaseId).toBe(target === first ? '7' : undefined);
}
for (const width of [320, 1440]) test(`Operations projection preserves a paged investigation and shared URL on Refresh at ${width}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 }); await open(page);
  await control(page, 'Next TempDB files page').focus(); await page.keyboard.press('Enter');
  await expect(rows(page)).toHaveCount(1); await expect(rows(page).first().getByRole('cell').first()).toHaveText('51');
  const count = sessions.get(page).reads.filter(item => item.kind === 'tempdb/files').length;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(card(page)).toContainText('Retained');
  await expect(rows(page)).toHaveCount(1); expect(sessions.get(page).reads.filter(item => item.kind === 'tempdb/files')).toHaveLength(count);
  await contextUnchanged(page);
  await control(page, 'Previous TempDB files page').focus(); await page.keyboard.press('Enter');
  await expect(rows(page)).toHaveCount(50); expect(sessions.get(page).reads.filter(item => item.kind === 'tempdb/files')).toHaveLength(count);
  await control(page, 'Next TempDB files page').click(); await expect(rows(page)).toHaveCount(1);
  expect(sessions.get(page).reads.filter(item => item.kind === 'tempdb/files')).toHaveLength(count);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  // Capture the received card below pinned navigation without changing behavioral-check width.
  const box = await card(page).boundingBox();
  await page.setViewportSize({ width, height: Math.max(1000, Math.ceil(box.height) + 800) });
  await card(page).scrollIntoViewIfNeeded(); await page.evaluate(() => document.activeElement?.blur());
  await mkdir('../artifacts/dashboard-operations-projections/screenshots', { recursive: true });
  await card(page).screenshot({ path: `../artifacts/dashboard-operations-projections/screenshots/built-projection-${width}.png` });
});

test('Operations projection retries the same failed cursor and only successful explicit latest replaces the retained page', async ({ page }) => {
  await open(page); const session = sessions.get(page);
  session.handler = async (route, item) => {
    if (item.kind !== 'tempdb/files') return false;
    await json(route, { detail: 'SECRET_NATIVE_FAILURE' }, 503); return true;
  };
  await control(page, 'Next TempDB files page').click(); await expect(card(page).getByRole('alert')).toBeVisible();
  await expect(rows(page)).toHaveCount(50);
  const failedCursor = session.reads.at(-1).cursor;
  session.handler = undefined; await control(page, 'Retry TempDB files read').click();
  await expect(rows(page)).toHaveCount(1); expect(session.reads.at(-1).cursor).toBe(failedCursor);
  session.handler = async (route, item) => {
    if (item.kind !== 'tempdb/files') return false;
    await json(route, {}, 503); return true;
  };
  await control(page, 'Return TempDB files to latest').click(); await expect(card(page).getByRole('alert')).toBeVisible();
  await expect(rows(page)).toHaveCount(1);
  session.handler = undefined; session.runId = newerRun;
  await control(page, 'Return TempDB files to latest').click(); await expect(rows(page)).toHaveCount(50);
  await expect(card(page)).toContainText(newerRun); await expect(card(page).getByRole('alert')).toHaveCount(0);
  await contextUnchanged(page); await expect(page.locator('body')).not.toContainText('SECRET_NATIVE_FAILURE');
});

test('Operations projection aborts a held continuation on server navigation and rejects its late receipt', async ({ page }) => {
  await open(page); let announce, release, finished;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const ended = new Promise(resolve => { finished = resolve; });
  sessions.get(page).handler = async (route, item) => {
    if (item.target !== first || item.kind !== 'tempdb/files' || !item.cursor) return false;
    announce(route.request()); await gate;
    await json(route, pageFor(first, 'tempdb/files', true)).catch(() => {}); finished(); return true;
  };
  await control(page, 'Next TempDB files page').click(); const request = await started;
  const aborted = page.waitForEvent('requestfailed', { predicate: item => item === request });
  sessions.get(page).runId = newerRun;
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second); await aborted;
  await expect(rows(page)).toHaveCount(50); release(); await ended;
  await contextUnchanged(page, second);
  await expect(rows(page)).toHaveCount(50); await expect(card(page)).toContainText(newerRun);
  await expect(card(page)).not.toContainText(run);
  expect(sessions.get(page).reads.filter(item => item.kind === 'tempdb/files').at(-1).target).toBe(second);
  await expect(card(page).getByRole('alert')).toHaveCount(0);
});
