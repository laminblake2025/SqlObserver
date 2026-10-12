import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { editStartsUtc, editEndsUtc, maintenanceEditId } from '../tools/maintenance-edit-fixture.mjs';
import { waitCaptureUtc, waitCaptureId, waitSessionIdentity } from '../tools/wait-captured-session-fixture.mjs';

const origin = 'http://127.0.0.1:4325';
const first = '11111111-1111-4111-8111-000000000001', second = '11111111-1111-4111-8111-000000000002';
const fixedNow = new Date('2026-10-02T12:00:00.000Z');
const href = (page, target = first, extra = {}) => '/#/' + page + '?' + new URLSearchParams({ target, range: '1h', ...extra });
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const editor = page => page.getByRole('form', { name: 'Edit maintenance window', exact: true });
const windows = page => page.getByRole('region', { name: 'Configured maintenance windows', exact: true });
const captured = page => page.getByRole('region', { name: 'Captured sessions by server wait category', exact: true });
async function fixture(page, extra = {}) {
  await page.clock.install({ time: fixedNow });
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', chartReferenceRole: 'viewer',
    maintenanceEditFixture: '1', maintenanceEditSession: crypto.randomUUID(), waitCapturedSessionFixture: '1', ...extra
  }).map(([name, value]) => ({ name, value, url: origin })));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
}
async function openEditor(page) {
  await page.getByRole('button', { name: 'Edit window', exact: true }).click();
  await expect(editor(page)).toBeVisible();
  await expect(editor(page).getByRole('textbox', { name: 'Edit reason', exact: true })).toBeFocused();
}
test.beforeEach(async ({}, testInfo) => {
  await testInfo.attach('workflow-evidence', { body: JSON.stringify({ source: 'Synthetic native metadata and administrative receipts; no SQL execution or connected VM data', executedAtUtc: new Date().toISOString(), captureUtc: waitCaptureUtc }), contentType: 'application/json' });
});

