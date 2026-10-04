import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { nativeFileObservedUtc } from '../tools/dashboard-native-insights-fixture.mjs';
import { databaseFileWindowHistoryBackend } from '../tools/database-file-window-history-fixture.mjs';

const origin = 'http://127.0.0.1:4326';
const first = '11111111-1111-4111-8111-000000000001', second = '11111111-1111-4111-8111-000000000002';
const from = '2026-10-02T08:00:00.123456Z', to = '2026-10-02T12:00:00.654321Z';
const digest = value => value.toString(16).padStart(12, '0') + '0'.repeat(52);
const hash = (surface, extras = {}, target = first) => '#/' + surface + '?' + new URLSearchParams({ target, range: 'custom', from, to, ...extras });
const queryHash = (index = 0, extras = {}) => hash('queries', { to: '2026-10-02T12:00:00.000000Z', databaseId: index === 1 ? '6' : '5', queryFingerprint: digest(4096 + index), planFingerprint: digest(8192 + index), source: 'query_store', ...extras });
const fileHash = (file = '1', target = first) => hash('resources', { resourceDatabaseId: '5', resourceFileId: file }, target);
const params = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const trend = page => page.getByRole('region', { name: 'Selected plan delta trend', exact: true });
const waits = page => page.getByRole('region', { name: 'Selected plan wait category trend', exact: true });
const files = page => page.getByRole('region', { name: 'Database file allocation and autogrowth history', exact: true });
const allocation = page => page.locator('details[aria-label="Selected file allocation and autogrowth"]');
async function setup(page, mode = 'normal') {
  await page.clock.install({ time: new Date('2026-10-02T12:30:00.000Z') });
  await page.context().addCookies(['dashboardRefactor', 'nativeInsightsFixture', 'queryRowTrendFixture', 'databaseScopeFixture'].map(name => ({ name, value: '1', url: origin })).concat([
    { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'nativeInsightsMode', value: mode, url: origin },
  ]));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
}
async function openAllocation(page) {
  await files(page).locator('summary').filter({ hasText: 'Selected file allocation and autogrowth' }).click();
}
async function openWaitValues(page) { await waits(page).getByText('Hourly wait values and coverage', { exact: true }).click(); }
const protectedReads = page => {
  const reads = []; page.on('request', request => { if (/\/runs\/.*(?:\/text|\/content)$/.test(new URL(request.url()).pathname)) reads.push(request.url()); }); return reads;
};
test.beforeEach(async ({}, testInfo) => {
  await testInfo.attach('evidence-context', { body: JSON.stringify({ source: 'Synthetic native DTO fixture; no connected VM evidence', executedAtUtc: new Date().toISOString(), observedAtUtc: nativeFileObservedUtc }, null, 2), contentType: 'application/json' });
});

test('selected plan wait categories retain real gaps exact counters and a shared keyboard inspection', async ({ page }) => {
  await setup(page); const sql = protectedReads(page); const exactPin = '2026-10-02T11:00:00.123456Z';
  await page.goto('/' + queryHash(0, { planWaitCategory: '3', planInspectUtc: exactPin }));
  await expect(waits(page)).toBeVisible(); await openWaitValues(page);
  const values = waits(page).getByRole('region', { name: 'Selected plan hourly wait values', exact: true });
  await page.getByRole('combobox', { name: 'Plan wait category', exact: true }).selectOption('3');
  await expect(values.getByRole('cell', { name: '80', exact: true })).toBeVisible();
  await expect(values.getByRole('cell', { name: '200', exact: true })).toBeVisible();
  await expect(values).toContainText('No observed group rows; gap.'); await expect(values).toContainText('1 reset');
  const waitSvg = waits(page).getByRole('img', { name: /^Hourly comparable plan waits \(ms\)/ });
  await expect(waitSvg.locator('circle')).toHaveCount(2);
  expect((await waitSvg.locator('path.chart-series').evaluateAll(paths => paths.map(path => path.getAttribute('d')))).every(path => !path.includes('L'))).toBe(true);
  await expect(page.getByLabel('Plan inspection time (UTC)', { exact: true })).toHaveValue('2026-10-02T11:00:00.123');
  await page.getByRole('button', { name: 'Align plan charts', exact: true }).click();
  await expect(trend(page).locator('.chart-crosshair')).toHaveCount(2);
  expect(new Set(await trend(page).locator('.chart-crosshair text').allTextContents()).size).toBe(1);
  expect(params(page).get('planWaitCategory')).toBe('3'); expect(params(page).get('planInspectUtc')).toBe(exactPin);
  await page.reload(); await expect(waits(page)).toBeVisible(); await openWaitValues(page);
  await expect(page.getByRole('combobox', { name: 'Plan wait category', exact: true })).toHaveValue('3');
  await expect(trend(page).locator('.chart-crosshair')).toHaveCount(2);
  await page.getByRole('button', { name: 'Align plan charts', exact: true }).click(); expect(params(page).get('planInspectUtc')).toBe(exactPin);
  await page.getByRole('combobox', { name: 'Plan wait category', exact: true }).selectOption('6');
  await expect(values.getByRole('cell', { name: '20', exact: true })).toHaveCount(2);
  await page.goBack(); await expect(page.getByRole('combobox', { name: 'Plan wait category', exact: true })).toHaveValue('3');
  await page.goForward(); await expect(page.getByRole('combobox', { name: 'Plan wait category', exact: true })).toHaveValue('6');
  await page.getByLabel('Plan inspection time (UTC)', { exact: true }).fill('2026-10-02T12:00');
  await page.getByRole('button', { name: 'Align plan charts', exact: true }).click();
  await expect(trend(page).getByRole('alert')).toContainText('before its end');
  await expect(trend(page).locator('.chart-crosshair')).toHaveCount(0); expect(params(page).has('planInspectUtc')).toBe(false);
  await page.getByRole('button', { name: 'Clear plan alignment', exact: true }).click(); await expect(trend(page).getByRole('alert')).toHaveCount(0);
  await page.evaluate(next => { location.hash = next; }, queryHash(0, { planWaitCategory: '3' }) + '&planWaitCategory=6');
  await expect(waits(page)).toContainText('The requested plan wait category is invalid or duplicated.');
  await expect(waits(page).getByRole('img', { name: /^Hourly comparable plan waits \(ms\)/ })).toHaveCount(0);
  await page.getByRole('combobox', { name: 'Plan wait category', exact: true }).selectOption('3');
  await expect(waits(page).getByRole('img', { name: /^Hourly comparable plan waits \(ms\)/ })).toBeVisible();
  expect(params(page).getAll('planWaitCategory')).toEqual(['3']);
  await page.context().addCookies([{ name: 'nativeInsightsMode', value: 'unsafe-waits', url: origin }, { name: 'queryRowTrendState', value: 'partial', url: origin }]);
  await page.reload(); await expect(waits(page)).toBeVisible(); await page.getByRole('combobox', { name: 'Plan wait category', exact: true }).selectOption('3'); await openWaitValues(page);
  await expect(waits(page)).toContainText('hourly values exceed chart precision');
  await expect(waits(page)).toContainText('9007199254740993'); await expect(waits(page)).toContainText('Partial or ambiguous bucket');
  await expect(waits(page).getByRole('img', { name: /^Hourly comparable plan waits \(ms\)/ }).locator('circle')).toHaveCount(0);
  expect(sql).toEqual([]);
});

