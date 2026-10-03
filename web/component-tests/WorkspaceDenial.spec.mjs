import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, from, to, registered, nativeValue } from './workspaceDenialFixture.mjs';

const origin = 'http://127.0.0.1:4328', contexts = new WeakMap();
const palette = page => page.getByRole('dialog', { name: 'Jump to evidence', exact: true });
const inventory = page => page.getByRole('combobox', { name: 'Workspace database', exact: true });
const options = page => inventory(page).getByRole('option');
const resultButtons = page => palette(page).locator('.command-palette-result');
const queryResult = page => palette(page).getByRole('button', { name: /^Open query history/ });
const firstCommit = async page => JSON.parse(await page.getByRole('status', { name: 'First committed workspace context' }).textContent());
const providerText = 'SECRET_NATIVE_PROVIDER <img src="https://denial.invalid" onerror="window.denialInjected=true">';
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function held(predicate, status = 200) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    if (!predicate(item)) return false;
    const value = status === 200 ? nativeValue(item, context) : { detail: providerText };
    announce(route.request()); await gate;
    try { await json(route, value, status); }
    catch (failure) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure; }
    finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { generation: 1, requests: [], unexpected: [], handler: undefined }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Runtime error: ${error.message}`));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async reason => { context.unexpected.push(`${reason}: ${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject('External request');
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const item = registered(url, context); if (!item || request.method() !== 'GET') return reject('Unexpected native API');
    context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    await json(route, nativeValue(item, context));
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Only registered native-shaped local traffic and no runtime errors').toEqual([]);
});
async function mount(page, withInventory = true) {
  await page.goto(`/component-tests/workspace-denial.html${withInventory ? '' : '?inventory=0'}`);
  await expect(page.getByRole('heading', { name: 'Workspace denial harness', exact: true })).toBeVisible();
  if (withInventory) await expect(options(page)).toHaveCount(27);
}
async function open(page, category = 'Servers') {
  await page.getByRole('button', { name: 'Open evidence search', exact: true }).focus(); await page.keyboard.press('Control+k');
  await expect(palette(page)).toBeVisible();
  if (category !== 'Servers') await palette(page).getByRole('button', { name: category, exact: true }).click();
}
async function deniedPalette(page) {
  await expect(palette(page)).toContainText('Access to this evidence was denied');
  await expect(resultButtons(page)).toHaveCount(0); await expect(palette(page)).not.toContainText('SECRET_NATIVE_PROVIDER');
}
async function cancelRequest(page, request, change) {
  const failed = page.waitForEvent('requestfailed', { predicate: item => item === request }); await change(); await failed;
}

