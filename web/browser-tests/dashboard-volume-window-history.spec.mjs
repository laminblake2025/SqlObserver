import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000001';
const other = '11111111-1111-4111-8111-000000000002';
const key = 'a'.repeat(64), absentKey = 'e'.repeat(64);
const from = '2026-10-03T10:00:00.123456Z', to = '2026-10-03T11:00:00.123456Z';
const contexts = new WeakMap();
const history = page => page.getByRole('region', { name: 'SQL volume window history', exact: true });
const selected = page => history(page).getByRole('region', { name: 'Selected volume history', exact: true });
const href = (extras = {}, id = target) => '/#/resources?' + new URLSearchParams({ target: id, range: 'custom', from, to, compare: '1', databaseId: '5', ...extras });
const params = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const time = (windowFrom, minutes) => new Date(Date.parse(windowFrom) + minutes * 60000).toISOString();
function pageHeader(url) {
  const p = url.searchParams;
  return { instanceId: url.pathname.split('/')[4], targetRevision: Number(p.get('targetRevision')), fromUtc: p.get('fromUtc'), toUtc: p.get('toUtc'),
    cutoffUtc: '2026-10-03T12:00:00.123456Z', repositoryTimeUtc: '2026-10-03T12:00:00.123456Z', readEpoch: 'd'.repeat(64),
    state: 'available', reason: 'retained_window', coverage: { runCount: 3, completeRunCount: 3, gapRunCount: 0, retainedObservationCount: 3, retentionDays: 30, hasVisibilityGap: false }, nextCursor: null };
}
function nativePage(url) {
  const common = pageHeader(url), p = url.searchParams;
  const exact = /\/([a-f0-9]{64})\/history$/.exec(url.pathname);
  if (!exact) return { ...common, items: [{ sampleTargetRevision: 1, volumeKey: key, identityKind: 'volume_id', observationCount: 3,
    firstCompletedAtUtc: time(common.fromUtc, 5), lastCompletedAtUtc: time(common.fromUtc, 15), firstTotalBytes: '9223372036854775807', firstAvailableBytes: '4611686018427387903',
    lastTotalBytes: '9223372036854775807', lastAvailableBytes: '4611686018427387901', comparisonState: 'eligible', availableBytesDelta: '-2', totalBytesDelta: '0' }] };
  const missing = exact[1] === absentKey;
  return { ...common, sampleTargetRevision: Number(p.get('sampleTargetRevision')), volumeKey: exact[1], identityKind: missing ? null : 'volume_id',
    ...(missing ? { state: 'unavailable', reason: 'retained_evidence_unavailable', coverage: { ...common.coverage, completeRunCount: 0, gapRunCount: 3, retainedObservationCount: 0, hasVisibilityGap: true } } : {}),
    items: [5, 10, 15].map((minute, index) => ({ runId: `55555555-5555-4555-8555-${(Math.floor(Date.parse(common.fromUtc) / 60000) * 10 + index).toString(16).padStart(12, '0')}`, completedAtUtc: time(common.fromUtc, minute),
      observedAtUtc: missing ? null : time(common.fromUtc, minute), collectedAtUtc: missing ? null : time(common.fromUtc, minute), outcome: 'succeeded', lossKind: 'none', minimumLostItems: 0,
      state: missing ? 'unavailable' : 'available', reason: missing ? 'volume_absent' : 'observed_capacity', mappedFileCount: missing ? null : 2,
      totalBytes: missing ? null : '9223372036854775807', availableBytes: missing ? null : String(4611686018427387903n - BigInt(index)) })) };
}
async function json(route, body, status = 200) { await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) }); }
test.beforeEach(async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-03T12:00:00Z') });
  await page.context().addCookies(['dashboardRefactor', 'resourceInspectionFixture', 'sqlVolumeFixture'].map(name => ({ name, value: '1', url: origin })).concat([{ name: 'chartReferenceRole', value: 'viewer', url: origin }]));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  const context = { reads: [], errors: [], handler: undefined }; contexts.set(page, context);
  page.on('pageerror', error => context.errors.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) { context.errors.push('External traffic ' + url.origin); return route.abort(); }
    if (!/\/resources\/volumes\/(?:history|[a-f0-9]{64}\/history)$/.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') { context.errors.push('Unexpected mutation'); return route.abort(); }
    context.reads.push(url);
    if (context.handler && await context.handler(route, url)) return;
    return json(route, nativePage(url));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).errors).toEqual([]));
async function openSelected(page) {
  await page.goto(href({ volumeRevision: '1', volumeKey: key }));
  await expect(selected(page)).toContainText('4611686018427387903');
}