test('selected plan and changed window reject a delayed prior plan response', async ({ page }) => {
  await setup(page); const sql = protectedReads(page); let release, started = false, returned = false;
  const gate = new Promise(resolveGate => { release = resolveGate; });
  await page.route('**/query-performance/databases/5/history/*/plans/*/trend?*', async route => {
    const url = new URL(route.request().url());
    if (!url.pathname.includes(digest(4096))) return route.continue();
    const response = await route.fetch(); started = true; await gate;
    await route.fulfill({ response }).catch(() => {}); returned = true;
  });
  await page.goto('/' + queryHash()); await expect.poll(() => started).toBe(true);
  try {
    await page.evaluate(next => { location.hash = next; }, queryHash(1, { from: '2026-10-02T09:00:00.123456Z' }));
    await expect(waits(page)).toBeVisible(); await page.getByRole('combobox', { name: 'Plan wait category', exact: true }).selectOption('3'); await openWaitValues(page);
    await expect(waits(page)).toContainText('1080');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(waits(page)).not.toContainText('280 ms');
  expect(params(page).get('queryFingerprint')).toBe(digest(4097)); expect(params(page).get('from')).toBe('2026-10-02T09:00:00.123456Z'); expect(sql).toEqual([]);
});

test('selected file allocation preserves exact bytes percent growth unknown maximum and loaded snapshot in Rewind', async ({ page }) => {
  await setup(page); await page.goto('/' + fileHash()); await openAllocation(page);
  await expect(allocation(page)).toContainText('9223372036854775807 bytes'); await expect(allocation(page)).toContainText('67108864 bytes per growth event');
  await expect(allocation(page)).toContainText('exact retained receipt'); await expect(allocation(page)).toContainText('Rewind');
  const chosen = files(page).getByRole('button', { name: 'View read and write latency trend for database 5, file SYNTHETIC_percent_log', exact: true });
  await chosen.click(); await expect(chosen).toHaveAttribute('aria-pressed', 'true');
  await expect(allocation(page)).toContainText('10% per growth event'); await expect(allocation(page)).toContainText('Unlimited or unknown; source does not distinguish');
  await expect(allocation(page)).toContainText('log / online'); expect(params(page).get('resourceFileId')).toBe('2');
  expect(params(page).get('from')).toBe(from); expect(params(page).get('to')).toBe(to);
  await page.reload(); await openAllocation(page); await expect(allocation(page)).toContainText('10% per growth event');
  expect(params(page).get('resourceDatabaseId')).toBe('5'); expect(params(page).get('resourceFileId')).toBe('2');
});

test('missing and off-page historical file allocation never substitute another received row', async ({ page }) => {
  await setup(page); const backend=databaseFileWindowHistoryBackend({fileCount:102});
  await page.route('**/resources/files/history**',async route=>{
    const url=new URL(route.request().url()),p=url.searchParams,binding={targetId:url.pathname.split('/')[4],targetRevision:Number(p.get('targetRevision')),fromUtc:p.get('fromUtc'),toUtc:p.get('toUtc')};
    const run=/\/history\/([a-f0-9-]+)$/.exec(url.pathname),cursor=p.get('cursor')??undefined,limit=Number(p.get('limit'));
    await route.fulfill({status:200,json:run?backend.snapshot(binding,{runId:run[1],sampleTargetRevision:Number(p.get('sampleTargetRevision'))},cursor,limit):backend.read(binding,cursor,limit)});
  });
  await page.goto('/' + fileHash('102')); await openAllocation(page);
  await expect(allocation(page)).toContainText('Selected file allocation is unavailable in received exact snapshot pages.');
  await expect(allocation(page)).not.toContainText('9223372036854775807 bytes');
  await files(page).getByRole('button', { name: 'Next file snapshot page', exact: true }).click();
  await expect(allocation(page)).toContainText('Autogrowth disabled');
  await page.goto('/' + fileHash('999')); await openAllocation(page); await expect(allocation(page)).toContainText("No other file's configuration is substituted.");
});

test('partial failed and delayed historical file reads keep their source identity explicit',async({page})=>{
  await setup(page);
  await page.context().addCookies([{ name: 'nativeInsightsMode', value: 'file-stale', url: origin }]);
  await page.goto('/' + fileHash()); await page.reload(); await openAllocation(page); await expect(allocation(page)).toContainText('Receipt status: partial');
  let release, started = false, returned = false; const gate = new Promise(resolveGate => { release = resolveGate; });
  await page.route('**/observation-targets/' + first + '/resources/files/history/*?*', async route => {
    const response = await route.fetch(); started = true; await gate; await route.fulfill({ response }).catch(() => {}); returned = true;
  });
  await page.reload(); await expect.poll(() => started).toBe(true);
  try { await page.evaluate(next => { location.hash = next; }, fileHash('1', second)); await openAllocation(page); await expect(files(page)).toContainText('SYNTHETIC_exact_data_02'); }
  finally { release(); }
  await expect.poll(() => returned).toBe(true); expect(params(page).get('target')).toBe(second);
  await page.context().addCookies([{ name: 'nativeInsightsMode', value: 'file-error', url: origin }]); await page.reload();
  await expect(files(page).getByRole('region',{name:'File collection receipts',exact:true}).getByRole('alert')).toBeVisible();
  await expect(allocation(page)).toContainText('Unavailable');await expect(files(page)).not.toContainText('9223372036854775807 bytes');
});

test('health check workflow links preserve exact scope and choose configuration or database options explicitly', async ({ page }) => {
  await setup(page); await page.goto('/' + hash('operations', { databaseId: '6', compare: '1' }));
  const nav = page.getByRole('navigation', { name: 'Additional health checks', exact: true });
  for (const [label, section] of [['Configuration drift', 'summary'], ['Database options', 'databases']]) {
    const link = nav.getByRole('link', { name: label, exact: true }); await expect(link).toBeVisible();
    const destination = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
    expect(destination.get('target')).toBe(first); expect(destination.get('range')).toBe('custom');
    expect(destination.get('from')).toBe(from); expect(destination.get('to')).toBe(to);
    expect(destination.get('databaseId')).toBe('6'); expect(destination.get('compare')).toBe('1'); expect(destination.get('healthSection')).toBe(section);
  }
  await nav.getByRole('link', { name: 'Configuration drift', exact: true }).click(); await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.goBack(); await nav.getByRole('link', { name: 'Database options', exact: true }).click(); await expect(page.getByRole('tab', { name: 'Databases', exact: true })).toHaveAttribute('aria-selected', 'true');
});

for (const width of [320, 390, 768, 1440]) for (const surface of ['selected-plan-waits', 'selected-file-allocation']) {
  test(`${surface} evidence remains accessible and locally scrollable at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 }); await setup(page);
    if (surface === 'selected-plan-waits') { await page.goto('/' + queryHash()); await expect(waits(page)).toBeVisible(); await openWaitValues(page); await expect(page.getByText('Query performance request failed.', { exact: true })).toHaveCount(0); }
    else { await page.goto('/' + fileHash()); await openAllocation(page); await expect(allocation(page)).toContainText('9223372036854775807 bytes'); }
    const scroll = surface === 'selected-plan-waits' ? waits(page).getByRole('region', { name: 'Selected plan hourly wait values', exact: true }) : files(page).getByRole('region', { name: 'Historical database file facts', exact: true });
    await scroll.focus(); await expect(scroll).toBeFocused();
    if (await scroll.evaluate(element => element.scrollWidth > element.clientWidth)) { await scroll.press('ArrowRight'); await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0); await scroll.evaluate(element => { element.scrollLeft = 0; }); }
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('native-insights-axe', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations, JSON.stringify(results.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })))).toEqual([]);
    const folder = resolve('../artifacts/dashboard-native-insights/screenshots'); await mkdir(folder, { recursive: true });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `${surface}-${width}.png`), fullPage: true });
  });
}
