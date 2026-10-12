import { test, expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const targetId = 'dfb2b72c-4ad4-4765-9523-528c798482f0';
const output = resolve('../.artifacts/lab-dashboard-20261009');

test('QueryTextReader loads captured SQL only after a deliberate request for the selected session', async ({ page, request }) => {
  const runId = process.env.SQLOBSERVER_QUERYTEXT_VERIFICATION_RUN;
  expect(runId, 'A bounded native lab SQL probe must be running for this verification').toMatch(/^[0-9a-f]{32}$/);
  const application = `SQL Observer QueryTextReader verification ${runId}`;
  let snapshot;
  await expect.poll(async () => {
    const response = await request.get(`/api/v1/observation-targets/${targetId}/activity/live?application=${encodeURIComponent(application)}`);
    expect(response.status()).toBe(200);
    snapshot = await response.json();
    return snapshot.rows.some(row => row.application === application && row.queryId);
  }, { timeout: 40_000, intervals: [1000, 2000, 3000] }).toBe(true);
  const row = snapshot.rows.find(item => item.application === application && item.queryId);
  const queryReads = [];
  page.on('response', response => {
    if (new URL(response.url()).pathname.endsWith('/activity/live/query')) queryReads.push(response.status());
  });
  const params = new URLSearchParams({ target: targetId, range: '1h', sessionSnapshot: snapshot.snapshotId, sessionObserved: snapshot.observedUtc, sessionIdentity: row.identity });
  await page.goto(`/#/activity?${params}`);
  await page.getByRole('button', { name: 'Captured SQL', exact: true }).click();
  const load = page.getByRole('button', { name: 'Load captured SQL', exact: true });
  await expect(load).toBeVisible();
  expect(queryReads).toEqual([]);
  await load.click();
  await expect(page.locator('pre[aria-label="Captured executing SQL"]')).toHaveText("WAITFOR DELAY '00:05:00';");
  expect(queryReads).toEqual([200]);
  await page.screenshot({ path: resolve(output, 'querytextreader-connected.png'), fullPage: true });
  await writeFile(resolve(output, 'querytextreader-evidence.json'), JSON.stringify({
    checkedAtUtc: new Date().toISOString(), targetId, runId,
    snapshotId: snapshot.snapshotId, snapshotUtc: snapshot.observedUtc,
    identity: row.identity, sessionId: row.sessionId,
    deliberateRequest: true, queryHttpStatus: queryReads[0],
    capturedKnownProbeStatement: true, queryTextStoredInEvidence: false,
  }, null, 2));
});
