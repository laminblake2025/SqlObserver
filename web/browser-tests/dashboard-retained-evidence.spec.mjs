import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { installAlertLifecycleHistoryFixture, alertLifecycleHistoryBackend } from '../component-tests/alertLifecycleHistoryFixture.mjs';
import { retainedEvidenceNow, retainedCaptureTimes, retainedCaptureId } from '../tools/dashboard-retained-evidence-fixture.mjs';

const origin = 'http://127.0.0.1:4327';
const first = '11111111-1111-4111-8111-000000000001', second = '11111111-1111-4111-8111-000000000002';
const custom = { range: 'custom', from: '2026-10-02T10:00:00.000001Z', to: '2026-10-02T12:00:00.000000Z' };
const alertWindow = { range: 'custom', from: '2026-10-02T11:00:00.000000Z', to: '2026-10-02T12:00:00.000000Z' };
const href = (page, target = first, extra = {}) => '/#/' + page + '?' + new URLSearchParams({ target, range: '1h', ...extra });
const pin = (index = 0, capture = 1) => ({ sessionSnapshot: retainedCaptureId(index, capture), sessionObservedUtc: retainedCaptureTimes[capture] });
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const captures = page => page.getByRole('region', { name: 'Captured sessions by server wait category', exact: true });
const timeline = page => page.getByRole('region', { name: 'Investigation timeline', exact: true });
const alertAlign = (page, kind = 'fired') => timeline(page).getByRole('button', { name: `Align charts: Alert ${kind} · SYNTHETIC_BLOCKING_RULE`, exact: true }).first();
const refresh = page => page.getByRole('button', { name: '↻ Refresh', exact: true });
async function fixture(page, extra = {}) {
  await page.clock.install({ time: new Date(retainedEvidenceNow) });
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', chartReferenceRole: 'viewer', databaseScopeFixture: '1',
    retainedEvidenceFixture: '1', alertLifecycleHistoryFixture: '1', ...extra }).map(([name, value]) => ({ name, value, url: origin })));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
}
async function chooseAlerts(page) {
  await timeline(page).getByRole('combobox', { name: 'Timeline event category', exact: true }).selectOption('alert');
  const browser = timeline(page).locator('.dashboard-timeline-browser');
  await expect(browser).toHaveCount(1);
  if (!(await browser.evaluate(element => element.open))) await browser.locator('summary').click();
}
async function openCoverage(page) {
  const details = timeline(page).getByText('Timeline coverage', { exact: true }).locator('xpath=parent::details');
  if (!(await details.evaluate(node => node.open))) await details.locator('summary').click();
  return details;
}
test.beforeEach(async ({}, info) => {
  await info.attach('retained-evidence-provenance', { body: JSON.stringify({ source: 'Isolated synthetic native session metadata and current alert inbox; no SQL execution or connected VM data', executedAtUtc: new Date().toISOString(), repositoryTimeUtc: retainedEvidenceNow }), contentType: 'application/json' });
});

