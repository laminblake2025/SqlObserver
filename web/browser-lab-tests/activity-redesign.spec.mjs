import { test, expect } from '@playwright/test';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { loadActivityCapture } from '../src/features/activity/activityCaptureModel.ts';
import { buildSessionTriage, defaultTriageThresholds } from '../src/features/activity/sessionTriageModel.ts';
const target = 'dfb2b72c-4ad4-4765-9523-528c798482f0', output = resolve('../.artifacts/activity-redesign-20261009/native');
test('bounded native blocking workload flows through collector, repository, API and prioritized dashboard', async ({ page, request }) => {
  await mkdir(output, { recursive: true }); const { runId } = JSON.parse(await readFile(resolve(output, 'inputs.json'), 'utf8'));
  const application = 'SQL Observer Activity qualification ' + runId;
  const status = await request.get('/__lab/status'); expect(await status.json()).toMatchObject({ upstream: 'https://WIN-QNGOV5GDM24:5443', tls: 'validated', syntheticData: false });
  const access = await request.get(`/api/v1/me?targetId=${target}`); expect((await access.json()).targetRoles).toContain('QueryTextReader');
  let capture, groups, blocked, blocker;
  const replay = process.env.SQLOBSERVER_ACTIVITY_REPLAY_CAPTURE === '1' ? JSON.parse(await readFile(resolve(output, 'dashboard-evidence.json'), 'utf8')) : undefined;
  if (replay) expect(replay.runId).toBe(runId);
  await expect.poll(async () => {
    capture = await loadActivityCapture(async parameters => { const response = await request.get(`/api/v1/observation-targets/${target}/activity/live?${parameters}`); expect(response.status()).toBe(200); return response.json(); }, new AbortController().signal, replay ? { id: replay.snapshotId, observedUtc: replay.observedUtc } : undefined);
    groups = buildSessionTriage(capture.rows, defaultTriageThresholds);
    blocked = capture.rows.find(row => row.application === application + ' blocked' && row.blocker > 0);
    blocker = blocked && capture.rows.find(row => row.application === application + ' blocker' && row.sessionId === blocked.blocker);
    return Boolean(blocked && blocker);
  }, { timeout: 35_000, intervals: [1000, 2000, 3000] }).toBe(true);
  expect(capture.partial).toBe(false); expect(capture.truncated).toBe(false); expect(groups[0].sessionId).toBe(blocker.sessionId);
  const queryReads = []; page.on('response', response => { if (new URL(response.url()).pathname.endsWith('/activity/live/query')) queryReads.push(response.status()); });
  await page.goto('/#/activity?' + new URLSearchParams({ target, range: '1h', sessionSnapshot: capture.snapshotId, sessionObserved: capture.observedUtc, sessionIdentity: blocker.identity }));
  await expect(page.getByRole('heading', { name: 'Activity', exact: true })).toBeVisible();
  await expect(page.locator('.activity-summary-card').first().locator('strong')).toHaveText(String(groups.length));
  const details = page.getByRole('region', { name: 'Session details', exact: true }); await expect(details).toContainText('Session ' + blocker.sessionId);
  await details.getByRole('button', { name: 'Blocking chain', exact: true }).click();
  await expect(details.getByRole('button', { name: `Inspect captured session ${blocked.sessionId}/${blocked.requestId}`, exact: true })).toBeVisible(); expect(queryReads).toEqual([]);
  await page.screenshot({ path: resolve(output, 'blocking-1600.png') });
  await details.getByRole('button', { name: `Inspect captured session ${blocked.sessionId}/${blocked.requestId}`, exact: true }).click();
  await details.getByRole('button', { name: 'Captured SQL', exact: true }).click(); expect(queryReads).toEqual([]);
  await details.getByRole('button', { name: 'Load captured SQL', exact: true }).click(); await expect(details.locator('pre.query-text')).toContainText('LockScenario'); expect(queryReads).toEqual([200]);
  await details.getByRole('button', { name: 'Blocking chain', exact: true }).click();
  for (const size of [{ width: 1440, height: 900 }, { width: 390, height: 844 }]) { await page.setViewportSize(size); await page.getByRole('button', { name: `Inspect ${blocked.sessionId}/${blocked.requestId}`, exact: true }).scrollIntoViewIfNeeded(); await page.screenshot({ path: resolve(output, `blocking-${size.width}.png`) }); }
  await writeFile(resolve(output, 'dashboard-evidence.json'), JSON.stringify({ runId, verifiedAtUtc: new Date().toISOString(), retainedCaptureReplay: Boolean(replay), target, syntheticData: false, tls: 'validated', snapshotId: capture.snapshotId, observedUtc: capture.observedUtc, pagesRead: capture.pagesRead, requestRows: capture.rows.length, distinctSessions: groups.length, blockedSession: blocked.sessionId, blockerSession: blocker.sessionId, priorityFirst: groups[0].sessionId, identityPinned: true, deliberateSqlRead: true, queryHttpStatus: queryReads[0], knownGuardedStatement: true, protectedTextStored: false }, null, 2));
});