for (const status of [401, 403]) test(`inventory refresh HTTP ${status} removes prior names and disables unverified choices`, async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => { if (item.kind !== 'databases') return false; await json(route, { detail: providerText }, status); return true; };
  await page.getByRole('button', { name: 'Refresh inventory', exact: true }).click();
  await expect(inventory(page)).toBeDisabled(); await expect(inventory(page)).not.toContainText('SYNTHETIC Alpha');
  await expect(page.getByRole('alert')).toContainText('access'); await expect(page.locator('body')).not.toContainText('Previously loaded names are retained');
  await expect(page.locator('body')).not.toContainText('SECRET_NATIVE_PROVIDER');
  context.handler = undefined; context.generation = 2; await page.keyboard.press('F6');
  await expect(inventory(page)).toBeEnabled(); await expect(options(page)).toHaveCount(27);
  await expect(inventory(page)).toContainText('generation 2 database 5');
});
for (const status of [401, 403]) test(`later inventory page HTTP ${status} discards previous and newly received partial names`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => item.kind === 'databases' && Boolean(item.cursor), status);
  context.generation = 2; context.handler = pending.handle; await page.keyboard.press('F6'); await pending.started;
  await expect(inventory(page)).toContainText('generation 1 database 30'); await pending.finish();
  await expect(inventory(page)).toBeDisabled(); await expect(inventory(page)).not.toContainText('SYNTHETIC');
  await expect(page.getByRole('alert')).toContainText('access');
});
for (const later of [false, true]) test(`transient ${later ? 'later-page' : 'first-page'} inventory failure retains validated names with an explicit error`, async ({ page }) => {
  await mount(page); const context = contexts.get(page); context.generation = 2;
  context.handler = async (route, item) => {
    if (item.kind !== 'databases' || Boolean(item.cursor) !== later) return false;
    await json(route, { detail: providerText }, 503); return true;
  };
  await page.keyboard.press('F6'); await expect(page.getByRole('alert')).toContainText('Previously loaded names are retained');
  await expect(inventory(page)).toBeEnabled(); await expect(inventory(page)).toContainText('generation 1 database 30');
  if (later) await expect(inventory(page)).toContainText('generation 2 database 5');
  await expect(page.locator('body')).not.toContainText('SECRET_NATIVE_PROVIDER');
});
test('inventory target change hides prior names on its first commit and aborts a held old refresh', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => item.kind === 'databases' && item.id === alpha && !item.cursor, 403);
  context.handler = pending.handle; await page.keyboard.press('F6'); const request = await pending.started; context.handler = undefined;
  await cancelRequest(page, request, () => page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta));
  expect((await firstCommit(page)).options.join(' ')).not.toContain('SYNTHETIC Alpha');
  await expect(inventory(page)).toContainText('SYNTHETIC Beta'); await pending.finish();
  await expect(inventory(page)).toBeEnabled(); await expect(inventory(page)).not.toContainText('SYNTHETIC Alpha'); await expect(page.getByRole('alert')).toHaveCount(0);
});
test('unmount aborts inventory ownership and a late denial cannot remove a remounted receipt', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => item.kind === 'databases' && !item.cursor, 403);
  context.handler = pending.handle; await page.keyboard.press('F6'); const request = await pending.started;
  await cancelRequest(page, request, () => page.keyboard.press('F8')); await expect(inventory(page)).toHaveCount(0);
  context.handler = undefined; context.generation = 2; await page.keyboard.press('F8'); await expect(inventory(page)).toContainText('generation 2 database 30');
  await pending.finish(); await expect(inventory(page)).toBeEnabled(); await expect(page.getByRole('alert')).toHaveCount(0);
});

