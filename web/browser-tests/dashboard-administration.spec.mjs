import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { calendarListingUtc } from '../tools/maintenance-calendar-fixture.mjs';

const origin = 'http://127.0.0.1:4323';
const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const windowId = index => '99999999-9999-4999-8999-' + String(index).padStart(12, '0');
const href = (target = first, id) => '/#/maintenance?' + new URLSearchParams({ target, range: 'custom', from, to, ...(id ? { windowId: id } : {}) });
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const calendar = page => page.getByRole('region', { name: 'Maintenance calendar', exact: true });
const agenda = page => page.getByRole('region', { name: 'Maintenance date agenda', exact: true });
const day = (page, label) => calendar(page).getByRole('button', { name: new RegExp(label + '.*loaded windows') });

test.beforeEach(async ({}, testInfo) => {
  await testInfo.attach('administration-evidence-context', {
    body: JSON.stringify({ source: 'isolated synthetic fixture; no connected VM evidence', executedAtUtc: new Date().toISOString(), listingStartedUtc: calendarListingUtc,
      workspaceFromUtc: from, workspaceToUtc: to, browserTimeZone: 'America/New_York' }, null, 2), contentType: 'application/json',
  });
});

async function fixture(page, utc = true) {
  await page.context().addCookies([
    { name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'maintenanceCalendarFixture', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin },
  ]);
  await page.addInitScript(value => localStorage.setItem('sqlobserver.timeDisplayMode', value), utc ? 'utc' : 'local');
}
async function assertContext(page, target = first) {
  expect(scope(page).get('target')).toBe(target);
  expect(scope(page).get('from')).toBe(from);
  expect(scope(page).get('to')).toBe(to);
}
async function audit(page, testInfo, name) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), name + ' fits the viewport').toBeLessThanOrEqual(1);
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
  await testInfo.attach(name + '-axe', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
  expect(results.violations, JSON.stringify(results.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })))).toEqual([]);
}
async function capture(page, filename) {
  const folder = resolve('../artifacts/dashboard-administration/screenshots');
  await mkdir(folder, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await page.screenshot({ path: resolve(folder, filename), fullPage: true });
}

test('calendar month and date browsing select exact maintenance URLs through keyboard reload and history', async ({ page }) => {
  await fixture(page); const mutations = [];
  page.on('request', request => { if (!['GET', 'HEAD'].includes(request.method())) mutations.push(request.url()); });
  await page.goto(href(first, windowId(3)));
  const month = page.getByLabel('Calendar month', { exact: true });
  await expect(month).toHaveValue('2026-10');
  await page.getByRole('button', { name: 'Next calendar month', exact: true }).click();
  await expect(month).toHaveValue('2026-11');
  await page.getByRole('button', { name: 'Previous calendar month', exact: true }).click();
  await expect(month).toHaveValue('2026-10');
  const date = day(page, 'October 31, 2026'); await date.focus(); await date.press('Enter');
  await expect(date).toHaveAttribute('aria-pressed', 'true');
  const multi = agenda(page).getByRole('button', { name: /multi-day delivery suppression/ });
  await multi.focus(); await multi.press('Enter');
  await expect.poll(() => scope(page).get('windowId')).toBe(windowId(3));
  await expect(page.getByRole('complementary', { name: 'Maintenance window', exact: true })).toContainText(windowId(3));
  await assertContext(page);
  const cancelled = agenda(page).getByRole('button', { name: /cancelled overlap/ }); await cancelled.click();
  await expect.poll(() => scope(page).get('windowId')).toBe(windowId(2));
  await page.reload(); await expect(month).toHaveValue('2026-10');
  await expect(page.getByRole('complementary', { name: 'Maintenance window', exact: true })).toContainText(windowId(2));
  await page.goBack(); await expect.poll(() => scope(page).get('windowId')).toBe(windowId(3));
  await page.goForward(); await expect.poll(() => scope(page).get('windowId')).toBe(windowId(2));
  await assertContext(page); expect(mutations).toEqual([]);
});

test('calendar uses half-open multi-day coverage and retains cancelled windows without implying an all-clear day', async ({ page }) => {
  await fixture(page); await page.goto(href(first, windowId(3)));
  await expect(day(page, 'October 31, 2026')).toHaveAccessibleName(/2 loaded windows, 1 cancelled/);
  await day(page, 'October 31, 2026').click();
  await expect(agenda(page)).toContainText('cancelled overlap'); await expect(agenda(page)).toContainText('multi-day delivery suppression');
  await agenda(page).getByRole('button', { name: /cancelled overlap/ }).click();
  const detail = page.getByRole('complementary', { name: 'Maintenance window', exact: true });
  await expect(detail.getByRole('button', { name: 'Cancel window', exact: true })).toHaveCount(0);
  await page.getByLabel('Calendar month', { exact: true }).fill('2026-11');
  await expect(day(page, 'November 1, 2026')).toHaveAccessibleName(/3 loaded windows, 1 cancelled/);
  await expect(day(page, 'November 2, 2026')).toHaveAccessibleName(/0 loaded windows, 0 cancelled/);
  await day(page, 'November 2, 2026').click();
  await expect(agenda(page)).toContainText(/loaded|coverage|configured/i);
  await expect(agenda(page).getByRole('button', { name: /multi-day delivery suppression/ })).toHaveCount(0);
  await assertContext(page);
});

test('calendar restores later-page identity and delayed old-target evidence never replaces the current server', async ({ page }) => {
  await fixture(page);
  const reads = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/alerts/maintenance')) reads.push(request.url()); });
  await page.goto(href(first, windowId(5)));
  await expect(page.getByLabel('Calendar month', { exact: true })).toHaveValue('2026-09');
  await expect(page.getByRole('complementary', { name: 'Maintenance window', exact: true })).toContainText('previous-month maintenance');
  expect(reads).toHaveLength(2);
  let release, started = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/alerts/maintenance?*`, async route => {
    started = true; await gate; await route.continue().catch(() => {}); returned = true;
  });
  try {
    await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
    await expect.poll(() => started).toBe(true);
    await page.goto(href(second, windowId(103)));
    await expect(page.getByRole('complementary', { name: 'Maintenance window', exact: true })).toContainText('SYNTHETIC SQL 02 multi-day');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(calendar(page)).not.toContainText('SYNTHETIC SQL 01');
  await assertContext(page, second);
});

test('calendar local civil days follow the DST boundary and distinguish UTC day coverage', async ({ page }) => {
  await fixture(page, false); await page.goto(href(first, windowId(1)));
  await expect(page.getByLabel('Calendar month', { exact: true })).toHaveValue('2026-11');
  await expect(day(page, 'November 1, 2026')).toHaveAccessibleName(/2 loaded windows, 0 cancelled/);
  await day(page, 'November 1, 2026').click();
  await expect(agenda(page)).toContainText('01:30:00 EDT');
  await expect(agenda(page)).toContainText('02:30:00 EST');
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  await expect(day(page, 'November 1, 2026')).toHaveAccessibleName(/3 loaded windows, 1 cancelled/);
  await assertContext(page);
});

test('calendar distinguishes loading unavailable and empty bounded maintenance evidence', async ({ page }) => {
  await fixture(page);
  let release, started = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/alerts/maintenance?*`, async route => { started = true; await gate; await route.continue().catch(() => {}); });
  try {
    await page.goto(href(first, windowId(3))); await expect.poll(() => started).toBe(true);
    await expect(page.getByRole('status').filter({ hasText: /Loading maintenance windows/ })).toBeVisible();
    await expect(agenda(page).getByRole('button', { name: /delivery suppression/ })).toHaveCount(0);
  } finally { release(); }
  await expect(page.getByRole('complementary', { name: 'Maintenance window', exact: true })).toContainText(windowId(3));
  await page.unroute(`**/observation-targets/${first}/alerts/maintenance?*`);
  await page.context().addCookies([{ name: 'maintenanceCalendarMode', value: 'error', url: origin }]); await page.reload();
  await expect(page.getByRole('alert').filter({ hasText: 'Maintenance windows are temporarily unavailable.' })).toBeVisible();
  await expect(agenda(page).getByRole('button', { name: /delivery suppression/ })).toHaveCount(0);
  await page.context().addCookies([{ name: 'maintenanceCalendarMode', value: 'empty', url: origin }]); await page.reload();
  await expect(page.getByText('No maintenance windows are configured for this server.', { exact: true })).toBeVisible();
  await expect(calendar(page)).toContainText(/loaded|coverage/i);
  await expect(page.getByRole('complementary', { name: 'Maintenance window', exact: true })).toHaveCount(0);
});