test('Waits capture selection survives exact URL reload history and refresh independently of both category controls', async ({ page }) => {
  await fixture(page); const protectedReads = [], metadataReads = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('/activity/live/query')) protectedReads.push(request.url());
    if (url.pathname.endsWith('/activity/live')) metadataReads.push(url);
  });
  await page.goto(href('waits', first, { ...custom, waitCategory: '3', sessionWaitCategory: 'Lock' }));
  const selector = captures(page).getByRole('combobox', { name: 'Retained session capture', exact: true });
  await expect(selector).toHaveValue(retainedCaptureId());
  await selector.selectOption(retainedCaptureId(0, 1));
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ ...pin(), waitCategory: '3', sessionWaitCategory: 'Lock', from: custom.from, to: custom.to });
  await expect(captures(page).getByRole('table')).toContainText('Orders / capture 2');
  await expect(captures(page)).toContainText('Capture is truncated');
  await page.reload(); await expect(selector).toHaveValue(retainedCaptureId(0, 1));
  await expect(captures(page)).toContainText('3 matching requests of 7 loaded');
  await selector.selectOption(retainedCaptureId(0, 0)); await expect(captures(page).getByRole('table')).toContainText('Orders / capture 1');
  await page.goBack(); await expect(selector).toHaveValue(retainedCaptureId(0, 1));
  await expect(captures(page).getByRole('table')).toContainText('Orders / capture 2');
  await page.goForward(); await expect(selector).toHaveValue(retainedCaptureId());
  await captures(page).getByRole('combobox', { name: 'Session wait category', exact: true }).selectOption('I/O');
  await expect(captures(page).getByRole('table')).toContainText('PAGEIOLATCH_SH');
  expect(scope(page).get('sessionSnapshot')).toBe(retainedCaptureId());
  await page.getByRole('combobox', { name: 'Query Store category', exact: true }).selectOption('6');
  await expect.poll(() => scope(page).get('waitCategory')).toBe('6');
  expect(scope(page).get('sessionWaitCategory')).toBe('I/O'); expect(scope(page).get('sessionSnapshot')).toBe(retainedCaptureId());
  await expect(captures(page).getByRole('table')).toContainText('PAGEIOLATCH_SH');
  const before = metadataReads.length; await refresh(page).click();
  await expect.poll(() => metadataReads.length).toBeGreaterThan(before);
  await expect(captures(page).getByRole('table')).toContainText('Orders / capture 1');
  expect(scope(page).get('sessionObservedUtc')).toBe(retainedCaptureTimes[0]);
  expect(protectedReads).toEqual([]);
});

test('Waits restores an older capture omitted from the hundred offered choices through bounded pinned metadata', async ({ page }) => {
  await fixture(page, { retainedCaptureMode: 'history-bound' }); const reads = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live')) reads.push(new URL(request.url())); });
  await page.goto(href('waits', first, { ...custom, ...pin(0, 2), databaseId: '6', sessionWaitCategory: 'Lock', waitCategory: '6' }));
  await expect(captures(page)).toContainText('100 retained captures offered; 3 older captures omitted');
  await expect(captures(page)).toContainText('The requested capture is not in the offered history list. Its exact identity is retained');
  await expect(captures(page).getByRole('combobox', { name: 'Retained session capture', exact: true })).toHaveValue(retainedCaptureId(0, 2));
  await expect(captures(page)).toContainText('1 matching requests of 1 loaded');
  await expect(captures(page).getByRole('table')).toContainText('Warehouse / capture 3');
  expect(reads.length).toBeGreaterThan(0); expect(reads.every(url => url.searchParams.get('snapshot') === retainedCaptureId(0, 2))).toBe(true);
  expect(reads.every(url => url.searchParams.get('databaseId') === '6')).toBe(true);
  const link = captures(page).getByRole('link', { name: 'Inspect captured session 81 / request 0', exact: true });
  const linkScope = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
  expect(linkScope.get('sessionSnapshot')).toBe(retainedCaptureId(0, 2)); expect(linkScope.get('sessionObserved')).toBe(retainedCaptureTimes[2]);
  await page.reload(); await expect(captures(page).getByRole('table')).toContainText('Warehouse / capture 3');
  await captures(page).getByRole('button', { name: 'Clear retained capture', exact: true }).click();
  await expect.poll(() => scope(page).has('sessionSnapshot')).toBe(false);
  await expect(captures(page).getByRole('table')).toContainText('Warehouse / capture 1');
});

