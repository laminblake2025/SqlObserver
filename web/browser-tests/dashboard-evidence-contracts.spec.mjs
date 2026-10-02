import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { volumeObservedUtc } from '../tools/sql-volume-fixture.mjs';

const origin = 'http://127.0.0.1:4324';
const first = '11111111-1111-4111-8111-000000000001', second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const href = (page, target = first, rewind = false) => '/#/' + page + '?' + new URLSearchParams({ target, range: rewind ? 'custom' : '1h', ...(rewind ? { from, to } : {}) });
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const volume = page => page.getByRole('region', { name: 'SQL-reported target-host volume capacity', exact: true });
const volumeTable = page => volume(page).getByRole('table');
const invalidVolume = 'Volume evidence returned an invalid or mismatched response. Load the latest snapshot to retry.';
async function fixture(page, mode = 'current') {
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'sqlVolumeFixture', value: '1', url: origin },
    { name: 'sqlVolumeMode', value: mode, url: origin }, { name: 'resourceInspectionFixture', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
}
test.beforeEach(async ({}, testInfo) => {
  await testInfo.attach('evidence-context', { body: JSON.stringify({ source: 'Synthetic native DTO fixture; no connected VM evidence', executedAtUtc: new Date().toISOString(), observedAtUtc: volumeObservedUtc }, null, 2), contentType: 'application/json' });
});

test('SQL-target volume capacity renders exact large bytes unknown full and qualified estimates across pinned pages', async ({ page }) => {
  await fixture(page); const reads = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/resources/volumes')) reads.push(request.url()); });
  await page.goto(href('resources'));
  await expect(volumeTable(page)).toContainText('50.00%');
  await expect(volumeTable(page)).toContainText('4 days');
  await expect(volumeTable(page)).toContainText('Capacity unknown');
  await expect(volumeTable(page)).toContainText('Full');
  await expect(volumeTable(page)).toContainText('Needs history');
  await expect(volume(page).locator('[title="9223372036854775807 bytes"]')).toBeVisible();
  await expect(volume(page)).not.toContainText('C:\\');
  await volume(page).getByRole('button', { name: 'Next volume page', exact: true }).click();
  await expect(volume(page)).toContainText('Page 2');
  await expect(volumeTable(page)).toContainText('Not depleting');
  await expect(volumeTable(page)).toContainText('Unstable trend');
  expect(new URL(reads[1]).searchParams.get('cursor')).toBe('dm9sdW1lOjI=');
  await volume(page).getByRole('button', { name: 'Previous volume page', exact: true }).click();
  await expect(volumeTable(page)).toContainText('4 days');
  await expect(volume(page)).toContainText('Page 1');
  expect(scope(page).get('range')).toBe('1h');
});

test('partial and absent SQL-volume observations do not invent capacity deadlines or historical evidence', async ({ page }) => {
  await fixture(page, 'partial'); await page.goto(href('resources'));
  await expect(volume(page)).toContainText('Some volume evidence was lost during collection.');
  await expect(volume(page)).toContainText('At least 2 items lost.');
  await expect(volumeTable(page)).toContainText('No estimate');
  await expect(volumeTable(page)).not.toContainText('4 days');
  await page.context().addCookies([{ name: 'sqlVolumeMode', value: 'not-collected', url: origin }]); await page.reload();
  await expect(volume(page)).toContainText('SQL volume collection has not produced a snapshot yet.');
  await expect(volumeTable(page)).toHaveCount(0);
  let rewindReads = 0; page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/resources/volumes')) rewindReads++; });
  await page.goto(href('resources', first, true));
  await expect(volume(page)).toContainText('Historical volume capacity is not available in Rewind.');
  await expect(volumeTable(page)).toHaveCount(0); expect(rewindReads).toBe(0);
  expect(scope(page).get('from')).toBe(from); expect(scope(page).get('to')).toBe(to);
});

