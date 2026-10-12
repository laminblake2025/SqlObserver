import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { backupFixture } from '../tools/backup-assessment-fixture.mjs';
const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001';
const runId = '22222222-2222-4222-8222-222222222222';
const hash = value => value.toString(16).padStart(64, '0');
const states = new WeakMap();
const definitions = {
  availability: { name: 'AG replicas', role: 'article', next: 'Next ag replicas page', latest: 'Return ag replicas to latest', previous: 'Previous ag replicas page', retry: 'Retry ag replicas' },
  backups: { name: 'Backup assessment snapshot', role: 'article', next: 'Next backup page', latest: 'Return backup assessment to latest', previous: 'Previous backup page', retry: 'Retry backup assessment' },
  agent: { name: 'SQL Agent failures', role: 'region', next: 'Next history page', latest: 'Return SQL Agent history to latest', previous: 'Previous history page', retry: 'Retry SQL Agent history' },
};
const evidence = (page, surface) => page.getByRole(definitions[surface].role, { name: definitions[surface].name, exact: true });
const button = (page, surface, action) => evidence(page, surface).getByRole('button', { name: definitions[surface][action], exact: true });
const poll = page => page.getByRole('button', { name: 'Poll workspace', exact: true });
function availability(item) {
  const offset = item.cursor ? 50 : 0;
  const items = Array.from({ length: item.cursor ? 1 : 50 }, (_, n) => {
    const index = n + offset;
    const identity = { groupFingerprint: hash(1), replicaFingerprint: hash(10 + index), observerReplicaFingerprint: hash(10),
      replicaIsLocal: index === 0, visibilityScope: 'PrimaryAllKnown', stateObserved: true };
    return item.kind === 'replicas' ? { ...identity, role: index ? 'SECONDARY' : 'PRIMARY', operationalState: 'ONLINE', connectedState: 'CONNECTED' }
      : { ...identity, databaseFingerprint: hash(100), synchronizationState: 'SYNCHRONIZED', synchronizationHealth: 'HEALTHY', databaseState: 'ONLINE',
        isSuspended: false, logSendQueueKb: 0, logSendRateKbPerSecond: 0, redoQueueKb: 0, redoRateKbPerSecond: null, sourceLastCommitLocal: '2026-09-30T01:30:00.123456' };
  });
  return { schemaVersion: 2, targetId: item.target, atUtc: item.atUtc, targetRevision: states.get(item.page).revision, runId,
    sourceObservedAtUtc: new Date(Date.parse(item.atUtc) - 60000).toISOString(), completedAtUtc: new Date(Date.parse(item.atUtc) - 59000).toISOString(),
    state: 'Complete', evidenceStatus: 'retained', sourceRows: 102, outputItems: 102, retainedItems: 102,
    lossKind: 'none', truncated: false, collectorEnabled: true, items, hasMore: !item.cursor, nextCursor: item.cursor ? null : `${item.kind}-page-2` };
}
function native(item) {
  if (item.kind === 'backups') {
    const page = backupFixture(item.target, item.atUtc, 'mixed', item.cursor ? 50 : 0);
    // The older synthetic fixture truncates fractional cutoffs with Date.parse.
    // Native age arithmetic uses PostgreSQL microseconds; preserve that contract.
    const micros = value => {
      const parts = /^(.*T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6}))?Z$/.exec(value);
      return BigInt(Date.parse(`${parts[1]}Z`)) * 1000n + BigInt((parts[2] ?? '').padEnd(6, '0'));
    };
    for (const database of page.items) for (const check of [database.full, database.log])
      if (check.observedAgeSeconds !== null && check.selectedBackup?.finishUtc)
        check.observedAgeSeconds = Number(micros(item.atUtc) - micros(check.selectedBackup.finishUtc)) / 1_000_000;
    return page;
  }
  if (item.kind !== 'agent') return availability(item);
  const from = Date.parse(item.fromUtc), duration = Date.parse(item.toUtc) - from;
  const items = Array.from({ length: item.cursor ? 1 : 50 }, (_, n) => ({
    jobId: runId, historyInstanceId: 1000 + n + (item.cursor ? 50 : 0), stepId: 0, runStatus: 0,
    isJobOutcome: true, countsAsJobFailure: true, failureKind: 'Failed', retryAttempt: 0, durationSeconds: 42,
    firstObservedAtUtc: new Date(from + Math.floor(duration / 2)).toISOString(), sourceLocalStart: '2026-09-29T21:20:00',
    failureFingerprint: hash(1), contentAvailable: false,
  }));
  return { targetId: item.target, targetRevision: states.get(item.page).revision, runId, observedAtUtc: item.toUtc, state: 'Complete', evidence: null,
    coverageFromUtc: item.fromUtc, coverageToUtc: item.toUtc, items, hasMore: !item.cursor, nextCursor: item.cursor ? null : 'agent-page-2', truncated: false };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function hold(status = 200, transform = value => value) {
  let announce, release, finish;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const ended = new Promise(resolve => { finish = resolve; });
  return { started, async release() { release(); await ended; }, async handle(route, item) {
    announce(item); await gate;
    try { await json(route, status === 200 ? transform(native(item)) : { detail: 'SECRET_PROVIDER_RESPONSE' }, status); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { finish(); }
    return true;
  } };
}

test.beforeEach(async ({ page }) => {
  states.set(page, { reads: [], unexpected: [], revision: 1, handler: undefined });
  page.on('pageerror', error => states.get(page).unexpected.push(error.message));
  await page.clock.install({ time: new Date('2026-10-02T14:00:00Z') });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url()), state = states.get(page);
    if (url.origin !== origin) { state.unexpected.push(url.href); return route.abort(); }
    if (!url.pathname.startsWith('/api/')) return route.continue();
    const match = /^\/api\/(v1|v2)\/observation-targets\/([^/]+)\/(availability-groups\/(replicas|databases)|health\/backups|sql-agent\/failures)$/.exec(url.pathname);
    if (!match || route.request().method() !== 'GET' || url.searchParams.get('limit') !== '50') { state.unexpected.push(url.href); return route.abort(); }
    const item = { page, target: match[2], kind: match[4] ?? (match[3] === 'health/backups' ? 'backups' : 'agent'),
      atUtc: url.searchParams.get('atUtc'), fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'), cursor: url.searchParams.get('cursor') };
    state.reads.push(item);
    if (state.handler && await state.handler(route, item)) return;
    await json(route, native(item));
  });
});
test.afterEach(async ({ page }) => { expect(states.get(page).unexpected).toEqual([]); });
async function mount(page, surface, live = false) {
  await page.goto(`${origin}/component-tests/retained-operations.html`);
  await page.getByRole('combobox', { name: 'Operations screen', exact: true }).selectOption(surface);
  if (live) await page.getByRole('combobox', { name: 'Time mode', exact: true }).selectOption('1h');
  if (surface !== 'dashboard') await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50);
  else await expect(page.getByRole('region', { name: 'Dashboard Agent first-page evidence', exact: true })).toContainText('Rows: 50');
}
const reads = (page, surface) => states.get(page).reads.filter(item => item.kind === (surface === 'availability' ? 'replicas' : surface));

