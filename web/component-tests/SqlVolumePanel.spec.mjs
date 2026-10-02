import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const firstRun = 'abababab-abab-4bab-8bab-000000000001', latestRun = 'abababab-abab-4bab-8bab-000000000002';
const cursors = [undefined, 'bmF0aXZlOnZvbHVtZXM6Mg==', 'bmF0aXZlOnZvbHVtZXM6Mw=='];
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'SQL-reported target-host volume capacity', exact: true });
const table = page => panel(page).getByRole('table');
const action = (page, name) => panel(page).getByRole('button', { name, exact: true });
const next = page => action(page, 'Next volume page');
const previous = page => action(page, 'Previous volume page');
const first = page => action(page, 'First volume page');
const latest = page => action(page, 'Return to latest volume snapshot');
function nativePage(id, revision, run, index = 0, options = {}) {
  return { instanceId: id, targetRevision: revision, snapshotRunId: run, state: 'current', reason: 'completed',
    completedAtUtc: '2026-10-02T10:00:01.123456Z', lossKind: 'none', minimumLostItems: 0, hasVisibilityGap: false,
    repositoryTimeUtc: '2026-10-02T10:00:02.123456Z', nextCursor: (options.cursors ?? cursors)[index + 1] ?? null,
    items: Array.from({ length: options.fullPages ? 25 : index === 2 ? 4 : 25 }, (_, offset) => ({
      volumeKey: id.slice(-8) + (index * 25 + offset + 1).toString(16).padStart(8, '0') + '0'.repeat(48),
      identityKind: 'volume_id', mappedFileCount: 1, totalBytes: '107374182400',
      availableBytes: run === latestRun ? '53687091200' : ['96636764160', '85899345920', '75161927680'][options.fullPages ? index % 3 : index],
      observedAtUtc: '2026-10-02T10:00:00.123456Z', trendState: index === 1 ? 'not_depleting' : 'estimated',
      trendSampleDays: 14, daysUntilFull: index === 1 ? null : index === 2 ? 8.2 : 3.2,
    })) };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function holdNext() {
  let start, release, complete;
  const started = new Promise(resolve => { start = resolve; }), gate = new Promise(resolve => { release = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    if (item.index !== 1) return false;
    const value = nativePage(item.id, context.revision.get(item.id), firstRun, 1); start(route.request()); await gate;
    try { await json(route, value); } catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { requests: [], unexpected: [], revision: new Map([[alpha, 1], [beta, 1]]), run: firstRun,
    expired: false, handler: undefined, cursors, maximumPages: 3, pageOptions: undefined };
  contexts.set(page, context); page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/resources\/volumes$/.exec(url.pathname);
    const cursor = url.searchParams.get('cursor') ?? undefined, index = context.cursors.indexOf(cursor);
    if (!match || ![alpha, beta].includes(match[1]) || request.method() !== 'GET' || url.searchParams.get('limit') !== '25'
      || [...url.searchParams.keys()].some(key => !['limit', 'cursor'].includes(key)) || index < 0 || index >= context.maximumPages) return reject();
    const item = { id: match[1], cursor, index, url }; context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    if (context.expired && cursor) return json(route, { detail: 'SECRET_EXPIRED_CURSOR' }, 400);
    return json(route, nativePage(item.id, context.revision.get(item.id), context.run, index, context.pageOptions));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only registered local native volume reads may run').toEqual([]));
async function mount(page) { await page.goto('/component-tests/sql-volumes.html'); await expectPage(page, 0); }
async function expectPage(page, index) {
  await expect(panel(page)).toContainText(`Page ${index + 1} · ${index === 2 ? 4 : 25} volumes`);
  await expect(table(page).locator('tbody > tr')).toHaveCount(index === 2 ? 4 : 25);
  await expect(table(page).locator('tbody > tr').first()).toContainText(['4 days', 'Not depleting', '9 days'][index]);
}

test('three validated pages restore locally after native cursor expiry without changing the captured evidence', async ({ page }) => {
  await mount(page); await next(page).click(); await expectPage(page, 1); await next(page).click(); await expectPage(page, 2);
  await expect(table(page).locator('caption')).toHaveText('Retained SQL-reported volume capacity');
  await expect(panel(page)).toContainText('automatic volume refresh paused');
  const context = contexts.get(page); expect(context.requests.map(item => item.cursor)).toEqual(cursors);
  context.run = latestRun; context.expired = true;
  await previous(page).click(); await expectPage(page, 1); await first(page).click(); await expectPage(page, 0);
  await expect(table(page).locator('tbody > tr').first()).toContainText('10.00%');
  await next(page).click(); await expectPage(page, 1); await next(page).click(); await expectPage(page, 2);
  expect(context.requests).toHaveLength(3); await expect(latest(page)).toBeEnabled();
});

test('workspace polling retains page two and keyboard focus while first-page latest observation still refreshes normally', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-02T12:00:00Z') });
  await mount(page); const context = contexts.get(page); context.run = latestRun;
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
  await expect(table(page).locator('tbody > tr').first()).toContainText('50.00%'); expect(context.requests).toHaveLength(2);
  await next(page).click(); await expectPage(page, 1); const count = context.requests.length;
  await page.getByRole('checkbox', { name: 'Automatic workspace poll', exact: true }).check();
  await next(page).focus(); const focused = await next(page).elementHandle();
  await page.clock.runFor(61_000);
  await expectPage(page, 1); await expect(next(page)).toBeFocused(); expect(await focused.evaluate(element => element.isConnected)).toBe(true);
  expect(context.requests).toHaveLength(count);
});

test('Next pending through workspace refresh dispatches one read and preserves its snapshot until verified', async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = holdNext(); context.handler = held.handle;
  await next(page).click(); await held.started;
  await expect(next(page)).toBeDisabled(); await expect(first(page)).toBeDisabled();
  await expect(table(page).locator('tbody > tr')).toHaveCount(25);
  await page.getByRole('button', { name: 'Advance workspace poll', exact: true }).click();
  expect(context.requests).toHaveLength(2); await expect(panel(page)).toContainText('Loading another page');
  await held.finish(); await expectPage(page, 1); expect(context.requests).toHaveLength(2);
});

test('explicit latest renewal discards captured rows, reports safe failure and retries an unbound native first page', async ({ page }) => {
  await mount(page); await next(page).click(); await expectPage(page, 1);
  const context = contexts.get(page); let fail = true;
  context.handler = async (route, item) => { if (item.cursor || !fail) return false; fail = false; await json(route, { detail: '<script>SECRET_LATEST</script>' }, 503); return true; };
  await latest(page).click(); await expect(panel(page).getByRole('alert')).toContainText('The volume capacity request failed. Refresh and retry.');
  await expect(table(page)).toHaveCount(0); await expect(panel(page)).not.toContainText('SECRET_LATEST');
  context.run = latestRun; await action(page, 'Load latest snapshot').click(); await expectPage(page, 0);
  await expect(table(page).locator('tbody > tr').first()).toContainText('50.00%');
  expect(context.requests.map(item => item.cursor)).toEqual([undefined, cursors[1], undefined, undefined]);
  await expect(table(page).locator('caption')).toHaveText('Latest SQL-reported volume capacity');
  await expect(latest(page)).toHaveCount(0);
  await expect(first(page)).toBeDisabled();
});

for (const failure of ['superseded', 'malformed']) test(`${failure} unread continuation clears captured pages and requires explicit latest recovery`, async ({ page }) => {
  await mount(page); await next(page).click(); await expectPage(page, 1); const context = contexts.get(page);
  context.handler = async (route, item) => {
    if (item.index !== 2) return false;
    const value = nativePage(item.id, 1, firstRun, 2);
    if (failure === 'malformed') value.items[0].volumeKey = '<img src=x onerror=alert(1)>';
    else { value.state = 'superseded'; value.reason = 'snapshot_superseded'; value.hasVisibilityGap = true;
      value.items = value.items.map(item => ({ ...item, trendState: 'evidence_unavailable', trendSampleDays: 0, daysUntilFull: null })); }
    await json(route, value); return true;
  };
  await next(page).click(); await expect(panel(page).getByRole('alert')).toContainText(failure === 'malformed' ? 'invalid or mismatched' : 'newer volume snapshot');
  await expect(table(page)).toHaveCount(0); await expect(first(page)).toHaveCount(0);
  await expect(panel(page).locator('img, script')).toHaveCount(0);
  context.handler = undefined; context.run = latestRun;
  await action(page, 'Load latest snapshot').click(); await expectPage(page, 0); await expect(first(page)).toBeDisabled();
  await expect(table(page).locator('tbody > tr').first()).toContainText('50.00%');
  expect(context.requests.at(-1).cursor).toBeUndefined();
});

test('a native 1000-row snapshot may end on page forty but a continuation beyond that bound discards every captured page', async ({ page }) => {
  test.setTimeout(90_000);
  const context = contexts.get(page);
  // Forty registered pages of 25 ordered native rows. The extra opaque token
  // can be returned as a continuation, but its page is never registered/read.
  const tokens = [undefined, ...Array.from({ length: 40 }, (_, index) => Buffer.from(`native:volumes:${index + 2}`).toString('base64'))];
  context.cursors = tokens; context.maximumPages = 40;
  context.pageOptions = { fullPages: true, cursors: tokens.slice(0, 40) };
  await page.goto('/component-tests/sql-volumes.html');
  await expect(panel(page)).toContainText('Page 1 · 25 volumes');
  for (let index = 1; index < 40; index++) {
    await next(page).click(); await expect(panel(page)).toContainText(`Page ${index + 1} · 25 volumes`);
  }
  await expect(table(page).locator('tbody > tr')).toHaveCount(25); await expect(next(page)).toBeDisabled();
  expect(context.requests).toHaveLength(40); expect(context.requests.map(item => item.cursor)).toEqual(tokens.slice(0, 40));
  await first(page).click(); await expect(panel(page)).toContainText('Page 1 · 25 volumes'); expect(context.requests).toHaveLength(40);
  context.pageOptions = { fullPages: true, cursors: tokens };
  await page.reload(); await expect(panel(page)).toContainText('Page 1 · 25 volumes');
  for (let index = 1; index < 39; index++) {
    await next(page).click(); await expect(panel(page)).toContainText(`Page ${index + 1} · 25 volumes`);
  }
  await next(page).click();
  await expect(panel(page).getByRole('alert')).toContainText('The volume snapshot exceeds its supported size. Load the latest snapshot to retry.');
  await expect(table(page)).toHaveCount(0); await expect(first(page)).toHaveCount(0); await expect(previous(page)).toHaveCount(0);
  await expect(action(page, 'Load latest snapshot')).toBeEnabled();
  expect(context.requests).toHaveLength(80); expect(context.requests.every(item => item.index < 40)).toBe(true);
});

for (const change of ['target', 'revision', 'rewind', 'unmount']) test(`${change} cancels pending continuation and fences its late receipt`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), held = holdNext(); context.handler = held.handle;
  await next(page).click(); const request = await held.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  if (change === 'target') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  if (change === 'revision') { context.revision.set(alpha, 2); await page.getByRole('combobox', { name: 'Target revision', exact: true }).selectOption('2'); }
  if (change === 'rewind') await page.getByRole('combobox', { name: 'Time context', exact: true }).selectOption('rewind');
  if (change === 'unmount') await page.getByRole('button', { name: 'Unmount volume panel', exact: true }).click();
  await aborted; await held.finish();
  if (['target', 'revision'].includes(change)) {
    await expectPage(page, 0); await expect(latest(page)).toHaveCount(0); await expect(panel(page).getByRole('alert')).toHaveCount(0);
    expect(context.requests.at(-1).cursor).toBeUndefined();
    expect(context.requests.at(-1).id).toBe(change === 'target' ? beta : alpha);
    if (change === 'target') await expect(panel(page).locator('[title^="Opaque volume key 00000001"]')).toHaveCount(0);
  } else if (change === 'rewind') {
    await expect(table(page)).toHaveCount(0); await expect(panel(page)).toContainText('Historical volume capacity is not available in Rewind.');
    expect(context.requests).toHaveLength(2);
    context.handler = undefined; await page.getByRole('combobox', { name: 'Time context', exact: true }).selectOption('live');
    await expectPage(page, 0); expect(context.requests.at(-1).cursor).toBeUndefined();
  } else { await expect(panel(page)).toHaveCount(0); context.handler = undefined;
    await page.getByRole('button', { name: 'Mount volume panel', exact: true }).click(); await expectPage(page, 0);
    expect(context.requests.at(-1).cursor).toBeUndefined(); }
});

for (const width of [320, 390, 768, 1440]) test(`retained volume pages and explicit latest control are readable and keyboard accessible at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page);
  await next(page).focus(); await page.keyboard.press('Enter'); await expectPage(page, 1);
  const scroll = panel(page).getByRole('region', { name: 'SQL-reported volume capacity table', exact: true });
  await scroll.focus(); await expect(scroll).toBeFocused();
  const focus = await scroll.evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  if (await scroll.evaluate(element => element.scrollWidth > element.clientWidth)) {
    await page.keyboard.press('ArrowRight'); await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }
  for (const control of [first(page), previous(page), next(page), latest(page)]) {
    await control.focus(); await expect(control).toBeFocused(); const box = await control.boundingBox();
    expect(box.height).toBeGreaterThanOrEqual(40); expect(box.width).toBeGreaterThanOrEqual(90);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`volume-browse-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-volume-browsing/screenshots', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => {
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; });
    window.scrollTo({ top: 0, left: 0, behavior: 'instant' });
  });
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: `${directory}/volume-browse-mounted-${width}.png`, fullPage: true });
});