test('malformed foreign revision and incompatible volume trends fail visibly without crashing Resources', async ({ page }) => {
  await fixture(page); const errors = []; page.on('pageerror', error => errors.push(error.message));
  for (const mode of ['malformed', 'foreign', 'revision', 'invalid-capacity', 'invalid-trend', 'oversized']) {
    await page.context().addCookies([{ name: 'sqlVolumeMode', value: mode, url: origin }]);
    await page.goto(href('resources')); await page.reload();
    await expect(volume(page).getByRole('alert')).toContainText(invalidVolume);
    await expect(volumeTable(page)).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'Resources for SYNTHETIC SQL 01', exact: true })).toBeVisible();
  }
  expect(errors).toEqual([]);
  await page.context().addCookies([{ name: 'sqlVolumeMode', value: 'current', url: origin }]);
  await volume(page).getByRole('button', { name: 'Load latest snapshot', exact: true }).click();
  await expect(volumeTable(page)).toContainText('4 days');
});

test('superseded or mismatched volume pagination clears the previous page and requires a fresh snapshot', async ({ page }) => {
  await fixture(page); await page.goto(href('resources')); await expect(volumeTable(page)).toContainText('4 days');
  await page.context().addCookies([{ name: 'sqlVolumeMode', value: 'superseded', url: origin }]);
  await volume(page).getByRole('button', { name: 'Next volume page', exact: true }).click();
  await expect(volumeTable(page)).toHaveCount(0);
  await expect(volume(page)).toContainText(/newer|superseded/i);
  await page.context().addCookies([{ name: 'sqlVolumeMode', value: 'current', url: origin }]);
  await volume(page).getByRole('button', { name: 'Load latest snapshot', exact: true }).click();
  await expect(volumeTable(page)).toContainText('4 days');
  await page.route(`**/observation-targets/${first}/resources/volumes?*`, async route => {
    const response = await route.fetch(), body = await response.json();
    if (new URL(route.request().url()).searchParams.has('cursor')) body.snapshotRunId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
    await route.fulfill({ response, json: body });
  });
  await volume(page).getByRole('button', { name: 'Next volume page', exact: true }).click();
  await expect(volumeTable(page)).toHaveCount(0);
  await expect(volume(page).getByRole('alert')).toContainText(/snapshot|mismatched/i);
});