for (const surface of Object.keys(definitions)) for (const live of [false, true]) test(`${surface} keeps investigated page and cursor on ${live ? 'Live' : 'Rewind'} polling until explicit latest`, async ({ page }) => {
  await mount(page, surface, live); const first = reads(page, surface).at(-1);
  await button(page, surface, 'next').click(); await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(1);
  await expect(evidence(page, surface)).toContainText('Page 2'); const count = reads(page, surface).length;
  await page.clock.fastForward(60000); await poll(page).click();
  await expect(evidence(page, surface)).toContainText('Retained investigation page');
  await expect(evidence(page, surface)).toContainText('Page 2'); expect(reads(page, surface)).toHaveLength(count);
  await button(page, surface, 'previous').click(); await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50);
  const previous = reads(page, surface).at(-1); expect(previous.atUtc ?? previous.toUtc).toBe(first.atUtc ?? first.toUtc);
  await button(page, surface, 'latest').focus(); await page.keyboard.press('Enter');
  await expect(evidence(page, surface).getByText(/Retained investigation page/)).toHaveCount(0);
  const latest = reads(page, surface).at(-1); expect(latest.cursor).toBeNull();
  if (live) expect(latest.atUtc ?? latest.toUtc).not.toBe(first.atUtc ?? first.toUtc);
});

for (const surface of Object.keys(definitions)) test(`${surface} keeps the received page and retries the same cursor after navigation and latest failures`, async ({ page }) => {
  await mount(page, surface); let fail = true;
  states.get(page).handler = async (route, item) => { if (item.kind !== (surface === 'availability' ? 'replicas' : surface) || !fail) return false; await json(route, {}, 503); return true; };
  await button(page, surface, 'next').click(); await expect(evidence(page, surface).getByRole('alert')).toBeVisible();
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50); await expect(evidence(page, surface)).toContainText('Page 1');
  const failedCursor = reads(page, surface).at(-1).cursor; expect(failedCursor).not.toBeNull(); fail = false;
  await button(page, surface, 'retry').click(); await expect(evidence(page, surface)).toContainText('Page 2');
  expect(reads(page, surface).at(-1).cursor).toBe(failedCursor);
  fail = true; await button(page, surface, 'latest').click(); await expect(evidence(page, surface).getByRole('alert')).toBeVisible();
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(1); await expect(evidence(page, surface)).toContainText('Page 2');
  await poll(page).click(); await expect(evidence(page, surface)).toContainText('Page 2'); fail = false;
  await button(page, surface, 'retry').click(); await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50);
  await expect(evidence(page, surface)).toContainText('Page 1'); expect(reads(page, surface).at(-1).cursor).toBeNull();
});

