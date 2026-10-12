import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const origin = 'http://127.0.0.1:4328', contexts = new WeakMap();
const graph = page => page.getByRole('region', { name: 'Blocking chains', exact: true });
const answer = page => page.getByRole('region', { name: 'Current blocking evidence', exact: true });
const blockingReads = page => contexts.get(page).reads.filter(read => read.kind === 'blocking');
function payload(target, revision = 7) {
  const root = target === alpha ? 50 : 80, blocked = target === alpha ? 61 : 91;
  return { instanceId: target, repositoryTimeUtc: '2026-01-15T13:01:00Z',
    evidence: { runId: target === alpha ? '33333333-3333-4333-8333-000000000001' : '33333333-3333-4333-8333-000000000002',
      targetRevision: String(revision), collectorId: 'blocking.current', freshness: 'current', outcome: 'succeeded', reason: 'completed',
      isPartial: false, completedAtUtc: '2026-01-15T13:00:01Z' },
    items: [{ blockedSessionId: blocked, blockerKind: 'session', blockerSessionId: root, rootBlockerSessionId: root, waitType: 'LCK_M_X',
      waitingTaskCount: '1', waitDurationMilliseconds: '3000', chainDepth: 1, chainState: 'resolved', observedAtUtc: '2026-01-15T13:00:00.123456Z' }],
    maximumChainDepth: 32, maximumGraphNodes: 256 };
}
const json = (route, value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
const exactKeys = (params, keys) => [...params.keys()].length === keys.length && keys.every(key => params.getAll(key).length === 1);
function expectedWindow(params) {
  return ['2026-01-13', '2026-01-14'].some(day => params.get('fromUtc') === `${day}T12:00:00.123456Z`
    && params.get('toUtc') === `${day}T13:00:00.123456Z`);
}
test.beforeEach(async ({ page }) => {
  const context = { reads: [], unexpected: [], analytics: '503', blocking: undefined }; contexts.set(page, context);
  await page.clock.setFixedTime(new Date('2026-01-15T13:01:00Z'));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  page.on('pageerror', error => context.unexpected.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url()), params = url.searchParams;
    if (url.origin === origin && !url.pathname.startsWith('/api/')) return route.continue();
    if (url.origin !== origin || request.method() !== 'GET') { context.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); }
    if (url.pathname === '/api/v1/overview' && exactKeys(params, ['fromUtc', 'toUtc', 'useLatestStatus', 'targetId'])
      && params.get('useLatestStatus') === 'true' && [alpha, beta].includes(params.get('targetId')) && expectedWindow(params)) {
      context.reads.push({ kind: 'analytics', url: url.href });
      if (context.analytics === 'malformed') return json(route, { protected: 'DO_NOT_DISPLAY_INVALID_PROVIDER_PAYLOAD' });
      if (context.analytics === 'empty') return json(route, { targetId: params.get('targetId'), fromUtc: params.get('fromUtc'), toUtc: params.get('toUtc'),
        refreshedAtUtc: '2026-01-15T13:01:00Z', targets: [], evidence: [] });
      return json(route, { detail: 'DO_NOT_DISPLAY_ANALYTICS_PROVIDER_ERROR' }, 503);
    }
    const current = /^\/api\/v1\/observation-targets\/([^/]+)\/activity\/blocking\/current$/u.exec(url.pathname);
    if (current && [alpha, beta].includes(current[1]) && url.search === '?limit=256') {
      context.reads.push({ kind: 'blocking', target: current[1], url: url.href });
      return context.blocking ? context.blocking(route, current[1]) : json(route, payload(current[1]));
    }
    const history = /^\/api\/v1\/observation-targets\/([^/]+)\/(alerts|lifecycle)\/history$/u.exec(url.pathname);
    if (history && [alpha, beta].includes(history[1]) && exactKeys(params, ['targetRevision', 'fromUtc', 'toUtc', 'limit'])
      && ['7', '8'].includes(params.get('targetRevision')) && params.get('limit') === '100' && expectedWindow(params)) {
      context.reads.push({ kind: 'history', url: url.href }); return json(route, {}, 503);
    }
    context.unexpected.push(url.href); return route.abort();
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Fixed exact synthetic GET registry and no page errors').toEqual([]));
async function mount(page, mode = '503') {
  contexts.get(page).analytics = mode;
  await page.goto('/component-tests/independent-blocking.html');
  await page.getByText('Current blocking graph evidence', { exact: true }).click();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toBeVisible();
  await expect(graph(page)).toContainText('Session 61');
}
function hold() {
  let release, announce; const gate = new Promise(resolve => release = resolve), started = new Promise(resolve => announce = resolve);
  return { started, release, handler: async (route, target) => { announce(route.request()); await gate; await json(route, payload(target)).catch(() => {}); } };
}

for (const mode of ['503', 'malformed', 'empty']) test(`current blocking survives ${mode} analytics without fabricating historical charts`, async ({ page }) => {
  await mount(page, mode);
  await expect(answer(page)).toContainText('does not reconstruct the selected history window');
  await expect(answer(page)).toContainText('1 distinct blocked session');
  await expect(graph(page)).toContainText('independent of the selected timeline window');
  await expect(page.getByRole('heading', { name: 'SQL workload', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Blocked sessions', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('DO_NOT_DISPLAY');
  expect(blockingReads(page)).toHaveLength(1);
  const before = page.url();
  await answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(graph(page).getByRole('heading', { name: 'Blocking chains', exact: true })).toBeFocused();
  await expect(graph(page)).toContainText('Selected head-blocker evidence'); expect(page.url()).toBe(before);
  await page.getByRole('button', { name: 'Change history window', exact: true }).click();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toBeVisible();
  expect(blockingReads(page)).toHaveLength(1);
  expect(new URL(page.url()).hash).toContain('2026-01-13');
});

test('pending and failed current refresh retain disclosed evidence and explicit retry recovers independently', async ({ page }) => {
  await mount(page); const pending = hold(); contexts.get(page).blocking = pending.handler;
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click(); await pending.started;
  await expect(answer(page)).toContainText('Previously received snapshot'); await expect(graph(page)).toContainText('Refresh pending');
  pending.release(); await expect(answer(page)).toContainText('Received current snapshot');
  contexts.get(page).blocking = route => json(route, { detail: 'DO_NOT_DISPLAY_SOURCE_ERROR' }, 503);
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await expect(graph(page).getByRole('button', { name: 'Retry current blocking', exact: true })).toBeVisible();
  await expect(answer(page)).toContainText('Previously received snapshot'); await expect(graph(page)).toContainText('Session 61');
  await expect(page.locator('body')).not.toContainText('DO_NOT_DISPLAY');
  contexts.get(page).blocking = undefined; await graph(page).getByRole('button', { name: 'Retry current blocking', exact: true }).click();
  await expect(graph(page).getByRole('button', { name: 'Retry current blocking', exact: true })).toHaveCount(0);
  await expect(answer(page)).toContainText('Received current snapshot'); expect(blockingReads(page)).toHaveLength(4);
});

for (const status of [401, 403]) test(`typed ${status} purges current blocking and holds the source until explicit access retry`, async ({ page }) => {
  await mount(page); contexts.get(page).blocking = route => json(route, { detail: 'DO_NOT_DISPLAY_DENIED' }, status);
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await expect(graph(page).getByRole('button', { name: 'Retry current blocking access', exact: true })).toBeVisible();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toHaveCount(0);
  await expect(graph(page)).not.toContainText('Session 61'); await expect(graph(page)).toContainText('Protected blocking evidence was removed');
  await expect(page.getByLabel('Parent blocking denial notifications', { exact: true })).toHaveText(JSON.stringify([status]));
  contexts.get(page).blocking = undefined;
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await page.getByRole('button', { name: 'Change history window', exact: true }).click();
  await page.getByRole('button', { name: 'Deactivate dashboard', exact: true }).click();
  await expect(graph(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Activate dashboard', exact: true }).click();
  await expect(graph(page).getByRole('button', { name: 'Retry current blocking access', exact: true })).toBeVisible();
  expect(blockingReads(page)).toHaveLength(2);
  await expect(page.getByLabel('Parent blocking denial notifications', { exact: true })).toHaveText(JSON.stringify([status]));
  await graph(page).getByRole('button', { name: 'Retry current blocking access', exact: true }).click();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toBeVisible();
  expect(blockingReads(page)).toHaveLength(3);
});

test('an aborted previous-target forbidden response cannot notify or deny the current health parent', async ({ page }) => {
  await mount(page);
  let release, announce;
  const gate = new Promise(resolve => release = resolve), started = new Promise(resolve => announce = resolve);
  contexts.get(page).blocking = async route => {
    announce(route.request()); await gate;
    await json(route, { detail: 'DO_NOT_DISPLAY_LATE_DENIAL' }, 403).catch(() => {});
  };
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click(); const request = await started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  contexts.get(page).blocking = undefined;
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  await aborted; release();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 80', exact: true })).toBeVisible();
  await expect(page.getByLabel('Parent blocking denial notifications', { exact: true })).toHaveText('[]');
  await expect(graph(page).getByRole('button', { name: 'Retry current blocking access', exact: true })).toHaveCount(0);
});

test('a pending previous target is aborted and cannot publish into the current target', async ({ page }) => {
  await mount(page); const pending = hold(); contexts.get(page).blocking = pending.handler;
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click(); const request = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  contexts.get(page).blocking = undefined; await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  await aborted; pending.release();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 80', exact: true })).toBeVisible();
  await expect(graph(page)).toContainText('Session 91'); await expect(graph(page)).not.toContainText('Session 61');
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toHaveCount(0);
});

test('inactive generation aborts old current read and reactivation reads fresh evidence', async ({ page }) => {
  await mount(page); const pending = hold(); contexts.get(page).blocking = pending.handler;
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click(); const request = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('button', { name: 'Deactivate dashboard', exact: true }).click(); await aborted; pending.release();
  await expect(answer(page)).toHaveCount(0); await expect(graph(page)).toHaveCount(0);
  contexts.get(page).blocking = undefined; await page.getByRole('button', { name: 'Activate dashboard', exact: true }).click();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toBeVisible();
  expect(blockingReads(page)).toHaveLength(3);
});

test('caller revision fences unconfirmed graph roots and a fresh matching receipt restores inspection', async ({ page }) => {
  await mount(page); await page.getByRole('button', { name: 'Change revision', exact: true }).click();
  await expect(answer(page)).toContainText('No confirmed head blocker');
  await expect(answer(page)).toContainText('not an all-clear');
  contexts.get(page).blocking = (route, target) => json(route, payload(target, 8));
  await page.getByRole('button', { name: 'Refresh blocking', exact: true }).click();
  await expect(answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true })).toBeVisible();
});

test('an absent native collection receipt stays unknown while analytics is unavailable', async ({ page }) => {
  contexts.get(page).blocking = (route, target) => json(route, { ...payload(target), evidence: null, items: [] });
  await page.goto('/component-tests/independent-blocking.html');
  await page.getByText('Current blocking graph evidence', { exact: true }).click();
  await expect(answer(page)).toContainText('collection time unavailable; repository checked');
  await expect(answer(page)).toContainText('not an all-clear');
  await expect(graph(page)).toContainText('Collection time unavailable; repository checked');
  await expect(graph(page)).not.toContainText('No blocked sessions in this complete received collection');
  expect(blockingReads(page)).toHaveLength(1);
});

for (const width of [320, 390, 768, 1440]) test(`independent current blocking is accessible in dark mode at ${width}`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await mount(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  const button = answer(page).getByRole('button', { name: 'Inspect head blocker 50', exact: true });
  expect((await button.boundingBox()).height).toBeGreaterThanOrEqual(44);
  await button.focus(); await page.keyboard.press('Enter');
  await expect(graph(page).getByRole('heading', { name: 'Blocking chains', exact: true })).toBeFocused();
  const violations = (await new AxeBuilder({ page }).analyze()).violations;
  await info.attach('independent-blocking-axe', { body: JSON.stringify(violations), contentType: 'application/json' }); expect(violations).toEqual([]);
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-independent-blocking-2026-10-04/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true }); await page.screenshot({ path: `${directory}/blocking-${width}.png`, fullPage: true });
});
