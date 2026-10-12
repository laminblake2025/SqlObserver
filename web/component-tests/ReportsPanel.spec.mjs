import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { target, otherTarget, runId, otherRunId, postPath, reportRun, reportRunForRequest, reportBackend, reportHref } from './reportsFixture.mjs';

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
function heldResponse(body, status = 200) {
  const started = deferred(); const released = deferred(); const completed = deferred();
  return {
    started: started.promise,
    release: async () => { released.resolve(); await completed.promise; },
    handle: async route => {
      started.resolve(route.request()); await released.promise;
      try { await json(route, body, status); }
      catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
      finally { completed.resolve(); }
    },
  };
}
test.beforeEach(async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-24T13:00:00Z'));
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
async function mount(page, handler, hash = reportHref()) {
  contexts.get(page).handler = handler;
  await page.goto(`/component-tests/reports.html${hash}`);
  await expect(page.getByRole('heading', { name: 'Reports panel harness' })).toBeVisible();
}
async function fillWindow(page, from = '2026-08-01T13:45', to = '2026-08-02T13:45') {
  const range = page.getByRole('combobox', { name: 'Time range', exact: true });
  if (await range.inputValue() !== 'custom') await range.selectOption('custom');
  const editor = page.getByRole('form', { name: 'Custom UTC time range', exact: true });
  if (!await editor.isVisible()) await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await editor.getByLabel('From (UTC)', { exact: true }).fill(from);
  await editor.getByLabel('To (UTC)', { exact: true }).fill(to);
  await editor.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(editor).toHaveCount(0);
}
function assertOperation(body, reportKind, window) {
  expect(body).toEqual({ reportKind, operationId: expect.any(String), ...window });
  expect(body.operationId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
}

test('renders the four fixed report kinds and inert server text; Instance Health remains a current snapshot in Rewind', async ({ page }) => {
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
  await expect(panel(page).locator('input')).toHaveCount(0);
  await expect(panel(page)).toContainText(/current snapshot/i);
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('custom');
  await expect(panel(page).locator('.report-window')).toHaveCount(0);
  await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  await expect(htmlLink(page)).toHaveAttribute('target', '_blank');
  await expect(htmlLink(page)).toHaveAttribute('rel', 'noreferrer');
  await expect(csvLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/health.csv`);
  await expect(csvLink(page)).toHaveAttribute('download', '');
  await expect(panel(page)).toContainText('09/25/2026');
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
  await fillWindow(page, '2026-08-01T13:45', '2026-08-08T13:46');
  await expect(panel(page).getByRole('alert')).toHaveText('Update the shared time range to generate this report. Choose a window of at most 7 days.');
  await expect(generate(page)).toBeDisabled();
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
  const sharedRange = page.getByRole('combobox', { name: 'Time range', exact: true });
  const sharedEditorToggle = page.getByRole('button', { name: 'Edit UTC range', exact: true });
  for (const [control, minimumWidth] of [[sharedRange, 140], [sharedEditorToggle, 100]]) {
    const bounds = await control.boundingBox();
    expect(bounds.width).toBeGreaterThanOrEqual(minimumWidth);
    expect(bounds.height).toBeGreaterThanOrEqual(40);
  }
  await expect(panel(page).getByRole('form', { name: 'Generate server report', exact: true })).toBeVisible();
  await expect(panel(page).locator('form.report-form')).toHaveCount(1);
  await expect(panel(page).locator('.report-field')).toHaveCount(3);
  await expect(panel(page).locator('.report-window-fields .report-field')).toHaveCount(2);
  await expect(panel(page).locator('.report-actions').getByRole('button', { name: 'Generate report', exact: true })).toBeVisible();
  const layout = await panel(page).evaluate(element => {
    const box = node => { const rect = node.getBoundingClientRect(); return {
      left: rect.left, right: rect.right, top: rect.top, bottom: rect.bottom, width: rect.width, height: rect.height,
    }; };
    const picker = element.querySelector('.report-field select');
    const fields = [...element.querySelectorAll('.report-window-fields .report-field')].map(field => ({
      label: box(field.querySelector('dt')), value: box(field.querySelector('dd')),
    }));
    return { picker: box(picker), pickerLabel: box(element.querySelector('label')), fields,
      window: box(element.querySelector('.report-window')), action: box(element.querySelector('.report-actions')) };
  });
  expect(layout.picker.width).toBeGreaterThanOrEqual(220);
  expect(layout.picker.height).toBeGreaterThanOrEqual(40);
  expect(layout.pickerLabel.bottom).toBeLessThanOrEqual(layout.picker.top + 1);
  for (const field of layout.fields) {
    expect(field.value.width).toBeGreaterThanOrEqual(220);
    expect(field.value.height).toBeGreaterThan(0);
    expect(field.label.bottom).toBeLessThanOrEqual(field.value.top + 1);
    expect(field.label.left).toBeGreaterThanOrEqual(field.value.left - 1);
    expect(field.label.right).toBeLessThanOrEqual(field.value.right + 1);
  }
  const [fromField, toField] = layout.fields;
  if (width <= 390) expect(toField.label.top).toBeGreaterThanOrEqual(fromField.value.bottom);
  else {
    expect(Math.abs(fromField.label.top - toField.label.top)).toBeLessThanOrEqual(1);
    expect(toField.value.left).toBeGreaterThanOrEqual(fromField.value.right);
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
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-report-expiry/legacy/screenshots/', import.meta.url));
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

test('a lost Live receipt freezes its first window through clock, refresh and display changes; verified success permits a fresh window', async ({ page }) => {
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
  }, reportHref({ reportKind: 'performance-window', range: '1h' }));
  await generate(page).click();
  await expect(panel(page).getByRole('alert')).toHaveText('The report response could not be verified.');
  await expect(htmlLink(page)).toHaveCount(0);
  expect(backend.operations.size).toBe(1);
  assertOperation(JSON.parse(bodies[0]), 'performance-window', { fromUtc: '2026-09-24T12:00:00.000Z', toUtc: '2026-09-24T13:00:00.000Z' });
  await page.clock.setFixedTime(new Date('2026-09-24T14:00:00Z'));
  await page.getByRole('button', { name: 'Refresh context', exact: true }).click();
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(panel(page).locator('time[datetime="2026-09-24T12:00:00.000Z"]')).toBeVisible();
  await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/html`);
  expect(bodies[1]).toBe(bodies[0]);
  expect(backend.operations.size).toBe(1);
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('utc');
  await generate(page).click();
  await expect.poll(() => backend.operations.size).toBe(2);
  const newest = [...backend.operations.values()][1];
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${newest.receipt.runId}/html`);
  expect(JSON.parse(bodies[2]).operationId).not.toBe(JSON.parse(bodies[1]).operationId);
  assertOperation(JSON.parse(bodies[2]), 'performance-window', { fromUtc: '2026-09-24T13:00:00.000Z', toUtc: '2026-09-24T14:00:00.000Z' });
  expect(backend.operations.size).toBe(2);
});

test('kind, From, To, range and server edits each release an uncertain operation instead of replaying another context', async ({ page }) => {
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
  await fillWindow(page, '2026-08-01T13:45', '2026-08-02T13:46');
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await uncertain();
  await fillWindow(page, '2026-08-01T13:44', '2026-08-02T13:46');
  await uncertain();
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('6h');
  await uncertain();
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
  await expect(panel(page).getByRole('heading')).toHaveText('Reports for Reporting SQL');
  await uncertain();
  expect(bodies).toHaveLength(6);
  expect(new Set(bodies.map(body => body.operationId)).size).toBe(6);
  assertOperation(bodies[0], 'instance-health');
  assertOperation(bodies[1], 'performance-window', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' });
  assertOperation(bodies[2], 'performance-window', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:46:00.000Z' });
  assertOperation(bodies[3], 'performance-window', { fromUtc: '2026-08-01T13:44:00.000Z', toUtc: '2026-08-02T13:46:00.000Z' });
  assertOperation(bodies[4], 'performance-window', { fromUtc: '2026-09-24T07:00:00.000Z', toUtc: '2026-09-24T13:00:00.000Z' });
  expect(bodies[5]).toEqual({ ...bodies[4], operationId: bodies[5].operationId });
  expect(backend.operations.size).toBe(6);
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
  await generate(page).focus();
  await page.keyboard.press('Enter');
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

test('the shared custom editor preserves exact microsecond scope bounds into native report payloads and displayed provenance', async ({ page }) => {
  const fromUtc = '2026-08-01T13:45:00.123456Z';
  const toUtc = '2026-08-02T13:45:00.654321Z';
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON();
    assertOperation(body, 'performance-window', { fromUtc, toUtc });
    await json(route, reportRunForRequest(body)); return true;
  }, reportHref({ reportKind: 'performance-window', from: fromUtc, to: toUtc }));
  await expect(panel(page).locator('time').nth(0)).toHaveAttribute('datetime', fromUtc);
  await expect(panel(page).locator('time').nth(1)).toHaveAttribute('datetime', toUtc);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  const editor = page.getByRole('form', { name: 'Custom UTC time range', exact: true });
  await expect(editor.getByLabel('From (UTC)', { exact: true })).toHaveValue('2026-08-01T13:45:00.123');
  await expect(editor.getByLabel('To (UTC)', { exact: true })).toHaveValue('2026-08-02T13:45:00.654');
  await editor.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(editor).toHaveCount(0);
  await generate(page).click();
  await expect(csvLink(page)).toHaveAttribute('href', `${postPath(target)}/${runId}/performance.csv`);
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('unknown and duplicate URL report selections refuse generation until the picker repairs the selection', async ({ page }) => {
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON();
    assertOperation(body, 'instance-health'); await json(route, reportRunForRequest(body)); return true;
  }, reportHref({ reportKind: '<img src=x onerror=alert(1)>' }));
  for (const hash of [reportHref({ reportKind: '<img src=x onerror=alert(1)>' }),
    `${reportHref({ reportKind: 'performance-window' })}&reportKind=instance-health`]) {
    await page.evaluate(value => { location.hash = value; }, hash);
    await expect(kind(page)).toHaveValue('');
    await expect(panel(page).getByRole('alert')).toBeVisible();
    await expect(generate(page)).toBeDisabled();
    await expect(htmlLink(page)).toHaveCount(0);
    await expect(panel(page).locator('img, script')).toHaveCount(0);
    expect(contexts.get(page).requests).toHaveLength(0);
  }
  await kind(page).selectOption('instance-health');
  await expect(generate(page)).toBeEnabled();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  const repaired = await page.evaluate(() => new URLSearchParams(location.hash.split('?')[1]).getAll('reportKind'));
  expect(repaired).toEqual(['instance-health']);
  await generate(page).click(); await expect(htmlLink(page)).toBeVisible();
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('invalid, reversed, future and over-31-day shared windows refuse native IO and can be repaired through global controls', async ({ page }) => {
  const badWindows = [
    { from: '', to: '2026-08-02T13:45:00.000Z' },
    { from: '2026-02-30T13:45:00Z', to: '2026-03-01T13:45:00Z' },
    { from: '2026-08-02T13:45:00Z', to: '2026-08-01T13:45:00Z' },
    { from: '2026-09-24T13:00:00Z', to: '2026-09-25T13:00:00Z' },
    { from: '2026-08-01T13:45:00Z', to: '2026-09-01T13:45:00.000001Z' },
  ];
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const body = route.request().postDataJSON();
    assertOperation(body, 'capacity-readiness', { fromUtc: '2026-08-01T13:45:00.000Z', toUtc: '2026-08-02T13:45:00.000Z' });
    await json(route, reportRunForRequest(body)); return true;
  }, reportHref({ reportKind: 'capacity-readiness', ...badWindows[0] }));
  for (const window of badWindows) {
    await page.evaluate(hash => { location.hash = hash; }, reportHref({ reportKind: 'capacity-readiness', ...window }));
    await expect(panel(page).getByRole('alert')).toBeVisible();
    await expect(generate(page)).toBeDisabled();
    await expect(htmlLink(page)).toHaveCount(0);
    expect(contexts.get(page).requests).toHaveLength(0);
  }
  await fillWindow(page);
  await expect(generate(page)).toBeEnabled();
  await generate(page).click(); await expect(csvLink(page)).toBeVisible();
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('shared scope edits abort pending work and fence late exports and late safe-error receipts', async ({ page }) => {
  const denied = heldResponse({ detail: 'SECRET_LATE_FAILURE' }, 403);
  const accepted = heldResponse(reportRunForRequest({ reportKind: 'performance-window',
    fromUtc: '2026-09-24T12:00:00.000Z', toUtc: '2026-09-24T13:00:00.000Z' }));
  let posts = 0;
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await (++posts === 1 ? denied : accepted).handle(route); return true;
  }, reportHref({ reportKind: 'performance-window' }));
  await generate(page).click(); const first = await denied.started;
  const firstCancelled = page.waitForEvent('requestfailed', { predicate: request => request === first });
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('1h');
  await firstCancelled; await denied.release();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await expect(generate(page)).toBeEnabled();
  await generate(page).click(); const second = await accepted.started;
  const secondCancelled = page.waitForEvent('requestfailed', { predicate: request => request === second });
  await fillWindow(page);
  await secondCancelled; await accepted.release();
  await expect(htmlLink(page)).toHaveCount(0); await expect(csvLink(page)).toHaveCount(0);
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  await expect(panel(page)).not.toContainText('SECRET_LATE_FAILURE');
  await expect(generate(page)).toBeEnabled();
  expect(contexts.get(page).requests).toHaveLength(2);
});

const generateNew = page => panel(page).getByRole('button', { name: 'Generate new report', exact: true });
const expired = page => panel(page).getByText(/^This report expired\./u);
async function expiryClock(page) {
  const at = new Date('2026-09-24T13:00:00.000Z');
  await page.clock.install({ time: at }); await page.clock.pauseAt(at);
}

test('exports retire at the native half-open expiry boundary without an unrelated render or automatic POST', async ({ page }) => {
  await expiryClock(page);
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRunForRequest(route.request().postDataJSON(), {
      expiresAtUtc: '2026-09-24T13:00:01.001000+00:00' })); return true;
  });
  await generate(page).click(); await expect(htmlLink(page)).toBeVisible(); await expect(csvLink(page)).toBeVisible();
  await page.clock.runFor(1000); await expect(htmlLink(page)).toBeVisible();
  await page.clock.runFor(1); await expect(htmlLink(page)).toHaveCount(0); await expect(csvLink(page)).toHaveCount(0);
  await expect(expired(page)).toBeVisible(); await expect(generateNew(page)).toBeEnabled();
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('a native submillisecond expiry stays usable before its instant and expires on the first representable later millisecond', async ({ page }) => {
  await expiryClock(page);
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRunForRequest(route.request().postDataJSON(), { expiresAtUtc: '2026-09-24T13:00:00.000001+00:00' })); return true;
  });
  await generate(page).click(); await expect(htmlLink(page)).toBeVisible();
  await page.clock.runFor(1); await expect(htmlLink(page)).toHaveCount(0); await expect(expired(page)).toBeVisible();
  expect(contexts.get(page).requests).toHaveLength(1);
});

for (const expiresAtUtc of ['2026-09-24T13:00:00.000000+00:00', '2026-09-24T12:59:59.999999+00:00'])
  test(`an already expired native receipt at ${expiresAtUtc} never advertises export links`, async ({ page }) => {
    await mount(page, async route => {
      if (!isPost(route)) return false;
      await json(route, reportRunForRequest(route.request().postDataJSON(), { expiresAtUtc })); return true;
    });
    await page.evaluate(() => {
      window.reportAdvertisedExports = [];
      new MutationObserver(() => { for (const link of document.querySelectorAll('.report-export-links a')) window.reportAdvertisedExports.push(link.getAttribute('href')); })
        .observe(document.getElementById('root'), { childList: true, subtree: true });
    });
    await generate(page).click(); await expect(expired(page)).toBeVisible();
    await expect(htmlLink(page)).toHaveCount(0); await expect(csvLink(page)).toHaveCount(0);
    expect(await page.evaluate(() => window.reportAdvertisedExports)).toEqual([]);
    await expect(generateNew(page)).toBeEnabled(); expect(contexts.get(page).requests).toHaveLength(1);
  });

for (const wake of ['focus', 'visibilitychange']) test(`${wake} wake retires stale exports after the browser clock passes expiry`, async ({ page }) => {
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRunForRequest(route.request().postDataJSON(), { expiresAtUtc: '2026-09-24T13:00:10.123456+00:00' })); return true;
  });
  await generate(page).click(); await expect(htmlLink(page)).toBeVisible();
  // setFixedTime changes the wall clock without advancing the outstanding timer.
  await page.clock.setFixedTime(new Date('2026-09-24T13:00:11Z'));
  await page.evaluate(event => { (event === 'focus' ? window : document).dispatchEvent(new Event(event)); }, wake);
  await expect(htmlLink(page)).toHaveCount(0); await expect(csvLink(page)).toHaveCount(0); await expect(expired(page)).toBeVisible();
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('export activation rechecks expiry before dispatch even when a throttled timer has not rendered the expired state', async ({ page }) => {
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRunForRequest(route.request().postDataJSON(), { expiresAtUtc: '2026-09-24T13:00:10.123456+00:00' })); return true;
  });
  await generate(page).click(); await expect(csvLink(page)).toBeVisible();
  const staleAnchor = await csvLink(page).elementHandle();
  await page.clock.setFixedTime(new Date('2026-09-24T13:00:11Z'));
  // Native anchor activation avoids a focus wake, exercising the click-time guard itself.
  await staleAnchor.evaluate(anchor => anchor.click());
  await expect(expired(page)).toBeVisible(); await expect(csvLink(page)).toHaveCount(0); await expect(htmlLink(page)).toHaveCount(0);
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('an uncertain Live attempt keeps its operation and exact window through expiry; verified expiry permits a fresh generation', async ({ page }) => {
  await expiryClock(page); let repositoryUtc = '2026-09-24T13:00:00.000Z';
  const backend = reportBackend({ receiptOverrides: () => ({ snapshotUtc: repositoryUtc,
    expiresAtUtc: new Date(Date.parse(repositoryUtc) + 86_400_000).toISOString() }) });
  const bodies = [];
  await mount(page, async route => {
    if (!isPost(route)) return false;
    const raw = route.request().postData(); bodies.push(raw);
    const accepted = backend.accept(target, route.request().postDataJSON(), raw);
    if (bodies.length === 1) await route.abort('failed'); else await json(route, accepted.receipt);
    return true;
  }, reportHref({ reportKind: 'performance-window', range: '1h' }));
  await generate(page).click(); await expect(panel(page).getByRole('alert')).toHaveText('The report response could not be verified.');
  await page.clock.fastForward(86_402_000); repositoryUtc = '2026-09-25T13:00:02.000Z';
  await page.getByRole('button', { name: 'Refresh context', exact: true }).click();
  await generate(page).click(); await expect(expired(page)).toBeVisible();
  expect(bodies[1]).toBe(bodies[0]); expect(backend.operations.size).toBe(1);
  await expect(htmlLink(page)).toHaveCount(0);
  await generateNew(page).click(); await expect(htmlLink(page)).toBeVisible();
  const original = JSON.parse(bodies[0]), fresh = JSON.parse(bodies[2]);
  expect(fresh.operationId).not.toBe(original.operationId); expect(backend.operations.size).toBe(2);
  assertOperation(fresh, 'performance-window', { fromUtc: '2026-09-25T12:00:02.000Z', toUtc: repositoryUtc });
  expect(fresh.toUtc).not.toBe(original.toUtc);
});

test('target ABA cannot revive a held old receipt or its export expiry timer', async ({ page }) => {
  const old = heldResponse(reportRun({ expiresAtUtc: '2026-09-24T13:00:01Z' })); let posts = 0;
  await mount(page, async route => {
    if (!isPost(route)) return false;
    if (++posts === 1) await old.handle(route); else await json(route, reportRun({ runId: otherRunId })); return true;
  });
  await generate(page).click(); const request = await old.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget); await aborted;
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(target);
  await expect(generate(page)).toBeEnabled(); await generate(page).click();
  await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${otherRunId}/html`);
  await old.release(); await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${otherRunId}/html`);
  await expect(expired(page)).toHaveCount(0); expect(contexts.get(page).requests).toHaveLength(2);
});

test('unmounting cancels pending generation and prevents late receipt recovery after remount', async ({ page }) => {
  const old = heldResponse(reportRun()); let posts = 0;
  await mount(page, async route => {
    if (!isPost(route)) return false;
    if (++posts === 1) await old.handle(route); else await json(route, reportRun({ runId: otherRunId })); return true;
  });
  await generate(page).click(); const request = await old.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await page.getByRole('button', { name: 'Unmount reports panel', exact: true }).click(); await aborted;
  await page.getByRole('button', { name: 'Mount reports panel', exact: true }).click(); await old.release();
  await expect(htmlLink(page)).toHaveCount(0); await expect(generate(page)).toBeEnabled();
  await generate(page).click(); await expect(htmlLink(page)).toHaveAttribute('href', `${postPath(target)}/${otherRunId}/html`);
  expect(contexts.get(page).requests).toHaveLength(2);
});

for (const width of [320, 390, 768, 1440]) test(`expired report recovery is accessible in dark layout at ${width}px`, async ({ page }, info) => {
  await expiryClock(page); await page.setViewportSize({ width, height: 1000 });
  await mount(page, async route => {
    if (!isPost(route)) return false;
    await json(route, reportRunForRequest(route.request().postDataJSON(), { expiresAtUtc: '2026-09-24T13:00:00.000001+00:00' })); return true;
  }, reportHref({ reportKind: 'performance-window', from: '2026-08-01T13:45:00.123456Z', to: '2026-08-02T13:45:00.654321Z' }));
  await generate(page).focus(); await page.keyboard.press('Enter'); await expect(htmlLink(page)).toBeVisible();
  await page.clock.runFor(1); await expect(expired(page)).toBeVisible();
  await expect(htmlLink(page)).toHaveCount(0); await expect(csvLink(page)).toHaveCount(0);
  await expect(panel(page).locator('time').nth(0)).toHaveAttribute('datetime', '2026-08-01T13:45:00.123456Z');
  await expect(panel(page).locator('time').nth(1)).toHaveAttribute('datetime', '2026-08-02T13:45:00.654321Z');
  await generateNew(page).focus(); await expect(generateNew(page)).toBeFocused();
  const outline = await generateNew(page).evaluate(element => ({ style: getComputedStyle(element).outlineStyle,
    width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(outline.style).not.toBe('none'); expect(outline.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  // Exact expiry assertions are complete; axe needs asynchronous browser timers.
  await page.clock.resume();
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`report-expiry-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-report-expiry/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await page.screenshot({ path: `${directory}/report-expiry-${width}.png`, fullPage: true });
});
