import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-28T00:00:00.000Z', to = '2026-09-29T00:00:00.000Z';
const href = target => `/#/queries?${new URLSearchParams({ target, range: 'custom', from, to })}`;
const body = url => ({ targetId: url.pathname.split('/')[4], fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'),
  metric: url.searchParams.get('metric'), semantics: 'plan_cache_delta', coverage: 'observed_entries_only',
  sourceFrames: 2, incompleteFrames: 1, comparableRows: 1, unavailableRows: 3, boundaryRows: 1, hasMore: false,
  items: [{ databaseId: 6, databaseIncarnation: 'a'.repeat(64), queryFingerprint: 'b'.repeat(64), planFingerprint: 'c'.repeat(64),
    cpuMilliseconds: url.pathname.includes(second) ? '2.00000000000000000000' : '9007199254740993.00100000000000000000',
    durationMilliseconds: '0.50000000000000000000', executions: '9007199254740993', logicalReads: '1', writes: '2', rows: '3',
    comparableRows: 1, firstObservedAtUtc: '2026-09-28T11:00:00Z', lastObservedAtUtc: '2026-09-28T12:00:00Z' }] });

test('cache view preserves exact numbers, exposes coverage and follows the shared window and metric', async ({ page }) => {
  const requests = [];
  await page.route('**/query-performance/cache/top?*', async route => {
    const url = new URL(route.request().url()); requests.push(url);
    await route.fulfill({ json: body(url) });
  });
  await page.goto(href(first));
  await page.getByRole('tab', { name: 'Cache interval deltas' }).click();
  const panel = page.getByRole('region', { name: 'Cache interval deltas' });
  await expect(panel.getByRole('cell', { name: '9007199254740993.001', exact: true })).toBeVisible();
  await expect(panel.getByRole('cell', { name: '9007199254740993', exact: true })).toBeVisible();
  await expect(panel.getByText('3 unavailable comparisons', { exact: false })).toBeVisible();
  await expect(panel.getByText('1 boundary crossings excluded', { exact: false })).toBeVisible();
  await expect(panel.getByText('1 of 2 captures incomplete', { exact: false })).toBeVisible();
  await panel.getByRole('combobox', { name: 'Rank cache activity by' }).selectOption('duration');
  await expect(panel.getByRole('table', { name: 'Top 25 observed cache query plans by duration' })).toBeVisible();
  await expect(page).toHaveURL(/queryView=cache/);
  await expect(page).toHaveURL(/queryMetric=duration/);
  expect(requests.at(-1).searchParams.get('metric')).toBe('duration');
  for (const request of requests) {
    expect(request.pathname).toContain(first);
    expect(Date.parse(request.searchParams.get('fromUtc'))).toBe(Date.parse(from));
    expect(Date.parse(request.searchParams.get('toUtc'))).toBe(Date.parse(to));
  }
  const accessibility = await new AxeBuilder({ page }).include('section[aria-label="Cache interval deltas"]').analyze();
  expect(accessibility.violations).toEqual([]);
});

test('changing server during a pending cache read cannot display the previous server evidence', async ({ page }) => {
  let release, notify;
  const gate = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { notify = resolve; });
  await page.route('**/query-performance/cache/top?*', async route => {
    const url = new URL(route.request().url());
    if (url.pathname.includes(first)) { notify(); await gate; }
    await route.fulfill({ json: body(url) }).catch(() => {});
  });
  await page.goto(href(first));
  await page.getByRole('tab', { name: 'Cache interval deltas' }).click();
  await started;
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await expect(page.getByRole('heading', { name: 'Queries for SYNTHETIC SQL 02' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Cache interval deltas' })).toHaveAttribute('aria-selected', 'true');
  const panel = page.getByRole('region', { name: 'Cache interval deltas' });
  await expect(panel.getByRole('cell', { name: '2', exact: true })).toBeVisible();
  release();
  await expect(panel.getByText('9007199254740993.001', { exact: true })).toHaveCount(0);
  await expect(page).toHaveURL(new RegExp(second));
});

test('a shared cache URL restores view and metric across reload, Rewind edits and browser history', async ({ page }) => {
  const requests = [];
  await page.route('**/query-performance/cache/top?*', async route => {
    const url = new URL(route.request().url()); requests.push(url);
    await route.fulfill({ json: body(url) });
  });
  await page.goto(`${href(first)}&queryView=cache&queryMetric=logical_reads`);
  const panel = page.getByRole('region', { name: 'Cache interval deltas' });
  await expect(page.getByRole('tab', { name: 'Cache interval deltas' })).toHaveAttribute('aria-selected', 'true');
  await expect(panel.getByRole('combobox', { name: 'Rank cache activity by' })).toHaveValue('logical_reads');
  await expect(panel.getByRole('table', { name: 'Top 25 observed cache query plans by logical reads' })).toBeVisible();
  await page.reload();
  await expect(panel.getByRole('combobox', { name: 'Rank cache activity by' })).toHaveValue('logical_reads');
  await expect(panel.getByRole('table')).toBeVisible();
  await page.getByRole('button', { name: 'Edit UTC range' }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-28T08:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-28T20:00');
  await page.getByRole('button', { name: 'Apply range' }).click();
  await expect(panel.getByRole('table')).toBeVisible();
  await expect.poll(() => Date.parse(requests.at(-1).searchParams.get('fromUtc'))).toBe(Date.parse('2026-09-28T08:00:00Z'));
  expect(requests.at(-1).searchParams.get('metric')).toBe('logical_reads');
  await expect(page).toHaveURL(/queryView=cache/);
  await expect(page).toHaveURL(/queryMetric=logical_reads/);
  await panel.getByRole('combobox', { name: 'Rank cache activity by' }).selectOption('writes');
  await expect(panel.getByRole('table', { name: 'Top 25 observed cache query plans by writes' })).toBeVisible();
  await page.goBack();
  await expect(panel.getByRole('combobox', { name: 'Rank cache activity by' })).toHaveValue('logical_reads');
  await expect(panel.getByRole('table', { name: 'Top 25 observed cache query plans by logical reads' })).toBeVisible();
});