for (const [kind, category] of [['servers', 'Servers'], ['databases', 'Databases'], ['alerts', 'Current alerts']]) {
  for (const status of [401, 403]) test(`palette ${category} later-page HTTP ${status} removes matches and keyboard reopening requires a new receipt`, async ({ page }) => {
    await mount(page, false); const context = contexts.get(page), pending = held(item => item.kind === kind && Boolean(item.cursor), status);
    context.handler = pending.handle; await open(page, category); await pending.started; await expect(resultButtons(page)).toHaveCount(10);
    await pending.finish(); await deniedPalette(page);
    await palette(page).getByRole('searchbox').focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
    await expect(palette(page)).toBeVisible(); expect(new URL(page.url()).hash).toBe('');
    await page.keyboard.press('Escape'); await expect(palette(page)).not.toBeVisible();
    await expect(page.getByRole('button', { name: 'Open evidence search', exact: true })).toBeFocused();
    context.handler = undefined; await open(page, category); await expect(resultButtons(page)).toHaveCount(10);
    await expect(palette(page)).not.toContainText('Access to this evidence was denied');
  });
  test(`palette ${category} transient later-page failure keeps validated matches and keyboard navigation`, async ({ page }) => {
    await mount(page, false); const context = contexts.get(page), pending = held(item => item.kind === kind && Boolean(item.cursor), 503);
    context.handler = pending.handle; await open(page, category); await pending.started; await pending.finish();
    await expect(palette(page)).toContainText('incomplete'); await expect(resultButtons(page)).toHaveCount(10);
    await palette(page).getByRole('searchbox').focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
    const hash = new URL(page.url()).hash, params = new URLSearchParams(hash.split('?')[1]);
    expect(params.get('from')).toBe(from); expect(params.get('to')).toBe(to);
    expect(hash.split('?')[0]).toBe(kind === 'alerts' ? '#/alerts' : '#/health');
    await expect(palette(page)).not.toBeVisible();
  });
}
for (const status of [401, 403]) test(`query catalog HTTP ${status} is caller denial rather than editable unavailable suggestions`, async ({ page }) => {
  await mount(page, false); contexts.get(page).handler = async (route, item) => {
    if (item.kind !== 'catalog') return false; await json(route, { detail: providerText }, status); return true;
  };
  await open(page, 'Query hash'); await deniedPalette(page);
  await expect(palette(page).getByRole('spinbutton', { name: 'Database ID', exact: true })).toBeDisabled();
  await expect(palette(page).locator('datalist option')).toHaveCount(0);
});
for (const status of [401, 403]) test(`exact query lookup HTTP ${status} clears suggestions and fences previously verified navigation`, async ({ page }) => {
  await mount(page, false); await open(page, 'Query hash'); const search = palette(page).getByRole('searchbox', { name: 'Enter query hash', exact: true });
  await search.fill('a'.repeat(64)); await expect(queryResult(page)).toBeVisible();
  const pending = held(item => item.kind === 'history' && item.hash === 'b'.repeat(64), status); contexts.get(page).handler = pending.handle;
  await search.fill('b'.repeat(64)); await pending.started; await expect(queryResult(page)).toHaveCount(0); await pending.finish();
  await deniedPalette(page); await expect(palette(page).locator('datalist option')).toHaveCount(0);
  await expect(palette(page).getByRole('spinbutton', { name: 'Database ID', exact: true })).toBeDisabled();
});
test('transient exact query lookup failure keeps database suggestions and permits a subsequent verified lookup', async ({ page }) => {
  await mount(page, false); await open(page, 'Query hash'); const context = contexts.get(page);
  context.handler = async (route, item) => { if (item.kind !== 'history') return false; await json(route, { detail: providerText }, 503); return true; };
  const search = palette(page).getByRole('searchbox', { name: 'Enter query hash', exact: true }); await search.fill('b'.repeat(64));
  await expect(palette(page)).toContainText('Query history could not be checked'); await expect(queryResult(page)).toHaveCount(0);
  await expect(palette(page).locator('datalist option')).toHaveCount(1); await expect(palette(page).getByRole('spinbutton', { name: 'Database ID' })).toBeEnabled();
  context.handler = undefined; await search.fill('a'.repeat(64)); await expect(queryResult(page)).toBeVisible();
});
test('palette target change removes prior matches and errors before effects; late denial cannot replace the new target search', async ({ page }) => {
  await mount(page, false); const context = contexts.get(page), pending = held(item => item.kind === 'databases' && item.id === alpha && Boolean(item.cursor), 403);
  context.handler = pending.handle; await open(page, 'Databases'); const request = await pending.started; await expect(resultButtons(page)).toHaveCount(10);
  context.handler = undefined; await cancelRequest(page, request, () => page.keyboard.press('F2'));
  const commit = await firstCommit(page); expect(commit.results).toEqual([]); expect(commit.status.join(' ')).not.toContain('denied');
  await expect(palette(page)).toContainText('26 databases searched'); await pending.finish();
  await expect(palette(page)).not.toContainText('SYNTHETIC Alpha'); await expect(palette(page)).not.toContainText('denied');
  await expect(resultButtons(page).first()).toContainText('SYNTHETIC Beta');
});
for (const width of [320, 390, 768, 1440]) test(`workspace denial workflow is accessible and bounded at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await mount(page);
  contexts.get(page).handler = async (route, item) => {
    if (!['databases', 'catalog'].includes(item.kind)) return false; await json(route, { detail: providerText }, 403); return true;
  };
  await page.keyboard.press('F6'); await expect(inventory(page)).toBeDisabled(); await open(page, 'Query hash'); await deniedPalette(page);
  await page.keyboard.press('Tab'); const close = palette(page).getByRole('button', { name: 'Close search', exact: true });
  await close.focus(); await expect(close).toBeFocused();
  expect(await close.evaluate(element => parseFloat(getComputedStyle(element).outlineWidth))).toBeGreaterThanOrEqual(3);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await palette(page).evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  const owner = await palette(page).boundingBox();
  for (const control of await palette(page).locator('button,input').all()) {
    const bounds = await control.boundingBox(); expect(bounds.x).toBeGreaterThanOrEqual(owner.x - 1);
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(owner.x + owner.width + 1);
  }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`workspace-denial-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  await palette(page).evaluate(element => { element.scrollTop = 0; element.scrollLeft = 0; element.querySelector('.command-palette-results')?.scrollTo(0, 0); });
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-workspace-triage/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await palette(page).screenshot({ path: `${directory}/workspace-denial-${width}.png` });
  await page.keyboard.press('Escape'); await expect(page.getByRole('button', { name: 'Open evidence search', exact: true })).toBeFocused();
});
