import { test, expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const targetId = 'dfb2b72c-4ad4-4765-9523-528c798482f0';
const output = resolve('../.artifacts/lab-dashboard-20261009');

test('real lab inventory, overview, summary and captured activity reach the current UI', async ({ page, request }) => {
  await mkdir(output, { recursive: true });
  const status = await request.get('/__lab/status');
  expect(await status.json()).toMatchObject({ upstream: 'https://WIN-QNGOV5GDM24:5443', tls: 'validated', syntheticData: false });
  const inventory = await request.get('/api/v1/observation-targets/');
  expect(inventory.status()).toBe(200);
  expect((await inventory.json()).items.map(item => item.instanceId)).toEqual([targetId]);
  const health = await request.get(`/api/v1/observation-targets/${targetId}/health`);
  expect(health.status()).toBe(200);
  const evidence = await health.json();
  expect(evidence.state).toBe('current');
  expect(evidence.collectors.some(item => item.lastExecutionOutcome === 'succeeded')).toBe(true);
  expect(Date.now() - Date.parse(evidence.collectors[0].lastSuccessAtUtc)).toBeLessThan(120_000);
  const live = await request.get(`/api/v1/observation-targets/${targetId}/activity/live`);
  expect(live.status()).toBe(200);
  const snapshot = await live.json();
  expect(snapshot.state).toBe('available');
  expect(snapshot.snapshotId).toMatch(/^[0-9a-f-]{36}$/);
  const responses = [];
  page.on('response', response => { if (response.url().includes('/api/v1/')) responses.push({ path: new URL(response.url()).pathname, status: response.status() }); });
  await page.goto('/#/overview?range=1h');
  await expect(page.getByRole('heading', { name: 'Fleet overview', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'SQL Server 2025 disposable lab', exact: true })).toBeVisible();
  await page.screenshot({ path: resolve(output, 'fleet-connected.png'), fullPage: true });
  await page.getByRole('link', { name: 'SQL Server 2025 disposable lab', exact: true }).click();
  await expect(page.getByRole('navigation', { name: 'Server diagnostics', exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Summary', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'SQL workload', exact: true })).toBeVisible();
  await page.screenshot({ path: resolve(output, 'server-connected.png'), fullPage: true });
  await page.getByRole('link', { name: 'Activity', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Activity', exact: true })).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Observed:' })).toBeVisible();
  await expect(page.getByRole('table', { name: 'All captured sessions for this observation' })).toBeVisible();
  await page.screenshot({ path: resolve(output, 'activity-connected.png'), fullPage: true });
  await writeFile(resolve(output, 'connection-evidence.json'), JSON.stringify({
    checkedAtUtc: new Date().toISOString(), upstream: 'https://WIN-QNGOV5GDM24:5443', targetId,
    lastCollectionUtc: evidence.collectors[0].lastSuccessAtUtc, metrics: evidence.coreMetrics.length,
    snapshotId: snapshot.snapshotId, snapshotUtc: snapshot.observedUtc, sessionRows: snapshot.rows.length,
    responses,
  }, null, 2));
});

test('lab relay refuses cross-origin, forged-host and forwarded requests before upstream access', async ({ request }) => {
  for (const headers of [
    { Origin: 'https://example.invalid' }, { Host: 'example.invalid' },
    { 'X-Forwarded-Host': 'localhost' }, { 'Sec-Fetch-Site': 'cross-site' },
  ]) expect((await request.get('/api/v1/observation-targets/', { headers })).status()).toBe(403);
  expect((await request.post('/api/v1/observation-targets', { data: {}, headers: { Origin: 'https://example.invalid' } })).status()).toBe(403);
  expect((await request.post('/api/v1/observation-targets', { data: {} })).status()).toBe(403);
  const origin = new URL(test.info().project.use.baseURL).origin;
  expect((await request.post('/api/v1/__lab_nonexistent_route', { data: { probe: true }, headers: { Origin: origin } })).status()).toBe(404);
  const permission = await request.get(`/api/v1/me?targetId=${targetId}`);
  expect(permission.status()).toBe(200);
  const access = await permission.json();
  expect(access).toMatchObject({ active: true, targetId });
  expect(access.targetRoles).toContain('QueryTextReader');
  expect(access.targetRoles.some(role => ['Viewer', 'Operator', 'TargetAdministrator'].includes(role))).toBe(true);
});
