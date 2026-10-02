import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { target, otherTarget, runId, otherRunId, postPath, reportRun, reportRunForRequest, reportBackend } from './reportsFixture.mjs';

const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: /^Reports for / });
const generate = page => panel(page).getByRole('button', { name: 'Generate report', exact: true });
const kind = page => page.getByRole('combobox', { name: 'Report type', exact: true });
const htmlLink = page => panel(page).getByRole('link', { name: 'Open printable HTML', exact: true });
const csvLink = page => panel(page).getByRole('link', { name: 'Download section CSV', exact: true });
function isPost(route, id = target) {
  const url = new URL(route.request().url());
  return route.request().method() === 'POST' && url.pathname === postPath(id) && url.search === '';
}
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function deferred() { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; }
function heldResponse(body) {
  const started = deferred(); const released = deferred(); const completed = deferred();
  return {
    started: started.promise,
    release: async () => { released.resolve(); await completed.promise; },
    handle: async route => {
      started.resolve(route.request()); await released.promise;
      try { await json(route, body); }
      catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
      finally { completed.resolve(); }
    },
  };
}
test.beforeEach(async ({ page }) => {
  const context = { unexpected: [], requests: [], handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'http://127.0.0.1:4328') {
      context.unexpected.push(`External request: ${url.href}`); await route.abort();
    } else if (/^\/api(?:\/|$)/.test(url.pathname)) {
      context.requests.push(route.request());
      if (!context.handler || !await context.handler(route)) {
        context.unexpected.push(`Unmocked API: ${route.request().method()} ${url.href}`); await route.abort();
      }
    } else await route.continue();
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Only registered local API fixtures may be requested; page errors must be absent').toEqual([]);
});
async function mount(page, handler) {
  contexts.get(page).handler = handler;
  await page.goto('/component-tests/reports.html');
  await expect(page.getByRole('heading', { name: 'Reports panel harness' })).toBeVisible();
}
async function fillWindow(page, from = '2026-08-01T13:45', to = '2026-08-02T13:45') {
  await page.getByLabel('From (UTC)', { exact: true }).fill(from);
  await page.getByLabel('To (UTC, exclusive)', { exact: true }).fill(to);
}
function assertOperation(body, reportKind, window) {
  expect(body).toEqual({ reportKind, operationId: expect.any(String), ...window });
  expect(body.operationId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
}

test('renders the four fixed report kinds and inert server text; snapshot generation has no window', async ({ page }) => {
  await mount(page, async route => {
    if (!isPost(route)) return false;
    assertOperation(route.request().postDataJSON(), 'instance-health');
    expect(route.request().headers()['content-type']).toBe('application/json');
    await json(route, reportRun()); return true;
  });
  expect(await kind(page).locator('option').evaluateAll(options => options.map(option => ({ value: option.value, text: option.textContent })))).toEqual([
    { value: 'instance-health', text: 'Instance Health' }, { value: 'performance-window', text: 'Performance Window' },
    { value: 'incident-evidence', text: 'Incident Evidence' }, { value: 'capacity-readiness', text: 'Capacity/Readiness' },
  ]);
  await expect(panel(page).getByRole('heading')).toHaveText('Reports for Production SQL <img src=x onerror=alert(1)>');
  await expect(panel(page).locator('img, script')).toHaveCount(0);
  await expect(page.getByLabel('From (UTC)', { exact: true })).toHaveCount(0);
  await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  await expect(htmlLink(page)).toHaveAttribute('target', '_blank');
  await expect(htmlLink(page)).toHaveAttribute('rel', 'noreferrer');
  await expect(csvLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/health.csv`);
  await expect(csvLink(page)).toHaveAttribute('download', '');
  await expect(panel(page)).toContainText('08/25/2026');
  await expect(panel(page)).toContainText('13:00:00 UTC');
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('submits UTC half-open bounded windows and selects each fixed export section', async ({ page }) => {
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON();
    assertOperation(body, body.reportKind, { fromUtc: '2026-08-01T13:45:00.000Z',
      toUtc: body.reportKind === 'capacity-readiness' ? '2026-09-01T13:45:00.000Z' : '2026-08-08T13:45:00.000Z' });
    await json(route, reportRunForRequest(body)); return true;
  });
  for (const [value, section, days] of [['performance-window', 'performance', 7], ['incident-evidence', 'incidents', 7], ['capacity-readiness', 'capacity', 31]]) {
    await kind(page).selectOption(value);
    await expect(htmlLink(page)).toHaveCount(0);
    await expect(page.getByRole('group', { name: `UTC window (maximum ${days} days)` })).toBeVisible();
    await fillWindow(page, '2026-08-01T13:45', days === 31 ? '2026-09-01T13:45' : '2026-08-08T13:45');
    await generate(page).click();
    await expect(csvLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/${section}.csv`);
    await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  }
  expect(contexts.get(page).requests).toHaveLength(3);
});

