import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const run = index => `33333333-3333-4333-8333-${String(index).padStart(12, '0')}`;
const contexts = new WeakMap();
const db = page => page.getByRole('region', { name: 'Database health', exact: true });
const config = page => page.getByRole('region', { name: 'Server configuration changes', exact: true });
const dbNext = page => db(page).getByRole('button', { name: 'Next database page', exact: true });
const configNext = page => config(page).getByRole('button', { name: 'Next 100', exact: true });
const dbLatest = page => db(page).getByRole('button', { name: 'Return to latest database snapshot', exact: true });
const configLatest = page => config(page).getByRole('button', { name: 'Return to latest configuration history', exact: true });
const refresh = page => page.getByRole('button', { name: 'Refresh workspace', exact: true });
const commit = async page => JSON.parse(await page.getByRole('status', { name: 'First committed health context', exact: true }).textContent());
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function collector(id = 'engine.databases') {
  return { collectorId: id, manifestVersion: 1, outputSchemaVersion: 1, state: 'current', reason: 'none',
    lastExecutionOutcome: 'succeeded', circuitState: 'closed', durationMilliseconds: 10, retryCount: 0,
    sourceRows: 5, outputRows: 5, insertedRows: 5, duplicateRows: 0, rejectedRows: 0, responseBytes: 100,
    persistedBytes: 100, sampleLossKind: null, minimumLostItems: null, lossCountIsExact: null,
    minimumLostBytes: null, hasVisibilityGap: false, scheduledAtUtc: '2026-01-15T13:00:00Z',
    lastAttemptAtUtc: '2026-01-15T13:00:00Z', lastSuccessAtUtc: '2026-01-15T13:00:00Z', nextDueAtUtc: '2026-01-15T13:01:00Z' };
}
function dbCursor(item, last) {
  return Buffer.from(['d', item.target.replaceAll('-', ''), run(item.generation).replaceAll('-', ''), item.revision, last].join('\n')).toString('base64url');
}
function packet(item, longNames = false) {
  const name = item.target === alpha ? 'Alpha' : 'Beta';
  if (item.kind === 'databases') {
    const ids = item.index === 0 ? [5, 6] : item.index === 1 ? [7, 8] : [9];
    return { instanceId: item.target, repositoryTimeUtc: `2026-01-15T13:${String(item.generation).padStart(2, '0')}:00Z`, collector: collector(),
      items: ids.map(databaseId => ({ databaseId, name: longNames ? `SYNTHETIC_${name}_g${item.generation}_${'D'.repeat(140)}_${databaseId}`
        : `SYNTHETIC ${name} g${item.generation} database ${databaseId}`, state: 'online', recoveryModel: 'full', userAccess: 'multi_user',
        isReadOnly: false, compatibilityLevel: 160, logReuseWaitDescription: 'nothing', observedAtUtc: '2026-01-15T13:00:00.123456Z', collector: collector() })),
      nextCursor: item.index < 2 ? dbCursor(item, ids.at(-1)) : null };
  }
  if (item.kind === 'files') return { instanceId: item.target, repositoryTimeUtc: '2026-01-15T13:00:00Z', collector: collector('engine.files'),
    nextCursor: 'bounded-files-have-more', items: [{ databaseId: 5, fileId: 1, logicalName: `SYNTHETIC ${name} logical file`, fileType: 'rows', state: 'online',
      sizeBytes: '1000', maximumSizeBytes: null, growthBytes: '100', growthPercent: 0, readCount: '20', writeCount: '10', bytesRead: '200',
      bytesWritten: '100', ioStallMilliseconds: '30', readStallMilliseconds: '20', writeStallMilliseconds: '10', observedAtUtc: '2026-01-15T13:00:00Z', collector: collector('engine.files') }] };
  const detected = new Date(Date.parse(item.fromUtc) + 600_000).toISOString();
  return { targetId: item.target, fromUtc: item.fromUtc, toUtc: item.toUtc, targetRevision: item.revision, publicationGeneration: item.generation,
    coverage: 'observed_changes_only', nextCursor: item.index === 0 ? `SYNTHETIC-config-${item.generation}-page-2` : null,
    items: [{ configurationId: item.index + 1, configurationName: longNames ? `SYNTHETIC_${name}_g${item.generation}_${'C'.repeat(140)}`
      : `SYNTHETIC ${name} g${item.generation} configuration ${item.index + 1}`, sourceType: 'int', configuredValue: '8', inUseValue: '8',
      previousConfiguredValue: '4', previousInUseValue: '4', configuredChanged: true, inUseChanged: true,
      previousObservedAtUtc: new Date(Date.parse(item.fromUtc) - 60_000).toISOString(), observedAtUtc: detected, detectedAtUtc: detected, runId: run(item.index + 1) }] };
}
function hold(status = 200, transform = value => value) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    const body = status === 200 ? transform(packet(item, context.longNames)) : { detail: 'SECRET_LATE_PROVIDER' };
    announce(route.request()); await gate;
    try { await json(route, body, status); }
    catch (failure) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure; }
    finally { complete(); }
    return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { generation: 1, revision: 1, reads: [], unexpected: [], handler: undefined, longNames: false };
  contexts.set(page, context); page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/health\/(databases|files|server-configuration-changes)$/u.exec(url.pathname);
    if (!match || ![alpha, beta].includes(match[1]) || request.method() !== 'GET') return reject();
    const kind = match[2] === 'server-configuration-changes' ? 'configuration' : match[2];
    const cursor = url.searchParams.get('cursor');
    const keys = [...url.searchParams.keys()].sort().join('|');
    if (keys !== (kind === 'configuration' ? cursor ? 'cursor|fromUtc|limit|toUtc' : 'fromUtc|limit|toUtc' : cursor ? 'cursor|limit' : 'limit') ||
      url.searchParams.get('limit') !== (kind === 'configuration' ? '100' : '25') || kind === 'files' && cursor) return reject();
    const item = { kind, target: match[1], index: 0, cursor, generation: context.generation, revision: context.revision,
      fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc') };
    if (kind === 'configuration') {
      if (!Number.isFinite(Date.parse(item.fromUtc)) || Date.parse(item.toUtc) <= Date.parse(item.fromUtc)) return reject();
      if (cursor) {
        const decoded = /^SYNTHETIC-config-(\d+)-page-2$/u.exec(cursor); if (!decoded) return reject();
        item.generation = Number(decoded[1]); item.index = 1;
      }
    } else if (cursor) {
      const parts = Buffer.from(cursor, 'base64url').toString('utf8').split('\n');
      if (parts.length !== 5 || parts[0] !== 'd' || parts[1] !== item.target.replaceAll('-', '') || !['6', '8'].includes(parts[4])) return reject();
      item.generation = Number(parts[2].slice(-12)); item.revision = Number(parts[3]); item.index = parts[4] === '6' ? 1 : 2;
    }
    context.reads.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    await json(route, packet(item, context.longNames));
  });
});
test.afterEach(async ({ page }) => { expect(contexts.get(page).unexpected, 'Only registered local native API shapes; no external traffic or page errors').toEqual([]); });
async function mount(page) {
  await page.goto('/component-tests/retained-health-pages.html');
  await expect(db(page)).toContainText('Page 1'); await expect(config(page)).toContainText('configuration 1');
}
async function pinBoth(page) {
  await dbNext(page).click(); await configNext(page).click();
  await expect(db(page)).toContainText('Page 2'); await expect(config(page)).toContainText('Page 2');
}