test('maintenance editing is explicit and retry retains operation revision and untouched microsecond bounds', async ({ page }) => {
  await fixture(page, { maintenanceEditMode: 'retry' }); const puts = [];
  page.on('request', request => { if (request.method() === 'PUT') puts.push(request.postDataJSON()); });
  await page.goto(href('maintenance', first, { windowId: maintenanceEditId() }));
  await expect(windows(page)).toContainText('SYNTHETIC precise maintenance A');
  await expect(editor(page)).toHaveCount(0); expect(puts).toEqual([]);
  await openEditor(page);
  await editor(page).getByRole('button', { name: 'Close editor', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Edit window', exact: true })).toBeFocused();
  await openEditor(page); await editor(page).getByRole('textbox', { name: 'Edit reason', exact: true }).fill('SYNTHETIC reason-only update');
  await editor(page).getByRole('button', { name: 'Save window changes', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The maintenance change could not be saved.' })).toBeVisible();
  await expect(editor(page)).toBeVisible();
  await editor(page).getByRole('button', { name: 'Save window changes', exact: true }).click();
  await expect(windows(page)).toContainText('SYNTHETIC reason-only update'); await expect(editor(page)).toHaveCount(0);
  expect(puts).toHaveLength(2); expect(puts[1]).toEqual(puts[0]);
  expect(puts[0]).toMatchObject({ windowId: maintenanceEditId(), expectedRevision: 7, startsAtUtc: editStartsUtc, endsAtUtc: editEndsUtc, reason: 'SYNTHETIC reason-only update' });
  expect(puts[0].operationId).toMatch(/^[0-9a-f-]{36}$/i); expect(scope(page).get('windowId')).toBe(maintenanceEditId());
});

test('maintenance conflicts require refreshed evidence and cancelled or Viewer windows stay read-only', async ({ page }) => {
  await fixture(page, { maintenanceEditMode: 'conflict' }); let mutations = 0;
  page.on('request', request => { if (!['GET', 'HEAD'].includes(request.method())) mutations++; });
  await page.goto(href('maintenance', first, { windowId: maintenanceEditId() })); await openEditor(page);
  await editor(page).getByRole('textbox', { name: 'Edit reason', exact: true }).fill('SYNTHETIC conflicted edit');
  await editor(page).getByRole('button', { name: 'Save window changes', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'This window changed. Refresh it before trying again.' })).toBeVisible();
  await expect(windows(page)).not.toContainText('SYNTHETIC conflicted edit');
  await expect(editor(page).getByRole('button', { name: 'Save window changes', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Refresh maintenance windows', exact: true }).click();
  await expect(editor(page)).toHaveCount(0); expect(mutations).toBe(1);
  await page.goto(href('maintenance', first, { windowId: maintenanceEditId(0, 3) }));
  await expect(page.getByText('SYNTHETIC cancelled maintenance', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit window', exact: true })).toHaveCount(0);
  await page.context().addCookies([{ name: 'maintenanceEditRole', value: 'viewer', url: origin }]); await page.reload();
  await page.goto(href('maintenance', first, { windowId: maintenanceEditId() }));
  await expect(windows(page)).not.toContainText('SYNTHETIC precise maintenance A');
  await expect(page.getByRole('alert').filter({ hasText: 'Maintenance access is unavailable. Restore Target Administrator access and retry.' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit window', exact: true })).toHaveCount(0); expect(mutations).toBe(1);
});

test('late maintenance saves cannot overwrite a different selection target or revoked role', async ({ page }) => {
  await fixture(page);
  for (const change of ['selection', 'target', 'history', 'role']) {
    await page.context().addCookies([{ name: 'maintenanceEditRole', value: 'administrator', url: origin }]);
    if (change === 'history') await page.goto(href('maintenance', first, { windowId: maintenanceEditId(0, 2) }));
    await page.goto(href('maintenance', first, { windowId: maintenanceEditId() })); await page.reload(); await openEditor(page);
    await editor(page).getByRole('textbox', { name: 'Edit reason', exact: true }).fill('SYNTHETIC obsolete ' + change);
    let release, started = false, returned = false; const gate = new Promise(resolve => { release = resolve; });
    const routePattern = `**/observation-targets/${first}/alerts/maintenance/${maintenanceEditId()}`;
    await page.route(routePattern, async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      started = true; await gate;
      await route.fulfill({ status: 200, contentType: 'application/json', json: { auditId: '89898989-8989-4989-8989-898989898989', recordedAtUtc: '2026-10-02T12:00:01.000Z' } }).catch(() => {}); returned = true;
    });
    try {
      await editor(page).getByRole('button', { name: 'Save window changes', exact: true }).click(); await expect.poll(() => started).toBe(true);
      if (change === 'role') {
        await page.context().addCookies([{ name: 'maintenanceEditRole', value: 'viewer', url: origin }]);
        await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
        await expect(page.getByRole('button', { name: 'Edit window', exact: true })).toHaveCount(0);
      } else if (change === 'history') {
        await page.goBack();
      } else {
        await page.evaluate(hash => { location.hash = hash; }, href('maintenance', change === 'target' ? second : first, { windowId: maintenanceEditId(change === 'target' ? 1 : 0, 2) }).slice(1));
      }
      await expect(editor(page)).toHaveCount(0);
    } finally { release(); }
    await expect.poll(() => returned).toBe(true); await page.unroute(routePattern);
    await expect(windows(page)).not.toContainText('SYNTHETIC obsolete ' + change);
    expect(scope(page).get('target')).toBe(change === 'target' ? second : first);
    expect(scope(page).get('windowId')).toBe(change === 'role' ? maintenanceEditId() : maintenanceEditId(change === 'target' ? 1 : 0, 2));
  }
});

test('native captured wait categories preserve database scope paging and exact metadata inspection across reload', async ({ page }) => {
  await fixture(page); const reads = [], protectedReads = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (/\/activity\/live$/.test(url.pathname)) reads.push(url);
    if (url.pathname.endsWith('/activity/live/query')) protectedReads.push(url);
  });
  await page.goto(href('waits')); await expect(captured(page)).toContainText('3 matching requests of 7 loaded');
  await expect(captured(page)).toContainText('2 metadata pages');
  expect(reads[0].searchParams.get('includeIdle')).toBe('true'); expect(reads[0].searchParams.get('includeSystem')).toBe('true');
  expect(reads[1].searchParams.get('snapshot')).toBe(waitCaptureId()); expect(reads[1].searchParams.get('cursor')).toBe('d2FpdDoy');
  const category = captured(page).getByRole('combobox', { name: 'Session wait category', exact: true });
  for (const [name, native] of [['I/O', 'PAGEIOLATCH_SH'], ['Parallelism', 'CXCONSUMER'], ['Idle', 'SLEEP_TASK']]) {
    await category.selectOption(name); await expect(captured(page).getByRole('table')).toContainText(native);
    await expect(captured(page).getByRole('table').locator('tbody tr')).toHaveCount(1);
    expect(scope(page).get('sessionWaitCategory')).toBe(name); expect(scope(page).has('waitCategory')).toBe(false);
  }
  await page.reload(); await expect(category).toHaveValue('Idle'); await expect(captured(page).getByRole('table')).toContainText('SLEEP_TASK');
  await category.selectOption('Lock');
  const inspect = captured(page).getByRole('link', { name: 'Inspect captured session 72 / request 0', exact: true });
  const link = new URLSearchParams((await inspect.getAttribute('href')).split('?')[1]);
  expect(link.get('sessionSnapshot')).toBe(waitCaptureId()); expect(link.get('sessionIdentity')).toBe(waitSessionIdentity(72));
  expect(link.get('sessionObserved')).toBe(waitCaptureUtc); expect(link.get('range')).toBe('1h');
  await inspect.click(); await expect(page.getByRole('region', { name: 'Session details', exact: true })).toContainText('72');
  await page.reload(); await expect(page.getByRole('region', { name: 'Session details', exact: true })).toContainText('72');
  expect(protectedReads).toEqual([]);
  await page.goto(href('waits', first, { databaseId: '6' }));
  await expect(captured(page)).toContainText('1 matching requests of 1 loaded');
  await expect(captured(page).getByRole('table')).toContainText('Warehouse'); await expect(captured(page).getByRole('table')).not.toContainText('Orders');
  await expect(captured(page)).toContainText('server wait counters remain server-wide');
  expect(reads.at(-1).searchParams.get('databaseId')).toBe('6'); expect(protectedReads).toEqual([]);
  const beforeInvalid = reads.length;
  await page.goto(href('waits', first, { databaseId: '6', sessionWaitCategory: 'Unknown' }));
  await expect(captured(page)).toContainText('The requested session wait category is invalid or duplicated.');
  await expect(captured(page).getByRole('table')).toHaveCount(0); expect(reads.length).toBe(beforeInvalid);
  await category.selectOption('Lock'); await expect(captured(page)).toContainText('1 matching requests of 1 loaded');
});

test('captured wait evidence reports truncation bounds unavailable history retention and errors without substituting sessions', async ({ page }) => {
  await fixture(page); const reads = [];
  page.on('request', request => { if (/\/activity\/live(?:\/history)?$/.test(new URL(request.url()).pathname)) reads.push(request.url()); });
  for (const [mode, text] of [['truncated', 'Capture is truncated; missing requests may have matching waits.'],
    ['stale', 'stale evidence'], ['bounded', 'Metadata paging stopped at the 11-page / 512-row bound; coverage is partial.']]) {
    await page.context().addCookies([{ name: 'waitCapturedSessionMode', value: mode, url: origin }]); await page.goto(href('waits')); await page.reload();
    const before = reads.length; await expect(captured(page)).toContainText(text);
    if (mode === 'bounded') { await expect(captured(page)).toContainText('512 loaded'); await expect(captured(page)).toContainText('11 metadata pages'); expect(reads.length - before).toBeLessThanOrEqual(11); }
  }
  await page.context().addCookies([{ name: 'waitCapturedSessionMode', value: 'error', url: origin }]); await page.reload();
  await expect(captured(page).getByRole('alert')).toContainText('Collection evidence is unavailable');
  await expect(captured(page).getByRole('table')).toHaveCount(0);
  await page.context().addCookies([{ name: 'waitCapturedSessionMode', value: 'current', url: origin }]);
  await captured(page).getByRole('button', { name: 'Retry captured sessions', exact: true }).click();
  await expect(captured(page)).toContainText('3 matching requests of 7 loaded');
  await page.goto(href('waits', first, { range: 'custom', from: '2026-10-02T11:00:00.000001Z', to: '2026-10-02T12:00:00.000000Z' }));
  await expect(captured(page).getByRole('combobox', { name: 'Retained session capture', exact: true })).toHaveValue(waitCaptureId());
  await expect(captured(page)).toContainText('Selected retained capture');
  await page.context().addCookies([{ name: 'waitCapturedSessionMode', value: 'boundary', url: origin }]); await page.reload();
  const captureSelector = captured(page).getByRole('combobox', { name: 'Retained session capture', exact: true });
  await expect(captureSelector).toHaveValue(waitCaptureId()); await expect(captureSelector.getByRole('option')).toHaveCount(1);
  await expect(captured(page)).toContainText('1 retained captures offered'); await expect(captured(page).getByRole('table')).toContainText('LCK_M_S');
  await expect(captured(page).getByRole('alert')).toHaveCount(0);
  await page.goto(href('waits', first, { range: 'custom', from: '2026-10-02T11:30:00.1234560Z', to: '2026-10-02T11:30:00.1234570Z' }));
  await expect(captureSelector).toHaveValue(waitCaptureId()); await expect(captureSelector.getByRole('option')).toHaveCount(1);
  await expect(captured(page)).toContainText('Selected retained capture'); await expect(captured(page).getByRole('alert')).toHaveCount(0);
  await page.context().addCookies([{ name: 'waitCapturedSessionMode', value: 'empty', url: origin }]); await page.reload();
  await expect(captured(page)).toContainText('No retained native session capture is available inside this window. The current capture has not been substituted.');
  await expect(captured(page).getByRole('table')).toHaveCount(0);
  const beforeExpired = reads.length;
  await page.goto(href('waits', first, { range: 'custom', from: '2026-09-26T11:00:00.000001Z', to: '2026-09-26T12:00:00.000000Z' }));
  await expect(captured(page)).toContainText('outside the 24-hour session capture retention');
  await expect(captured(page).getByRole('table')).toHaveCount(0); expect(reads.length).toBe(beforeExpired);
  await page.context().addCookies([{ name: 'waitCapturedSessionMode', value: 'expired', url: origin }]);
  await page.goto(href('waits'));
  await expect(captured(page)).toContainText('This captured snapshot has expired beyond the 24-hour retention.');
  await expect(captured(page).getByRole('table')).toContainText('LCK_M_X');
  await expect(captured(page).getByRole('link', { name: /^Inspect captured session/ })).toHaveCount(0);
  await expect(captured(page)).toContainText('inspection links are unavailable');
});

test('delayed captured sessions cannot republish an old server or historical window', async ({ page }) => {
  await fixture(page); let release, started = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  const pattern = `**/observation-targets/${first}/activity/live?*`;
  await page.route(pattern, async route => { started = true; await gate; await route.continue().catch(() => {}); returned = true; });
  try {
    await page.goto(href('waits')); await expect.poll(() => started).toBe(true);
    await page.goto(href('waits', second)); await expect(captured(page)).toContainText('3 matching requests of 7 loaded');
    await expect(captured(page).getByRole('table')).toContainText('Orders 02');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true); await page.unroute(pattern);
  const links = await captured(page).getByRole('link', { name: /^Inspect captured session/ }).evaluateAll(nodes => nodes.map(node => node.getAttribute('href')));
  expect(links.every(link => link.includes(second) && link.includes(waitCaptureId(1)))).toBe(true);
  const custom = { range: 'custom', from: '2026-10-02T11:00:00.000001Z', to: '2026-10-02T12:00:00.000000Z' };
  await page.goto(href('waits', second, custom)); await expect(captured(page)).toContainText('Selected retained capture');
  let releaseHistory, historyStarted = false, historyReturned = false; const historyGate = new Promise(resolve => { releaseHistory = resolve; });
  await page.route(`**/observation-targets/${second}/activity/live/history?*`, async route => {
    if (new URL(route.request().url()).searchParams.get('to') !== custom.to) return route.continue();
    historyStarted = true; await historyGate; await route.continue().catch(() => {}); historyReturned = true;
  });
  try {
    await page.getByRole('button', { name: '↻ Refresh', exact: true }).click(); await expect.poll(() => historyStarted).toBe(true);
    await page.evaluate(hash => { location.hash = hash; }, href('waits', second, { ...custom, to: '2026-10-02T11:20:00.000000Z' }).slice(1));
    await expect(captured(page)).toContainText('No retained native session capture is available inside this window.');
  } finally { releaseHistory(); }
  await expect.poll(() => historyReturned).toBe(true); await expect(captured(page).getByRole('table')).toHaveCount(0);
  await expect(captured(page)).not.toContainText('Selected retained capture');
});

async function auditAndCapture(page, testInfo, prefix, width) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
  await testInfo.attach(prefix + '-axe', { body: JSON.stringify(result.violations, null, 2), contentType: 'application/json' });
  expect(result.violations, JSON.stringify(result.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })))).toEqual([]);
  const folder = resolve('../artifacts/dashboard-workflow-completion/screenshots'); await mkdir(folder, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await page.screenshot({ path: resolve(folder, `${prefix}-${width}.png`), fullPage: true });
}
for (const width of [320, 390, 768, 1440]) {
  test(`explicit maintenance editor remains keyboard accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page);
    await page.goto(href('maintenance', first, { windowId: maintenanceEditId() })); await openEditor(page);
    await editor(page).getByRole('textbox', { name: 'Edit reason', exact: true }).fill('SYNTHETIC reason-only edit draft; exact UTC bounds retained.');
    await page.keyboard.press('Tab'); await expect(editor(page).getByRole('button', { name: 'Save window changes', exact: true })).toBeFocused();
    await auditAndCapture(page, testInfo, 'maintenance-editor', width);
  });
  test(`native captured wait sessions remain keyboard accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page, { waitCapturedSessionMode: 'truncated' });
    await page.goto(href('waits')); await expect(captured(page)).toContainText('3 matching requests of 7 loaded');
    const scroll = captured(page).getByRole('region', { name: 'Captured session wait matches', exact: true });
    await scroll.focus(); await expect(scroll).toBeFocused();
    if (width <= 768) {
      expect(await scroll.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
      await page.keyboard.press('ArrowRight');
      await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
      await scroll.evaluate(element => { element.scrollLeft = 0; });
    }
    await page.keyboard.press('Tab'); await expect(captured(page).getByRole('link', { name: 'Inspect captured session 51 / request 0', exact: true })).toBeFocused();
    await auditAndCapture(page, testInfo, 'wait-captured-sessions', width);
  });
}