test('delayed old-server volume responses and failed refresh cannot republish previous capacity', async ({ page }) => {
  await fixture(page); let release, started = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/resources/volumes?*`, async route => { started = true; await gate; await route.continue().catch(() => {}); returned = true; });
  try {
    await page.goto(href('resources')); await expect.poll(() => started).toBe(true);
    await page.goto(href('resources', second));
    await expect(volumeTable(page)).toContainText('4 days');
    await expect(volume(page).locator('[title^="Opaque volume key 00000002"]')).toHaveCount(4);
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(volume(page).locator('[title^="Opaque volume key 00000001"]')).toHaveCount(0);
  await page.context().addCookies([{ name: 'sqlVolumeMode', value: 'error', url: origin }]);
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
  await expect(volume(page).getByRole('alert')).toContainText('The volume capacity request failed. Refresh and retry.');
  await expect(volumeTable(page)).toHaveCount(0);
});

async function queryFixture(page) {
  await fixture(page); await page.context().addCookies([{ name: 'queryRowTrendFixture', value: '1', url: origin }]);
  await page.route('**/query-performance/top?*', async route => {
    const response = await route.fetch(), body = await response.json();
    body.items.push({ ...body.items[0], source: 'plan_cache', sourceState: 'disabled', semantics: 'plan_cache_cumulative',
      planFingerprint: null, queryFingerprint: 'd'.repeat(64), collectionRunId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', observationKey: 'f'.repeat(32) });
    await route.fulfill({ response, json: body });
  });
}
const sourceControl = page => page.getByRole('combobox', { name: 'Source', exact: true });
const ranking = page => page.getByRole('region', { name: 'Ranked query observations', exact: true });
const rankingRows = page => ranking(page).locator('tbody tr');

test('query source filter restores native rankings through reload back forward and independent query selection', async ({ page }) => {
  await queryFixture(page); await page.goto(href('queries', first, true) + '&querySource=plan_cache');
  await expect(sourceControl(page)).toHaveValue('plan_cache'); await expect(rankingRows(page)).toHaveCount(1);
  await expect(ranking(page)).toContainText('dddddddddddd');
  await sourceControl(page).selectOption('query_store');
  await expect.poll(() => scope(page).get('querySource')).toBe('query_store');
  await expect(rankingRows(page)).toHaveCount(3);
  await page.reload(); await expect(sourceControl(page)).toHaveValue('query_store'); await expect(rankingRows(page)).toHaveCount(3);
  await page.goBack(); await expect(sourceControl(page)).toHaveValue('plan_cache'); await expect(rankingRows(page)).toHaveCount(1);
  await page.goForward(); await expect(sourceControl(page)).toHaveValue('query_store');
  await ranking(page).getByRole('button', { name: /000000001000/ }).click();
  await expect.poll(() => scope(page).get('source')).toBe('query_store');
  expect(scope(page).get('querySource')).toBe('query_store');
  expect(scope(page).get('from')).toBe(from); expect(scope(page).get('to')).toBe(to);
});

test('invalid or duplicate query source URL values show no fallback ranking and valid controls repair the selection', async ({ page }) => {
  await queryFixture(page);
  for (const invalid of ['unknown', 'query_store&querySource=plan_cache']) {
    await page.goto(href('queries', first, true) + '&querySource=' + invalid);
    await expect(sourceControl(page)).toHaveValue('invalid');
    await expect(page.getByRole('alert').filter({ hasText: 'The shared URL contains an invalid or duplicate query source filter.' })).toBeVisible();
    await expect(sourceControl(page).getByRole('option', { name: 'Invalid source filter', exact: true })).toHaveCount(1);
    await expect(rankingRows(page)).toHaveCount(0);
    await sourceControl(page).selectOption('mixed');
    await expect(rankingRows(page)).toHaveCount(4);
    await expect.poll(() => scope(page).getAll('querySource').length).toBeLessThanOrEqual(1);
  }
});

test('query source filter survives metric view and database controls while delayed history cannot overwrite it', async ({ page }) => {
  await queryFixture(page); await page.goto(href('queries', first, true) + '&querySource=query_store');
  await expect(rankingRows(page)).toHaveCount(3);
  await page.getByRole('combobox', { name: 'Rank by', exact: true }).selectOption('duration');
  await expect.poll(() => scope(page).get('queryMetric')).toBe('duration'); expect(scope(page).get('querySource')).toBe('query_store');
  await page.getByRole('combobox', { name: 'Database', exact: true }).selectOption('5');
  await expect(rankingRows(page)).toHaveCount(2); expect(scope(page).get('querySource')).toBe('query_store');
  await page.getByRole('tab', { name: 'Cache interval deltas', exact: true }).click();
  await expect.poll(() => scope(page).get('queryView')).toBe('cache'); expect(scope(page).get('querySource')).toBe('query_store');
  await page.getByRole('tab', { name: 'Top queries', exact: true }).click(); await expect(sourceControl(page)).toHaveValue('query_store');
  let release, started = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/query-performance/databases/5/history/*', async route => { started = true; await gate; await route.continue().catch(() => {}); returned = true; });
  try {
    await ranking(page).getByRole('button', { name: /000000001000/ }).click(); await expect.poll(() => started).toBe(true);
    await sourceControl(page).selectOption('plan_cache'); await expect(rankingRows(page)).toHaveCount(1);
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  const history = page.getByText('Bounded history observations', { exact: true });
  await expect(history).toBeVisible(); await history.click();
  await expect(history.locator('xpath=parent::details')).toContainText('CPU 120');
  await expect(sourceControl(page)).toHaveValue('plan_cache'); expect(scope(page).get('querySource')).toBe('plan_cache');
});

test('protected query text is bound to the selected observation and rejects a late result after query and window changes', async ({ page }) => {
  await queryFixture(page);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '1', url: origin }]);
  const queryA = '000000001000' + '0'.repeat(52), queryB = '000000001002' + '0'.repeat(52);
  const textA = 'SELECT 101 AS synthetic_query_a_only;', textB = 'SELECT 202 AS synthetic_query_b_only;';
  const reads = [];
  let release, delayed = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/query-performance/databases/5/history/*/runs/*/text', async route => {
    const match = /\/history\/([0-9a-f]{64})\/runs\/([^/]+)\/text$/.exec(new URL(route.request().url()).pathname);
    reads.push(match[1]);
    if (match[1] === queryB) { delayed = true; await gate; }
    await route.fulfill({ status: 200, contentType: 'application/json', json: {
      targetId: first, databaseId: 5, queryFingerprint: match[1], collectionRunId: match[2],
      status: 'available', text: match[1] === queryA ? textA : textB,
    } }).catch(() => {});
    if (match[1] === queryB) returned = true;
  });
  await page.goto(href('queries', first, true) + '&querySource=query_store');
  const selected = page.getByRole('heading', { name: 'Selected query', exact: true }).locator('xpath=parent::section');
  const captured = page.locator('pre[aria-label="Captured Query Store text"]');
  const openText = page.getByRole('button', { name: 'Open query text for selected observation', exact: true });
  await ranking(page).getByRole('button', { name: /000000001000/ }).click();
  await expect(selected).toContainText(queryA); await expect(openText).toBeVisible();
  expect(reads).toEqual([]);
  await openText.click(); await expect(captured).toHaveText(textA);
  await sourceControl(page).selectOption('plan_cache');
  await expect(selected).toContainText('The inspected observation is hidden by the ranking source filter; its history remains selected.');
  await expect(captured).toHaveText(textA); expect(reads).toEqual([queryA]);
  expect(scope(page).get('queryFingerprint')).toBe(queryA); expect(scope(page).get('source')).toBe('query_store');
  await sourceControl(page).selectOption('query_store');
  const rowB = ranking(page).getByRole('button', { name: /000000001002/ });
  await rowB.click(); await expect(rowB).toHaveAttribute('aria-pressed', 'true');
  await expect(selected).toContainText(queryB); await expect(captured).toHaveCount(0);
  expect(reads).toEqual([queryA]);
  try {
    await openText.click(); await expect.poll(() => delayed).toBe(true);
    const next = scope(page);
    next.set('queryFingerprint', queryA); next.set('planFingerprint', '000000002000' + '0'.repeat(52));
    next.set('from', '2026-09-26T09:00:00.123456Z');
    await page.evaluate(hash => { location.hash = hash; }, '#/queries?' + next);
    await expect.poll(() => scope(page).get('from')).toBe('2026-09-26T09:00:00.123456Z');
    await expect(selected).toContainText(queryA); await expect(captured).toHaveCount(0);
    await expect(selected).not.toContainText('Opening query text');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(captured).toHaveCount(0); await expect(page.getByText(textB, { exact: true })).toHaveCount(0);
  await expect(sourceControl(page)).toHaveValue('query_store');
  expect(scope(page).get('source')).toBe('query_store'); expect(scope(page).get('querySource')).toBe('query_store');
  expect(reads).toEqual([queryA, queryB]);
});

for (const width of [320, 390, 768, 1440]) {
  test(`populated native SQL volume capacity and forecasts remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page); await page.goto(href('resources'));
    await expect(volumeTable(page)).toContainText('4 days');
    const scroll = volume(page).getByRole('region', { name: 'Latest SQL-reported volume capacity', exact: true });
    await scroll.focus(); await expect(scroll).toBeFocused();
    if (width < 960) {
      expect(await scroll.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
      await scroll.press('ArrowRight');
      await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
      await scroll.evaluate(element => { element.scrollLeft = 0; });
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('volume-axe', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations, JSON.stringify(results.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })))).toEqual([]);
    const folder = resolve('../artifacts/dashboard-evidence-contracts/screenshots'); await mkdir(folder, { recursive: true });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `sql-volume-capacity-${width}.png`), fullPage: true });
  });
}