test('rejects an oversized window before IO and retries a safe failure with the unchanged operation payload', async ({ page }) => {
  const bodies = [];
  await mount(page, async route => {
    if (!isPost(route)) return false;
    bodies.push(route.request().postDataJSON());
    if (bodies.length === 1) await json(route, { detail: '<script>SECRET_BACKEND_FAILURE</script>' }, 403);
    else await json(route, reportRunForRequest(route.request().postDataJSON()));
    return true;
  });
  await kind(page).selectOption('performance-window');
  await expect(generate(page)).toBeDisabled();
  await fillWindow(page, '2026-08-01T13:45', '2026-08-08T13:46');
  await generate(page).click();
  await expect(panel(page).getByRole('alert')).toHaveText('Choose a window of at most 7 days.');
  expect(contexts.get(page).requests).toHaveLength(0);
  await fillWindow(page);
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await generate(page).click();
  await expect(panel(page).getByRole('alert')).toHaveText('Report access is not authorized.');
  await expect(panel(page)).not.toContainText('SECRET_BACKEND_FAILURE');
  await expect(htmlLink(page)).toHaveCount(0);
  await generate(page).click();
  await expect(htmlLink(page)).toBeVisible();
  expect(bodies).toHaveLength(2);
  expect(bodies[1]).toEqual(bodies[0]);
  assertOperation(bodies[1], 'performance-window', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' });
});

test('changing report kind and unmounting abort pending generation and exclude late exports', async ({ page }) => {
  const old = heldResponse(reportRun()); const next = heldResponse(reportRunForRequest({ reportKind: 'performance-window',
    fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' }));
  let posts = 0;
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await (++posts === 1 ? old : next).handle(route); return true;
  });
  await generate(page).click();
  const oldRequest = await old.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: request => request === oldRequest });
  await expect(panel(page).getByRole('button', { name: 'Generating…', exact: true })).toBeDisabled();
  await kind(page).selectOption('performance-window');
  await cancelled; await old.release();
  await expect(htmlLink(page)).toHaveCount(0);
  await fillWindow(page); await generate(page).click();
  const nextRequest = await next.started;
  const closedCancellation = page.waitForEvent('requestfailed', { predicate: request => request === nextRequest });
  await panel(page).getByRole('button', { name: 'Close', exact: true }).click();
  await closedCancellation; await next.release();
  await expect(page.getByRole('status')).toHaveText('Panel closed');
  await expect(panel(page)).toHaveCount(0);
  expect(contexts.get(page).requests).toHaveLength(2);
});