test('invalid duplicate missing shifted and expired Waits capture identities never substitute the latest capture', async ({ page }) => {
  await fixture(page); const reads = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live')) reads.push(request.url()); });
  const validPin = href('waits', first, { ...custom, ...pin() });
  for (const url of [validPin + '&sessionSnapshot=' + retainedCaptureId(), validPin + '&sessionObservedUtc=' + encodeURIComponent(retainedCaptureTimes[1]),
    href('waits', first, { ...custom, sessionSnapshot: 'invalid', sessionObservedUtc: retainedCaptureTimes[1] }),
    href('waits', first, { ...custom, sessionSnapshot: retainedCaptureId() })]) {
    const before = reads.length;
    await page.goto(url);
    await expect(captures(page)).toContainText('The requested session capture is invalid, duplicated, or outside the selected window.');
    await expect(captures(page).getByRole('table')).toHaveCount(0); expect(reads.length).toBe(before);
  }
  await page.goto(href('waits', first, { ...custom, sessionSnapshot: '74747474-7474-4747-8747-747474747474', sessionObservedUtc: retainedCaptureTimes[1] }));
  await expect(captures(page)).toContainText('The session capture is unavailable or expired. No current capture has been substituted');
  await expect(captures(page).getByRole('table')).toHaveCount(0);
  await page.context().addCookies([{ name: 'retainedCaptureMode', value: 'time-shift', url: origin }]);
  await page.goto(href('waits', first, { ...custom, ...pin() }));
  await expect(captures(page).getByRole('alert')).toContainText('Session snapshot did not match the requested capture and observation time.');
  await expect(captures(page).getByRole('table')).toHaveCount(0);
  const beforeExpired = reads.length;
  await page.goto(href('waits', first, { range: 'custom', from: '2026-10-01T10:00:00.000001Z', to: '2026-10-02T10:00:00.000000Z',
    sessionSnapshot: retainedCaptureId(), sessionObservedUtc: '2026-10-01T11:00:00.123456Z' }));
  await expect(captures(page).getByRole('alert')).toContainText('The requested session capture has expired beyond the 24-hour retention.');
  await expect(captures(page).getByRole('table')).toHaveCount(0); expect(reads.length).toBe(beforeExpired);
});

test('delayed pinned Waits metadata cannot publish after target or exact window changes', async ({ page }) => {
  await fixture(page); let release, started = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  const pattern = `**/observation-targets/${first}/activity/live?*`;
  await page.route(pattern, async route => { started = true; await gate; await route.continue().catch(() => {}); returned = true; });
  try {
    await page.goto(href('waits', first, { ...custom, ...pin() })); await expect.poll(() => started).toBe(true);
    await page.evaluate(hash => { location.hash = hash; }, href('waits', second, { ...custom, ...pin(1) }).slice(1));
    await expect(captures(page).getByRole('table')).toContainText('Orders 02 / capture 2');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true); await page.unroute(pattern);
  await expect(captures(page).getByRole('table')).not.toContainText('Orders / capture 2');
  let releaseWindow, windowStarted = false, windowReturned = false;
  const windowGate = new Promise(resolve => { releaseWindow = resolve; });
  const secondPattern = `**/observation-targets/${second}/activity/live?*`;
  await page.route(secondPattern, async route => { windowStarted = true; await windowGate; await route.continue().catch(() => {}); windowReturned = true; });
  try {
    await refresh(page).click(); await expect.poll(() => windowStarted).toBe(true);
    await page.evaluate(hash => { location.hash = hash; }, href('waits', second, { ...custom, to: '2026-10-02T11:10:00.000000Z', ...pin(1) }).slice(1));
    await expect(captures(page)).toContainText('The requested session capture is invalid, duplicated, or outside the selected window.');
  } finally { releaseWindow(); }
  await expect.poll(() => windowReturned).toBe(true); await expect(captures(page).getByRole('table')).toHaveCount(0);
});