for (const width of [320, 390, 768, 1440]) test(`Rewind volume identity, comparison and completion-time evidence are accessible at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await openSelected(page);
  await expect(page.getByRole('region', { name: 'Previous volume history', exact: true })).toContainText('4611686018427387903');
  await expect(history(page)).toContainText(/completion/i);
  await expect(history(page)).toContainText(/incarnation/i);
  expect(params(page)).toMatchObject({ target, range: 'custom', from, to, compare: '1', volumeRevision: '1', volumeKey: key });
  const inspect = history(page).getByRole('button', { name: `Inspect volume revision 1 key ${key}`, exact: true });
  await inspect.focus(); await page.keyboard.press('Enter'); await expect(selected(page)).toContainText('4611686018427387903');
  await page.reload(); await expect(selected(page)).toContainText('4611686018427387903');
  const clear = history(page).getByRole('button', { name: 'Clear volume selection', exact: true }); await clear.focus(); await expect(clear).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a','wcag2aa','wcag21aa','best-practice']).analyze()).violations;
  await info.attach(`volume-history-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const folder = resolve('../artifacts/dashboard-volume-window-history/screenshots'); await mkdir(folder, { recursive: true });
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); document.querySelectorAll('.table-scroll').forEach(el => { el.scrollLeft = 0; }); scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  await page.screenshot({ path: resolve(folder, `workspace-volume-history-${width}.png`), fullPage: true });
});

test('an exact older revision and absent volume survive reload and shared window changes without selecting another identity', async ({ page }) => {
  await page.route('**/api/v1/observation-targets?*', async route => {
    const response = await route.fetch(), data = await response.json();
    await route.fulfill({ response, json: { ...data, items: data.items.map(item => item.instanceId === target ? { ...item, configurationRevision: 3 } : item) } });
  });
  await page.route(`**/api/v1/observation-targets/${target}/resources/volumes?*`, async route => {
    const response = await route.fetch(), data = await response.json(); await route.fulfill({ response, json: { ...data, targetRevision: 3 } });
  });
  await page.goto(href({ volumeRevision: '2', volumeKey: absentKey }));
  await expect(selected(page)).toContainText(/absent|unavailable/i);
  expect(contexts.get(page).reads.filter(url => /\/[^/]{64}\/history$/.test(url.pathname)).every(url => url.pathname.includes(absentKey) && url.searchParams.get('sampleTargetRevision') === '2')).toBe(true);
  await page.reload(); await expect(selected(page)).toContainText(/absent|unavailable/i);
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('1h');
  await expect.poll(() => params(page)).toMatchObject({ range: '1h', volumeRevision: '2', volumeKey: absentKey });
  await expect(selected(page)).toContainText(/absent|unavailable/i);
  await history(page).getByRole('button', { name: 'Clear volume selection', exact: true }).click();
  await expect.poll(() => params(page).volumeKey).toBeUndefined(); await expect(selected(page)).toHaveCount(0);
});

test('invalid repeated volume identity refuses selected history and never silently selects the catalog first row', async ({ page }) => {
  await page.goto(href({ volumeRevision: '1', volumeKey: key }) + '&volumeRevision=1');
  await expect(page.getByRole('alert').filter({ hasText: /requested volume identity or sample revision/i })).toBeVisible();
  await expect(selected(page)).toHaveCount(0);
  expect(contexts.get(page).reads.some(url => /\/[^/]{64}\/history$/.test(url.pathname))).toBe(false);
});

test('a denied exact history clears received evidence and explicit retry restores the same read identity', async ({ page }) => {
  await openSelected(page); const context = contexts.get(page); let denied = true;
  context.handler = async (route, url) => { if (!url.pathname.includes(key) || !denied) return false; await json(route, { detail: 'SECRET_DENIAL' }, 403); return true; };
  await history(page).getByRole('button', { name: 'Refresh selected volume history', exact: true }).click();
  await expect(history(page)).not.toContainText('4611686018427387903'); await expect(history(page)).not.toContainText('SECRET_DENIAL');
  await expect(history(page).getByRole('button', { name: 'Retry volume history access', exact: true })).toBeVisible();
  denied = false; await history(page).getByRole('button', { name: 'Retry volume history access', exact: true }).click();
  await expect(selected(page)).toContainText('4611686018427387903'); expect(params(page).volumeKey).toBe(key);
});

test('server navigation fences a held old volume response and preserves only the new target selection', async ({ page }) => {
  await openSelected(page); const context = contexts.get(page); let started, release, finished;
  const start = new Promise(resolve => { started = resolve; }), gate = new Promise(resolve => { release = resolve; }), end = new Promise(resolve => { finished = resolve; });
  context.handler = async (route, url) => {
    if (!url.pathname.includes(target) || !url.pathname.includes(key)) return false;
    started(); await gate;
    try { await json(route, nativePage(url)); } catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; } finally { finished(); }
    return true;
  };
  await history(page).getByRole('button', { name: 'Refresh selected volume history', exact: true }).click(); await start;
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(other);
  await expect.poll(() => params(page).target).toBe(other); await expect(selected(page)).toHaveCount(0);
  release(); await end; await expect(selected(page)).toHaveCount(0); expect(params(page).volumeKey).toBeUndefined();
});

test('independent retained capacity remains available when core analytics fails and accepts the shared keyboard inspection', async ({ page }) => {
  await page.route('**/api/v1/overview?*', route => json(route, { detail: 'SECRET_CORE_ANALYTICS' }, 503));
  await openSelected(page);
  await expect(history(page)).not.toContainText('SECRET_CORE_ANALYTICS');
  await page.getByLabel('Resource inspection time (UTC)', { exact: true }).fill('2026-10-03T10:10:00.123');
  await page.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect.poll(() => params(page).resourceAt).toBe('2026-10-03T10:10:00.123Z');
  await expect(history(page).locator('.chart-crosshair')).toHaveCount(1);
  expect(params(page)).toMatchObject({ from, to, volumeRevision: '1', volumeKey: key });
});
