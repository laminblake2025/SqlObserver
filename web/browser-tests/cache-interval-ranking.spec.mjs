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

test('cache drilldown restores from its URL and pages exact samples with comparison gaps', async ({ page }) => {
  const historyRequests=[];
  await page.route('**/query-performance/cache/top?*', route => route.fulfill({json:body(new URL(route.request().url()))}));
  const sample=index=>{
    const observed=new Date(Date.parse('2026-09-28T12:00:00Z')-index*1000).toISOString();
    const unknown=index===0;
    return {collectionRunId:`11111111-1111-4111-8111-${String(index+1).padStart(12,'0')}`,cacheEntryFingerprint:'d'.repeat(64),sourceEpoch:'e'.repeat(64),
      observedAtUtc:observed,committedAtUtc:observed,captureComplete:false,transition:unknown?'reset':'comparable',
      intervalFromUtc:unknown?null:new Date(Date.parse(observed)-1000).toISOString(),intervalToUtc:unknown?null:observed,
      cpuMilliseconds:unknown?null:'0.25000000000000000000',durationMilliseconds:unknown?null:'0.50000000000000000000',
      executions:unknown?null:'9007199254740993',logicalReads:unknown?null:'1',writes:unknown?null:'2',rows:unknown?null:'3'};
  };
  await page.route('**/query-performance/cache/databases/6/history/*/plans/*?*',async route=>{
    const url=new URL(route.request().url());historyRequests.push(url);
    const earlier=url.searchParams.has('cursor');
    await route.fulfill({json:{targetId:first,databaseId:6,databaseIncarnation:'a'.repeat(64),queryFingerprint:'b'.repeat(64),planFingerprint:'c'.repeat(64),
      fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc'),semantics:'plan_cache_delta',coverage:'observed_entries_only',
      revision:1,snapshotUtc:'2026-09-29T00:00:00Z',totalRows:102,nextCursor:earlier?null:'YQ.Yg',
      items:earlier?[sample(100),sample(101)]:Array.from({length:100},(_,index)=>sample(index))}});
  });
  await page.goto(`${href(first)}&queryView=cache`);
  await page.getByRole('button',{name:'Inspect cccccccccccc…'}).click();
  const history=page.getByRole('region',{name:'Cache query interval history'});
  await expect(history.getByText('100 of 102 observed samples',{exact:false})).toBeVisible();
  await expect(history.getByRole('cell',{name:'Counters reset',exact:true})).toBeVisible();
  await expect(history.getByRole('cell',{name:'Unavailable',exact:true})).toHaveCount(3);
  await expect(history.getByRole('cell',{name:'9007199254740993',exact:true})).toHaveCount(99);
  const trend=history.getByRole('region',{name:'Cache interval rate trend'});
  await expect(trend.getByRole('img',{name:/Measured cache interval rates \(CPU ms\/s\)/})).toBeVisible();
  await expect(trend.locator('path.chart-series')).toHaveCount(99);
  await expect(trend.getByText('99 measured intervals plotted · 1 unavailable comparisons',{exact:true})).toBeVisible();
  expect(await trend.locator('circle.chart-marker title').first().textContent()).toMatch(/0\.25$/);

  await expect(page).toHaveURL(/cacheIncarnation=/);
  await page.reload();
  await expect(history.getByText('100 of 102 observed samples',{exact:false})).toBeVisible();
  await history.getByRole('button',{name:'Load earlier cache samples'}).click();
  await expect(history.getByText('102 of 102 observed samples',{exact:false})).toBeVisible();
  expect(historyRequests.at(-1).searchParams.get('cursor')).toBe('YQ.Yg');
  expect(historyRequests.at(-1).searchParams.get('databaseIncarnation')).toBe('a'.repeat(64));
  await expect(history.getByRole('button',{name:'Load earlier cache samples'})).toHaveCount(0);
  await expect(trend.locator('path.chart-series')).toHaveCount(101);
  await page.getByRole('combobox',{name:'Rank cache activity by'}).selectOption('duration');
  await expect(trend.getByRole('img',{name:/Measured cache interval rates \(Duration ms\/s\)/})).toBeVisible();
  expect(await trend.locator('circle.chart-marker title').first().textContent()).toMatch(/0\.5$/);
  const beforeDrag=historyRequests.length;
  const svg=trend.getByRole('img'); await svg.scrollIntoViewIfNeeded();
  const bounds=await svg.boundingBox();
  expect(bounds).not.toBeNull();
  const left=bounds.x+(48+572*10/24)/650*bounds.width, right=bounds.x+(48+572*14/24)/650*bounds.width;
  await page.mouse.move(left,bounds.y+bounds.height/2); await page.mouse.down();
  await page.mouse.move(right,bounds.y+bounds.height/2,{steps:4}); await page.mouse.up();
  await expect.poll(()=>historyRequests.length).toBeGreaterThan(beforeDrag);
  await expect(trend.getByRole('img')).toBeVisible();
  const narrowed=historyRequests.at(-1);
  expect(Date.parse(narrowed.searchParams.get('fromUtc'))).toBeGreaterThan(Date.parse(from));
  expect(Date.parse(narrowed.searchParams.get('toUtc'))).toBeLessThan(Date.parse(to));
  expect(narrowed.searchParams.get('cursor')).toBeNull();
  await expect(page).toHaveURL(/cacheIncarnation=/); await expect(page).toHaveURL(/queryMetric=duration/);

  const accessibility=await new AxeBuilder({page}).include('section[aria-label="Cache query interval history"]').analyze();
  expect(accessibility.violations).toEqual([]);
  await page.getByRole('combobox',{name:'Target server'}).selectOption(second);
  await expect(history).toHaveCount(0); await expect(page).not.toHaveURL(/cacheIncarnation=/);
});