test('changing target clears pending generation and allows the new target to generate', async ({ page }) => {
  const old = heldResponse(reportRun());
  await mount(page, async route => {
    if (isPost(route)) { await old.handle(route); return true; }
    if (isPost(route, otherTarget)) { await json(route, reportRun({ targetId: otherTarget, runId: otherRunId })); return true; }
    return false;
  });
  await generate(page).click();
  const oldRequest = await old.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: request => request === oldRequest });
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
  await cancelled; await old.release();
  await expect(panel(page).getByRole('heading')).toHaveText('Reports for Reporting SQL');
  await expect(generate(page)).toBeEnabled();
  await expect(htmlLink(page)).toHaveCount(0);
  await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(otherTarget)}/${otherRunId}/html`);
});

test('changing target removes a completed old-target result before any new export', async ({ page }) => {
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRun()); return true;
  });
  await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
  await expect(panel(page).getByRole('heading')).toHaveText('Reports for Reporting SQL');
  await expect(htmlLink(page)).toHaveCount(0);
  await expect(csvLink(page)).toHaveCount(0);
  await expect(panel(page)).not.toContainText('Ready through');
  expect(contexts.get(page).requests).toHaveLength(1);
});

for (const width of [320, 390, 768, 1440]) test(`report controls and exports remain readable and accessible at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRunForRequest(route.request().postDataJSON())); return true;
  });
  await kind(page).selectOption('capacity-readiness'); await fillWindow(page);
  await expect(panel(page).getByRole('form', { name: 'Generate server report', exact: true })).toBeVisible();
  await expect(panel(page).locator('form.report-form')).toHaveCount(1);
  await expect(panel(page).locator('.report-field')).toHaveCount(3);
  await expect(panel(page).locator('.report-window-fields .report-field')).toHaveCount(2);
  await expect(panel(page).locator('.report-actions').getByRole('button', { name: 'Generate report', exact: true })).toBeVisible();
  const layout = await panel(page).evaluate(element => {
    const box = node => { const rect = node.getBoundingClientRect(); return {
      left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, width: rect.width, height: rect.height,
    }; };
    const fields = [...element.querySelectorAll('.report-field')].map(field => ({
      label: box(field.querySelector('label')), control: box(field.querySelector('input, select')),
    }));
    return { fields, window: box(element.querySelector('.report-window')), action: box(element.querySelector('.report-actions')) };
  });
  for (const field of layout.fields) {
    expect(field.control.width).toBeGreaterThanOrEqual(220);
    expect(field.control.height).toBeGreaterThanOrEqual(40);
    expect(field.label.bottom).toBeLessThanOrEqual(field.control.top + 1);
    expect(field.label.left).toBeGreaterThanOrEqual(field.control.left - 1);
    expect(field.label.right).toBeLessThanOrEqual(field.control.right + 1);
  }
  const [fromField, toField] = layout.fields.slice(1);
  if (width <= 390) expect(toField.control.top).toBeGreaterThanOrEqual(fromField.control.bottom);
  else {
    expect(Math.abs(fromField.control.top - toField.control.top)).toBeLessThanOrEqual(1);
    expect(toField.control.left).toBeGreaterThanOrEqual(fromField.control.right);
  }
  expect(layout.action.top).toBeGreaterThanOrEqual(layout.window.bottom);
  await generate(page).click();
  await expect(csvLink(page)).toBeVisible();
  await expect(panel(page).locator('.report-result .report-export-links').getByRole('link')).toHaveCount(2);
  for (const link of [htmlLink(page), csvLink(page)]) expect((await link.boundingBox()).height).toBeGreaterThanOrEqual(40);
  await htmlLink(page).focus(); await expect(htmlLink(page)).toBeFocused();
  await page.keyboard.press('Tab'); await expect(csvLink(page)).toBeFocused();
  const overflow = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
    elements: [...document.querySelectorAll('body *')].filter(element => element.getBoundingClientRect().right > innerWidth)
      .map(element => ({ tag: element.tagName, className: element.className, width: element.getBoundingClientRect().width })).slice(0, 12) }));
  expect(overflow.scrollWidth <= overflow.width, JSON.stringify(overflow)).toBe(true);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`reports-panel-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true }); const path = `${directory}reports-panel-${width}.png`;
  await page.screenshot({ path, fullPage: true }); await info.attach(`reports-panel-${width}`, { path, contentType: 'image/png' });
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(panel(page)).toContainText('09:00:00 EDT');
});

test('invalid success receipts retry the identical accepted operation and recover its existing run without duplicates', async ({ page }) => {
  const changes = [() => null, value => ({ ...value, targetId: otherTarget }), value => ({ ...value, kind: 0 }),
    value => ({ ...value, state: '<img src=x onerror=alert(1)>' }), value => ({ ...value, parameterDigest: 'a'.repeat(64) })];
  let attempts = 0;
  const operations = [];
  const backend = reportBackend();
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON(); operations.push(body.operationId);
    const value = backend.accept(target, body, route.request().postData()).receipt;
    const change = changes[Math.floor(attempts / 2)];
    await json(route, attempts++ % 2 === 0 ? change(value) : value); return true;
  });
  await kind(page).selectOption('performance-window'); await fillWindow(page);
  for (let index = 0; index < changes.length; index++) {
    await generate(page).click();
    await expect(panel(page).getByRole('alert')).toHaveText('The report response could not be verified.');
    await expect(htmlLink(page)).toHaveCount(0); await expect(csvLink(page)).toHaveCount(0);
    await expect(panel(page).locator('img, script')).toHaveCount(0);
    await generate(page).click();
    const accepted = [...backend.operations.values()][index];
    await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${accepted.receipt.runId}/html`);
    await expect(csvLink(page)).toHaveAttribute('href', `${postPath(target)}/${accepted.receipt.runId}/performance.csv`);
    await expect(panel(page).getByRole('alert')).toHaveCount(0);
  }
  expect(operations).toHaveLength(10);
  for (let index = 0; index < operations.length; index += 2) expect(operations[index + 1]).toBe(operations[index]);
  expect(new Set(operations).size).toBe(5);
  expect(backend.operations.size).toBe(5);
});