test('workspace polls retain investigated database and configuration pages with their original cursor trail', async ({ page }) => {
  await mount(page); await pinBoth(page); const context = contexts.get(page); context.generation = 2;
  await refresh(page).click(); await expect.poll(() => context.reads.filter(item => item.kind === 'files').length).toBe(2);
  await expect(db(page)).toContainText('Alpha g1 database 7'); await expect(config(page)).toContainText('Alpha g1 configuration 2');
  expect(context.reads.filter(item => item.kind === 'configuration')).toHaveLength(2);
  await dbNext(page).click(); await expect(db(page)).toContainText('Alpha g1 database 9');
  expect(context.reads.filter(item => item.kind === 'databases').at(-1).generation).toBe(1);
  await expect(db(page)).toContainText('Retained database snapshot'); await expect(config(page)).toContainText('Retained configuration history');
});
test('previous and first pages restore validated receipts without substituting a polled first page', async ({ page }) => {
  await mount(page); await pinBoth(page); const context = contexts.get(page); context.generation = 2;
  await refresh(page).click(); await expect.poll(() => context.reads.filter(item => item.kind === 'files').length).toBe(2);
  const count = context.reads.length;
  await db(page).getByRole('button', { name: 'First database page', exact: true }).click();
  await expect(db(page)).toContainText('Alpha g1 database 5'); await dbNext(page).click();
  await config(page).getByRole('button', { name: 'Previous 100', exact: true }).click(); await configNext(page).click();
  expect(context.reads).toHaveLength(count); await expect(db(page)).toContainText('Page 2');
});
test('explicit Return to latest keeps loaded pages through pending and failed refresh and resets only on success', async ({ page }) => {
  await mount(page); await pinBoth(page); const context = contexts.get(page), d = hold(503), c = hold(503);
  context.handler = (route, item, ctx) => (item.kind === 'databases' ? d : item.kind === 'configuration' ? c : undefined)?.handle(route, item, ctx) ?? false;
  await dbLatest(page).click(); await configLatest(page).click(); await Promise.all([d.started, c.started]);
  await expect(db(page)).toContainText('Page 2'); await expect(config(page)).toContainText('configuration 2');
  await expect(dbLatest(page)).toBeDisabled(); await expect(configLatest(page)).toBeDisabled();
  await Promise.all([d.finish(), c.finish()]); await expect(db(page).getByRole('alert')).toContainText('loaded page remains available');
  await expect(config(page).getByRole('alert')).toContainText('loaded page remains available');
  context.handler = undefined; context.generation = 2; await dbLatest(page).click(); await configLatest(page).click();
  await expect(db(page)).toContainText('Alpha g2 database 5'); await expect(config(page)).toContainText('Alpha g2 configuration 1');
  await expect(db(page)).toContainText('Page 1'); await expect(config(page)).toContainText('Page 1');
  await expect(db(page).getByRole('alert')).toHaveCount(0); await expect(config(page).getByRole('alert')).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('SECRET_LATE_PROVIDER');
});
test('idle first-page polling updates latest snapshots while file health remains explicitly bounded', async ({ page }) => {
  await mount(page); const context = contexts.get(page); context.generation = 2; await refresh(page).click();
  await expect(db(page)).toContainText('Alpha g2 database 5'); await expect(config(page)).toContainText('Alpha g2 configuration 1');
  const files = page.getByRole('region', { name: 'Logical-file health', exact: true });
  await expect(files).toContainText('bounded first page'); await expect(files.getByRole('navigation')).toHaveCount(0);
});
test('a selected database beyond the first page retains its searched trail across workspace polls', async ({ page }) => {
  await mount(page); await page.getByRole('combobox', { name: 'Database context', exact: true }).selectOption('9');
  await expect(db(page)).toContainText('Page 3'); await expect(db(page)).toContainText('Alpha g1 database 9');
  const context = contexts.get(page); context.generation = 2; await refresh(page).click();
  await expect.poll(() => context.reads.filter(item => item.kind === 'files').length).toBe(2);
  await expect(db(page)).toContainText('Page 3'); await expect(db(page)).toContainText('Alpha g1 database 9');
  expect(context.reads.filter(item => item.kind === 'databases' && item.cursor)).toHaveLength(2);
  await dbLatest(page).click(); await expect(db(page)).toContainText('Alpha g2 database 9');
});
test('missing database searches disclose their bounded search and preserve the visited pages for navigation', async ({ page }) => {
  await mount(page); await page.getByRole('combobox', { name: 'Database context', exact: true }).selectOption('99');
  await expect(db(page)).toContainText('not in the first 500 database rows'); await expect(db(page)).toContainText('Page 3');
  await db(page).getByRole('button', { name: 'Previous database page', exact: true }).click(); await expect(db(page)).toContainText('Page 2');
  await expect(db(page).getByRole('button', { name: /Use .* as database context/u, pressed: true })).toHaveCount(0);
});
test('same-task duplicate navigation dispatches one request per owning pane', async ({ page }) => {
  await mount(page); const context = contexts.get(page), d = hold(), c = hold();
  context.handler = (route, item, ctx) => item.cursor ? (item.kind === 'databases' ? d : c).handle(route, item, ctx) : false;
  await dbNext(page).evaluate(element => { element.click(); element.click(); });
  await configNext(page).evaluate(element => { element.click(); element.click(); }); await Promise.all([d.started, c.started]);
  expect(context.reads.filter(item => item.kind === 'databases' && item.cursor)).toHaveLength(1);
  expect(context.reads.filter(item => item.kind === 'configuration' && item.cursor)).toHaveLength(1);
  await Promise.all([d.finish(), c.finish()]); await expect(db(page)).toContainText('Page 2'); await expect(config(page)).toContainText('Page 2');
});