test('retained lifecycle markers preserve resolved and refired cycles in database context', async ({ page }) => {
  await fixture(page); const reads=[],mutations=[];
  page.on('request',request=>{if(request.url().includes('/alerts/history'))reads.push(new URL(request.url()));if(!['GET','HEAD'].includes(request.method()))mutations.push(request.url());});
  await page.goto(href('health',first,{...alertWindow,databaseId:'6',compare:'1',healthSection:'summary'}));await chooseAlerts(page);
  await expect(timeline(page).getByRole('button',{name:/^Align charts: Alert /})).toHaveCount(16);
  await expect(alertAlign(page,'resolved')).toBeVisible();await alertAlign(page,'resolved').focus();await page.keyboard.press('Enter');
  await expect(alertAlign(page,'resolved')).toHaveAttribute('aria-pressed','true');
  await expect(page.getByRole('region',{name:'Selected alert lifecycle event',exact:true})).toContainText('Alert resolved');
  const saved=scope(page).get('timelineEvent');expect(saved).toMatch(/^alert-history:/);await page.reload();await chooseAlerts(page);
  expect(scope(page).get('timelineEvent')).toBe(saved);await expect(alertAlign(page,'resolved')).toHaveAttribute('aria-pressed','true');
  const coverage=await openCoverage(page);await expect(coverage).toContainText('server-wide');
  expect(reads.some(url=>/history\/[a-f0-9-]{36}$/.test(url.pathname))).toBe(true);expect(reads.every(url=>!url.searchParams.has('databaseId'))).toBe(true);expect(mutations).toEqual([]);
});

test('retained marker pages cache visited rows and reject duplicate or shifted receipts honestly',async({page})=>{
  await fixture(page);const session=await installAlertLifecycleHistoryFixture(page,{origin,backend:alertLifecycleHistoryBackend({count:101})});
  await page.goto(href('health',first,{...alertWindow,healthSection:'summary'}));await chooseAlerts(page);
  const panel=page.getByRole('region',{name:'Alert lifecycle history',exact:true});await panel.getByText('Browse alert lifecycle events',{exact:true}).click();
  await expect(panel).toContainText('100 received of 101');await panel.getByRole('button',{name:'Next alert history page',exact:true}).click();await expect(panel).toContainText('101 received of 101');
  const before=session.reads.length,markerCount=await timeline(page).getByRole('button',{name:/^Align charts: Alert /}).count();await panel.getByRole('button',{name:'First alert history page',exact:true}).click();await panel.getByRole('button',{name:'Next alert history page',exact:true}).click();expect(session.reads).toHaveLength(before);
  session.handler=async(route,item)=>{const value=session.backend.read(item.binding);value.targetId=second;await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(value)});return true;};
  await panel.getByRole('button',{name:'Refresh alert history',exact:true}).click();await expect(panel.getByRole('alert')).toContainText('invalid or mismatched receipt');
  await expect(panel).toContainText('101 received of 101');await expect(timeline(page).getByRole('button',{name:/^Align charts: Alert /})).toHaveCount(markerCount);
});

test('late retained alert reads cannot publish after server or window changes',async({page})=>{
  await fixture(page);let release,started=false,returned=false;const gate=new Promise(resolve=>{release=resolve;});const pattern=`**/observation-targets/${first}/alerts/history?*`;
  await page.route(pattern,async route=>{started=true;await gate;await route.continue().catch(()=>{});returned=true;});
  try{await page.goto(href('health',first,{...alertWindow,healthSection:'summary'}));await expect.poll(()=>started).toBe(true);
    await page.evaluate(hash=>{location.hash=hash;},href('health',second,{...alertWindow,healthSection:'summary'}).slice(1));await chooseAlerts(page);await expect(alertAlign(page,'resolved')).toBeVisible();
  }finally{release();}await expect.poll(()=>returned).toBe(true);await page.unroute(pattern);
  const selected=alertAlign(page,'resolved');await selected.click();await expect(page.getByRole('region',{name:'Selected alert lifecycle event',exact:true})).toContainText('Alert resolved');
  expect(scope(page).get('target')).toBe(second);expect(scope(page).get('timelineEvent')).toMatch(/^alert-history:/);
  await page.evaluate(hash=>{location.hash=hash;},href('health',second,{...alertWindow,from:'2026-10-02T11:30:00.000000Z',healthSection:'summary'}).slice(1));await chooseAlerts(page);
  await expect(page.getByRole('region',{name:'Selected alert lifecycle event',exact:true})).toHaveCount(0);await expect(alertAlign(page,'resolved')).toBeVisible();
});