for (const surface of Object.keys(definitions)) test(`${surface} rejects a changed continuation run without replacing investigated rows`, async ({ page }) => {
  await mount(page, surface);
  states.get(page).handler = async (route, item) => {
    if (!item.cursor || item.kind !== (surface === 'availability' ? 'replicas' : surface)) return false;
    await json(route, { ...native(item), runId: '33333333-3333-4333-8333-000000000001' }); return true;
  };
  await button(page, surface, 'next').click(); await expect(evidence(page, surface).getByRole('alert')).toContainText(/snapshot changed/);
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50); await expect(evidence(page, surface)).toContainText('Page 1');
});

for (const surface of Object.keys(definitions)) test(`${surface} fences held continuation on target change and hides old rows in the first commit`, async ({ page }) => {
  await mount(page, surface); const pending = hold();
  states.get(page).handler = (route, item) => item.cursor ? pending.handle(route, item) : false;
  await button(page, surface, 'next').click(); await pending.started;
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption('11111111-1111-4111-8111-000000000002');
  const commit = JSON.parse(await page.getByRole('status', { name: 'First committed operations context', exact: true }).textContent());
  expect(commit.rows).toBe(0); expect(commit.alerts).toBe(0);
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50); await pending.release();
  await expect(evidence(page, surface)).toContainText('Page 1'); await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50);
});

for (const surface of Object.keys(definitions)) test(`${surface} drops received rows after permission denial`, async ({ page }) => {
  await mount(page, surface);
  states.get(page).handler = async (route, item) => { if (!item.cursor) return false; await json(route, { detail: 'SECRET_PROVIDER_RESPONSE' }, 403); return true; };
  await button(page, surface, 'next').click();
  if (surface === 'availability') {
    const family = page.getByRole('region', { name: 'Per-replica availability evidence', exact: true });
    await expect(family.getByRole('alert')).toBeVisible(); await expect(family.getByRole('table')).toHaveCount(0);
    await expect(family.getByRole('button', { name: 'Retry AG evidence access', exact: true })).toBeVisible();
  } else await expect(evidence(page, surface).getByRole('alert')).toBeVisible();
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(0); await expect(page.getByText('SECRET_PROVIDER_RESPONSE')).toHaveCount(0);
});

for (const surface of Object.keys(definitions)) test(`${surface} owns one held navigation request across duplicate clicks and a workspace tick`, async ({ page }) => {
  await mount(page, surface); const pending = hold(); const before = reads(page, surface).length;
  states.get(page).handler = (route, item) => item.cursor ? pending.handle(route, item) : false;
  await button(page, surface, 'next').evaluate(element => { element.click(); element.click(); }); await pending.started;
  // A workspace tick must not cross the shared 30-second request timeout.
  await poll(page).click();
  await expect(evidence(page, surface)).toHaveAttribute('aria-busy', 'true'); expect(reads(page, surface)).toHaveLength(before + 1);
  await pending.release(); await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(1);
  await expect(evidence(page, surface)).toContainText('Page 2');
});

for (const surface of Object.keys(definitions)) test(`${surface} resets investigated position on an explicit precise window change`, async ({ page }) => {
  await mount(page, surface); await button(page, surface, 'next').click(); await expect(evidence(page, surface)).toContainText('Page 2');
  await page.getByRole('combobox', { name: 'Selected window', exact: true }).selectOption('second');
  const commit = JSON.parse(await page.getByRole('status', { name: 'First committed operations context', exact: true }).textContent());
  expect(commit.rows).toBe(0); expect(commit.alerts).toBe(0);
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50); await expect(evidence(page, surface)).toContainText('Page 1');
  const requested = reads(page, surface).at(-1); expect(requested.atUtc ?? requested.toUtc).toBe('2026-09-30T13:00:00.000002Z');
  expect(requested.cursor).toBeNull();
});

for (const surface of Object.keys(definitions)) test(`${surface} aborts held reads on unmount and starts a clean first page on remount`, async ({ page }) => {
  await mount(page, surface); const pending = hold();
  states.get(page).handler = (route, item) => item.cursor ? pending.handle(route, item) : false;
  await button(page, surface, 'next').click(); await pending.started;
  await page.getByRole('button', { name: 'Unmount operations', exact: true }).click();
  await expect(evidence(page, surface)).toHaveCount(0); await pending.release();
  await page.getByRole('button', { name: 'Mount operations', exact: true }).click();
  await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50); await expect(evidence(page, surface)).toContainText('Page 1');
});

