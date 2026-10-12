import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const first = '22222222-2222-4222-8222-000000000001';
const second = '22222222-2222-4222-8222-000000000002';
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'Configured alert rules', exact: true });
const button = (page, name) => panel(page).getByRole('button', { name, exact: true });
const authority = page => page.getByRole('combobox', { name: 'Rule authority', exact: true });
const selection = page => page.getByRole('combobox', { name: 'Selected rule', exact: true });
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function nativeRule(ruleId, name, overrides = {}) {
  return { ruleId, name, kind: 'metric_threshold', metricId: 'sqlserver.cpu.percent', comparison: 'greater_than_or_equal',
    threshold: 80, hysteresis: 5, confirmationCount: 2, confirmationSeconds: 120,
    evaluationSeconds: 60, clearConfirmationCount: 2, enabled: true, revision: 1,
    updatedAtUtc: '2026-10-02T12:00:00.123456Z', ...overrides };
}
function nativeCatalog(targetId) {
  return { targetId, digest: 'a'.repeat(64), items: [{ name: 'cpu-warning', kind: 'metric_threshold',
    metricId: 'sqlserver.cpu.percent', unit: 'percent', comparison: 'greater_than_or_equal', threshold: 80,
    hysteresis: 5, confirmationCount: 2, confirmationSeconds: 120, evaluationSeconds: 60, clearConfirmationCount: 2 }] };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function applyDisable(context, item) {
  const rules = context.rules.get(item.target);
  const index = rules.findIndex(rule => rule.ruleId === item.id);
  rules[index] = { ...rules[index], enabled: false, revision: rules[index].revision + 1 };
}
function applyFormMutation(context, item) {
  const rules = context.rules.get(item.target);
  const { operationId: _operationId, expectedRevision: _revision, ...values } = item.body;
  if (item.action === 'create') rules.push({ ...values, revision: 1, updatedAtUtc: '2026-10-02T12:05:00.123456Z' });
  else {
    const index = rules.findIndex(rule => rule.ruleId === item.id);
    rules[index] = { ...values, revision: rules[index].revision + 1, updatedAtUtc: '2026-10-02T12:05:00.123456Z' };
  }
}
function heldDisable(status = 204) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item) {
    announce(route.request()); await gate;
    try {
      if (status === 204) await route.fulfill({ status });
      else await json(route, { detail: 'SECRET_STALE_PROVIDER <script>window.ruleInjected=true</script>' }, status);
    } catch (error) {
      if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error;
    } finally { complete(); }
    return true;
  } };
}

test.beforeEach(async ({ page }) => {
  const context = { rules: new Map([[alpha, [nativeRule(first, 'Alpha.cpu-warning'), nativeRule(second, 'Alpha.memory-warning')]],
    [beta, [nativeRule(first, 'Beta.cpu-warning'), nativeRule(second, 'Beta.memory-warning')]]]), reads: [], mutations: [], unexpected: [], handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/rules(?:\/(catalog)|\/([^/]+)(?:\/(disable))?)?$/.exec(url.pathname);
    if (!match || !context.rules.has(match[1])) return reject();
    if (request.method() === 'GET' && match[2] && !url.search) return json(route, nativeCatalog(match[1]));
    if (request.method() === 'GET' && !match[2] && !match[3] && url.search === '?limit=100') {
      context.reads.push(match[1]);
      return json(route, { targetId: match[1], listingStartedUtc: '2026-10-02T12:00:00.123456Z',
        items: context.rules.get(match[1]), hasMore: false, nextCursor: null });
    }
    const action = request.method() === 'POST' && !match[2] && !match[3] ? 'create'
      : request.method() === 'PUT' && match[3] && !match[4] ? 'update'
      : request.method() === 'POST' && match[4] ? 'disable' : undefined;
    if (!action || url.search || action !== 'create' && !context.rules.get(match[1]).some(rule => rule.ruleId === match[3])) return reject();
    const item = { target: match[1], id: match[3], body: request.postDataJSON() };
    if (action !== 'disable') {
      item.action = action;
      item.id = action === 'create' ? item.body.ruleId : match[3];
      const keys = ['name', 'kind', 'metricId', 'comparison', 'threshold', 'hysteresis', 'confirmationCount',
        'confirmationSeconds', 'evaluationSeconds', 'clearConfirmationCount', 'enabled', 'ruleId', 'operationId'];
      if (action === 'update') keys.push('expectedRevision');
      if (Object.keys(item.body).sort().join('|') !== keys.sort().join('|') || !uuid.test(item.body.operationId) ||
          !uuid.test(item.id) || item.body.ruleId !== item.id || item.body.kind !== 'metric_threshold' ||
          item.body.metricId !== 'sqlserver.cpu.percent' || item.body.enabled !== true ||
          action === 'update' && item.body.expectedRevision !== context.rules.get(item.target).find(rule => rule.ruleId === item.id).revision)
        return reject();
    }
    context.mutations.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    if (action === 'disable') applyDisable(context, item);
    else applyFormMutation(context, item);
    await route.fulfill({ status: 204 });
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'No API proxy, external traffic or runtime errors').toEqual([]);
});
async function mount(page) {
  await page.goto('/component-tests/alert-rules.html');
  await expect(panel(page).getByRole('complementary', { name: 'Alpha.cpu-warning', exact: true })).toBeVisible();
  await expect(button(page, 'Disable rule')).toBeEnabled();
}
async function startHeld(page, status) {
  const held = heldDisable(status); contexts.get(page).handler = held.handle;
  await mount(page); await button(page, 'Disable rule').click();
  const request = await held.started;
  await expect(button(page, 'Disabling…')).toBeDisabled();
  return { held, request };
}
async function finishCancelled(page, pending, change) {
  const aborted = page.waitForEvent('requestfailed', { predicate: request => request === pending.request });
  await change(); await aborted; await pending.held.finish();
  await expect(panel(page).getByText('Rule disabled. The list is refreshing.', { exact: true })).toHaveCount(0);
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  expect(contexts.get(page).mutations).toHaveLength(1);
}