test('a lost receipt replays the accepted operation while a verified success permits a fresh report', async ({ page }) => {
  const backend = reportBackend();
  const bodies = [];
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON();
    bodies.push(route.request().postData());
    const accepted = backend.accept(target, body, route.request().postData());
    if (bodies.length === 1) await route.abort('failed');
    else await json(route, accepted.receipt);
    return true;
  });
  await generate(page).click();
  await expect(panel(page).getByRole('alert')).toHaveText('The report response could not be verified.');
  await expect(htmlLink(page)).toHaveCount(0);
  expect(backend.operations.size).toBe(1);
  await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  expect(bodies[1]).toBe(bodies[0]);
  expect(backend.operations.size).toBe(1);
  await generate(page).click();
  await expect.poll(() => backend.operations.size).toBe(2);
  const newest = [...backend.operations.values()][1];
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${newest.receipt.runId}/html`);
  expect(JSON.parse(bodies[2]).operationId).not.toBe(JSON.parse(bodies[1]).operationId);
  expect(backend.operations.size).toBe(2);
});

test('kind, UTC window and server edits each release an uncertain operation instead of replaying another context', async ({ page }) => {
  const backend = reportBackend();
  const bodies = [];
  await mount(page, async route => {
    const id = isPost(route) ? target : isPost(route, otherTarget) ? otherTarget : undefined;
    if (!id) return false;
    const body = route.request().postDataJSON(); bodies.push(body);
    const accepted = backend.accept(id, body, route.request().postData());
    await json(route, { ...accepted.receipt, state: 'ready' });
    return true;
  });
  async function uncertain() {
    await generate(page).click();
    await expect(panel(page).getByRole('alert')).toHaveText('The report response could not be verified.');
    await expect(htmlLink(page)).toHaveCount(0);
  }
  await uncertain();
  await kind(page).selectOption('performance-window'); await fillWindow(page);
  await uncertain();
  await page.getByLabel('To (UTC, exclusive)', { exact: true }).fill('2026-08-02T13:46');
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await uncertain();
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
  await expect(panel(page).getByRole('heading')).toHaveText('Reports for Reporting SQL');
  await uncertain();
  expect(bodies).toHaveLength(4);
  expect(new Set(bodies.map(body => body.operationId)).size).toBe(4);
  assertOperation(bodies[0], 'instance-health');
  assertOperation(bodies[1], 'performance-window', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' });
  assertOperation(bodies[2], 'performance-window', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:46:00.000Z' });
  expect(bodies[3]).toEqual({ ...bodies[2], operationId: bodies[3].operationId });
  expect(backend.operations.size).toBe(4);
});

test('Enter submits the report form and duplicate pending submissions materialize only one operation', async ({ page }) => {
  const backend = reportBackend();
  const pending = heldResponse(reportRunForRequest({ reportKind: 'performance-window',
    fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' }));
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON();
    assertOperation(body, 'performance-window', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' });
    backend.accept(target, body, route.request().postData());
    await pending.handle(route); return true;
  });
  await kind(page).selectOption('performance-window'); await fillWindow(page);
  await page.getByLabel('To (UTC, exclusive)', { exact: true }).press('Enter');
  await pending.started;
  await expect(panel(page).getByRole('button', { name: 'Generating…', exact: true })).toBeDisabled();
  await panel(page).getByRole('form', { name: 'Generate server report', exact: true })
    .evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
  expect(backend.operations.size).toBe(1);
  await pending.release();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  expect(contexts.get(page).requests).toHaveLength(1);
  expect(backend.operations.size).toBe(1);
});