test('one native seventh-zero custom URL preserves identical exact request bounds across Dashboard Waits and Resources', async ({ page }) => {
  await fixture(page); const requested = [];
  page.on('request', request => requested.push(new URL(request.url())));
  const window = { range: 'custom', from: '2026-10-02T10:00:00.1234560Z', to: '2026-10-02T11:59:00.6543210Z' };
  const normalized = { fromUtc: '2026-10-02T10:00:00.123456Z', toUtc: '2026-10-02T11:59:00.654321Z' };
  for (const destination of ['health', 'waits', 'resources']) {
    const before = requested.length;
    await page.goto(href(destination, first, { ...window, ...(destination === 'health' ? { healthSection: 'summary' } : {}) }));
    await expect.poll(() => requested.slice(before).some(url => destination === 'waits' ? url.pathname.endsWith('/activity/waits/trend') : url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first)).toBe(true);
    const api = requested.slice(before).find(url => destination === 'waits' ? url.pathname.endsWith('/activity/waits/trend') : url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first);
    expect(api.searchParams.get('fromUtc')).toBe(normalized.fromUtc); expect(api.searchParams.get('toUtc')).toBe(normalized.toUtc);
    expect(scope(page).get('from')).toBe(window.from); expect(scope(page).get('to')).toBe(window.to);
    const edit = page.getByRole('button', { name: 'Edit UTC range', exact: true }); await edit.click();
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
    await expect.poll(() => scope(page).get('from')).toBe(normalized.fromUtc); expect(scope(page).get('to')).toBe(normalized.toUtc);
  }
});

async function auditAndCapture(page, info, prefix, width) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
  await info.attach(prefix + '-axe', { body: JSON.stringify(result.violations, null, 2), contentType: 'application/json' });
  expect(result.violations, JSON.stringify(result.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) })))).toEqual([]);
  const folder = resolve('../artifacts/dashboard-retained-evidence/screenshots'); await mkdir(folder, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await page.screenshot({ path: resolve(folder, `${prefix}-${width}.png`), fullPage: true });
}
for (const width of [320, 390, 768, 1440]) {
  test(`pinned omitted Waits capture stays accessible and locally scrollable at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page, { retainedCaptureMode: 'history-bound' });
    await page.goto(href('waits', first, { ...custom, ...pin(0, 1) }));
    await expect(captures(page).getByRole('table')).toContainText('Orders / capture 2');
    await expect(captures(page)).toContainText('Its exact identity is retained');
    await expect(captures(page)).toContainText('Capture is truncated');
    const select = captures(page).getByRole('combobox', { name: 'Retained session capture', exact: true });
    await select.focus(); await expect(select).toBeFocused();
    const scroll = captures(page).getByRole('region', { name: 'Captured session wait matches', exact: true });
    await scroll.focus(); await expect(scroll).toBeFocused();
    if (width <= 768) {
      expect(await scroll.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
      await page.keyboard.press('ArrowRight'); await expect.poll(() => scroll.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
      await scroll.evaluate(element => { element.scrollLeft = 0; });
    }
    await page.keyboard.press('Tab'); await expect(captures(page).getByRole('link', { name: 'Inspect captured session 51 / request 0', exact: true })).toBeFocused();
    await auditAndCapture(page, info, 'waits-pinned-capture', width);
  });
  test(`retained alert lifecycle markers retain exact keyboard evidence and responsive coverage at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 }); await fixture(page);
    await page.goto(href('health', first, { ...alertWindow, databaseId: '6', healthSection: 'summary' })); await chooseAlerts(page);
    await expect(alertAlign(page)).toBeVisible(); await alertAlign(page).focus(); await page.keyboard.press('Enter');
    await expect(alertAlign(page)).toBeFocused(); await expect(alertAlign(page)).toHaveAttribute('aria-pressed', 'true');
    await page.reload(); await chooseAlerts(page); await expect(alertAlign(page)).toHaveAttribute('aria-pressed', 'true');
    await openCoverage(page); await expect(timeline(page)).toContainText('server-wide');
    await expect(page.getByRole('region',{name:'Selected alert lifecycle event',exact:true})).toContainText('Exact lookup does not substitute the newest cycle');
    await auditAndCapture(page, info, 'dashboard-alert-markers', width);
  });
}