for (const change of ['target', 'window', 'revision']) test(`changed ${change} fences held continuation receipts and hides investigated rows before effects`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), d = hold(), c = hold();
  context.handler = (route, item, ctx) => item.cursor ? (item.kind === 'databases' ? d : c).handle(route, item, ctx) : false;
  await dbNext(page).click(); await configNext(page).click(); const requests = await Promise.all([d.started, c.started]);
  const aborted = requests.map(request => page.waitForEvent('requestfailed', { predicate: value => value === request }));
  context.handler = undefined;
  if (change === 'target') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  else if (change === 'window') await page.getByRole('combobox', { name: 'Diagnostic window', exact: true }).selectOption('second');
  else { context.revision = 2; await page.getByRole('button', { name: 'Advance target revision', exact: true }).click(); }
  await Promise.all(aborted); const first = await commit(page);
  expect(first.databaseText).not.toContain('database 7'); expect(first.configurationText).not.toContain('configuration 1');
  await Promise.all([d.finish(), c.finish()]); await expect(db(page)).toContainText('Page 1'); await expect(config(page)).toContainText('Page 1');
  if (change === 'target') { await expect(db(page)).not.toContainText('SYNTHETIC Alpha'); await expect(config(page)).not.toContainText('SYNTHETIC Alpha'); }
  await expect(db(page).getByRole('alert')).toHaveCount(0); await expect(config(page).getByRole('alert')).toHaveCount(0);
});
test('unmount aborts both native continuation requests and late responses cannot enter a remounted investigation', async ({ page }) => {
  await mount(page); const context = contexts.get(page), d = hold(), c = hold();
  context.handler = (route, item, ctx) => item.cursor ? (item.kind === 'databases' ? d : c).handle(route, item, ctx) : false;
  await dbNext(page).click(); await configNext(page).click(); const requests = await Promise.all([d.started, c.started]);
  const aborted = requests.map(request => page.waitForEvent('requestfailed', { predicate: value => value === request }));
  await page.getByRole('button', { name: 'Unmount health pages', exact: true }).click(); await Promise.all(aborted);
  context.handler = undefined; context.generation = 2; await Promise.all([d.finish(), c.finish()]);
  await page.getByRole('button', { name: 'Mount health pages', exact: true }).click();
  await expect(db(page)).toContainText('Alpha g2 database 5'); await expect(config(page)).toContainText('Alpha g2 configuration 1');
});
test('live configuration paging preserves original effective UTC bounds across moving refresh windows', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-01-15T14:00:00Z')); await mount(page);
  await page.getByRole('combobox', { name: 'Diagnostic window', exact: true }).selectOption('live');
  await expect(config(page)).toContainText('configuration 1'); await configNext(page).click(); await expect(config(page)).toContainText('configuration 2');
  const context = contexts.get(page), original = context.reads.filter(item => item.kind === 'configuration').at(-1);
  await page.clock.setFixedTime(new Date('2026-01-15T14:05:00Z')); context.generation = 2; await refresh(page).click();
  await expect.poll(() => context.reads.filter(item => item.kind === 'files').length).toBe(2);
  const reads = context.reads.filter(item => item.kind === 'configuration'); expect(reads.at(-1)).toBe(original);
  await expect(config(page)).toContainText('Publication 1'); await configLatest(page).click();
  await expect(config(page)).toContainText('Publication 2'); const latest = context.reads.filter(item => item.kind === 'configuration').at(-1);
  expect(Date.parse(latest.toUtc) - Date.parse(original.toUtc)).toBe(300_000);
});
for (const field of ['publicationGeneration', 'targetRevision']) test(`mismatched configuration ${field} refuses the continuation while preserving the loaded page`, async ({ page }) => {
  await mount(page); contexts.get(page).handler = async (route, item) => {
    if (item.kind !== 'configuration' || !item.cursor) return false;
    const value = packet(item); value[field] += 1; await json(route, value); return true;
  };
  await configNext(page).click(); await expect(config(page).getByRole('alert')).toContainText('loaded page remains available');
  await expect(config(page)).toContainText('configuration 1'); await expect(config(page)).not.toContainText('configuration 2');
});
test('selected configuration scans retain their page and focus the matching native row without automatic poll replacement', async ({ page }) => {
  await mount(page); await page.getByRole('combobox', { name: 'Selected configuration run', exact: true }).selectOption(run(2));
  await expect(config(page)).toContainText('selected configuration scan is not in this loaded page');
  await configNext(page).click(); const row = config(page).locator('[data-configuration-run]'); await expect(row).toBeFocused();
  const context = contexts.get(page), count = context.reads.filter(item => item.kind === 'configuration').length;
  context.generation = 2; await refresh(page).click(); await expect.poll(() => context.reads.filter(item => item.kind === 'files').length).toBe(2);
  expect(context.reads.filter(item => item.kind === 'configuration')).toHaveLength(count); await expect(row).toContainText('Alpha g1 configuration 2');
  await configLatest(page).click();
  await expect(config(page)).toContainText('Latest history for the selected window.');
  await expect(config(page)).toContainText('Alpha g2 configuration 1');
  for (const generation of [3, 4]) {
    context.generation = generation; await refresh(page).click();
    await expect(config(page)).toContainText(`Alpha g${generation} configuration 1`);
    await expect(config(page)).toContainText('Latest history for the selected window.');
  }
});
test('disabled collection context hides loaded configuration evidence on its first commit and permits no new history read', async ({ page }) => {
  await mount(page); await configNext(page).click(); const context = contexts.get(page), count = context.reads.filter(item => item.kind === 'configuration').length;
  await page.getByRole('button', { name: 'Disable collection context', exact: true }).click();
  expect((await commit(page)).configurationText).not.toContain('configuration 2'); await expect(config(page)).toContainText('collection is disabled');
  await expect(configLatest(page)).toHaveCount(0); await refresh(page).click();
  await expect.poll(() => context.reads.filter(item => item.kind === 'files').length).toBe(2); expect(context.reads.filter(item => item.kind === 'configuration')).toHaveLength(count);
});