for (const width of [320, 390, 768, 1440]) {
  test(`maintenance calendar keyboard and cancelled multi-day evidence remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page);
    await page.goto(href(first, windowId(3)));
    const date = day(page, 'October 31, 2026'); await date.focus(); await date.press('Enter');
    await expect(date).toBeFocused(); await expect(date).toHaveAttribute('aria-pressed', 'true');
    await expect(agenda(page)).toContainText('cancelled overlap');
    await agenda(page).getByRole('button', { name: /cancelled overlap/ }).focus();
    await page.keyboard.press('Enter');
    await expect.poll(() => scope(page).get('windowId')).toBe(windowId(2));
    await audit(page, testInfo, 'maintenance-calendar');
    await capture(page, `maintenance-calendar-${width}.png`);
  });
}

const permissionsHref = (target = first) => '/#/permissions?' + new URLSearchParams({ target, range: 'custom', from, to });
const generatedSql = page => page.getByRole('textbox', { name: 'Generated permission SQL', exact: true });
async function generateScript(page, principal = "LAB\\O'Brien]sql", database = "Orders]O'Brien") {
  await page.getByRole('combobox', { name: 'SQL Server version', exact: true }).selectOption('16');
  await page.getByLabel('Existing Windows principal', { exact: true }).fill(principal);
  await page.getByRole('checkbox', { name: 'Query Store access for one database', exact: true }).check();
  await page.getByLabel('Query Store database', { exact: true }).fill(database);
  await page.getByRole('button', { name: 'Generate SQL', exact: true }).click();
  await expect(generatedSql(page)).toBeVisible();
}

test('offline permission generation escapes identifiers and only explicit actions copy select or download SQL', async ({ page, context }) => {
  await fixture(page); await context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin });
  const mutations = []; page.on('request', request => { if (!['GET', 'HEAD'].includes(request.method())) mutations.push(request.url()); });
  await page.goto(permissionsHref());
  await expect(page.getByRole('heading', { name: 'Permission script for SYNTHETIC SQL 01', exact: true })).toBeVisible();
  await expect(generatedSql(page)).toHaveCount(0);
  await generateScript(page);
  const sql = await generatedSql(page).inputValue();
  expect(sql).toContain("N'LAB\\O''Brien]sql'");
  expect(sql).toContain("[LAB\\O'Brien]]sql]");
  expect(sql).toContain("USE [Orders]]O'Brien]");
  expect(sql).not.toContain('__SQLOBSERVER_');
  await page.getByRole('button', { name: 'Select SQL', exact: true }).click();
  await expect(generatedSql(page)).toBeFocused();
  expect(await generatedSql(page).evaluate(input => [input.selectionStart, input.selectionEnd])).toEqual([0, sql.length]);
  await page.getByRole('button', { name: 'Copy SQL', exact: true }).click();
  await expect.poll(() => page.evaluate(async () => (await navigator.clipboard.readText()).replaceAll('\r\n', '\n'))).toBe(sql);
  const downloaded = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download SQL', exact: true }).click();
  const download = await downloaded; expect(download.suggestedFilename()).toMatch(/\.sql$/);
  expect(await readFile(await download.path(), 'utf8')).toBe(sql);
  await assertContext(page); expect(mutations).toEqual([]);
});

test('permission generator clears stale output on version principal operation and target changes', async ({ page }) => {
  await fixture(page); await page.goto(permissionsHref()); await generateScript(page);
  await page.getByRole('combobox', { name: 'SQL Server version', exact: true }).selectOption('17');
  await expect(generatedSql(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Generate SQL', exact: true }).click(); await expect(generatedSql(page)).toBeVisible();
  await page.getByLabel('Existing Windows principal', { exact: true }).fill('LAB\\different');
  await expect(generatedSql(page)).toHaveCount(0);
  await page.getByRole('checkbox', { name: 'Logical file metadata', exact: true }).check();
  await page.getByRole('checkbox', { name: 'Replication monitoring', exact: true }).check();
  await page.getByLabel('Distribution database', { exact: true }).fill('distribution');
  await page.getByRole('button', { name: 'Generate SQL', exact: true }).click(); await expect(generatedSql(page)).toBeVisible();
  await page.getByRole('combobox', { name: 'Operation', exact: true }).selectOption('Remove');
  await expect(generatedSql(page)).toHaveCount(0);
  await expect(page.getByLabel('Query Store database', { exact: true })).toHaveCount(0);
  await expect(page.getByLabel('Distribution database', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Generate SQL', exact: true }).click();
  await expect(generatedSql(page)).toBeVisible();
  expect(await generatedSql(page).inputValue()).not.toContain("USE [Orders]]O'Brien]");
  await page.goto(permissionsHref(second));
  await expect(page.getByRole('heading', { name: 'Permission script for SYNTHETIC SQL 02', exact: true })).toBeVisible();
  await expect(generatedSql(page)).toHaveCount(0);
  await expect(page.getByLabel('Existing Windows principal', { exact: true })).toHaveValue('');
  await assertContext(page, second);
});

test('permission generator requires target administrator access and revocation hides existing SQL', async ({ page }) => {
  await fixture(page); await page.context().addCookies([{ name: 'maintenanceCalendarRole', value: 'viewer', url: origin }]);
  await page.goto(permissionsHref());
  await expect(page.getByText('The Target Administrator role on this server unlocks the permission script generator.', { exact: true })).toBeVisible();
  await expect(page.getByRole('form', { name: 'Offline permission script generator', exact: true })).toHaveCount(0);
  await expect(generatedSql(page)).toHaveCount(0);
  await page.context().addCookies([{ name: 'maintenanceCalendarRole', value: 'administrator', url: origin }]); await page.reload();
  await generateScript(page);
  await page.context().addCookies([{ name: 'maintenanceCalendarRole', value: 'viewer', url: origin }]);
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(generatedSql(page)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Generate SQL', exact: true })).toHaveCount(0);
});

for (const width of [320, 390, 768, 1440]) {
  test(`permission generator escaped SQL and keyboard actions remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page); await page.goto(permissionsHref());
    await generateScript(page);
    await page.getByRole('button', { name: 'Select SQL', exact: true }).focus(); await page.keyboard.press('Enter');
    await expect(generatedSql(page)).toBeFocused();
    expect(await generatedSql(page).evaluate(input => input.selectionEnd - input.selectionStart)).toBeGreaterThan(100);
    await audit(page, testInfo, 'permission-generator');
    await capture(page, `permission-generator-${width}.png`);
  });
}