test('Agent history invalidates its retained page immediately on a target revision change', async ({ page }) => {
  await mount(page, 'agent'); await button(page, 'agent', 'next').click(); await expect(evidence(page, 'agent')).toContainText('Page 2');
  states.get(page).revision = 2; await page.getByRole('button', { name: 'Change target revision', exact: true }).click();
  const commit = JSON.parse(await page.getByRole('status', { name: 'First committed operations context', exact: true }).textContent());
  expect(commit.rows).toBe(0); await expect(evidence(page, 'agent').locator('tbody tr')).toHaveCount(50);
  await expect(evidence(page, 'agent')).toContainText('Page 1'); expect(reads(page, 'agent').at(-1).cursor).toBeNull();
});

test('independent AG streams keep their own cutoff and disclose a mismatched live publication', async ({ page }) => {
  await mount(page, 'availability', true); await button(page, 'availability', 'next').click();
  await expect(evidence(page, 'availability')).toContainText('Page 2'); await page.clock.fastForward(60000); await poll(page).click();
  await expect(page.getByRole('region', { name: 'Per-replica availability evidence', exact: true }).getByRole('alert')).toContainText('different cutoffs, runs or target revisions');
  await expect(evidence(page, 'availability')).toContainText('Page 2');
  await page.getByRole('button', { name: 'Return both AG streams to latest', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Per-replica availability evidence', exact: true }).getByRole('alert')).toHaveCount(0);
  await expect(evidence(page, 'availability')).toContainText('Page 1');
});

test('dashboard Agent first-page evidence continues reading each workspace poll', async ({ page }) => {
  await mount(page, 'dashboard', true); const before = reads(page, 'agent').at(-1).toUtc, count = reads(page, 'agent').length;
  await page.clock.fastForward(60000); await poll(page).click();
  await expect.poll(() => reads(page, 'agent').length).toBe(count + 1);
  await expect(page.getByRole('region', { name: 'Dashboard Agent first-page evidence', exact: true })).toContainText(reads(page, 'agent').at(-1).toUtc);
  expect(reads(page, 'agent').at(-1).toUtc).not.toBe(before);
});

for (const width of [320, 390, 768, 1440]) test(`operations pages remain readable, keyboard operable and accessible in dark theme at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await page.goto(`${origin}/component-tests/retained-operations.html`);
  for (const surface of Object.keys(definitions)) {
    await page.getByRole('combobox', { name: 'Operations screen', exact: true }).selectOption(surface);
    await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(50);
    await button(page, surface, 'next').focus(); await page.keyboard.press('Enter');
    await expect(evidence(page, surface).locator('tbody tr')).toHaveCount(1);
    if (surface === 'availability') {
      const databaseStream = page.getByRole('article', { name: 'AG database replicas', exact: true });
      await databaseStream.getByRole('button', { name: 'Next ag database replicas page', exact: true }).click();
      await expect(databaseStream.locator('tbody tr')).toHaveCount(1);
    }
    const table = evidence(page, surface).locator('.table-scroll').first(); await expect(table).toHaveAttribute('tabindex', '0');
    await page.keyboard.press('Tab'); await table.focus(); await expect(table).toBeFocused();
    const focus = await table.evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
    expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
    if (await table.evaluate(element => element.scrollWidth > element.clientWidth)) {
      await page.keyboard.press('ArrowRight'); await expect.poll(() => table.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`operations-${surface}-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
    const directory = fileURLToPath(new URL('../../artifacts/dashboard-retained-pagers/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
    await page.locator('.table-scroll').evaluateAll(elements => elements.forEach(element => { element.scrollLeft = 0; element.blur(); }));
    await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' })); await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
    await page.screenshot({ path: `${directory}/operations-pages-${surface}-${width}.png`, fullPage: true });
  }
});


test('Dashboard Agent drops unavailable markers after a failed refresh and recovers on the next poll', async ({ page }) => {
  await mount(page, 'dashboard', true);
  const dashboard = page.getByRole('region', { name: 'Dashboard Agent first-page evidence', exact: true });
  states.get(page).handler = async (route, item) => {
    if (item.kind !== 'agent') return false;
    await json(route, { detail: 'SECRET_PROVIDER_RESPONSE' }, 503); return true;
  };
  await poll(page).click();
  await expect(dashboard.getByRole('alert')).toBeVisible();
  await expect(dashboard).not.toContainText('Rows:');
  await expect(page.getByText('SECRET_PROVIDER_RESPONSE')).toHaveCount(0);
  states.get(page).handler = undefined;
  await poll(page).click();
  await expect(dashboard).toContainText('Rows: 50');
  await expect(dashboard.getByRole('alert')).toHaveCount(0);
});