for (const status of [401, 403]) test(`definitive ${status} removes retained database and configuration receipts until a validated fresh read succeeds`, async ({ page }) => {
  await mount(page); await pinBoth(page); const context = contexts.get(page);
  context.handler = async (route, item) => {
    if (item.kind === 'files') return false;
    await json(route, { detail: 'SECRET_PERMISSION_PROVIDER' }, status); return true;
  };
  await dbLatest(page).click(); await configLatest(page).click();
  await expect(db(page).getByRole('alert')).toBeVisible(); await expect(config(page).getByRole('alert')).toBeVisible();
  await expect(db(page)).not.toContainText('Alpha g1 database'); await expect(config(page)).not.toContainText('Alpha g1 configuration');
  await expect(dbNext(page)).toHaveCount(0); await expect(configNext(page)).toHaveCount(0);
  context.handler = undefined; context.generation = 2; await dbLatest(page).click(); await configLatest(page).click();
  await expect(db(page)).toContainText('Alpha g2 database 5'); await expect(config(page)).toContainText('Alpha g2 configuration 1');
  await expect(page.locator('body')).not.toContainText('SECRET_PERMISSION_PROVIDER');
});

for (const width of [320, 390, 768, 1440]) test(`retained native health pages have accessible dark responsive layout at ${width}px`, async ({ page }, info) => {
  const context = contexts.get(page); context.longNames = true; await page.setViewportSize({ width, height: 1000 });
  await page.goto('/component-tests/retained-health-pages.html'); await expect(dbNext(page)).toBeEnabled(); await expect(configNext(page)).toBeEnabled();
  await pinBoth(page);
  await page.keyboard.press("Tab"); await dbLatest(page).focus(); await expect(dbLatest(page)).toBeFocused();
  const focus = await dbLatest(page).evaluate(element => ({ width: parseFloat(getComputedStyle(element).outlineWidth), style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  await config(page).getByRole('button', { name: 'Previous 100', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(config(page)).toContainText('Page 1'); await configNext(page).focus(); await page.keyboard.press('Enter'); await expect(config(page)).toContainText('Page 2');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  for (const pane of [db(page), config(page)]) expect(await pane.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`health-pages-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    document.querySelectorAll('[tabindex]').forEach(element => { if (element instanceof HTMLElement) element.scrollLeft = 0; });
    window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); }); await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-retained-pagers/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.screenshot({ path: `${directory}/health-pages-${width}.png`, fullPage: true });
});
