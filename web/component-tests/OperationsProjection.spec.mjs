import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, paths, legacyKinds, collectorKind, routeHash, runId, projection, decodeCursor, providerText } from './operationsProjectionFixture.mjs';

const origin = 'http://127.0.0.1:4328', sessions = new WeakMap();
const panel = page => page.getByRole('region', { name: 'Health checks', exact: true });
const card = (page, label = 'TempDB files') => panel(page).getByRole('article', { name: label, exact: true });
const button = (page, action, label = 'TempDB files') => card(page, label).getByRole('button', { name: `${action} ${label} ${action === 'Return' ? 'to latest' : action === 'Retry' || action === 'Cancel' ? 'read' : 'page'}`, exact: true });
const namedTable = (page, kind = 'tempdb-files', label = 'TempDB files') => card(page, label).getByRole('table', { name: new RegExp(`^${kind} named evidence`) });
const refresh = page => page.getByRole('button', { name: 'Refresh workspace', exact: true });
const reads = (page, kind = 'tempdb-files') => sessions.get(page).reads.filter(item => item.kind === kind);
const firstCommit = async page => JSON.parse(await page.getByRole('status', { name: 'First committed projection context', exact: true }).textContent());
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function held(status = 200, transform = value => value) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async release() { release(); await finished; }, async handle(route, item) {
    const body = status === 200 ? transform(projection(item)) : { detail: providerText };
    announce({ request: route.request(), item }); await gate;
    try { await json(route, body, status); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const session = { generation: 1, collectorGenerations: {}, revision: 1, reads: [], unexpected: [], handler: undefined };
  sessions.set(page, session); page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async reason => { session.unexpected.push(`${reason}: ${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject('External request');
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const target = [alpha, beta].find(id => url.pathname.includes(`/${id}/`));
    const entry = target && Object.entries(paths).find(([, path]) => path.replace(':id', target) === url.pathname);
    if (!entry || request.method() !== 'GET') return reject('Unregistered native API');
    const kind = entry[0], cursor = url.searchParams.get('cursor');
    const canonical = !legacyKinds.includes(kind);
    if ([...url.searchParams.keys()].sort().join('|') !== (canonical ? 'atUtc|limit' : cursor ? 'cursor|limit' : 'limit') ||
      url.searchParams.get('limit') !== '50' || canonical && !Number.isFinite(Date.parse(url.searchParams.get('atUtc')))) return reject('Unexpected native parameters');
    const latestGeneration = session.collectorGenerations[collectorKind(kind)] ?? session.generation;
    let item = { kind, target, cursor, index: 0, generation: latestGeneration, revision: session.revision, atUtc: url.searchParams.get('atUtc') };
    if (cursor) {
      try { item = { ...item, ...decodeCursor(cursor, target, kind) }; }
      catch { return reject('Invalid native continuation cursor'); }
    }
    session.reads.push(item);
    // Native v1 continuation reads are bound to the latest collector run/revision;
    // an expired unread cursor is HTTP 400, while already received pages stay local.
    if (cursor && (item.generation !== latestGeneration || item.revision !== session.revision)) return route.fulfill({ status: 400 });
    if (session.handler && await session.handler(route, item)) return;
    await json(route, projection(item));
  });
});
test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Only registered local native-shaped API requests; external traffic and runtime errors are refused').toEqual([]);
});
async function mount(page, tab = 'tempdb') {
  await page.goto(`/component-tests/operations-projection.html${routeHash(alpha, tab)}`);
  await expect(page.getByRole('heading', { name: 'Operations projection harness', exact: true })).toBeVisible();
  if (tab === 'tempdb') await expectFile(page, 1001);
  else await supporting(page, tab === 'backups' ? 'Earlier backup observations' : 'Earlier availability summary');
}
async function supporting(page, label) {
  const summary = panel(page).locator('summary').filter({ hasText: label });
  if (!await summary.evaluate(element => element.parentElement.open)) await summary.click();
}
async function expectFile(page, id) {
  const summary = card(page).locator('summary').filter({ hasText: /^Browse TempDB file evidence \(/ });
  await expect(summary).toBeVisible();
  if (!await summary.evaluate(element => element.parentElement.open)) await summary.click();
  await expect(namedTable(page).getByRole('cell', { name: String(id), exact: true })).toBeVisible();
}
async function missingFile(page, id) { await expect(namedTable(page).getByRole('cell', { name: String(id), exact: true })).toHaveCount(0); }
async function nextFilePage(page, id = 1051) { await button(page, 'Next').click(); await expectFile(page, id); }

test('investigated TempDB pages survive polling and unread expired cursors until an explicit latest recovery', async ({ page }) => {
  await mount(page); await nextFilePage(page); const session = sessions.get(page); session.generation = 2;
  await refresh(page).click(); await expect(card(page, 'TempDB summary').locator('.operation-projection-receipt')).toContainText(runId(2));
  await expectFile(page, 1051); await missingFile(page, 2001);
  await expect(card(page).locator('.operation-projection-receipt')).toContainText(runId(1));
  expect(reads(page)).toHaveLength(2); expect(reads(page, 'tempdb-summary')).toHaveLength(2);
  await button(page, 'Next').click(); await expect(card(page).getByRole('alert')).toBeVisible();
  await expectFile(page, 1051); await missingFile(page, 1101); expect(reads(page).at(-1).generation).toBe(1);
  await button(page, 'Previous').click(); await expectFile(page, 1001);
  const count = reads(page).length; await button(page, 'Next').click(); await expectFile(page, 1051); expect(reads(page)).toHaveLength(count);
  await button(page, 'Return').click(); await expectFile(page, 2001); await expect(card(page).getByRole('alert')).toHaveCount(0);
});
test('previous and first restore original validated pages rather than replacing them with a polled first page', async ({ page }) => {
  await mount(page); await nextFilePage(page); await nextFilePage(page, 1101); const session = sessions.get(page); session.generation = 2;
  await refresh(page).click(); await expect(card(page, 'TempDB summary').locator('.operation-projection-receipt')).toContainText(runId(2));
  const count = reads(page).length; await button(page, 'Previous').click(); await expectFile(page, 1051);
  await button(page, 'First').click(); await expectFile(page, 1001); await button(page, 'Next').click(); await expectFile(page, 1051);
  expect(reads(page)).toHaveLength(count); await expect(namedTable(page).getByRole('row')).toHaveCount(51);
});
test('successful explicit latest resets only after its fresh receipt and later idle polls resume', async ({ page }) => {
  await mount(page); await nextFilePage(page); const session = sessions.get(page); session.generation = 2;
  const pending = held(); session.handler = (route, item) => item.kind === 'tempdb-files' && !item.cursor ? pending.handle(route, item) : false;
  await button(page, 'Return').click(); await pending.started; await expectFile(page, 1051);
  await expect(button(page, 'Return')).toBeDisabled(); await pending.release(); await expectFile(page, 2001); await missingFile(page, 1051);
  session.handler = undefined; session.generation = 3; await refresh(page).click(); await expectFile(page, 3001);
});
test('failed explicit latest retains the investigated receipt and retry repeats that latest operation', async ({ page }) => {
  await mount(page); await nextFilePage(page); const session = sessions.get(page), pending = held(503); session.generation = 2;
  session.handler = (route, item) => item.kind === 'tempdb-files' && !item.cursor ? pending.handle(route, item) : false;
  await button(page, 'Return').click(); await pending.started; await expectFile(page, 1051); await pending.release();
  await expect(card(page).getByRole('alert')).toBeVisible(); await expectFile(page, 1051);
  session.handler = undefined; await button(page, 'Retry').click(); await expectFile(page, 2001); expect(reads(page).at(-1).cursor).toBeNull();
  await expect(page.locator('body')).not.toContainText('SECRET_PROJECTION_PROVIDER');
});
test('failed continuation retries its original cursor without silently changing to the latest run', async ({ page }) => {
  await mount(page); const session = sessions.get(page);
  session.handler = async (route, item) => { if (item.kind !== 'tempdb-files' || !item.cursor) return false; await json(route, { detail: providerText }, 503); return true; };
  await button(page, 'Next').click(); await expect(card(page).getByRole('alert')).toBeVisible(); await expectFile(page, 1001);
  const failed = reads(page).at(-1); session.handler = undefined;
  // Different collectors may advance independently; this TempDB run remains current.
  session.collectorGenerations.backups = 2; session.collectorGenerations.availability = 2;
  await button(page, 'Retry').click(); await expectFile(page, 1051);
  expect(reads(page).at(-1).cursor).toBe(failed.cursor); expect(reads(page).at(-1).generation).toBe(1);
});
test('same-task duplicate Next clicks create one continuation owner and retain the current page while pending', async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held();
  session.handler = (route, item) => item.kind === 'tempdb-files' && item.cursor ? pending.handle(route, item) : false;
  await button(page, 'Next').evaluate(element => { element.click(); element.click(); }); await pending.started;
  expect(reads(page).filter(item => item.cursor)).toHaveLength(1); await expectFile(page, 1001);
  await expect(button(page, 'Cancel')).toBeEnabled(); await pending.release(); await expectFile(page, 1051);
});
test('Cancel aborts only its card and Retry resumes the cancelled continuation without mixing snapshots', async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held();
  session.handler = (route, item) => item.kind === 'tempdb-files' && item.cursor ? pending.handle(route, item) : false;
  await button(page, 'Next').click(); const { request, item } = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request }); await button(page, 'Cancel').click(); await aborted;
  await expectFile(page, 1001); await expect(card(page, 'TempDB summary').getByRole('alert')).toHaveCount(0);
  session.handler = undefined; await button(page, 'Retry').click(); await expectFile(page, 1051); expect(reads(page).at(-1).cursor).toBe(item.cursor);
  await pending.release(); await expectFile(page, 1051); await missingFile(page, 1001);
});

for (const change of ['target', 'revision', 'window', 'database']) test(`changed ${change} hides receipts on its first commit and fences a held old continuation`, async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held();
  session.handler = (route, item) => item.kind === 'tempdb-files' && item.cursor ? pending.handle(route, item) : false;
  await button(page, 'Next').click(); const { request } = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request }); session.handler = undefined; session.generation = 2;
  if (change === 'target') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  else if (change === 'revision') { session.revision = 2; await page.getByRole('button', { name: 'Advance target revision', exact: true }).click(); }
  else await page.getByRole('combobox', { name: change === 'window' ? 'Diagnostic window' : 'Database context', exact: true }).selectOption(change === 'window' ? 'second' : '8');
  await aborted; expect((await firstCommit(page)).projectionText.join(' ')).not.toContain(runId(1));
  await pending.release(); await expectFile(page, 2001); await expect(card(page).locator('.operation-projection-receipt')).toContainText(runId(2, 'tempdb-files', change === 'target' ? beta : alpha));
  await expect(card(page).getByRole('alert')).toHaveCount(0); await missingFile(page, 1051);
});
test('unmount aborts outstanding reads and late receipts cannot replace a remounted context', async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held();
  session.handler = (route, item) => item.kind === 'tempdb-files' && item.cursor ? pending.handle(route, item) : false;
  await button(page, 'Next').click(); const { request } = await pending.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('button', { name: 'Unmount operations', exact: true }).click(); await aborted;
  session.handler = undefined; session.generation = 2; await page.getByRole('button', { name: 'Mount operations', exact: true }).click(); await expectFile(page, 2001);
  await pending.release(); await expectFile(page, 2001); await missingFile(page, 1051);
});
test('a newer idle poll cancels the older automatic read and owns the displayed receipt', async ({ page }) => {
  await mount(page); const session = sessions.get(page), pending = held(); session.generation = 2;
  session.handler = (route, item) => item.kind === 'tempdb-files' ? pending.handle(route, item) : false;
  await refresh(page).click(); const { request } = await pending.started; await expectFile(page, 1001);
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request }); session.handler = undefined; session.generation = 3;
  await refresh(page).click(); await aborted; await expectFile(page, 3001); await pending.release(); await expectFile(page, 3001);
  await expect(card(page).getByRole('alert')).toHaveCount(0);
});
for (const field of ['runId', 'targetRevision', 'observedAtUtc']) test(`changed native ${field} continuation is refused while the last validated page remains visible`, async ({ page }) => {
  await mount(page); sessions.get(page).handler = async (route, item) => {
    if (item.kind !== 'tempdb-files' || !item.cursor) return false;
    const value = projection(item);
    if (field === 'runId') value.runId = runId(2);
    else if (field === 'targetRevision') value.targetRevision += 1;
    else { value.observedAtUtc = '2026-01-15T13:05:00.123456Z'; value.evidence.observedAtUtc = value.observedAtUtc; }
    await json(route, value); return true;
  };
  await button(page, 'Next').click(); await expect(card(page).getByRole('alert')).toBeVisible(); await expectFile(page, 1001); await missingFile(page, 1051);
  await expect(card(page).locator('.operation-projection-receipt')).toContainText(runId(1));
});
for (const status of [401, 403]) test(`HTTP ${status} removes authorized retained rows and rechecking latest needs a validated new receipt`, async ({ page }) => {
  await mount(page); await nextFilePage(page); const session = sessions.get(page);
  session.handler = async (route, item) => { if (item.kind !== 'tempdb-files') return false; await json(route, { detail: providerText }, status); return true; };
  await button(page, 'Return').click(); await expect(card(page).getByRole('alert')).toBeVisible();
  await expect(card(page).getByRole('table')).toHaveCount(0); await expect(card(page)).not.toContainText(runId(1));
  session.handler = undefined; session.generation = 2; await button(page, 'Return').click(); await expectFile(page, 2001);
  await expect(page.locator('body')).not.toContainText('SECRET_PROJECTION_PROVIDER');
});
test('native PermissionDenied collector state remains an explicit source gap, distinct from caller denial', async ({ page }) => {
  sessions.get(page).handler = async (route, item) => {
    if (item.kind !== 'tempdb-files') return false;
    await json(route, { ...projection(item), state: 'PermissionDenied', items: [], hasMore: false, nextCursor: null }); return true;
  };
  await page.goto(`/component-tests/operations-projection.html${routeHash()}`);
  await expect(card(page)).toContainText('The collector could not read this evidence');
  await expect(card(page).locator('.operation-projection-receipt')).toContainText(runId(1));
  await expect(card(page).getByRole('table')).toHaveCount(0); await expect(card(page).getByRole('alert')).toHaveCount(0);
});
test('a newer source revision is disclosed when source GET follows a target edit after App metadata was loaded', async ({ page }) => {
  // Native source reads enforce current database revision2; the already-loaded App metadata is revision1.
  sessions.get(page).revision = 2; await mount(page);
  await expect(card(page).locator('.operation-projection-receipt')).toContainText('Source target revision: 2');
  expect((await firstCommit(page)).revision).toBe(1); await expect(card(page).getByRole('alert')).toHaveCount(0);
});

test('backup and both availability legacy trails retain independently beneath bounded canonical source disclosures', async ({ page }) => {
  await mount(page, 'backups'); await button(page, 'Next', 'Backups').click();
  await expect(namedTable(page, 'backups', 'Backups').getByRole('row')).toHaveCount(51);
  await panel(page).getByRole('tab', { name: 'Availability groups', exact: true }).click(); await supporting(page, 'Earlier availability summary');
  await button(page, 'Next', 'Availability Group replicas').click();
  await expect(card(page, 'Availability Group replicas').locator('.operation-coverage')).toContainText('SecondaryLocalOnly');
  await button(page, 'Next', 'Availability Group replicas').click();
  await expect(namedTable(page, 'ag-replicas', 'Availability Group replicas').getByRole('row')).toHaveCount(11);
  await expect(card(page, 'Availability Group replicas').locator('.operation-coverage')).toContainText('ResolvingLocalOnly');
  const session = sessions.get(page); session.generation = 2; await refresh(page).click();
  await expect(card(page, 'Availability Group replicas').locator('.operation-projection-receipt')).toContainText(runId(1, 'ag-replicas'));
  await expect(card(page, 'Availability Group databases').locator('.operation-projection-receipt')).toContainText(runId(2, 'ag-databases'));
  await panel(page).getByRole('tab', { name: 'Backups', exact: true }).click(); await supporting(page, 'Earlier backup observations');
  await expect(card(page, 'Backups').locator('.operation-projection-receipt')).toContainText(runId(1, 'backups'));
  expect(reads(page, 'backups')).toHaveLength(2); expect(reads(page, 'ag-replicas')).toHaveLength(3);
});
test('wrong-target receipts fail safely and never render a row under another server heading', async ({ page }) => {
  await mount(page); sessions.get(page).handler = async (route, item) => {
    if (item.kind !== 'tempdb-files' || !item.cursor) return false;
    await json(route, { ...projection(item), targetId: beta }); return true;
  };
  await button(page, 'Next').click(); await expect(card(page).getByRole('alert')).toBeVisible(); await expectFile(page, 1001); await missingFile(page, 1051);
});

for (const width of [320, 390, 768, 1440]) test(`native projection investigation is accessible in dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1200 }); await mount(page); await nextFilePage(page);
  const article = card(page), scroller = article.getByRole('region', { name: 'tempdb-files named evidence scrollable evidence', exact: true });
  await page.keyboard.press('Tab'); await button(page, 'Previous').focus(); await page.keyboard.press('Enter'); await expectFile(page, 1001);
  await button(page, 'Next').focus(); await page.keyboard.press('Enter'); await expectFile(page, 1051);
  await scroller.focus(); await expect(scroller).toBeFocused();
  const focus = await scroller.evaluate(element => ({ width: parseFloat(getComputedStyle(element).outlineWidth), style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(3);
  if (await scroller.evaluate(element => element.scrollWidth > element.clientWidth)) {
    await page.keyboard.press('ArrowRight'); await expect.poll(() => scroller.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await article.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  for (const control of await article.locator('button').all()) {
    const bounds = await control.boundingBox(), owner = await article.boundingBox();
    expect(bounds.x).toBeGreaterThanOrEqual(owner.x - 1); expect(bounds.x + bounds.width).toBeLessThanOrEqual(owner.x + owner.width + 1);
  }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`operations-projection-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  await scroller.evaluate(element => { element.scrollLeft = 0; element.scrollTop = 0; element.blur(); });
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-operations-projections/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await article.screenshot({ path: `${directory}/operations-projection-${width}.png` });
});