test('normal disable submits native revision and operation identity once and refreshes enabled state', async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  await button(page, 'Disable rule').focus(); await page.keyboard.press('Enter');
  await expect(panel(page)).toContainText('Rule disabled. The list is refreshing.');
  await expect(button(page, 'Disable rule')).toHaveCount(0);
  const detail = panel(page).getByRole('complementary', { name: 'Alpha.cpu-warning', exact: true });
  await expect(detail).toContainText('Revision 2'); await expect(detail).toContainText('Disabled');
  expect(context.mutations).toHaveLength(1); expect(context.reads).toEqual([alpha, alpha]);
  const body = context.mutations[0].body;
  expect(body).toEqual({ ...nativeRule(first, 'Alpha.cpu-warning'), enabled: false, expectedRevision: 1, operationId: body.operationId });
  expect(uuid.test(body.operationId)).toBe(true);
});

test('uncertain failure retains the operation identity for retry without reflecting provider content', async ({ page }) => {
  const context = contexts.get(page); let firstAttempt = true;
  context.handler = async route => {
    if (!firstAttempt) return false;
    firstAttempt = false; await json(route, { detail: 'SECRET_PROVIDER <img src=https://invalid.example>' }, 503); return true;
  };
  await mount(page); await button(page, 'Disable rule').click();
  await expect(panel(page).getByRole('alert')).toHaveText('Alert rule changes are temporarily unavailable. Retry the same action.');
  await expect(button(page, 'Disable rule')).toBeEnabled();
  expect(context.reads).toEqual([alpha]);
  await button(page, 'Disable rule').click(); await expect(button(page, 'Disable rule')).toHaveCount(0);
  expect(context.mutations).toHaveLength(2); expect(context.mutations[1]).toEqual(context.mutations[0]);
  await expect(panel(page)).not.toContainText('SECRET_PROVIDER');
});

for (const status of [204, 503]) test(`authority loss cancels held ${status}, clears pending state, and regrant retries the same operation`, async ({ page }) => {
  const pending = await startHeld(page, status), context = contexts.get(page);
  await finishCancelled(page, pending, () => authority(page).selectOption('reader'));
  await expect(button(page, 'Disable rule')).toHaveCount(0); expect(context.reads).toEqual([alpha]);
  await authority(page).selectOption('administrator'); await expect(button(page, 'Disable rule')).toBeEnabled();
  context.handler = undefined;
  await button(page, 'Disable rule').click(); await expect(button(page, 'Disable rule')).toHaveCount(0);
  expect(context.mutations).toHaveLength(2); expect(context.mutations[1]).toEqual(context.mutations[0]);
});

test('changing selected rule cancels the old action and its late success cannot refresh or replace the new pending action', async ({ page }) => {
  const pending = await startHeld(page, 204), context = contexts.get(page);
  const aborted = page.waitForEvent('requestfailed', { predicate: request => request === pending.request });
  await selection(page).selectOption(second); await aborted;
  await expect(button(page, 'Disable rule')).toBeEnabled();
  const next = heldDisable(503); context.handler = next.handle;
  await button(page, 'Disable rule').click(); await next.started;
  await pending.held.finish(); await expect(button(page, 'Disabling…')).toBeDisabled();
  expect(context.reads).toEqual([alpha]);
  await next.finish(); await expect(button(page, 'Disable rule')).toBeEnabled();
  await expect(panel(page).getByRole('alert')).toHaveText('Alert rule changes are temporarily unavailable. Retry the same action.');
  expect(context.mutations.map(item => item.id)).toEqual([first, second]);
  await selection(page).selectOption(first); await expect(panel(page).getByRole('alert')).toHaveCount(0);
  context.handler = undefined;
  await button(page, 'Disable rule').click(); await expect(button(page, 'Disable rule')).toHaveCount(0);
  expect(context.mutations[2]).toEqual(context.mutations[0]);
});

