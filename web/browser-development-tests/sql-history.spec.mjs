import { test, expect } from '@playwright/test';

const first = '00000000-0000-4000-8000-000000000001';
const second = '00000000-0000-4000-8000-000000000002';
const units = {
  'engine.user_connections': 'connections',
  'engine.process_physical_memory_bytes': 'GiB',
  'engine.os_available_memory_bytes': 'GiB',
  'engine.memory_grants_pending': 'grants',
  'engine.scheduler_runnable_tasks': 'tasks',
  'engine.batch_requests_per_second': 'batches/sec',
  'engine.sql_scheduler_cpu_percent': '%',
};

async function seedWindow(request) {
  const response = await request.get('/api/v1/observation-targets?limit=10');
  expect(response.status(), 'Start the guarded stack with tools/dev-up.ps1').toBe(200);
  const targets = await response.json();
  expect(targets.items.map(item => item.instanceId).sort()).toEqual([first, second]);
  for (const item of targets.items) {
    expect(item.displayName).toMatch(/^\[Sample\] /);
    expect(item.host).toMatch(/\.invalid$/);
    expect(item.configurationRevision).toBe(1);
  }
  const to = targets.items.find(item => item.instanceId === first).discoveryRequestedAtUtc;
  // Keep the stored microseconds: JavaScript Date otherwise truncates the
  // exclusive end and can accidentally omit the fixture's final sample.
  const from = new Date(Date.parse(to) - 3600000).toISOString().replace(/\.\d{3}Z$/, to.slice(19));
  return { from, to };
}

test('Vite, development authentication and PostgreSQL return SQL history with bounded rates and target scope', async ({ request }) => {
  const { from, to } = await seedWindow(request);
  for (const target of [first, second]) {
    const response = await request.get(`/api/v1/overview?${new URLSearchParams({ targetId: target, fromUtc: from, toUtc: to, useLatestStatus: 'true' })}`);
    expect(response.status()).toBe(200);
    const overview = await response.json();
    expect(overview.evidence).toHaveLength(1);
    const evidence = overview.evidence[0];
    expect(evidence.targetId).toBe(target);
    const sql = evidence.series.filter(series => Object.hasOwn(units, series.metric));
    expect(sql.map(series => series.metric).sort()).toEqual(Object.keys(units).sort());
    for (const series of sql) {
      expect(series.targetId).toBe(target);
      expect(series.unit).toBe(units[series.metric]);
      expect(series.points.some(point => point.value !== null)).toBe(true);
    }
    for (const [metric, expected] of [['engine.sql_scheduler_cpu_percent', 25], ['engine.batch_requests_per_second', 100],
      ['engine.os_available_memory_bytes', 16], ['engine.memory_grants_pending', target === first ? 2 : 0],
      ['engine.scheduler_runnable_tasks', target === first ? 3 : 1]]) {
      const values = sql.find(series => series.metric === metric).points.filter(point => point.value !== null).map(point => point.value);
      expect(values.length).toBeGreaterThan(0);
      for (const value of values) expect(value).toBeCloseTo(expected, 8);
    }
    const connections = sql.find(series => series.metric === 'engine.user_connections').points.filter(point => point.value !== null);
    for (const point of connections) {
      expect(point.value).toBeGreaterThanOrEqual(target === first ? 40 : 12);
      expect(point.value).toBeLessThanOrEqual(target === first ? 55 : 27);
    }
  }
  const foreign = await request.get('/api/v1/observation-targets/00000000-0000-4000-8000-000000000999/analytics/sql-baselines?'
    + new URLSearchParams({ metricKey: 'engine.sql_scheduler_cpu_percent', targetRevision: '1', catalogVersion: '2', generation: '1',
      fromUtc: new Date(Date.parse(to.slice(0, 10) + 'T00:00:00Z') - 28 * 86400000).toISOString(), toUtc: to.slice(0, 10) + 'T00:00:00Z' }));
  expect(foreign.status()).toBe(403);
});

test('Resources reads the real API, preserves the saved window on server change and shows missing baseline history', async ({ page, request }) => {
  const { from, to } = await seedWindow(request);
  const reads = [], errors = [];
  page.on('response', response => {
    if (response.url().includes('/analytics/sql-baselines?')) reads.push(response);
  });
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`/#/resources?${new URLSearchParams({ target: first, range: 'custom', from, to })}`);
  await expect(page.getByRole('heading', { name: 'Resources for [Sample] Orders SQL', exact: true })).toBeVisible();
  await expect(page.getByText('No SQL baseline covers these UTC hours.', { exact: false })).toBeVisible();
  const cpu = page.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true }) });
  await expect(cpu.getByRole('img')).toBeVisible();
  await expect(cpu.locator('.chart-baseline-band')).toHaveCount(0);
  await expect.poll(() => reads.filter(response => response.url().includes(first)).length).toBe(7);
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await expect(page.getByRole('heading', { name: 'Resources for [Sample] Reporting SQL', exact: true })).toBeVisible();
  await expect.poll(() => reads.filter(response => response.url().includes(second)).length).toBe(7);
  const selected = new URLSearchParams(page.url().split('?')[1]);
  expect(selected.get('from')).toBe(from); expect(selected.get('to')).toBe(to);
  const end = Date.parse(to.slice(0, 10) + 'T00:00:00Z');
  for (const response of reads) {
    expect(response.status()).toBe(200);
    const url = new URL(response.url());
    expect(url.searchParams.get('targetRevision')).toBe('1');
    expect(url.searchParams.get('catalogVersion')).toBe('2');
    expect(Date.parse(url.searchParams.get('toUtc'))).toBe(end);
    expect(Date.parse(url.searchParams.get('fromUtc'))).toBe(end - 28 * 86400000);
    const bundle = await response.json();
    expect(bundle.targetId).toBe(url.pathname.includes(first) ? first : second);
    expect(bundle.state).toBe('no_data'); expect(bundle.items).toEqual([]);
    expect(bundle.catalogVersion).toBe(2);
  }
  expect(errors).toEqual([]);
});