for (const change of ['target', 'unmount']) test(`${change} cancels a disable and refuses its late error or list refresh`, async ({ page }) => {
  const pending = await startHeld(page, 503), context = contexts.get(page);
  await finishCancelled(page, pending, () => change === 'target'
    ? page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta)
    : page.getByRole('button', { name: 'Unmount rules', exact: true }).click());
  if (change === 'target') {
    await expect(panel(page).getByRole('complementary', { name: 'Beta.cpu-warning', exact: true })).toBeVisible();
    await expect(button(page, 'Disable rule')).toBeEnabled(); expect(context.reads).toEqual([alpha, beta]);
  } else {
    await expect(panel(page)).toHaveCount(0); expect(context.reads).toEqual([alpha]);
    await page.getByRole('button', { name: 'Mount rules', exact: true }).click();
    await expect(button(page, 'Disable rule')).toBeEnabled();
  }
});

const ruleForm = (page, action) => panel(page).getByRole('form', {
  name: action === 'create' ? 'Create an alert rule' : 'Edit alert rule', exact: true,
});
async function prepareForm(page, action) {
  if (action === 'update') await button(page, 'Edit rule').click();
  const form = ruleForm(page, action);
  await expect(form.getByRole('button', { name: action === 'create' ? 'Create rule' : 'Save rule', exact: true })).toBeEnabled();
  await form.getByRole('textbox', { name: 'Rule name', exact: true }).fill('SYNTHETIC.pending-rule');
  return form;
}
async function submitHeldForm(page, action, status) {
  const held = heldDisable(status), context = contexts.get(page);
  context.handler = held.handle;
  const form = await prepareForm(page, action);
  await form.getByRole('textbox', { name: 'Rule name', exact: true }).press('Enter');
  const request = await held.started;
  await expect(form.getByRole('button', { name: action === 'create' ? 'Creating…' : 'Saving…', exact: true })).toBeDisabled();
  return { held, request, form };
}

for (const action of ['create', 'update']) test(`${action} Enter guards duplicate submits and preserves native identity on uncertain retry`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = await submitHeldForm(page, action, 503);
  await pending.form.evaluate(element => { element.requestSubmit(); element.requestSubmit(); });
  expect(context.mutations).toHaveLength(1);
  await pending.held.finish();
  await expect(pending.form.getByRole('alert')).toHaveText('Alert rule changes are temporarily unavailable. Retry the same action.');
  await expect(pending.form).not.toContainText('SECRET_STALE_PROVIDER');
  context.handler = undefined;
  await pending.form.getByRole('textbox', { name: 'Rule name', exact: true }).press('Enter');
  if (action === 'create') await expect(panel(page).getByRole('complementary', { name: 'SYNTHETIC.pending-rule', exact: true })).toBeVisible();
  else await expect(panel(page)).toContainText('Rule updated. The list is refreshing.');
  expect(context.mutations).toHaveLength(2);
  expect(context.mutations[1]).toEqual(context.mutations[0]);
  const body = context.mutations[0].body;
  expect(body.ruleId).toBe(action === 'create' ? context.mutations[0].id : first);
  expect(uuid.test(body.ruleId)).toBe(true); expect(uuid.test(body.operationId)).toBe(true);
  expect(body.expectedRevision).toBe(action === 'create' ? undefined : 1);
  expect(body.name).toBe('SYNTHETIC.pending-rule'); expect(body.threshold).toBe(80);
  expect(context.reads).toEqual([alpha, alpha]);
  await expect(page.locator('output[aria-label="Rule selection callbacks"]')).toHaveText(action === 'create' ? '1' : '0');
});

for (const action of ['create', 'update']) test(`${action} close cancels pending receipt and leaves reopened drafts and callbacks untouched`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = await submitHeldForm(page, action, action === 'create' ? 204 : 503);
  const aborted = page.waitForEvent('requestfailed', { predicate: request => request === pending.request });
  if (action === 'create') await button(page, 'Close').click();
  else await pending.form.getByRole('button', { name: 'Cancel', exact: true }).click();
  await aborted;
  if (action === 'create') await page.getByRole('button', { name: 'Mount rules', exact: true }).click();
  const reopened = await prepareForm(page, action);
  await reopened.getByRole('textbox', { name: 'Rule name', exact: true }).fill('SYNTHETIC.new-draft');
  await pending.held.finish();
  await expect(reopened.getByRole('textbox', { name: 'Rule name', exact: true })).toHaveValue('SYNTHETIC.new-draft');
  await expect(reopened.getByRole('button', { name: action === 'create' ? 'Create rule' : 'Save rule', exact: true })).toBeEnabled();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await expect(panel(page)).not.toContainText('Rule updated. The list is refreshing.');
  await expect(page.locator('output[aria-label="Rule selection callbacks"]')).toHaveText('0');
  expect(context.mutations).toHaveLength(1);
  expect(context.reads).toEqual(action === 'create' ? [alpha, alpha] : [alpha]);
});

for (const action of ['create', 'update']) test(`${action} authority loss aborts consumption and regrant explicitly retries the unchanged attempt`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = await submitHeldForm(page, action, 204);
  const aborted = page.waitForEvent('requestfailed', { predicate: request => request === pending.request });
  await authority(page).selectOption('reader'); await aborted;
  await expect(ruleForm(page, 'create')).toHaveCount(0); await expect(ruleForm(page, 'update')).toHaveCount(0);
  await pending.held.finish();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await expect(page.locator('output[aria-label="Rule selection callbacks"]')).toHaveText('0');
  expect(context.reads).toEqual([alpha]);
  await authority(page).selectOption('administrator');
  if (action === 'update') await expect(ruleForm(page, 'update')).toHaveCount(0);
  const reopened = await prepareForm(page, action);
  context.handler = undefined;
  await reopened.getByRole('textbox', { name: 'Rule name', exact: true }).press('Enter');
  await expect.poll(() => context.reads.length).toBe(2);
  expect(context.mutations).toHaveLength(2); expect(context.mutations[1]).toEqual(context.mutations[0]);
});

test('changing selection cancels a pending edit before its late receipt can affect another rule', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = await submitHeldForm(page, 'update', 204);
  const aborted = page.waitForEvent('requestfailed', { predicate: request => request === pending.request });
  await selection(page).selectOption(second); await aborted;
  const next = await prepareForm(page, 'update');
  await next.getByRole('textbox', { name: 'Rule name', exact: true }).fill('SYNTHETIC.second-draft');
  await pending.held.finish();
  await expect(next).toContainText('Edit Alpha.memory-warning');
  await expect(next.getByRole('textbox', { name: 'Rule name', exact: true })).toHaveValue('SYNTHETIC.second-draft');
  await expect(next.getByRole('button', { name: 'Save rule', exact: true })).toBeEnabled();
  await expect(panel(page)).not.toContainText('Rule updated. The list is refreshing.');
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  expect(context.reads).toEqual([alpha]); expect(context.mutations).toHaveLength(1);
});

for (const width of [320, 1440]) test(`create and edit forms expose dark accessible labels, focus and bounded layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page);
  for (const action of ['create', 'update']) {
    const form = await prepareForm(page, action), input = form.getByRole('textbox', { name: 'Rule name', exact: true });
    const heading = panel(page).locator('.detail-pane-heading > div:first-child');
    expect((await heading.boundingBox()).width, 'Rule identity must keep readable width beside or above actions').toBeGreaterThanOrEqual(180);
    await input.focus(); await expect(input).toBeFocused();
    const geometry = await input.evaluate(element => ({ left: element.getBoundingClientRect().left,
      right: element.getBoundingClientRect().right, outline: getComputedStyle(element).outlineStyle,
      outlineWidth: parseFloat(getComputedStyle(element).outlineWidth) }));
    expect(geometry.left).toBeGreaterThanOrEqual(0); expect(geometry.right).toBeLessThanOrEqual(width);
    expect(geometry.outline).not.toBe('none'); expect(geometry.outlineWidth).toBeGreaterThanOrEqual(2);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`rule-${action}-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots', import.meta.url));
    await mkdir(directory, { recursive: true });
    await input.evaluate(element => element.blur());
    await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
    await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
    await page.screenshot({ path: `${directory}/rule-${action}-${width}.png`, fullPage: true });
  }
  expect(contexts.get(page).mutations).toEqual([]);
});

for (const width of [320, 1440]) test(`alert rules table supports keyboard horizontal panning and accessible dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page);
  const region = panel(page).getByRole('region', { name: 'Alert rules table', exact: true });
  await expect(region).toHaveAttribute('tabindex', '0'); await region.focus(); await expect(region).toBeFocused();
  const geometry = await region.evaluate(element => ({ width: element.clientWidth, content: element.scrollWidth,
    outline: getComputedStyle(element).outlineStyle, outlineWidth: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(geometry.content).toBeGreaterThan(geometry.width); expect(geometry.outline).not.toBe('none'); expect(geometry.outlineWidth).toBeGreaterThanOrEqual(2);
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => region.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`alert-rules-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots', import.meta.url));
  await mkdir(directory, { recursive: true });
  await region.evaluate(element => { element.scrollLeft = 0; });
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: `${directory}/alert-rules-${width}.png`, fullPage: true });
});
