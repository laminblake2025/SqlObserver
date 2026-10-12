import { revealHealthSupport, revealDashboardSupport, expandDisclosure } from './redesign-controls.mjs';
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { nativeTargetAlertCursor } from '../tools/alert-selection-fixture.mjs';

const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.000Z';
const to = '2026-09-26T12:00:00.000Z';
const origin = 'http://127.0.0.1:4432';
const firstAlert = '44444444-4444-4444-8444-444444444444';
const pagedAlert = '66666666-6666-4666-8666-666666666666';
const href = (surface = 'overview', target = first, range = 'custom') =>
  `/#/${surface}?${new URLSearchParams({ target, range, ...(range === 'custom' ? { from, to } : {}) })}`;
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);

async function ensureUtcDisplay(page) {
  const toggle = page.getByRole('button', { name: 'Show UTC time', exact: true });
  if (await toggle.count()) await toggle.click();
}

async function fixture(page, role = 'viewer') {
  await page.context().addCookies([
    { name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: role, url: origin },
  ]);
}
async function open(page, surface = 'overview', target = first, range = 'custom') {
  await fixture(page);
  await page.goto(href(surface, target, range));
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue(range);
  if(surface==='health')await revealHealthSupport(page);
}
async function assertHistoricalScope(page, target = first, start = from, end = to) {
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ target, range: 'custom', from: start, to: end });
}
const mainNavigation = page => page.locator('nav');
const fleetStrip = page => page.getByRole('navigation', { name: 'Loaded server status', exact: true });
const sessionSnapshot = '88888888-8888-4888-8888-888888888888';
const sessionObserved = '2026-09-26T11:30:00.123456Z';

function nativeInteractionTarget(instanceId, overrides = {}) {
  return { instanceId, instanceKey: 'synthetic-interaction', displayName: 'SYNTHETIC interaction server',
    host: 'interaction.invalid', tcpPort: 1433, authenticationMode: 'windows_integrated_service_identity',
    encryptionMode: 'mandatory_validated', lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [],
    configurationRevision: 1, discoveryRequestedAtUtc: '2026-09-25T12:00:00Z',
    lastDiscoveryAtUtc: '2026-09-25T12:01:00Z', ...overrides };
}

test('uppercase native target URLs resolve catalog and direct identities while retaining exact report scope', async ({ page }) => {
  const canonicalId = 'a1111111-b111-4c11-8d11-e00000000001';
  const selected = nativeInteractionTarget(canonicalId);
  const preciseFrom = '2026-09-26T08:00:00.123456Z', preciseTo = '2026-09-26T12:00:00.654321Z';
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  let directLookup = false;
  const directReads = [];
  await fixture(page);
  await page.route('**/api/v1/observation-targets?*', route => route.fulfill({ status: 200,
    contentType: 'application/json', json: { items: directLookup ? [] : [selected], nextCursor: null } }));
  await page.route(`**/api/v1/observation-targets/${canonicalId}`, async route => {
    directReads.push(route.request().url());
    await route.fulfill({ status: 200, contentType: 'application/json', json: selected });
  });
  await page.goto(`/#/reports?${new URLSearchParams({ target: canonicalId.toUpperCase(), range: 'custom',
    from: preciseFrom, to: preciseTo, databaseId: '6', reportKind: 'incident-evidence' })}`);
  const reports = page.getByRole('region', { name: 'Reports for SYNTHETIC interaction server', exact: true });
  let catalogDirectReads = 0;
  for (const mode of ['catalog', 'direct']) {
    if (mode === 'direct') { directLookup = true; await page.reload(); }
    await expect(reports).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Target server', exact: true })).toHaveValue(canonicalId);
    await expect(reports.getByRole('combobox', { name: 'Report type', exact: true })).toHaveValue('incident-evidence');
    await expect(reports.locator('time').nth(0)).toHaveAttribute('datetime', preciseFrom);
    await expect(reports.locator('time').nth(1)).toHaveAttribute('datetime', preciseTo);
    expect(scope(page).get('target').toLowerCase()).toBe(canonicalId);
    expect(Object.fromEntries(scope(page))).toMatchObject({ range: 'custom', from: preciseFrom, to: preciseTo,
      databaseId: '6', reportKind: 'incident-evidence' });
    await expect(page.getByRole('alert').filter({ hasText: /selected server is unavailable|target response is invalid/i })).toHaveCount(0);
    if (mode === 'catalog') catalogDirectReads = directReads.length;
    else expect(directReads.length).toBeGreaterThan(catalogDirectReads);
  }
  expect(errors).toEqual([]);
});

test('native registration replaces a case-variant loaded identity once and closes the Add server drawer', async ({ page }) => {
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.text().includes('Encountered two children with the same key')) errors.push(message.text()); });
  await fixture(page, 'admin');
  let catalogReady, requestReady, finishRegistration;
  let catalogRows = [];
  let catalogReads = 0;
  const catalog = new Promise(resolve => { catalogReady = resolve; });
  const submitted = new Promise(resolve => { requestReady = resolve; });
  const accepted = new Promise(resolve => { finishRegistration = resolve; });
  await page.route('**/api/v1/observation-targets?*', async route => {
    if (++catalogReads === 1) catalogReady(route);
    else await route.fulfill({ status: 200, contentType: 'application/json', json: { items: catalogRows, nextCursor: null } });
  });
  await page.route('**/api/v1/observation-targets', async route => {
    expect(route.request().method()).toBe('POST');
    const body = route.request().postDataJSON(); requestReady(body);
    await accepted;
    const registered = nativeInteractionTarget(body.instanceId,
      { instanceKey: body.instanceKey, displayName: body.displayName, host: body.host, tcpPort: body.tcpPort,
        configurationRevision: 2, lifecycle: 'pending_discovery', capabilityStatus: 'pending', lastDiscoveryAtUtc: null });
    catalogRows = [registered, catalogRows[1]];
    await route.fulfill({ status: 200, contentType: 'application/json', json: registered });
  });
  await page.goto('/#/servers');
  const initialCatalog = await catalog;
  await page.getByRole('button', { name: '+ Add server', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Add server', exact: true });
  await expect(drawer).toBeVisible();
  await drawer.getByRole('textbox', { name: 'Stable key', exact: true }).fill('synthetic-registered');
  await drawer.getByRole('textbox', { name: 'Display name', exact: true }).fill('SYNTHETIC accepted registration');
  await drawer.getByRole('textbox', { name: 'Windows host', exact: true }).fill('registered.invalid');
  await drawer.getByRole('button', { name: 'Register target', exact: true }).click();
  const body = await submitted;
  expect(body).toMatchObject({ instanceId: expect.stringMatching(/^[0-9a-f-]{36}$/i),
    instanceKey: 'synthetic-registered', displayName: 'SYNTHETIC accepted registration', host: 'registered.invalid', tcpPort: 1433 });
  catalogRows = [
    nativeInteractionTarget(body.instanceId.toUpperCase(), { displayName: 'SYNTHETIC previous registration' }),
    nativeInteractionTarget(first, { instanceKey: 'synthetic-unrelated', displayName: 'SYNTHETIC unrelated server' }),
  ];
  await initialCatalog.fulfill({ status: 200, contentType: 'application/json', json: { items: catalogRows, nextCursor: null } });
  const serverTable = page.getByRole('table', { includeHidden: true }).filter({
    has: page.getByRole('columnheader', { name: 'Server', exact: true, includeHidden: true }),
  });
  await expect(serverTable.getByRole('link', { name: 'SYNTHETIC previous registration', exact: true, includeHidden: true })).toHaveCount(1);
  await expect(drawer.getByRole('button', { name: 'Registering…', exact: true })).toBeDisabled();
  finishRegistration();
  await expect(drawer).not.toBeVisible();
  await expect(serverTable.getByRole('link', { name: 'SYNTHETIC accepted registration', exact: true })).toHaveCount(1);
  await expect(serverTable.getByRole('link', { name: 'SYNTHETIC previous registration', exact: true })).toHaveCount(0);
  await expect(serverTable.getByRole('link', { name: 'SYNTHETIC unrelated server', exact: true })).toHaveCount(1);
  await expect(serverTable.getByRole('row')).toHaveCount(3);
  await expect(page.locator('.kpi').filter({ hasText: 'Registered targets' }).locator('strong')).toHaveText('2');
  const acceptedHref = await serverTable.getByRole('link', { name: 'SYNTHETIC accepted registration', exact: true }).getAttribute('href');
  expect(new URLSearchParams(acceptedHref.split('?')[1]).get('target')).toBe(body.instanceId.toLowerCase());
  await serverTable.getByRole('link', { name: 'SYNTHETIC accepted registration', exact: true }).click();
  const selectedTarget = page.getByRole('combobox', { name: 'Target server', exact: true });
  await expect(selectedTarget).toHaveValue(body.instanceId.toLowerCase());
  const sameIdentityOptions = await selectedTarget.locator('option').evaluateAll((options, id) =>
    options.filter(option => option.value.toLowerCase() === id.toLowerCase()).length, body.instanceId);
  expect(sameIdentityOptions).toBe(1);
  await expect(selectedTarget.locator('option')).toHaveCount(3);
  expect(errors).toEqual([]);
});

test('deadlock-linked Activity chooses its matching native capture over a newer unrelated snapshot', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-26T12:00:00Z'));
  await fixture(page);
  const eventId = '33333333-3333-4333-8333-333333333333';
  const matchedId = '88888888-8888-4888-8888-888888888889';
  const unrelatedId = '99999999-9999-4999-8999-999999999999';
  const matchedAt = '2026-09-26T10:00:00.123456Z';
  const newerAt = '2026-09-26T10:05:00.654321Z';
  const captureFrom = '2026-09-26T09:55:00.000Z', captureTo = '2026-09-26T10:10:00.000Z';
  const reads = [], histories = [], errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const livePath = `/api/v1/observation-targets/${first}/activity/live`;
  await page.route(url => url.origin === origin && [livePath, `${livePath}/history`].includes(url.pathname), async route => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/history')) {
      histories.push(url);
      await route.fulfill({ status: 200, contentType: 'application/json', json: [
        { id: unrelatedId, observedUtc: newerAt, truncated: false, deadlockEventId: null, deadlockOccurredUtc: null },
        { id: matchedId, observedUtc: matchedAt, truncated: false, deadlockEventId: eventId,
          deadlockOccurredUtc: '2026-09-26T10:00:00.000Z' },
      ] }); return;
    }
    if (!url.pathname.endsWith('/activity/live')) { await route.continue(); return; }
    reads.push(url);
    const matched = url.searchParams.get('snapshot') === matchedId;
    const row = { identity: (matched ? 'a' : 'e').repeat(64), sessionId: matched ? 61 : 81, requestId: 0,
      databaseId: 5, databaseName: 'Orders', login: 'synthetic_dba', clientHost: 'synthetic-app.invalid',
      application: matched ? 'SYNTHETIC matched deadlock capture' : 'SYNTHETIC unrelated newer capture',
      status: 'running', command: 'SELECT', isUser: true, waitType: null, blocker: null,
      cpuMs: '1200', memoryBytes: '1048576', reads: '100', writes: '0', logicalReads: '1000', elapsedMs: '12000',
      queryId: null, queryState: 'unavailable', engineStartup: '2026-09-25T00:00:00',
      sessionLogin: '2026-09-26T00:00:00', requestStart: '2026-09-26T09:59:00', delta: null };
    await route.fulfill({ status: 200, contentType: 'application/json', json: {
      snapshotId: matched ? matchedId : unrelatedId, observedUtc: matched ? matchedAt : newerAt,
      repositoryTimeUtc: '2026-09-26T12:00:00Z', state: 'available', truncated: false,
      rows: [row], databases: [{ id: 5, name: 'Orders' }], nextCursor: null,
    } });
  });
  await page.goto(href('deadlocks'));
  await page.getByRole('region', { name: 'Selected deadlock detail', exact: true })
    .getByRole('link', { name: /View activity at event time/ }).click();
  await assertHistoricalScope(page);
  expect(scope(page).get('event')).toBe(eventId);
  const history = page.getByRole('region', { name: 'Loaded session observations', exact: true });
  await expect(history.getByRole('table')).toContainText('SYNTHETIC matched deadlock capture');
  await expect(history.getByRole('table')).not.toContainText('SYNTHETIC unrelated newer capture');
  await expect(history.getByRole('button', { name: 'Inspect 61/0', exact: true })).toBeVisible();
  expect(histories).toHaveLength(1);
  expect(Object.fromEntries(histories[0].searchParams)).toEqual({ from: captureFrom, to: captureTo });
  expect([matchedAt, newerAt].every(value => Date.parse(value) >= Date.parse(captureFrom) && Date.parse(value) < Date.parse(captureTo))).toBe(true);
  expect(reads.length).toBeGreaterThan(0);
  expect(reads.every(url => url.searchParams.get('snapshot') === matchedId)).toBe(true);
  expect(errors).toEqual([]);
});

async function captureTargetResponseFailure(page, testInfo, name) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await testInfo.attach(`${name}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-response-contracts/screenshots');
  await mkdir(directory, { recursive: true });
  await page.screenshot({ path: resolve(directory, `${name}.png`), fullPage: true });
}

test('malformed server catalog fails visibly without rendering provider text and recovers through Refresh', async ({ page }, testInfo) => {
  await fixture(page);
  await page.setViewportSize({ width: 320, height: 1000 });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  let malformed = true;
  await page.route('**/api/v1/observation-targets?*', async route => {
    if (!malformed) return route.continue();
    await route.fulfill({ status: 200, contentType: 'application/json',
      body: JSON.stringify({ items: { displayName: 'password=synthetic-secret <script>bad()</script>' }, nextCursor: null }) });
  });
  await page.goto('/#/servers');
  await expect(page.getByRole('alert').filter({ hasText: 'SqlObserver target response is invalid. Refresh and retry.' })).toBeVisible();
  await expect(page.getByRole('textbox', { name: 'Search loaded servers', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  await captureTargetResponseFailure(page, testInfo, 'server-catalog-invalid-320');
  malformed = false;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Search loaded servers', exact: true })).toBeVisible();
  await expect(page.getByRole('table').getByRole('link', { name: 'SYNTHETIC SQL 01', exact: true })).toBeVisible();
  await expect(page.getByRole('alert').filter({ hasText: 'SqlObserver target response is invalid.' })).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('foreign direct server response never opens diagnostics and a valid retry preserves the requested window', async ({ page }, testInfo) => {
  await fixture(page);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.route('**/api/v1/observation-targets?*', route => route.fulfill({ status: 200,
    contentType: 'application/json', body: JSON.stringify({ items: [], nextCursor: null }) }));
  let foreign = true;
  await page.route(`**/api/v1/observation-targets/${first}`, async route => {
    const response = await route.fetch(); const target = await response.json();
    await route.fulfill({ response, json: foreign ? { ...target, instanceId: second } : target });
  });
  await page.goto(href('resources'));
  await expect(page.getByRole('alert').filter({ hasText: 'SqlObserver target response is invalid. Refresh and retry.' })).toBeVisible();
  await expect(page.locator('.target-surface')).toHaveCount(0);
  await assertHistoricalScope(page);
  await captureTargetResponseFailure(page, testInfo, 'direct-target-invalid-1440');
  foreign = false;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(page.locator('.target-surface')).toBeVisible();
  await expect(page.locator('.page-heading')).toContainText('SYNTHETIC SQL 01');
  await assertHistoricalScope(page);
  expect(errors).toEqual([]);
});

async function resourceUrlFixture(page) {
  await databaseFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  await page.context().addCookies([{ name: 'resourceInspectionFixture', value: '1', url: origin }]);
}
async function sessionUrlFixture(page) {
  await fixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  await page.context().addCookies([{ name: 'sessionSelectionFixture', value: '1', url: origin }]);
}
const sessionHref = (identity, observed = sessionObserved, snapshot = sessionSnapshot) => `${href('activity', first, '1h')}&${new URLSearchParams({ sessionSnapshot: snapshot, sessionObserved: observed, ...(identity ? { sessionIdentity: identity } : {}) })}`;
const seededIncident = 'aaaaaaaa-aaaa-4aaa-8aaa-000000000001';
const baselineCards = page => page.getByRole('region', { name: 'Latest SQL buckets and hourly baselines', exact: true });
const cpuBaselineCard = page => baselineCards(page).getByRole('region', { name: 'SQL scheduler CPU baseline comparison', exact: true });
const attentionSummary = page => page.getByRole('region', { name: 'What needs attention', exact: true });
const planAttention = (page, databaseId) => attentionSummary(page).getByRole('link', { name: new RegExp(`^Plan regression comparison.*database ${databaseId}`) }).locator('xpath=parent::li');
async function baselineSummaryFixture(page, state = 'normal') {
  await fixture(page);
  await page.context().addCookies([{ name: 'baselineKpiFixture', value: '1', url: origin }, { name: 'dashboardBaselineState', value: state, url: origin }]);
}

test('dashboard baseline cards compare the latest bucket with its matching UTC hour and disclose source quality without extra reads', async ({ page }) => {
  await baselineSummaryFixture(page);
  const reads = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/analytics/sql-baselines')) reads.push(request.url()); });
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  const card = cpuBaselineCard(page);
  await expect(card).toHaveAttribute('data-reference-state', 'available');
  await expect(card.locator('.dashboard-baseline-value strong')).toHaveText('70');
  await expect(card.locator('dd')).toHaveText(['50 %', '55 %', '60 %']);
  await expect(card).toContainText('11:30:00');
  await expect(card).toContainText('10 samples');
  await expect(card).toContainText('Reference quality: complete.');
  const before = reads.length;
  const details = card.getByText('SQL scheduler CPU source details', { exact: true });
  await details.focus(); await details.press('Enter');
  await expect(details).toBeFocused();
  await expect(card.locator('details')).toHaveAttribute('open', '');
  await expect(card).toContainText('UTC hour 155');
  await expect(card).toContainText('source 2026-08-29T00:00:00.000Z to 2026-09-26T00:00:00.000Z');
  await expect(card).toContainText('Dimension: server-wide');
  expect(reads).toHaveLength(before);
  await expect(baselineCards(page)).toContainText('not a healthy range, an alert verdict, or proof of what was known at Rewind time');
});

test('partial dashboard baseline evidence remains labelled and a missing latest bucket never falls back to an older value', async ({ page }) => {
  await baselineSummaryFixture(page, 'partial');
  await page.context().addCookies([{ name: 'dashboardObservationState', value: 'partial', url: origin }]);
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  const card = cpuBaselineCard(page);
  await expect(card).toHaveAttribute('data-observation-state', 'partial');
  await expect(card).toContainText('Partial SQL observations; this mean describes only available samples.');
  await expect(card).toContainText('Reference quality: partial.');
  await expect(card).toContainText('Source coverage 25%.');
  await page.context().addCookies([{ name: 'dashboardObservationState', value: 'missing', url: origin }]);
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(card).toHaveAttribute('data-observation-state', 'missing');
  await expect(card).toContainText('Mean unavailable');
  await expect(card).toContainText('Latest bucket has no usable samples.');
  await expect(card.locator('.dashboard-baseline-value strong')).toHaveCount(0);
});

test('missing-hour foreign and failed dashboard baselines keep current observations but never substitute a reference', async ({ page }) => {
  await baselineSummaryFixture(page);
  for (const state of ['missing-hour', 'foreign', 'error']) {
    await page.context().addCookies([{ name: 'dashboardBaselineState', value: state, url: origin }]);
    // An identical hash URL is same-document navigation; reload to read each
    // distinct synthetic reference failure rather than reusing the prior page.
    if (state === 'missing-hour') await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
    else { await page.reload(); await revealHealthSupport(page); await revealDashboardSupport(page); }
    const card = cpuBaselineCard(page);
    await expect(card.locator('.dashboard-baseline-value strong')).toHaveText('70');
    await expect(card).toHaveAttribute('data-reference-state', 'missing');
    await expect(card).toContainText('No matching hourly reference.');
    await expect(card.locator('dd')).toHaveCount(0);
    await baselineCards(page).getByText('Baseline reference coverage', { exact: true }).click();
    await expect(baselineCards(page)).toContainText(state === 'missing-hour' ? 'SQL p10\u2013p90 bands use matching hourly history' : 'SQL baseline references are unavailable.');
  }
});

test('a delayed old-server baseline cannot populate the new server KPI cards', async ({ page }) => {
  await baselineSummaryFixture(page);
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/analytics/sql-baselines?*`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await expect.poll(() => pending).toBe(true);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  const card = cpuBaselineCard(page);
  await expect(card.locator('.dashboard-baseline-value strong')).toHaveText('80');
  await expect(card.locator('dd')).toHaveText(['55 %', '60 %', '65 %']);
  release();
  await expect(card.locator('.dashboard-baseline-value strong')).toHaveText('80');
  await expect(card.locator('dd')).toHaveText(['55 %', '60 %', '65 %']);
  await card.getByText('SQL scheduler CPU source details', { exact: true }).click();
  await expect(card).toContainText(second); await expect(card).not.toContainText(first);
});

test('a delayed old-window baseline response never overwrites the matching new-hour reference', async ({ page }) => {
  await baselineSummaryFixture(page);
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  const card = cpuBaselineCard(page);
  await expect(card.locator('dd')).toHaveText(['50 %', '55 %', '60 %']);
  let release, pending = false, reads = 0;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/analytics/sql-baselines?*`, async route => {
    const response = await route.fetch();
    if (new URL(route.request().url()).searchParams.get('metricKey') !== 'engine.sql_scheduler_cpu_percent' || ++reads !== 1) return route.fulfill({ response });
    const body = await response.json();
    const hour = body.items.find(item => item.statistics.hourOfWeek === 154).statistics;
    Object.assign(hour, { p10: 85, median: 90, p90: 95 });
    pending = true; await gate; await route.fulfill({ response, json: body });
  });
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect.poll(() => pending).toBe(true);
  await page.evaluate(hash => { location.hash = hash; }, `${href('health').substring(1)}&healthSection=summary`.replace(encodeURIComponent(from), encodeURIComponent('2026-09-26T09:00:00.000Z')).replace(encodeURIComponent(to), encodeURIComponent('2026-09-26T11:00:00.000Z')));
  await expect(card.locator('.dashboard-baseline-value strong')).toHaveText('69');
  await expect(card.locator('dd')).toHaveText(['45 %', '50 %', '55 %']);
  release();
  await expect(card.locator('dd')).toHaveText(['45 %', '50 %', '55 %']);
  await expect(card).not.toContainText('90 %');
});

test('Investigate first promotes plan comparisons with correctly scoped temporal context and evidence links', async ({ page }) => {
  await timelineFixture(page);
  await page.context().addCookies([{ name: 'investigationContextFixture', value: '1', url: origin }]);
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  const summary = attentionSummary(page), plan = planAttention(page, 6);
  await expect(plan).toBeVisible();
  await expect(summary.getByRole('link', { name: /^Plan regression comparison/ })).toHaveCount(2);
  await expect(summary.locator('.dashboard-investigation-findings > li')).not.toHaveCount(0);
  expect(await summary.locator('.dashboard-investigation-findings > li').count()).toBeLessThanOrEqual(5);
  await plan.getByText('Observed temporal context (2)', { exact: true }).press('Enter');
  await expect(plan).toContainText('Warehouse options detected');
  await expect(plan).toContainText('Server configuration detected');
  await expect(plan).not.toContainText('Reporting options detected');
  await expect(plan).not.toContainText('Orders options detected');
  await expect(plan).toContainText('Detected 8 min before this last observed query group');
  await expect(plan).toContainText('Detected 7 min before this last observed query group');
  await expect(plan).toContainText('10:52:00'); await expect(plan).toContainText('10:53:00');
  const configuration = plan.getByRole('link', { name: 'Server configuration detected', exact: true });
  const configScope = new URLSearchParams((await configuration.getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(configScope)).toMatchObject({ target: first, range: 'custom', from, to, healthSection: 'summary', configurationRun: '22222222-2222-4222-8222-222222222222' });
  const databaseScope = new URLSearchParams((await plan.getByRole('link', { name: 'Warehouse options detected', exact: true }).getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(databaseScope)).toMatchObject({ target: first, range: 'custom', from, to, databaseId: '6' });
  await expect(summary).toContainText('Temporal proximity does not establish causation or the actual change time.');
  await workspaceDatabase(page).selectOption('6');
  await expect(summary.getByRole('link', { name: /^Plan regression comparison/ })).toHaveCount(1);
  await expect(planAttention(page, 6)).toBeVisible();
  await expect(summary.getByRole('link', { name: /^Plan regression comparison/ })).toHaveAttribute('href', /queryView=regressed/);
});

test('Investigate first retains explicit source gaps and never attaches temporal context to an out-of-window current finding', async ({ page }) => {
  await timelineFixture(page);
  await page.context().addCookies([{ name: 'investigationContextFixture', value: '1', url: origin }]);
  await page.route(`**/observation-targets/${first}/health/server-configuration-changes?*`, route => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }));
  await page.route('**/api/v1/overview?*', async route => {
    if (new URL(route.request().url()).searchParams.get('targetId') !== first) return route.continue();
    const response = await route.fetch(); const body = await response.json();
    body.evidence[0].issues.unshift({ targetId: first, server: 'SYNTHETIC SQL 01', title: 'SYNTHETIC current-only exception', detail: 'Synthetic current snapshot outside selected history.', destination: 'activity', priority: 0, observedAtUtc: '2026-09-26T12:00:00.000001Z' });
    await route.fulfill({ response, json: body });
  });
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  const summary = attentionSummary(page);
  const outside = summary.getByRole('link', { name: /^SYNTHETIC current-only exception/ }).locator('xpath=parent::li');
  await expect(outside).toContainText('Latest overview evidence outside the selected history window');
  await expect(outside.locator('details')).toHaveCount(0);
  const plan = planAttention(page, 6);
  await plan.getByText('Observed temporal context (1)', { exact: true }).click();
  await expect(plan).toContainText('Warehouse options detected');
  await expect(plan).not.toContainText('Server configuration detected');
  await summary.getByText('Investigation source coverage', { exact: true }).click();
  await expect(summary).toContainText('Server configuration change markers are unavailable.');
});
async function incidentWorkspaceFixture(page, retry = false) {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'incidentWorkspaceFixture', value: '1', url: origin }, ...(retry ? [{ name: 'incidentWorkspaceRetry', value: '1', url: origin }] : [])]);
}

test('the common incident action is available across target diagnostics and closing a draft returns keyboard focus', async ({ page }) => {
  await incidentWorkspaceFixture(page);
  for (const surface of ['overview', 'health', 'activity', 'waits', 'resources', 'queries', 'deadlocks', 'operations', 'alerts']) {
    await page.goto(href(surface));
    await openInvestigationWorkspace(page);
    const open = page.getByRole('button', { name: 'Open incident', exact: true });
    await expect(open).toHaveCount(1);
    await expect(open).toBeEnabled();
    await open.focus(); await open.press('Enter');
    await expect(page.getByLabel('Incident title', { exact: true })).toBeFocused();
    await page.getByLabel('Incident title', { exact: true }).fill(`SYNTHETIC ${surface} investigation`);
    await page.getByRole('button', { name: 'Close draft', exact: true }).press('Enter');
    await expect(open).toBeFocused();
    await expect(page.getByLabel('Incident title', { exact: true })).toHaveCount(0);
    await assertHistoricalScope(page);
  }
});

test('incident save pins the original window and revision while duplicate activation and retry keep one immutable operation', async ({ page }) => {
  await incidentWorkspaceFixture(page, true);
  await page.goto(`${href('resources')}&databaseId=6`);
  await openInvestigationWorkspace(page);
  await page.getByRole('button', { name: 'Open incident', exact: true }).click();
  const title = page.getByRole('textbox', { name: 'Incident title', exact: true }), notes = page.getByRole('textbox', { name: 'Notes', exact: true });
  await title.fill('SYNTHETIC resource investigation'); await notes.fill('Synthetic note\nNo connected VM evidence.');
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  const commands = [];
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/incidents/saved`, async route => {
    if (route.request().method() !== 'POST') return route.continue();
    commands.push(route.request().postDataJSON());
    if (commands.length === 1) await gate;
    await route.fulfill({ response: await route.fetch() });
  });
  await page.getByRole('button', { name: 'Save incident', exact: true }).evaluate(element => { element.click(); element.click(); });
  await expect.poll(() => commands.length).toBe(1);
  await expect(page.getByRole('button', { name: 'Saving\u2026', exact: true })).toBeDisabled();
  await expect(title).toBeDisabled(); await expect(notes).toBeDisabled();
  expect(commands[0]).toMatchObject({ expectedTargetRevision: 1, fromUtc: from, toUtc: to, title: 'SYNTHETIC resource investigation', notes: 'Synthetic note\nNo connected VM evidence.' });
  release();
  const retry = page.getByRole('button', { name: 'Retry same save', exact: true });
  await expect(retry).toBeEnabled();
  await expect(title).toBeDisabled(); await expect(notes).toBeDisabled();
  await retry.press('Enter');
  await expect.poll(() => commands.length).toBe(2);
  expect(commands[1]).toEqual(commands[0]);
  await expect(page).toHaveURL(/#\/health\?/);
  await expect.poll(() => scope(page).get('incident')).toMatch(/^[0-9a-f-]{36}$/);
  await assertHistoricalScope(page); await assertDatabaseScope(page, 6);
  await expect(page.getByRole('heading', { name: 'SYNTHETIC resource investigation', exact: true })).toBeVisible();
  await expect(page.getByText('No analytics evidence packets existed for this window when it was saved. This does not mean the server was healthy.', { exact: true })).toBeVisible();
  const incidentId = scope(page).get('incident');
  await page.reload();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC resource investigation', exact: true })).toBeVisible();
  expect(scope(page).get('incident')).toBe(incidentId);
  await expect(page.locator('.incident-notes')).toHaveText('Synthetic note\nNo connected VM evidence.');
});

test('saved incident links are canonical and delayed old-target or foreign reads never display another investigation', async ({ page }) => {
  await incidentWorkspaceFixture(page);
  await page.goto(href('waits'));
  await openInvestigationWorkspace(page);
  await page.getByRole('button', { name: 'Saved investigations', exact: true }).click();
  const retained = page.getByRole('link', { name: 'SYNTHETIC retained investigation 01', exact: true });
  await expect(retained).toHaveAttribute('href', /#\/health\?/);
  await expect(retained).toHaveAttribute('href', new RegExp(`incident=${seededIncident}`));
  await retained.press('Enter');
  await expect(page.getByRole('heading', { name: 'SYNTHETIC retained investigation 01', exact: true })).toBeVisible();
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/incidents/saved/${seededIncident}`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.reload(); await expect.poll(() => pending).toBe(true);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await openInvestigationWorkspace(page);
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toBeEnabled();
  release();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC retained investigation 01', exact: true })).toHaveCount(0);
  await page.unroute(`**/observation-targets/${first}/incidents/saved/${seededIncident}`);
  for (const [target, id] of [[second, seededIncident], [first, 'ffffffff-ffff-4fff-8fff-ffffffffffff']]) {
    await page.goto(`${href('health', target)}&incident=${id}`);
    await expect(page.getByText('This saved investigation was not found. Automatically correlated incidents remain available in Analytics.', { exact: true })).toBeVisible();
    await expect(page.locator('.saved-incident')).toHaveCount(0);
    expect(scope(page).get('incident')).toBe(id);
  }
});

test('Viewer activity inspection exposes metadata but no protected SQL action or incident mutation', async ({ page }) => {
  await sessionUrlFixture(page);
  let protectedReads = 0, incidentWrites = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++;
    if (new URL(request.url()).pathname.endsWith('/incidents/saved') && request.method() === 'POST') incidentWrites++;
  });
  await page.goto(sessionHref('c'.repeat(64)));
  const detail = page.getByRole('region', { name: 'Session details', exact: true });
  await expect(detail.getByRole('heading', { name: 'Session 51', exact: true })).toBeVisible();
  await detail.getByRole('button', { name: 'Captured SQL', exact: true }).click();
  await expect(detail).toContainText('Captured SQL is hidden. Query Text Reader access is required for this server.');
  await expect(detail.getByRole('button', { name: 'Load captured SQL', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toHaveCount(0);
  await expect(detail.locator('pre.query-text')).toHaveCount(0);
  expect(protectedReads).toBe(0); expect(incidentWrites).toBe(0);
});

test('revoking Query Text Reader while captured SQL is pending hides the action and rejects its late plaintext', async ({ page }) => {
  await sessionUrlFixture(page);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '1', url: origin }]);
  await page.goto(sessionHref('c'.repeat(64)));
  const detail = page.getByRole('region', { name: 'Session details', exact: true });
  await detail.getByRole('button', { name: 'Captured SQL', exact: true }).click();
  const load = detail.getByRole('button', { name: 'Load captured SQL', exact: true });
  await expect(load).toBeEnabled();
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/activity/live/query?*`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await load.press('Enter'); await expect.poll(() => pending).toBe(true);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '0', url: origin }]);
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await detail.getByRole('button', { name: 'Captured SQL', exact: true }).click();
  await expect(detail).toContainText('Captured SQL is hidden. Query Text Reader access is required for this server.');
  await expect(load).toHaveCount(0);
  release();
  await expect(detail.locator('pre.query-text')).toHaveCount(0);
  await expect(detail).not.toContainText('SELECT 1 AS synthetic_fixture_only;');
  await expect(detail.getByRole('heading', { name: 'Session 51', exact: true })).toBeVisible();
});

test('Resources URL restores receipt file node and inspection while pointer stays transient and selecting a file freezes the loaded window', async ({ page }) => {
  await resourceUrlFixture(page);
  let loadedWindow;
  page.on('response', async response => {
    const url = new URL(response.url());
    if (url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first && response.ok()) {
      const body = await response.json(); loadedWindow = { from: body.fromUtc, to: body.toUtc };
    }
  });
  await page.goto(href('resources', first, '1h'));
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 01', exact: true });
  const file = resources.getByRole('button', { name: 'View read and write latency trend for database 5, file Orders_data', exact: true });
  await file.click();
  await resources.getByRole('combobox', { name: 'NUMA node', exact: true }).selectOption('1');
  await resources.getByLabel('Resource inspection time (UTC)', { exact: true }).fill('2026-09-26T11:30:00.125');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ range: 'custom', ...loadedWindow, resourceDatabaseId: '5', resourceFileId: '1', numaNodeId: '1', resourceAt: '2026-09-26T11:30:00.125Z', fileSnapshotRevision: '1' });
  expect(scope(page).get('fileSnapshotRun')).toMatch(/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/u);
  const selectedUrl = page.url();
  const cpu = resources.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true }) }).locator('.overview-chart');
  await cpu.scrollIntoViewIfNeeded(); const box = await cpu.boundingBox(); expect(box).not.toBeNull();
  await page.mouse.move(box.x + box.width * 0.4, box.y + box.height * 0.5);
  await expect(resources.locator('.chart-crosshair')).toHaveCount(9);
  expect(page.url()).toBe(selectedUrl);
  await page.reload();
  await expect(file).toHaveAttribute('aria-pressed', 'true');
  await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('1');
  await expect(resources.getByRole('region', { name: 'Resource chart alignment', exact: true }).getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T11:30:00.125Z');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(9);
  await resources.getByRole('button', { name: 'Clear resource alignment', exact: true }).click();
  await expect.poll(() => scope(page).get('resourceAt')).toBe(null);
  await page.goBack();
  await expect(resources.locator('.chart-crosshair')).toHaveCount(9);
  await page.goForward();
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  const fixed = resources.getByRole('link', { name: 'Open fixed resource investigation', exact: true });
  await expect.poll(() => loadedWindow).toBeTruthy();
  const fixedParams = new URLSearchParams((await fixed.getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(fixedParams)).toMatchObject({ target: first, range: 'custom', ...loadedWindow, resourceDatabaseId: '5', resourceFileId: '1', numaNodeId: '1' });
  expect(scope(page).get('range')).toBe('custom');
  await fixed.press('Enter');
  await assertHistoricalScope(page, first, fixedParams.get('from'), fixedParams.get('to'));
  await expect(file).toHaveAttribute('aria-pressed', 'true');
});

test('Resources missing file node and out-of-window inspection remain explicit through reload without substituting evidence', async ({ page }) => {
  await resourceUrlFixture(page);
  await page.goto(`${href('resources', first, '1h')}&resourceDatabaseId=5&resourceFileId=99&numaNodeId=99&resourceAt=2026-09-26T10%3A00%3A00.000Z`);
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 01', exact: true });
  for (let attempt = 0; attempt < 2; attempt++) {
    await expect(resources).toContainText('This exact file is not in received pages. Its numeric identity is retained; no other file is selected.');
    await expect(resources).toContainText('Requested NUMA node 99 is not in the loaded node inventory. No other node has been selected.');
    await expect(resources).toContainText('Requested resource inspection time is outside the loaded shared window.');
    await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('99');
    await expect(resources.locator('button[aria-label^="View read and write latency trend"][aria-pressed="true"]')).toHaveCount(0);
    await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
    expect(scope(page).get('resourceFileId')).toBe('99');
    if (attempt === 0) await page.reload();
  }
  await resources.getByRole('button', { name: 'Clear resource selections', exact: true }).click();
  await expect.poll(() => scope(page).get('resourceFileId')).toBe(null);
  expect(scope(page).get('numaNodeId')).toBe(null);
  expect(scope(page).get('resourceAt')).toBe(null);
  expect(scope(page).get('range')).toBe('1h');
});

test('delayed old Resources file and NUMA evidence cannot repopulate selections on another server', async ({ page }) => {
  await resourceUrlFixture(page);
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let pending = 0;
  await page.route(`**/observation-targets/${first}/resources/**`, async route => {
    const response = await route.fetch(); pending++; await gate; await route.fulfill({ response });
  });
  await page.goto(`${href('resources', first, '1h')}&resourceDatabaseId=5&resourceFileId=1&numaNodeId=1&resourceAt=2026-09-26T11%3A30%3A00.000Z`);
  await expect.poll(() => pending).toBeGreaterThanOrEqual(2);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('resourceFileId')).toBe(null);
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 02', exact: true });
  await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('0');
  release();
  const fileHistory = resources.getByRole('region', { name: 'Selected file latency history', exact: true });
  await expect(fileHistory.getByText('Select a file in the table to inspect its history.', { exact: true })).toBeVisible();
  await expect(fileHistory.locator('.overview-chart')).toHaveCount(0);
  await expect(fileHistory).not.toContainText('Orders_data');
  await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('0');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  expect(scope(page).get('numaNodeId')).toBe(null);
  expect(scope(page).get('resourceAt')).toBe(null);
});

test('Activity URL finds the exact paged request identity rather than another row sharing its SPID and protects SQL behind an explicit action', async ({ page }) => {
  await sessionUrlFixture(page);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '1', url: origin }]);
  const reads = [], protectedReads = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('/activity/live')) reads.push(url);
    if (url.pathname.endsWith('/activity/live/query')) protectedReads.push(url);
  });
  await page.goto(sessionHref('c'.repeat(64)));
  const detail = page.getByRole('region', { name: 'Session details', exact: true });
  await expect(detail.getByRole('heading', { name: 'Session 51', exact: true })).toBeVisible();
  const captureReads = reads.filter(url => url.searchParams.get('snapshot') === sessionSnapshot);
  expect(captureReads).toHaveLength(2);
  expect(new Set(captureReads.map(url => url.searchParams.get('cursor')))).toEqual(new Set([null, 'cGFnZTI=']));
  expect(protectedReads).toHaveLength(0);
  expect(scope(page).get('range')).toBe('1h');
  await page.reload();
  await expect(detail.getByRole('heading', { name: 'Session 51', exact: true })).toBeVisible();
  expect(protectedReads).toHaveLength(0);
  await detail.getByRole('button', { name: 'Captured SQL', exact: true }).click();
  await detail.getByRole('button', { name: 'Load captured SQL', exact: true }).press('Enter');
  await expect(detail.locator('pre.query-text')).toHaveText('SELECT 1 AS synthetic_fixture_only;');
  expect(protectedReads).toHaveLength(1);
  expect(protectedReads[0].searchParams.get('identity')).toBe('c'.repeat(64));
  expect(protectedReads[0].searchParams.get('snapshot')).toBe(sessionSnapshot);
});

test('Activity loaded selection and chain survive reload and history navigation without exposing protected SQL', async ({ page }) => {
  await sessionUrlFixture(page);
  let protectedReads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++; });
  await page.goto(href('activity', first, '1h'));
  const table = page.getByRole('region', { name: 'Loaded session observations', exact: true });
  await table.getByRole('button', { name: 'Inspect 51/0', exact: true }).press('Enter');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ sessionSnapshot, sessionObserved, sessionIdentity: 'a'.repeat(64), range: '1h' });
  await expandDisclosure(page, 'Advanced session filters');
  const chain = page.getByRole('combobox', { name: 'Blocking chain (capture)', exact: true });
  await chain.selectOption('session:51');
  await expect.poll(() => scope(page).get('sessionChain')).toBe('session:51');
  await page.reload(); await expandDisclosure(page, 'Advanced session filters');
  await expect(chain).toHaveValue('session:51');
  await expect(page.getByRole('region', { name: 'Session details', exact: true }).getByRole('heading', { name: 'Session 51', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Clear chain filter', exact: true }).click();
  await expect.poll(() => scope(page).get('sessionChain')).toBe(null);
  await page.goBack(); await expect(chain).toHaveValue('session:51');
  await page.goForward(); await expect(chain).toHaveValue('');
  expect(protectedReads).toBe(0);
});

test('Activity missing snapshot identity or exact observed time never substitutes another selected session', async ({ page }) => {
  await sessionUrlFixture(page);
  let protectedReads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++; });
  for (const url of [sessionHref('f'.repeat(64)), sessionHref('a'.repeat(64), sessionObserved, '77777777-7777-4777-8777-777777777777'), sessionHref('a'.repeat(64), '2026-09-26T11:30:00.123457Z')]) {
    await page.goto(url);
    await expect(page.getByRole('region', { name: 'Session details', exact: true })).toHaveCount(0);
    await expect(page.locator('.activity-capture-screen')).toContainText(/requested.*(?:not present|unavailable|not found|did not match|does not match|missing|outside the 24-hour retention)|Session snapshot did not match the requested capture and observation time/i);
    await expect(page.locator('.activity-capture-screen button[aria-pressed="true"]').filter({ hasText: /^Inspect / })).toHaveCount(0);
    expect(scope(page).get('sessionIdentity')).toBeTruthy();
  }
  expect(protectedReads).toBe(0);
});

test('delayed old Activity selection cannot reappear after switching the workspace server', async ({ page }) => {
  await sessionUrlFixture(page);
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/activity/live?*`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.goto(sessionHref('c'.repeat(64)));
  await expect.poll(() => pending).toBe(true);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('sessionIdentity')).toBe(null);
  release();
  await expect(page.getByRole('region', { name: 'Loaded session observations', exact: true }).getByRole('button', { name: 'Inspect 51/0', exact: true })).toBeVisible();
  await expect(page.getByRole('region', { name: 'Session details', exact: true })).toHaveCount(0);
});

test('Waits URL restores a deliberate category and rejects invalid or duplicate selection without a substitute request', async ({ page }) => {
  await fixture(page);
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/query-performance/waits/top')) requests.push(new URL(request.url())); });
  await page.goto(`${href('waits')}&waitCategory=6`);
  const category = page.getByRole('combobox', { name: 'Query Store category', exact: true });
  await expect(category).toHaveValue('6');
  await category.selectOption('3');
  await expect.poll(() => scope(page).get('waitCategory')).toBe('3');
  await page.reload(); await expect(category).toHaveValue('3');
  await page.goBack(); await expect(category).toHaveValue('6');
  await page.goForward(); await expect(category).toHaveValue('3');
  for (const invalid of ['11', '3&waitCategory=6', 'not-a-category']) {
    const before = requests.length;
    await page.goto(`${href('waits')}&waitCategory=${invalid}`);
    await expect(category).toHaveValue('');
    await expect(page.getByText('The requested Query Store wait category is invalid. Choose a supported category; no other category has been selected.', { exact: true })).toBeVisible();
    expect(requests).toHaveLength(before);
  }
});

async function alertFixture(page, operator = false) {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'alertSelectionFixture', value: '1', url: origin }, ...(operator ? [{ name: 'alertOperatorFixture', value: '1', url: origin }] : [])]);
}

test('target alert URL restores a paged selection and preserves keyboard focus without refetching loaded rows', async ({ page }) => {
  await alertFixture(page);
  let reads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === `/api/v1/observation-targets/${first}/alerts/active`) reads++; });
  await page.goto(`${href('alerts')}&databaseId=6&alert=${pagedAlert}`);
  const observations = page.getByRole('region', { name: 'Target alert observations', exact: true });
  const paged = observations.getByRole('button', { name: 'SYNTHETIC paged alert', exact: true });
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  expect(reads).toBe(2);
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page);
  await expect(page.locator('.alert-triage-receipt').filter({ hasText: 'Native row-time cutoff:' })).toContainText('Native row-time cutoff:');
  await expect(page.locator('.alert-triage-note').filter({ hasText: 'Retained alert investigation;' })).toContainText('independent of Rewind and database selection');
  await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  const loadedReads = reads;
  const firstRow = observations.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true });
  const originalButton = await firstRow.elementHandle();
  await firstRow.focus();
  await firstRow.press('Enter');
  await expect.poll(() => scope(page).get('alert')).toBe(firstAlert);
  await expect(firstRow).toHaveAttribute('aria-pressed', 'true');
  await expect(firstRow).toBeFocused();
  expect(await originalButton.evaluate(element => element.isConnected)).toBe(true);
  expect(reads).toBe(loadedReads);
  await paged.focus();
  await paged.press('Enter');
  await expect.poll(() => scope(page).get('alert')).toBe(pagedAlert);
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  await expect(paged).toBeFocused();
  expect(reads).toBe(loadedReads);
  await page.getByRole('textbox', { name: 'Filter by rule identifier', exact: true }).fill('55555555');
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The selected alert is hidden by the current filters.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
  expect(scope(page).get('alert')).toBe(pagedAlert);
  await page.getByRole('button', { name: 'Show selected alert', exact: true }).click();
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  await page.reload();
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await assertDatabaseScope(page, 6);
  await page.goto(`${href('alerts')}&databaseId=6&alert=88888888-8888-4888-8888-888888888888`);
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The requested alert is not present in the bounded current server alerts.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
  await expect(observations.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true })).toHaveAttribute('aria-pressed', 'false');
});

test('fleet alert URL restores an exact paged alert and filtered or missing selection never substitutes another detail', async ({ page }) => {
  await alertFixture(page);
  let reads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/alerts/active') reads++; });
  await page.goto(`${href('alerts', '')}&alertTarget=${second}&alert=${pagedAlert}`);
  const observations = page.getByRole('region', { name: 'Fleet alert observations', exact: true });
  const paged = observations.getByRole('button', { name: 'Select SYNTHETIC paged alert on SYNTHETIC SQL 02', exact: true });
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  expect(reads).toBe(2);
  await expect(page.locator('.alert-triage-receipt').filter({ hasText: 'Native row-time cutoff:' })).toContainText('Native row-time cutoff:');
  await expect(page.locator('.alert-triage-note').filter({ hasText: 'Retained alert investigation;' })).toContainText('independent of Rewind and database selection');
  await page.getByRole('textbox', { name: 'Filter loaded alerts', exact: true }).fill('blocking');
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The selected alert is hidden by the current filters.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
  expect(scope(page).get('alert')).toBe(pagedAlert);
  await page.getByRole('button', { name: 'Show selected alert', exact: true }).click();
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  const loadedReads = reads;
  const firstRow = observations.getByRole('button', { name: 'Select SYNTHETIC sustained blocking on SYNTHETIC SQL 01', exact: true });
  const originalButton = await firstRow.elementHandle();
  await firstRow.focus();
  await firstRow.press('Enter');
  await expect.poll(() => scope(page).get('alert')).toBe(firstAlert);
  await expect.poll(() => scope(page).get('alertTarget')).toBe(first);
  await expect(firstRow).toBeFocused();
  expect(await originalButton.evaluate(element => element.isConnected)).toBe(true);
  expect(reads).toBe(loadedReads);
  await page.reload();
  await expect(firstRow).toHaveAttribute('aria-pressed', 'true');
  await page.goto(`${href('alerts', '')}&alertTarget=${first}&alert=88888888-8888-4888-8888-888888888888`);
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The requested alert is not present in the bounded current inbox.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
});

test('late acknowledgement from an old target alert selection cannot update another selected alert', async ({ page }) => {
  await alertFixture(page, true);
  let release, requested = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/alerts/${firstAlert}/acknowledge`, async route => { requested = true; await gate; await route.fulfill({ status: 204 }).catch(() => {}); returned = true; });
  try {
    await page.goto(`${href('alerts')}&databaseId=6&alert=${pagedAlert}`);
    const observations = page.getByRole('region', { name: 'Target alert observations', exact: true });
    await observations.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }).click();
    await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
    await expect.poll(() => requested).toBe(true);
    await observations.getByRole('button', { name: 'SYNTHETIC paged alert', exact: true }).click();
    await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  expect(scope(page).get('alert')).toBe(pagedAlert);
  await expect(page.getByRole('row').filter({ has: page.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }) }).getByRole('cell').first()).toHaveText('firing');
  await expect(page.getByRole('status').filter({ hasText: 'Acknowledgement recorded.' })).toHaveCount(0);
});

test('acknowledgement from a prior firing cycle cannot mark refreshed alert evidence and a new cycle uses a new operation', async ({ page }) => {
  await alertFixture(page, true);
  let release, recycled = false, returned = false;
  const operations = [];
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/alerts/active?*`, async route => {
    const response = await route.fetch();
    const body = await response.json();
    const updated = recycled ? { ...body, snapshotUtc: '2026-10-01T12:00:00.000Z', items: body.items.map(item => item.alertId === firstAlert ? { ...item, firstObservedUtc: '2026-10-01T09:00:00.000Z', firedUtc: '2026-10-01T09:01:00.000Z', value: 99 } : item) } : body;
    if (recycled && updated.nextCursor) updated.nextCursor = nativeTargetAlertCursor(first, updated.snapshotUtc, updated.items.at(-1));
    await route.fulfill({ response, json: updated });
  });
  await page.route(`**/${first}/alerts/${firstAlert}/acknowledge`, async route => {
    operations.push(route.request().postDataJSON().operationId);
    if (operations.length === 1) { await gate; await route.fulfill({ status: 204 }).catch(() => {}); returned = true; }
    else await route.fulfill({ status: 204 });
  });
  try {
    await page.goto(`${href('alerts')}&databaseId=6&alert=${firstAlert}`);
    await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
    await expect.poll(() => operations.length).toBe(1);
    recycled = true;
    await page.getByRole('button', { name: 'Return server alerts to latest', exact: true }).click();
    await expect(page.getByRole('complementary', { name: 'SYNTHETIC sustained blocking', exact: true }).locator('.alert-observed')).toContainText('99');
    await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(page.getByRole('row').filter({ has: page.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }) }).getByRole('cell').first()).toHaveText('firing');
  await expect(page.getByRole('status').filter({ hasText: 'Acknowledgement recorded.' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
  await expect.poll(() => operations.length).toBe(2);
  expect(operations[1]).not.toBe(operations[0]);
  await expect(page.getByRole('row').filter({ has: page.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }) }).getByRole('cell').first()).toHaveText('acknowledged');
});

test('query palette prefills only a selected database in the current server catalog and explains missing context', async ({ page }) => {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'paletteCatalogFixture', value: '1', url: origin }]);
  await page.goto(`${href('health')}&databaseId=6`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await page.keyboard.press('Control+k');
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  await dialog.getByRole('button', { name: 'Query hash', exact: true }).click();
  await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('6');
  await expect(dialog.locator('#query-search-databases option[value="6"]')).toHaveAttribute('label', 'Warehouse');
  await page.keyboard.press('Escape');
  await page.goto(`${href('health')}&databaseId=99`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await page.keyboard.press('Control+k');
  await dialog.getByRole('button', { name: 'Query hash', exact: true }).click();
  const database = dialog.getByRole('spinbutton', { name: 'Database ID', exact: true });
  await expect(database).toBeEnabled();
  await expect(database).toHaveValue('');
  await expect(dialog.getByRole('status').filter({ hasText: 'Workspace database ID 99 is absent' })).toBeVisible();
  await database.fill('6');
  await expect(database).toHaveValue('6');
  expect(scope(page).get('databaseId')).toBe('99');
});

test('delayed query palette catalog from a prior server cannot prefill the new server context', async ({ page }) => {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'paletteCatalogFixture', value: '1', url: origin }]);
  let release, requested = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/query-performance/status`, async route => { const response = await route.fetch(); requested = true; await gate; await route.fulfill({ response }).catch(() => {}); returned = true; });
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  try {
    await page.goto(`${href('health')}&databaseId=6`); await revealHealthSupport(page); await revealDashboardSupport(page);
    await page.keyboard.press('Control+k');
    await dialog.getByRole('button', { name: 'Query hash', exact: true }).click();
    await expect.poll(() => requested).toBe(true);
    await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toBeDisabled();
    await page.evaluate(hash => { location.hash = hash; }, `${href('health', second).slice(1)}&databaseId=5`);
    await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5');
    await expect(dialog.locator('#query-search-databases option[value="5"]')).toHaveAttribute('label', 'Orders 02');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5');
  await expect(dialog.locator('#query-search-databases option[value="6"]')).toHaveAttribute('label', 'Warehouse 02');
  await expect(dialog.getByRole('searchbox', { name: 'Enter query hash', exact: true })).toHaveValue('');
});

test('fleet CPU memory waits blocking and HA cells open their diagnostic evidence in the shared window', async ({ page }) => {
  await fixture(page);
  const cases = [['SQL scheduler CPU', 'resources', null], ['OS free via SQL', 'resources', null], ['Waits', 'waits', null], ['Blocking', 'activity', null], ['HA', 'operations', 'availability']];
  for (const [signal, destination, check] of cases) {
    await page.goto(href('overview', ''));
    const row = page.getByRole('region', { name: 'Server health grid', exact: true }).getByRole('row').filter({ has: page.getByRole('link', { name: 'SYNTHETIC SQL 01', exact: true }) });
    const link = row.getByRole('link', { name: new RegExp(`^${signal}:`) });
    const params = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
    expect(params.get('target')).toBe(first);
    expect(params.get('from')).toBe(from);
    expect(params.get('to')).toBe(to);
    expect(params.get('check')).toBe(check);
    await link.focus();
    await link.press('Enter');
    await expect.poll(() => new URL(page.url()).hash.split('?')[0]).toBe(`#/${destination}`);
    await assertHistoricalScope(page);
    await expect(page.getByRole('combobox', { name: 'Target server', exact: true })).toHaveValue(first);
  }
});

test('diagnostic navigation and server changes retain the selected historical window', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await open(page, 'health');
  await expect(page.getByRole('heading', { name: 'SYNTHETIC SQL 01', exact: true })).toBeVisible();
  for (const label of ['Waits', 'Resources', 'Queries', 'Deadlocks', 'Operations', 'Activity']) {
    await mainNavigation(page).getByRole('link', { name: label, exact: true }).click();
    await assertHistoricalScope(page);
    await expect(page.getByRole('combobox', { name: 'Target server' })).toHaveValue(first);
  }
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'SYNTHETIC SQL 02' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('authorized server selector retains historical and database scope across diagnostic navigation', async ({ page }) => {
  await databaseFixture(page); await page.goto(href('health')+'&databaseId=6'); await revealHealthSupport(page); await revealDashboardSupport(page);
  const selector=page.getByRole('combobox',{name:'Target server',exact:true});
  for(const label of [null,'Resources','Queries']){
    if(label)await page.getByRole('navigation',{name:'Server diagnostics',exact:true}).getByRole('link',{name:label,exact:true}).click();
    await expect(selector).toHaveValue(first);await expect(selector.locator('option[value!=""]')).toHaveCount(10);
    await expect(selector.locator('option').filter({hasText:'SYNTHETIC SQL 10'})).toHaveCount(1);
    await assertDatabaseScope(page,6);await assertHistoricalScope(page);
  }
  await selector.selectOption(first);await assertDatabaseScope(page,6);await assertHistoricalScope(page);
  await selector.selectOption(second);await assertHistoricalScope(page,second);
  await expect.poll(()=>scope(page).get('databaseId')).toBe(null);await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(selector).toHaveValue(second);await expect(selector.locator('option[value!=""]')).toHaveCount(10);
});

test('detail navigation cancels a held full-fleet read and does not request replacement fleet evidence',async({page})=>{
  await fixture(page);let release,requested=false,released=false,fleetReads=0;
  const gate=new Promise(done=>release=done);
  await page.route('**/api/v1/overview?*',async route=>{
    if(new URL(route.request().url()).searchParams.has('targetId'))return route.continue();
    fleetReads++;const response=await route.fetch(),body=await response.json();requested=true;await gate;
    await route.fulfill({response,json:{...body,targets:body.targets.map(item=>({...item,displayName:'STALE WINDOW '+item.displayName}))}}).catch(()=>{});released=true;
  });
  try{
    await page.goto('/#/overview?range=1h');await expect.poll(()=>requested).toBe(true);
    await page.evaluate(next=>location.hash=next,href('resources').slice(1));
    await expect(page.getByRole('region',{name:'Resources for SYNTHETIC SQL 01',exact:true})).toBeVisible();
    const selector=page.getByRole('combobox',{name:'Target server',exact:true});await expect(selector).toHaveValue(first);
    release();await expect.poll(()=>released).toBe(true);await expect(page.locator('body')).not.toContainText('STALE WINDOW');
    await page.getByRole('button',{name:'↻ Refresh',exact:true}).click();
    await expect(selector.locator('option[value!=""]')).toHaveCount(10);expect(fleetReads).toBe(1);await assertHistoricalScope(page);
  }finally{release();}
});

test('authorized selector preserves keyboard focus and Live scope during held catalog refresh',async({page})=>{
  await databaseFixture(page);await page.clock.install({time:new Date('2026-09-26T12:00:00Z')});
  let release,pending=false,catalogReads=0,fleetReads=0;const gate=new Promise(done=>release=done);
  page.on('request',request=>{const url=new URL(request.url());if(url.pathname==='/api/v1/overview'&&!url.searchParams.has('targetId'))fleetReads++;});
  await page.route('**/api/v1/observation-targets?*',async route=>{
    const response=await route.fetch();catalogReads++;if(catalogReads===2){pending=true;await gate;}
    await route.fulfill({response}).catch(()=>{});
  });
  try{
    await page.goto(href('health',first,'1h')+'&databaseId=6');
    const selector=page.getByRole('combobox',{name:'Target server',exact:true});await expect(selector).toHaveValue(first);await assertDatabaseScope(page,6);
    const original=await selector.elementHandle();await selector.focus();await page.clock.runFor(61_000);await expect.poll(()=>pending).toBe(true);
    await expect(selector).toBeFocused();await expect(selector.locator('option[value!=""]')).toHaveCount(10);expect(await original.evaluate(element=>element.isConnected)).toBe(true);
    release();await expect.poll(()=>catalogReads).toBe(2);await expect(selector).toBeFocused();expect(fleetReads).toBe(0);
    await selector.selectOption(second);await expect.poll(()=>scope(page).get('target')).toBe(second);await expect.poll(()=>scope(page).get('databaseId')).toBe(null);
    expect(Object.fromEntries(scope(page))).toMatchObject({target:second,range:'1h'});expect(scope(page).has('from')).toBe(false);expect(scope(page).has('to')).toBe(false);
    await page.reload();await expect(selector).toHaveValue(second);await expect(page.getByRole('combobox',{name:'Time range',exact:true})).toHaveValue('1h');expect(fleetReads).toBe(0);
  }finally{release();}
});

test('pause stops scheduled refresh, resume refreshes, and Rewind fixes the window', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/overview') requests.push(request.url()); });
  await page.clock.install({ time: new Date('2026-10-01T16:00:00Z') });
  await open(page, 'overview', first, '1h');
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Pause live updates', exact: true }).click();
  await expect(page.getByText('Live \u00b7 paused', { exact: true })).toBeVisible();
  const pausedRequests = requests.length;
  await page.clock.fastForward(70_000);
  expect(requests).toHaveLength(pausedRequests);
  await page.getByRole('button', { name: 'Resume live updates', exact: true }).click();
  await expect.poll(() => requests.length).toBeGreaterThan(pausedRequests);
  await expect(page.getByText('Live \u00b7 moving', { exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('custom');
  await expect(page.getByText('Rewind \u00b7 fixed', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Pause live updates', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toBeVisible();
  const rewindRequests = requests.length, selected = Object.fromEntries(scope(page));
  await page.clock.fastForward(70_000);
  expect(requests).toHaveLength(rewindRequests);
  expect(Object.fromEntries(scope(page))).toEqual(selected);
});

test('custom UTC editor supports keyboard dismissal, validation, apply and focus restoration', async ({ page }) => {
  await open(page);
  await ensureUtcDisplay(page);
  const edit = page.getByRole('button', { name: 'Edit UTC range', exact: true });
  await edit.click();
  const fromInput = page.getByLabel('From (UTC)', { exact: true });
  await expect(fromInput).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(fromInput).toHaveCount(0);
  await expect(edit).toBeFocused();
  await edit.press('Enter');
  await fromInput.fill('2026-09-26T13:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T12:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The end must be after the start.' })).toBeVisible();
  await assertHistoricalScope(page);
  await fromInput.fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
  await expect(fromInput).toHaveCount(0);
  await expect(edit).toBeFocused();
  const selectedUrl = page.url();
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Show local time', exact: true })).toBeVisible();
  expect(page.url()).toBe(selectedUrl);
});

test('custom UTC editor preserves exact microsecond URL bounds on unchanged Apply and on one-bound edits', async ({ page }) => {
  await fixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo })}`);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  const originalUrl = page.url();
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await expect(page.getByLabel('From (UTC)', { exact: true })).toHaveValue('2026-09-26T08:00:00.123');
  await expect(page.getByLabel('To (UTC)', { exact: true })).toHaveValue('2026-09-26T12:00:00.654');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page).toHaveURL(originalUrl);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T08:00:00.777');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T08:00:00.777Z', exactTo);
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T12:00:00.888');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T08:00:00.777Z', '2026-09-26T12:00:00.888Z');
});

test('custom UTC editor accepts tenths and hundredths and keeps invalid ranges from changing scope', async ({ page }) => {
  await open(page, 'health');
  for (const [fraction, normalized] of [['1', '100'], ['01', '010']]) {
    await ensureUtcDisplay(page);
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill(`2026-09-26T08:00:00.${fraction}`);
    await page.getByLabel('To (UTC)', { exact: true }).fill(`2026-09-26T12:00:00.${fraction}`);
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
    await assertHistoricalScope(page, first, `2026-09-26T08:00:00.${normalized}Z`, `2026-09-26T12:00:00.${normalized}Z`);
  }
  const valid = page.url();
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-08-01T08:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Choose a range of 31 days or less.' })).toBeVisible();
  expect(page.url()).toBe(valid);
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T13:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The end must be after the start.' })).toBeVisible();
  expect(page.url()).toBe(valid);
});

test('command palette keyboard results preserve Rewind scope and restore the invoking focus', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await open(page);
  const launch = page.getByRole('button', { name: /^Search/ });
  await launch.focus();
  await page.keyboard.press('Control+k');
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  const search = dialog.getByRole('searchbox', { name: 'Search servers' });
  await expect(search).toBeFocused();
  await expect(dialog.getByRole('status')).toHaveText('10 authorized servers searched');
  await page.keyboard.press('Escape');
  await expect(dialog).not.toBeVisible();
  await expect(launch).toBeFocused();
  await launch.press('Enter');
  await search.fill('SQL 02');
  const result = dialog.getByRole('button', { name: /SYNTHETIC SQL 02/ });
  await expect(result).toHaveCount(1);
  await search.press('ArrowDown');
  await expect(result).toBeFocused();
  await result.press('ArrowUp');
  await expect(search).toBeFocused();
  await search.press('ArrowDown');
  await result.press('Enter');
  await expect(dialog).not.toBeVisible();
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'Investigate SYNTHETIC SQL 02', exact: true })).toBeVisible();
  await expect(launch).toBeFocused();
  expect(errors).toEqual([]);
});

test('investigate-first and blocking drill-downs keep the server, window and event instant', async ({ page }) => {
  await open(page, 'health', second);
  await expect(page.getByRole('heading', { name: /Head blocker \u00b7 session 51/ })).toBeVisible();
  const blocking = page.locator('.server-dashboard-blocking');
  await expect(blocking.getByText('Session 72', { exact: true })).toBeVisible();
  await expect(blocking).toContainText('LCK_M_X \u00b7 12000 ms \u00b7 depth 1');
  await blocking.getByRole('link', { name: 'Open sessions and blocking \u2192', exact: true }).click();
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'SYNTHETIC SQL 02' })).toBeVisible();
  await page.goto(href('overview', second));
  const issue = page.locator('.overview-issue').filter({ hasText: 'Sessions blocked' });
  await issue.click();
  await assertHistoricalScope(page, second);
  expect(scope(page).get('at')).toBe('2026-09-26T11:59:30.000Z');
  await expect(page.getByRole('heading', { name: 'Live sessions', exact: true })).toBeVisible();
});

for (const width of [320, 1440]) test(`built dashboard inspects the native current head blocker within rewind at ${width}`, async ({ page }) => {
  const errors = [], reads = []; page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/blocking/current')) reads.push(request.url()); });
  await page.setViewportSize({ width, height: 1000 }); await open(page, 'health', second);
  const summary = page.getByRole('region', { name: 'What needs attention', exact: true });
  await expect(summary).toContainText('1 distinct blocked session');
  const button = summary.getByRole('button', { name: 'Inspect head blocker 51', exact: true });
  await button.focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Blocking chains', exact: true })).toBeFocused();
  await expect(page.locator('.selected-blocker')).toContainText('Session 72');
  await assertHistoricalScope(page, second); expect(reads).toHaveLength(1); expect(errors).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await mkdir('../artifacts/dashboard-retained-pagers/screenshots', { recursive: true });
  // Keep the pinned mobile navigation above the panel rather than obscuring this local capture.
  const capture = await summary.boundingBox();
  await page.setViewportSize({ width, height: Math.max(1000, Math.ceil(capture.height) + 800) });
  await summary.scrollIntoViewIfNeeded();
  await page.evaluate(() => document.activeElement?.blur());
  await summary.screenshot({ path: `../artifacts/dashboard-retained-pagers/screenshots/built-head-blocker-${width}.png` });
});

test('timeline rail and keyboard range select the shared workspace and restore it after reload', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/overview') requests.push(new URL(request.url())); });
  await open(page, 'health', second);
  const rail = page.getByRole('img', { name: /Timeline range selection/ });
  await expect(rail).toBeVisible(); await rail.scrollIntoViewIfNeeded();
  const box = await rail.boundingBox();
  await page.mouse.move(box.x + box.width * .25, box.y + box.height * .5); await page.mouse.down();
  await page.mouse.move(box.x + box.width * .7, box.y + box.height * .5, { steps: 8 }); await page.mouse.up();
  await expect.poll(() => Date.parse(scope(page).get('from')) > Date.parse(from) && Date.parse(scope(page).get('to')) < Date.parse(to)).toBe(true);
  const selected = Object.fromEntries(scope(page));
  await expect.poll(() => requests.some(url => url.searchParams.get('targetId') === second && url.searchParams.get('fromUtc') === selected.from && url.searchParams.get('toUtc') === selected.to)).toBe(true);
  await page.reload(); await assertHistoricalScope(page, second, selected.from, selected.to);
  const form = page.getByRole('form', { name: 'Select timeline window', exact: true });
  await expect(form).toBeVisible();
  const start = new Date(Date.parse(selected.from) + 60_000).toISOString(), end = new Date(Date.parse(selected.to) - 60_000).toISOString();
  await form.getByLabel('Timeline from UTC', { exact: true }).fill(start);
  await form.getByLabel('Timeline to UTC', { exact: true }).fill(end);
  await form.getByRole('button', { name: 'Apply timeline window', exact: true }).focus(); await page.keyboard.press('Enter');
  await assertHistoricalScope(page, second, start, end);
  const resources = mainNavigation(page).getByRole('link', { name: 'Resources', exact: true });
  await expect.poll(async () => new URLSearchParams((await resources.getAttribute('href')).split('?')[1]).get('from')).toBe(start);
  await resources.click(); await assertHistoricalScope(page, second, start, end);
});

for (const width of [320, 1440]) test(`built dashboard timeline dark layout and keyboard range at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await open(page, 'health', second);
  const timeline = page.locator('.dashboard-timeline');
  await expect(timeline.getByRole('img', { name: /Timeline range selection/ })).toBeVisible();
  const form = timeline.getByRole('form', { name: 'Select timeline window', exact: true });
  const start = form.getByLabel('Timeline from UTC', { exact: true }), end = form.getByLabel('Timeline to UTC', { exact: true });
  await start.focus(); await page.keyboard.press('Tab'); await expect(end).toBeFocused();
  await page.keyboard.press('Tab'); await expect(form.getByRole('button', { name: 'Apply timeline window' })).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).include('.dashboard-timeline').withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations;
  await info.attach(`built-timeline-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-fleet-timeline/screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  await timeline.locator('.dashboard-timeline-range').screenshot({ path: `${directory}/built-dashboard-timeline-${width}.png` });
});

test('fleet labels save in the built workspace and reload current revision without losing global time', async ({ page }) => {
  let revision = 1, environment = 'production', tags = ['east', 'critical']; const mutations = [];
  await page.route('**/api/v1/overview?*', async route => {
    const response = await route.fetch(), value = await response.json();
    value.targets = value.targets.map(target => target.targetId === first ? { ...target, environment, tags, groupingRevision: revision, canManageGrouping: true } : target);
    await route.fulfill({ response, json: value });
  });
  await page.route(`**/api/v1/observation-targets/${first}/grouping`, async route => {
    const body = route.request().postDataJSON(); mutations.push(body);
    expect(body.expectedRevision).toBe(revision);
    environment = body.environment.trim().toLowerCase(); tags = body.tags.map(tag => tag.trim().toLowerCase()).sort(); revision++;
    await route.fulfill({ status: 200, contentType: 'application/json', json: { instanceId: first, environment, tags, groupingRevision: revision } });
  });
  await open(page, 'overview', '');
  await page.getByRole('button', { name: 'Edit grouping for SYNTHETIC SQL 01', exact: true }).click();
  const form = page.getByRole('form', { name: 'Server grouping labels', exact: true });
  await form.getByLabel('Server environment').fill(' Staging '); await form.getByLabel('Server tags').fill('West, api');
  await form.getByRole('button', { name: 'Save labels', exact: true }).click();
  await expect(form).toHaveCount(0); await expect.poll(() => mutations.length).toBe(1);
  const row = page.getByRole('row').filter({ has: page.getByRole('link', { name: 'SYNTHETIC SQL 01', exact: true }) });
  await expect(row).toContainText('staging · api, west');
  await page.getByRole('button', { name: 'Edit grouping for SYNTHETIC SQL 01', exact: true }).click();
  await expect(form.getByLabel('Server environment')).toHaveValue('staging');
  await form.getByRole('button', { name: 'Cancel', exact: true }).click();
  await assertHistoricalScope(page, '', from, to);
});

test('chart drag selects one shared window and the comparison toggle survives navigation', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/overview') requests.push(new URL(request.url())); });
  await open(page, 'health', second);
  const workload = page.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL workload', exact: true }) });
  const chart = workload.getByRole('img');
  await expect(chart).toBeVisible();
  await chart.scrollIntoViewIfNeeded();
  const box = await chart.boundingBox();
  await page.mouse.move(box.x + box.width * .25, box.y + box.height * .5);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * .7, box.y + box.height * .5, { steps: 8 });
  await page.mouse.up();
  await expect.poll(() => {
    const current = scope(page);
    return Date.parse(current.get('from')) > Date.parse(from)
      && Date.parse(current.get('to')) < Date.parse(to);
  }).toBe(true);
  const selected = Object.fromEntries(scope(page));
  expect(Date.parse(selected.from)).toBeGreaterThan(Date.parse(from));
  expect(Date.parse(selected.to)).toBeLessThan(Date.parse(to));
  await expect(page.getByRole('heading', { name: 'SQL workload', exact: true })).toBeVisible();
  // The URL changes before React's hashchange render. Wait for a rendered link
  // to use the narrowed scope before toggling its controlled comparison input.
  const resourcesLink = mainNavigation(page).getByRole('link', { name: 'Resources', exact: true });
  await expect.poll(async () => {
    const rendered = new URLSearchParams((await resourcesLink.getAttribute('href')).split('?')[1]);
    return rendered.get('from') === selected.from && rendered.get('to') === selected.to;
  }).toBe(true);
  const compare = page.getByRole('checkbox', { name: 'Compare previous period', exact: true });
  await compare.focus();
  await compare.press('Space');
  await expect(compare).toBeChecked();
  await expect.poll(() => scope(page).get('compare')).toBe('1');
  await mainNavigation(page).getByRole('link', { name: 'Resources', exact: true }).click();
  await assertHistoricalScope(page, second, selected.from, selected.to);
  expect(scope(page).get('compare')).toBe('1');
  await expect.poll(() => requests.some(url => url.searchParams.get('targetId') === second && url.searchParams.get('fromUtc') === selected.from && url.searchParams.get('toUtc') === selected.to)).toBe(true);
});

test('viewer actions name the required roles and missing coverage is never an all-clear', async ({ page }) => {
  await fixture(page);
  await page.goto('/__fixture?mode=partial');
  await page.goto(href('overview'));
  await expect(page.getByRole('button', { name: '+ Add server', exact: true })).toHaveCount(0);
  await expect(page.getByText('0/1 servers reporting current SQL core evidence', { exact: true })).toBeVisible();
  await page.getByText('Collection coverage and evidence gaps', { exact: true }).click();
  await expect(page.getByText('Synthetic collection timeout', { exact: true })).toBeVisible();
  await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
  await openInvestigationWorkspace(page);
  await expect(page.getByText('Operator or Target Administrator access unlocks saving an incident for this server.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toHaveCount(0);
  await page.goto('/__fixture?mode=empty');
  await page.goto(href('overview'));
  await expect(page.getByRole('heading', { name: 'Start monitoring your SQL environment' })).toBeVisible();
  await expect(page.getByText('A Target Administrator with all-target access can add a server.', { exact: true })).toBeVisible();
  await page.goto('/__fixture?mode=error');
  await page.goto(href('overview'));
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toHaveCount(0);
});

test('fleet server search and grouping are local controls that preserve global time', async ({ page }) => {
  await open(page, 'overview', '');
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Group servers by', exact: true }).selectOption('tag');
  await expect(page.getByRole('rowheader', { name: /Tag: east/ })).toBeVisible();
  await page.getByRole('textbox', { name: 'Search loaded servers', exact: true }).fill('SQL 02');
  const grid = page.locator('.overview-servers');
  await expect(grid).toContainText('Showing 1 of 10 authorized targets');
  await grid.getByRole('link', { name: 'SYNTHETIC SQL 02', exact: true }).click();
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'Investigate SYNTHETIC SQL 02', exact: true })).toBeVisible();
});


test('deadlock relationships drill into the event activity and clear selection when the server changes', async ({ page }) => {
  await open(page, 'deadlocks');
  const detail = page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
  await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  const events = page.getByRole('complementary', { name: 'Captured deadlock events' });
  await expect(events.locator('button.event-card')).toHaveAttribute('aria-pressed', 'true');
  await expect(detail.getByRole('table')).toContainText('51');
  await expect(detail.getByRole('table')).toContainText('72');
  const activity = detail.getByRole('link', { name: /View activity at event time/ });
  const query = new URLSearchParams((await activity.getAttribute('href')).split('?')[1]);
  expect(query.get('event')).toBe('33333333-3333-4333-8333-333333333333');
  expect(query.get('target')).toBe(first);
  await activity.click();
  await assertHistoricalScope(page);
  expect(scope(page).get('event')).toBe('33333333-3333-4333-8333-333333333333');
  await page.goto(href('deadlocks'));
  await expect(detail.getByRole('table')).toBeVisible();
  let release, requested;
  const gate = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { requested = resolve; });
  await page.route(`**/${second}/deadlocks/*`, async route => {
    requested(); await gate; await route.continue().catch(() => {});
  });
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await started;
  await expect(detail.getByRole('table')).toHaveCount(0);
  await expect(detail.getByRole('status')).toContainText('Loading selected event');
  release();
  await expect(detail.getByRole('table')).toBeVisible();
  await assertHistoricalScope(page, second);
});


test('session inspection preserves its observation when filters remove the request and clears on target change', async ({ page }) => {
  await open(page, 'activity', first, '1h');
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const inspect = live.getByRole('button', { name: 'Inspect 51/0', exact: true });
  await expect(inspect).toBeVisible();
  await inspect.click();
  await expect(inspect).toHaveAttribute('aria-pressed', 'true');
  const detail = page.getByRole('region', { name: 'Session details', exact: true });
  await expect(detail).toContainText('This observation is preserved when filters change or the request disappears.');
  await expect(detail).toContainText('synthetic_dba');
  const captured = await detail.getByText(/^Observed /).textContent();
  await live.getByRole('checkbox', { name: 'Blocked only', exact: true }).check();
  await expect(inspect).toHaveCount(0);
  await expect(live.getByRole('button', { name: 'Inspect 72/0', exact: true })).toBeVisible();
  await expect(detail.getByText(/^Observed /)).toHaveText(captured);
  await expect(detail).toContainText('Cumulative CPU 1200 ms');
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await expect(detail).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'SYNTHETIC SQL 02' })).toBeVisible();
});


test('cache query ranking shows synthetic observations and opens scoped interval history', async ({ page }) => {
  await fixture(page);
  await page.goto(`${href('queries')}&queryView=cache`);
  const cache = page.getByRole('region', { name: 'Cache interval deltas', exact: true });
  const ranking = cache.getByRole('region', { name: 'Plan cache interval ranking', exact: true });
  await expect(ranking.getByRole('table')).toBeVisible();
  await expect(ranking).toContainText('Database ID 5');
  await expect(ranking).toContainText('bbbbbbbbbbbb');
  await expect(ranking.getByRole('cell', { name: '100', exact: true })).toBeVisible();
  await expect(cache.getByRole('alert')).toHaveCount(0);
  await ranking.getByRole('button', { name: 'Inspect cccccccccccc\u2026', exact: true }).click();
  const history = page.getByRole('region', { name: 'Cache query interval history', exact: true });
  await expect(history.getByRole('table')).toBeVisible();
  await expect(history).toContainText('Measured');
  await expect(history).toContainText('Opening captured query text and plan XML requires the QueryTextReader role and a read role on this server.');
  await expect(history.getByRole('button', { name: 'Open text', exact: true })).toHaveCount(0);
  await assertHistoricalScope(page);
});


test('changing the deadlock window clears the old detail while the same event reloads', async ({ page }) => {
  await open(page, 'deadlocks');
  const detail = page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
  await expect(detail.getByRole('table')).toBeVisible();
  await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  const event = page.getByRole('complementary', { name: 'Captured deadlock events' }).locator('button.event-card');
  await expect(event).toHaveAttribute('aria-pressed', 'true');
  let release, requested = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/deadlocks/33333333-3333-4333-8333-333333333333`, async route => {
    requested = true;
    await gate;
    await route.continue().catch(() => {});
  });
  try {
    await ensureUtcDisplay(page);
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
    await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
    await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
    await expect.poll(() => requested).toBe(true);
    await expect(detail).toHaveAttribute('aria-busy', 'true');
    await expect(detail.getByRole('table')).toHaveCount(0);
    await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toHaveCount(0);
    await expect(detail.getByRole('status')).toContainText('Loading selected event participants and relationships');
  } finally {
    release();
  }
  await expect(detail.getByRole('table')).toBeVisible();
  await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  await expect(detail).toHaveAttribute('aria-busy', 'false');
  await expect(event).toHaveAttribute('aria-pressed', 'true');
});


test('blocking chains filter loaded rows locally and preserve an unrelated inspected observation', async ({ page }) => {
  const requests = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.endsWith('/activity/live')) requests.push(request.url());
  });
  await page.context().addCookies([{ name: 'blockingChainFixture', value: '1', url: origin }]);
  await open(page, 'activity', first, '1h');
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const table = live.getByRole('region', { name: 'Loaded session observations', exact: true });
  const chain = live.getByRole('combobox', { name: 'Blocking chain (capture)', exact: true });
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  await page.getByRole('button', { name: 'Pause live updates', exact: true }).click();
  await table.getByRole('button', { name: 'Inspect 90/0', exact: true }).click();
  const detail = page.getByRole('region', { name: 'Session details', exact: true });
  await expect(detail.getByRole('heading', { name: 'Session 90 / 0', exact: true })).toBeVisible();
  const requestCount = requests.length;
  const firstChain = live.getByRole('button', { name: 'Filter chain: Session 51', exact: true });
  await firstChain.click();
  await expect(firstChain).toHaveAttribute('aria-pressed', 'true');
  await expect(chain).toHaveValue('session:51');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  for (const id of [51, 72, 73]) await expect(table.getByRole('button', { name: `Inspect ${id}/0`, exact: true })).toBeVisible();
  await expect(table.getByRole('button', { name: 'Inspect 90/0', exact: true })).toHaveCount(0);
  await expect(detail.getByRole('heading', { name: 'Session 90 / 0', exact: true })).toBeVisible();
  await chain.selectOption('session:81');
  await expect(live.getByRole('button', { name: 'Filter chain: Session 81', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  for (const id of [81, 82, 83]) await expect(table.getByRole('button', { name: `Inspect ${id}/0`, exact: true })).toBeVisible();
  await live.getByRole('button', { name: 'Clear chain filter', exact: true }).click();
  await expect(chain).toHaveValue('');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  expect(requests).toHaveLength(requestCount);
  await firstChain.click();
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect(chain).toHaveValue('');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  await expect(detail).toHaveCount(0);
});

test('a selected chain disappearing from the next snapshot produces explicit zero matches', async ({ page }) => {
  await page.context().addCookies([{ name: 'blockingChainFixture', value: '1', url: origin }]);
  await open(page, 'activity', first, '1h');
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const table = live.getByRole('region', { name: 'Loaded session observations', exact: true });
  const chain = live.getByRole('combobox', { name: 'Blocking chain (capture)', exact: true });
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  await page.getByRole('button', { name: 'Pause live updates', exact: true }).click();
  await live.getByRole('button', { name: 'Filter chain: Session 51', exact: true }).click();
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  await page.route(`**/${first}/activity/live?*`, async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.rows = body.rows.filter(row => ![51, 72, 73].includes(row.sessionId));
    await route.fulfill({ response, json: body });
  });
  await live.getByRole('button', { name: 'Refresh now', exact: true }).click();
  await expect(chain).toHaveValue('session:51');
  await expect(chain.getByRole('option', { name: 'Selected chain unavailable: session:51', exact: true })).toHaveCount(1);
  await expect(live.getByText(/^Showing 0 of 4 loaded session rows/)).toBeVisible();
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(0);
  await expect(live.getByText('Selected blocking chain is not present in this loaded page. No rows match; clear the chain filter or browse another snapshot/page.', { exact: true })).toBeVisible();
  await live.getByRole('button', { name: 'Clear chain filter', exact: true }).click();
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(4);
  for (const id of [81, 82, 83, 90]) await expect(table.getByRole('button', { name: `Inspect ${id}/0`, exact: true })).toBeVisible();
});


async function databaseFixture(page) {
  await fixture(page);
  await page.context().addCookies([{ name: 'databaseScopeFixture', value: '1', url: origin }]);
}
const workspaceDatabase = page => page.getByRole('combobox', { name: 'Workspace database', exact: true });
async function assertDatabaseScope(page, databaseId) {
  await expect.poll(() => scope(page).get('databaseId')).toBe(String(databaseId));
  await expect(workspaceDatabase(page)).toHaveValue(String(databaseId));
}

test('workspace database follows diagnostic navigation, time edits, reload and browser history', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  for (const label of ['Activity', 'Query performance', 'Resources', 'Waits', 'Deadlocks', 'Health checks']) {
    await mainNavigation(page).getByRole('link', { name: label, exact: true }).click();
    await assertDatabaseScope(page, 6);
    await assertHistoricalScope(page);
    await expect(page.getByTestId('database-context-notice')).toContainText('server-wide metrics, waits, blocking history, deadlocks, alerts and health checks remain server-wide.');
  }
  await mainNavigation(page).getByRole('link', { name: 'Resources', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await page.reload();
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page);
  await expect(page.getByRole('heading', { name: 'Resources for SYNTHETIC SQL 01' })).toBeVisible();
  await mainNavigation(page).getByRole('link', { name: 'Waits', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await page.goBack();
  await expect(page).toHaveURL(/#\/resources\?/);
  await assertDatabaseScope(page, 6);
  await page.goForward();
  await expect(page).toHaveURL(/#\/waits\?/);
  await assertDatabaseScope(page, 6);
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse 02 / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  await mainNavigation(page).getByRole('link', { name: 'Alerts', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption('');
  await expect.poll(() => scope(page).get('target')).toBe(null);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveCount(0);
});

test('live database selection updates the URL and sends the selected database to collection reads', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live')) requests.push(new URL(request.url())); });
  await databaseFixture(page);
  await page.goto(href('activity', first, '1h'));
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const table = live.getByRole('region', { name: 'Loaded session observations', exact: true });
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  await live.getByRole('combobox', { name: 'Database', exact: true }).selectOption('6');
  await assertDatabaseScope(page, 6);
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(1);
  await expect(table.getByRole('button', { name: 'Inspect 81/0', exact: true })).toBeVisible();
  await expect.poll(() => requests.some(url => url.searchParams.get('databaseId') === '6')).toBe(true);
  await workspaceDatabase(page).selectOption('5');
  await assertDatabaseScope(page, 5);
  await expect(live.getByRole('combobox', { name: 'Database', exact: true })).toHaveValue('5');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(2);
  await expect(table).not.toContainText('Warehouse');
});

test('database context scopes loaded cache and file rows without relabelling server resources', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('queries')}&queryView=cache&databaseId=6`);
  await assertDatabaseScope(page, 6);
  const ranking = page.getByRole('region', { name: 'Plan cache interval ranking', exact: true });
  await expect(ranking.getByRole('button', { name: /^Inspect [0-9a-f]{12}\u2026$/ })).toHaveCount(1);
  await expect(ranking).toContainText('dddddddddddd');
  await expect(ranking).not.toContainText('bbbbbbbbbbbb');
  await ranking.getByRole('button', { name: 'Inspect eeeeeeeeeeee\u2026', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Cache query interval history', exact: true }).getByRole('table')).toBeVisible();
  await workspaceDatabase(page).selectOption('5');
  await assertDatabaseScope(page, 5);
  for (const key of ['cacheDatabaseId', 'cacheIncarnation', 'cacheQuery', 'cachePlan']) expect(scope(page).get(key)).toBe(null);
  await expect(page.getByRole('region', { name: 'Cache query interval history', exact: true })).toHaveCount(0);
  await mainNavigation(page).getByRole('link', { name: 'Resources', exact: true }).click();
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  const files = page.getByRole('region', { name: 'Database file allocation and autogrowth history', exact: true });
  const facts = files.getByRole('region', { name: 'Historical database file facts', exact: true });
  await expect(facts.getByRole('table')).toContainText('Warehouse_data');
  await expect(facts.getByRole('table')).not.toContainText('Orders_data');
  await expect(page.getByText('Rows are filtered to database ID 6 within this received page. Empty filtered pages do not mean the database has no files.', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true })).toBeVisible();
  await expect(facts.getByRole('table').locator('tbody > tr')).toHaveCount(1);
  await files.getByRole('button', { name: 'View read and write latency trend for database 6, file Warehouse_data', exact: true }).click();
  const selectedFile = page.getByRole('region', { name: 'Selected file latency history', exact: true });
  await expect(selectedFile).toContainText('Database 6 / Warehouse_data');
  await workspaceDatabase(page).selectOption('5');
  await assertDatabaseScope(page, 5);
  await expect(selectedFile).toContainText('Select a file in the table to inspect its history.');
  await expect(selectedFile).not.toContainText('Warehouse_data');
});

test('unknown database scope stays explicit through missing inventory and clears safely', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('resources')}&databaseId=99`);
  await assertDatabaseScope(page, 99);
  await expect(workspaceDatabase(page).getByRole('option', { name: 'Database ID 99 (not in loaded inventory)', exact: true })).toHaveCount(1);
  await expect(page.locator('.database-scope-status')).toContainText('2 databases loaded');
  await expect(page.getByText('Rows are filtered to database ID 99 within this received page. Empty filtered pages do not mean the database has no files.', { exact: true })).toBeVisible();
  await expect(page.getByText('No file facts are present on this received page. Review the receipt state, loss and database filter.', { exact: true })).toBeVisible();
  await page.route(`**/${first}/health/databases?*`, route => route.fulfill({ status: 503, json: {} }));
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(page.locator('.database-scope-status')).toContainText('Database inventory unavailable.');
  await assertDatabaseScope(page, 99);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('');
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(page.getByRole('region', { name: 'Historical database file facts', exact: true }).getByRole('table')).toContainText('Orders_data');
  await page.goto(`${href('resources')}&databaseId=32768`);
  await expect(workspaceDatabase(page)).toHaveValue('');
});

test('delayed database inventory from an old target never supplies names for the new server', async ({ page }) => {
  await databaseFixture(page);
  let release, requested = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/health/databases?*`, async route => { requested = true; await gate; await route.continue().catch(() => {}); });
  try {
    await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
    await expect.poll(() => requested).toBe(true);
    await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
    await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse 02 / })).toHaveCount(1);
    await expect(workspaceDatabase(page).getByRole('option', { name: /^Orders 02 / })).toHaveCount(1);
  } finally { release(); }
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse \u00b7/ })).toHaveCount(0);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Orders \u00b7/ })).toHaveCount(0);
  await expect(page.locator('.database-scope-status')).toContainText('2 databases loaded');
  await expect(page.getByRole('combobox', { name: 'Target server', exact: true })).toHaveValue(second);
});


test('changing database context clears contradictory query and event selections', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('queries')}&databaseId=5&queryFingerprint=${'b'.repeat(64)}&planFingerprint=${'c'.repeat(64)}&source=query_store`);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  for (const key of ['queryFingerprint', 'planFingerprint', 'source']) expect(scope(page).get(key)).toBe(null);
  const at = new Date(Date.now() - 60_000).toISOString();
  await page.goto(`${href('activity', first, '1h')}&databaseId=5&at=${encodeURIComponent(at)}&event=33333333-3333-4333-8333-333333333333`);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  expect(scope(page).get('at')).toBe(null);
  expect(scope(page).get('event')).toBe(null);
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  await expect(live.getByRole('button', { name: 'Inspect 81/0', exact: true })).toBeVisible();
});


test('obsolete database change events cannot restore scope during a server or fleet navigation', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('resources')}&databaseId=5`);
  await assertDatabaseScope(page, 5);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  // Hash navigation and the old select event run in one browser task. React has
  // not rendered the new target when the obsolete control dispatches change.
  await workspaceDatabase(page).evaluate((select, nextHash) => {
    location.hash = nextHash;
    select.value = '6';
    select.dispatchEvent(new Event('change', { bubbles: true }));
  }, href('resources', second).slice(1));
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse 02 / })).toHaveCount(1);
  await expect(page.getByRole('heading', { name: 'Resources for SYNTHETIC SQL 02' })).toBeVisible();
  await workspaceDatabase(page).evaluate((select, nextHash) => {
    location.hash = nextHash;
    select.value = '6';
    select.dispatchEvent(new Event('change', { bubbles: true }));
  }, href('overview', '').slice(1));
  await expect.poll(() => scope(page).get('target')).toBe('');
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Fleet overview', exact: true })).toBeVisible();
});

const investigationTimeline = page => page.getByRole('region', { name: 'Investigation timeline', exact: true });
const planTimelineButtons = page => investigationTimeline(page).getByRole('button', { name: /^Align charts: Plan regression comparison/ });
async function browseTimelineEvents(page) {
  const browser = investigationTimeline(page).getByText(/^Browse loaded timeline events \(\d+\)$/).locator('xpath=parent::details');
  if (!(await browser.evaluate(element => element.open))) await browser.locator('summary').click();
  await expect(investigationTimeline(page).getByRole('region', { name: 'Loaded timeline event list', exact: true })).toBeVisible();
}
async function timelineFixture(page) {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'timelineFixture', value: '1', url: origin }]);
}

test('timeline URL restores the selected event with exact window and database context after reload', async ({ page }) => {
  await timelineFixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo, databaseId: '6', compare: '1', healthSection: 'summary' })}`);
  await browseTimelineEvents(page);
  const selected = planTimelineButtons(page);
  await expect(selected).toHaveCount(1);
  await selected.focus();
  await selected.press('Enter');
  await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  const selectedId = scope(page).get('timelineEvent'), selectedTime = scope(page).get('timelineAt');
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  await assertDatabaseScope(page, 6);
  await page.reload();
  await expect(investigationTimeline(page).getByRole('link', { name: 'Open selected event evidence', exact: true })).toBeVisible();
  await expect(investigationTimeline(page).getByText(/^Browse loaded timeline events \(\d+\)$/).locator('xpath=parent::details')).not.toHaveAttribute('open', '');
  await browseTimelineEvents(page);
  await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect(investigationTimeline(page).locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' }).locator('time')).toHaveAttribute('datetime', selectedTime);
  await expect.poll(() => page.locator('.server-dashboard .chart-crosshair').count()).toBeGreaterThanOrEqual(4);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  await assertDatabaseScope(page, 6);
  expect(scope(page).get('timelineEvent')).toBe(selectedId);
  expect(scope(page).get('compare')).toBe('1');
  await investigationTimeline(page).getByRole('button', { name: 'Clear timeline alignment', exact: true }).press('Enter');
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
  expect(scope(page).get('timelineAt')).toBe(null);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
});

test('aligning a Live timeline event freezes its loaded window and retains keyboard focus before reload', async ({ page }) => {
  await timelineFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  let loadedWindow;
  page.on('response', async response => {
    const url = new URL(response.url());
    if (url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first && response.ok()) {
      const body = await response.json();
      loadedWindow = { from: body.fromUtc, to: body.toUtc };
    }
  });
  await page.goto(`${href('health', first, '1h')}&databaseId=6`);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await expect.poll(() => loadedWindow).toBeTruthy();
  const window = { ...loadedWindow };
  const align = planTimelineButtons(page);
  const button = await align.elementHandle();
  const eventTime = await align.locator('xpath=ancestor::li').locator('time').getAttribute('datetime');
  await align.focus();
  await align.press('Enter');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ target: first, databaseId: '6', range: 'custom', from: window.from, to: window.to, timelineAt: eventTime });
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  await expect(align).toHaveAttribute('aria-pressed', 'true');
  await expect(align).toBeFocused();
  expect(await button.evaluate(element => element.isConnected)).toBe(true);
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('custom');
  await page.reload();
  await expect(investigationTimeline(page).getByRole('link', { name: 'Open selected event evidence', exact: true })).toBeVisible();
  await browseTimelineEvents(page);
  await expect(align).toHaveAttribute('aria-pressed', 'true');
  await expect(investigationTimeline(page).locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' }).locator('time')).toHaveAttribute('datetime', eventTime);
  await assertHistoricalScope(page, first, window.from, window.to);
  await assertDatabaseScope(page, 6);
});

test('selected Agent timeline marker retains its focused button during a pending refresh and becomes explicitly missing after failure', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  const timeline = investigationTimeline(page);
  const selected = timeline.getByRole('button', { name: /^Align charts: SQL Agent job failure/ }).first();
  await expect(selected).toBeVisible();
  await selected.focus(); await selected.press('Enter');
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^agent:/);
  const selectionId = scope(page).get('timelineEvent');
  const original = await selected.elementHandle();
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/sql-agent/failures?*`, async route => {
    pending = true; await gate;
    await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
  });
  // Native click() activates refresh without moving keyboard focus from the selected marker.
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).evaluate(element => element.click());
  await expect.poll(() => pending).toBe(true);
  await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect(selected).toBeFocused();
  expect(await original.evaluate(element => element.isConnected)).toBe(true);
  expect(scope(page).get('timelineEvent')).toBe(selectionId);
  release();
  await expect(timeline).toContainText('The requested timeline event is not present in the loaded evidence for this window. No other event has been selected.');
  await expect(timeline.getByRole('button', { name: /^Align charts: SQL Agent job failure/ })).toHaveCount(0);
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  expect(scope(page).get('timelineEvent')).toBe(selectionId);
  await timeline.getByText('Timeline coverage', { exact: true }).click();
  await expect(timeline).toContainText('SQL Agent first-observed markers are unavailable');
});

test('Overview rejects response bounds shifted by one microsecond from the fixed workspace window', async ({ page }) => {
  await timelineFixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  await page.route('**/api/v1/overview?*', async route => {
    if (new URL(route.request().url()).searchParams.get('targetId') !== first) return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, fromUtc: '2026-09-26T08:00:00.123457Z' } });
  });
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo, healthSection: 'summary' })}`);
  await expect(page.getByText('Overview returned a mismatched scope or time range.', { exact: true })).toBeVisible();
  await expect(investigationTimeline(page)).toBeVisible();
  await expect(page.locator('.server-dashboard-kpis')).toHaveCount(0);
  await expect(page.locator('.server-dashboard .overview-chart')).toHaveCount(0);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
});

test('Overview previous-period comparison requests retain the exact microsecond duration', async ({ page }) => {
  await timelineFixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  const expectedPrevious = '2026-09-26T03:59:59.592591Z';
  let comparison;
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first && url.searchParams.get('toUtc') === exactFrom)
      comparison = Object.fromEntries(url.searchParams);
  });
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo, compare: '1', healthSection: 'summary' })}`);
  await expect.poll(() => comparison).toMatchObject({ targetId: first, fromUtc: expectedPrevious, toUtc: exactFrom });
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await expect(page.getByText('Previous-period evidence is unavailable.', { exact: true })).toHaveCount(0);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
});

test('requested timeline selection remains explicit when hidden or absent and never selects another event', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&databaseId=6`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  const timeline = investigationTimeline(page), category = timeline.getByRole('combobox', { name: 'Timeline event category', exact: true });
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await planTimelineButtons(page).click();
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  const selectedHash = new URL(page.url()).hash;
  await category.selectOption('deadlock');
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  // Filtering preserves the exact URL selection, including externally restored selections.
  await page.evaluate(hash => { location.hash = hash; }, selectedHash);
  await expect(timeline).toContainText('The selected timeline event is hidden by this category.');
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  await timeline.getByRole('button', { name: 'Show selected timeline event', exact: true }).press('Enter');
  await expect(category).toHaveValue('all');
  await expect(planTimelineButtons(page)).toHaveAttribute('aria-pressed', 'true');
  const parameters = scope(page); parameters.set('timelineEvent', 'plan:missing');
  await page.evaluate(query => { location.hash = '#/health?' + query; }, parameters.toString());
  await expect(timeline).toContainText('The requested timeline event is not present in the loaded evidence for this window. No other event has been selected.');
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  await expect(page.locator('.server-dashboard .chart-crosshair')).toHaveCount(0);
  expect(scope(page).get('timelineEvent')).toBe('plan:missing');
  await page.reload();
  await expect(timeline).toContainText('No other event has been selected.');
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  await timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true }).click();
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
});

test('delayed requested timeline evidence cannot restore a selection after switching servers', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&databaseId=6`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await planTimelineButtons(page).click();
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let delayed = false;
  await page.route(`**/observation-targets/${first}/query-performance/regressions?*`, async route => {
    const response = await route.fetch(); delayed = true; await gate;
    await route.fulfill({ response });
  });
  await page.reload();
  await expect.poll(() => delayed).toBe(true);
  await expect(investigationTimeline(page)).toContainText('Loading the requested timeline event');
  await page.evaluate(hash => { location.hash = hash; }, href('health', second).substring(1));
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  release();
  await expect(investigationTimeline(page).locator('button[aria-pressed="true"]')).toHaveCount(0);
  await expect(investigationTimeline(page)).not.toContainText('Loading the requested timeline event');
  expect(scope(page).get('target')).toBe(second);
  expect(scope(page).get('timelineAt')).toBe(null);
});

test('Resources pointer and keyboard inspection align CPU memory PLE and file latency without changing the workspace window', async ({ page }) => {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'resourceInspectionFixture', value: '1', url: origin }]);
  await page.goto(`${href('resources')}&databaseId=5`);
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 01', exact: true });
  await resources.getByRole('button', { name: 'View read and write latency trend for database 5, file Orders_data', exact: true }).click();
  const charts = resources.locator('.overview-chart');
  await expect(charts).toHaveCount(9);
  const cpu = resources.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true }) }).locator('.overview-chart');
  await cpu.scrollIntoViewIfNeeded();
  const box = await cpu.boundingBox(); expect(box).not.toBeNull();
  await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.5);
  await expect(resources.locator('.chart-crosshair')).toHaveCount(9);
  const pointerLabels = await resources.locator('.chart-crosshair text').allTextContents();
  expect(new Set(pointerLabels).size).toBe(1);
  await assertHistoricalScope(page); await assertDatabaseScope(page, 5);
  const input = resources.getByLabel('Resource inspection time (UTC)', { exact: true });
  await input.fill('2026-09-26T10:30:00.125');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect(resources.getByRole('region', { name: 'Resource chart alignment', exact: true }).getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T10:30:00.125Z');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(9);
  expect(new Set(await resources.locator('.chart-crosshair text').allTextContents()).size).toBe(1);
  await assertHistoricalScope(page); await assertDatabaseScope(page, 5);
  await input.fill('2026-09-26T12:00');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect(input).toHaveAttribute('aria-invalid', 'true');
  await expect(resources.getByRole('alert')).toHaveText('Choose an inspection time inside the shared window, before its end.');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  await input.fill('2026-09-26T09:00');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(9);
  await resources.getByRole('button', { name: 'Clear resource alignment', exact: true }).press('Enter');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  await expect(resources.getByRole('region', { name: 'Resource chart alignment', exact: true }).getByRole('status')).toContainText('No resource alignment selected.');
  await assertHistoricalScope(page); await assertDatabaseScope(page, 5);
});

test('periodic live configuration history retains the selected scan and keyboard focus until explicit latest', async ({ page }) => {
  await timelineFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  let configurationRequests = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === `/api/v1/observation-targets/${first}/health/server-configuration-changes`) configurationRequests++;
  });
  await page.goto(`${href('health', first, '1h')}&healthSection=summary&configurationRun=22222222-2222-4222-8222-222222222222`);
  const selectedRun = page.locator('li[data-configuration-run="22222222-2222-4222-8222-222222222222"]');
  await expect(selectedRun).toBeFocused();
  const initiallyFocusedText = await selectedRun.textContent();
  const initiallyFocusedRequests = configurationRequests;
  // The workspace clock emits six 10-second ticks per history refresh.
  // runFor executes every tick; fastForward intentionally coalesces intervals.
  await page.clock.runFor(61_000);
  await expect.poll(() => configurationRequests).toBeGreaterThan(initiallyFocusedRequests);
  await expect(selectedRun).toHaveText(initiallyFocusedText);
  await expect(selectedRun).toContainText('cost threshold for parallelism');
  await expect(selectedRun).toBeFocused();
  const before = await selectedRun.textContent();
  const timeRange = page.getByRole('combobox', { name: 'Time range', exact: true });
  await timeRange.focus();
  await expect(timeRange).toBeFocused();
  const initialRequests = configurationRequests;
  await page.clock.runFor(61_000);
  await expect.poll(() => configurationRequests).toBeGreaterThan(initialRequests);
  // Independent timeline polling continues; the selected configuration receipt remains pinned.
  await expect(selectedRun).toHaveText(before);
  await expect(selectedRun).toContainText('cost threshold for parallelism');
  await expect(timeRange).toBeFocused();
  await expect(timeRange).toHaveValue('1h');
  expect(scope(page).get('configurationRun')).toBe('22222222-2222-4222-8222-222222222222');
  await expect(page.getByText(/Retained configuration history\. Workspace refresh/)).toBeVisible();
  await page.getByRole('button', { name: 'Return to latest configuration history', exact: true }).click();
  await expect(selectedRun).not.toHaveText(before);
  await expect(page.getByText(/Latest history for the selected window\./)).toBeVisible();
  await expect(timeRange).toHaveValue('1h');
  expect(scope(page).get('configurationRun')).toBe('22222222-2222-4222-8222-222222222222');
});

test('investigation timeline remains usable when dashboard metric series contain no observations', async ({ page }) => {
  await timelineFixture(page);
  await page.route('**/api/v1/overview?*', async route => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, evidence: body.evidence.map(item => ({ ...item, series: [] })) } });
  });
  await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  const timeline = investigationTimeline(page);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  const emptyCharts = page.locator('.server-dashboard .overview-empty');
  await expect.poll(() => emptyCharts.count()).toBeGreaterThanOrEqual(4);
  for (const placeholder of await emptyCharts.all()) await expect(placeholder).toHaveText('No comparable observations in this window. Missing samples are not plotted as zero.');
  const comparison = planTimelineButtons(page).first();
  const eventTime = await comparison.locator('xpath=ancestor::li').locator('time').getAttribute('datetime');
  await comparison.focus();
  await comparison.press('Enter');
  await expect(comparison).toHaveAttribute('aria-pressed', 'true');
  await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' })).toContainText('Chart crosshair:');
  await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' }).locator('time')).toHaveAttribute('datetime', eventTime);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  await expect(page.locator('.server-dashboard-chart:has(.overview-empty) .chart-crosshair')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Wait categories over time', exact: true }).locator('.chart-crosshair')).toHaveCount(1);
});

test('timeline category keyboard control aligns shared charts and preserves exact hidden selections', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  const timeline = investigationTimeline(page);
  const category = timeline.getByRole('combobox', { name: 'Timeline event category', exact: true });
  await category.focus();
  await category.press('Home');
  const planOptionIndex = await category.locator('option').evaluateAll(options => options.findIndex(option => option.value === 'plan_regression'));
  expect(planOptionIndex).toBeGreaterThan(0);
  for (let index = 0; index < planOptionIndex; index++) await category.press('ArrowDown');
  await category.press('Enter');
  await expect(category).toHaveValue('plan_regression');
  await expect(timeline.getByRole('listitem')).toHaveCount(2);
  const comparison = planTimelineButtons(page).first();
  const eventTime = await timeline.getByRole('listitem').first().locator('time').getAttribute('datetime');
  await comparison.focus();
  await comparison.press('Enter');
  await expect(comparison).toHaveAttribute('aria-pressed', 'true');
  await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' })).toContainText('Chart crosshair:');
  await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' }).locator('time')).toHaveAttribute('datetime', eventTime);
  await expect.poll(() => page.locator('.server-dashboard .chart-crosshair').count()).toBeGreaterThanOrEqual(4);
  await timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true }).press('Enter');
  await expect(comparison).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('.server-dashboard .chart-crosshair')).toHaveCount(0);
  await comparison.press('Enter');
  await category.focus();
  await category.press('Home');
  await category.press('ArrowDown');
  await category.press('Enter');
  await expect(category).toHaveValue('deadlock');
  await expect(timeline.getByRole('listitem')).toHaveCount(1);
  await expect(timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true })).toBeVisible();
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  await expect.poll(() => page.locator('.server-dashboard .chart-crosshair').count()).toBeGreaterThanOrEqual(4);
  await timeline.getByRole('button', { name: 'Show selected timeline event', exact: true }).press('Enter');
  await expect(category).toHaveValue('all');
  await expect(comparison).toBeFocused();
  await expect(comparison).toHaveAttribute('aria-pressed', 'true');
  await timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true }).press('Enter');
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
  await expect(page.locator('.server-dashboard .chart-crosshair')).toHaveCount(0);
  await timeline.getByText('Timeline coverage', { exact: true }).click();
  await expect(timeline).toContainText('Missing observations are not evidence of normal health.');
});

test('timeline database context filters plan comparisons while retaining server deadlocks and opens the current plan', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&compare=1`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await expect(planTimelineButtons(page)).toHaveAccessibleName(/database 6/);
  const timeline = investigationTimeline(page);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  await expect(timeline.getByRole('button', { name: 'Align charts: Warehouse options detected', exact: true })).toHaveCount(1);
  await expect(timeline.getByRole('button', { name: 'Align charts: Orders options detected', exact: true })).toHaveCount(0);
  await expect(timeline.getByRole('button', { name: 'Align charts: Server configuration detected', exact: true })).toHaveCount(1);
  const evidence = timeline.getByRole('link', { name: /^Open evidence: Plan regression comparison/ });
  const params = new URLSearchParams((await evidence.getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(params)).toMatchObject({ target: first, databaseId: '6', range: 'custom', from, to, compare: '1', queryView: 'regressed', queryMetric: 'duration', source: 'query_store', queryFingerprint: 'd'.repeat(64), planFingerprint: 'e'.repeat(64) });
  await evidence.focus();
  await evidence.press('Enter');
  await assertHistoricalScope(page);
  await assertDatabaseScope(page, 6);
  const regressions = page.getByRole('region', { name: 'Possible plan regression comparisons', exact: true });
  await expect(regressions).toContainText('dddddddddddd');
  await expect(regressions).not.toContainText('bbbbbbbbbbbb');
  await expect(page.getByRole('region', { name: 'Possible plan regressions', exact: true })).toContainText('This is a correlation, not proof');
  await page.goBack();
  await assertDatabaseScope(page, 6);
  await browseTimelineEvents(page);
  const configurationEvidence = investigationTimeline(page).getByRole('link', { name: 'Open evidence: Server configuration detected', exact: true });
  await expect(configurationEvidence).toBeVisible();
  const configurationParams = new URLSearchParams((await configurationEvidence.getAttribute('href')).split('?')[1]);
  expect(configurationParams.get('healthSection')).toBe('summary');
  expect(configurationParams.get('configurationRun')).toBe('22222222-2222-4222-8222-222222222222');
  await configurationEvidence.click();
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect.poll(() => scope(page).get('healthSection')).toBe('summary');
  await assertDatabaseScope(page, 6);
  const selectedConfigurationRun = page.locator('li[data-configuration-run="22222222-2222-4222-8222-222222222222"]');
  await expect(selectedConfigurationRun).toHaveCount(1);
  await expect(selectedConfigurationRun).toHaveAttribute('tabindex', '-1');
  await expect(selectedConfigurationRun).toBeFocused();
  await expect(selectedConfigurationRun).toContainText('cost threshold for parallelism');
  await page.reload();
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
  await assertDatabaseScope(page, 6);
  await expect(selectedConfigurationRun).toBeFocused();
  await ensureUtcDisplay(page);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
  await assertDatabaseScope(page, 6);
  await expect.poll(() => scope(page).get('healthSection')).toBe('summary');
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
});

test('failed plan comparison refresh removes old leads and reports unavailable coverage honestly', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await page.route(`**/${first}/query-performance/regressions?*`, route => route.fulfill({ status: 503, json: {} }));
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(planTimelineButtons(page)).toHaveCount(0);
  const timeline = investigationTimeline(page);
  await timeline.getByText('Timeline coverage', { exact: true }).click();
  await expect(timeline).toContainText(/plan.*unavailable/i);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  await page.unroute(`**/${first}/query-performance/regressions?*`);
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(planTimelineButtons(page)).toHaveCount(2);
});

test('delayed old-target plan comparisons never reappear in the new server timeline', async ({ page }) => {
  await timelineFixture(page);
  let release, requested = false, continued = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/query-performance/regressions?*`, async route => { requested = true; await gate; await route.continue().catch(() => {}); continued = true; });
  try {
    await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
    await expect.poll(() => requested).toBe(true);
    await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
    await browseTimelineEvents(page);
    await expect(planTimelineButtons(page)).toHaveCount(2);
    await expect(planTimelineButtons(page).first()).toHaveAccessibleName(/query 777777777777/);
    await expect(planTimelineButtons(page).last()).toHaveAccessibleName(/query 888888888888/);
  } finally { release(); }
  await expect.poll(() => continued).toBe(true);
  await expect(investigationTimeline(page).getByRole('button', { name: /Plan regression comparison.*query (bbbbbbbbbbbb|dddddddddddd)/ })).toHaveCount(0);
  await assertHistoricalScope(page, second);
});

test('timeline plan comparisons clear during a window read and reject its delayed result after another rewind', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(href('health')); await revealHealthSupport(page); await revealDashboardSupport(page);
  await browseTimelineEvents(page);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  let release, requested = false, continued = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/query-performance/regressions?*`, async route => {
    const start = new URL(route.request().url()).searchParams.get('fromUtc');
    if (Date.parse(start) === Date.parse('2026-09-26T09:00:00Z')) { requested = true; await gate; await route.continue().catch(() => {}); continued = true; }
    else await route.continue();
  });
  const editWindow = async (start, end) => {
    await ensureUtcDisplay(page);
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill(start);
    await page.getByLabel('To (UTC)', { exact: true }).fill(end);
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  };
  try {
    await editWindow('2026-09-26T09:00', '2026-09-26T11:00');
    await expect.poll(() => requested).toBe(true);
    await browseTimelineEvents(page);
    await expect(planTimelineButtons(page)).toHaveCount(0);
    await editWindow('2026-09-26T10:00', '2026-09-26T11:00');
    await browseTimelineEvents(page);
    await expect(planTimelineButtons(page)).toHaveCount(2);
  } finally { release(); }
  await expect.poll(() => continued).toBe(true);
  for (const button of await planTimelineButtons(page).all()) {
    const row = button.locator('xpath=ancestor::li');
    const at = await row.locator('time').getAttribute('datetime');
    expect(Date.parse(at)).toBeGreaterThanOrEqual(Date.parse('2026-09-26T10:00:00Z'));
    expect(Date.parse(at)).toBeLessThan(Date.parse('2026-09-26T11:00:00Z'));
    const params = new URLSearchParams((await row.getByRole('link').getAttribute('href')).split('?')[1]);
    expect(Date.parse(params.get('from'))).toBe(Date.parse('2026-09-26T10:00:00Z'));
    expect(Date.parse(params.get('to'))).toBe(Date.parse('2026-09-26T11:00:00Z'));
  }
});

const healthContractFailure = 'Health evidence returned an invalid or mismatched response. Refresh to retry.';
test('malformed foreign and oversized Health evidence fails visibly without crashing the dashboard', async ({ page }) => {
  await fixture(page);
  const crashes = []; page.on('pageerror', error => crashes.push(error.message));
  let mode = 'malformed';
  await page.route(`**/observation-targets/${first}/health`, async route => {
    const response = await route.fetch(), body = await response.json();
    if (mode === 'malformed') body.coreMetrics = null;
    else if (mode === 'foreign') body.instanceId = second;
    else return route.fulfill({ status: 200, contentType: 'application/json', headers: { 'content-length': String(9 * 1024 * 1024) }, body: '{}' });
    await route.fulfill({ response, json: body });
  });
  for (const next of ['malformed', 'foreign', 'oversized']) {
    mode = next;
    if (next === 'malformed') await page.goto(`${href('health')}&healthSection=summary`); await revealHealthSupport(page); await revealDashboardSupport(page);
    else { await page.reload(); await revealHealthSupport(page); await revealDashboardSupport(page); }
    await expect(page.getByRole('alert').filter({ hasText: healthContractFailure })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Latest SQL buckets and hourly baselines', exact: true })).toBeVisible();
    await expect(page.getByText('Evaluated using repository time:', { exact: false })).toHaveCount(0);
    await assertHistoricalScope(page);
  }
  expect(crashes).toEqual([]);
});

test('Resources rejects a malformed refreshed file snapshot while retaining independent authorized charts', async ({ page }) => {
  await resourceUrlFixture(page);
  const crashes = []; page.on('pageerror', error => crashes.push(error.message));
  await page.goto(href('resources'));
  const files = page.getByRole('region', { name: 'Historical database file facts', exact: true });
  await expect(files).toContainText('Orders_data');
  await page.route(`**/observation-targets/${first}/resources/files/history/*?*`, async route => {
    const response = await route.fetch(), body = await response.json();
    body.items[0].readCount = '-1'; body.items[0].logicalName = 'SYNTHETIC rejected file';
    await route.fulfill({ response, json: body });
  });
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Database file history returned invalid or mismatched evidence.' })).toBeVisible();
  await expect(files).toContainText('Orders_data');
  await expect(page.getByText('SYNTHETIC rejected file', { exact: false })).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Per-NUMA page life expectancy', exact: true }).locator('.overview-chart')).toHaveCount(2);
  await assertHistoricalScope(page); expect(crashes).toEqual([]);
});

test('malformed database option and mismatched configuration history remain explicit gaps in Health', async ({ page }) => {
  await timelineFixture(page);
  const crashes = []; page.on('pageerror', error => crashes.push(error.message));
  await page.route(`**/observation-targets/${first}/health/database-option-changes?*`, async route => {
    const response = await route.fetch(), body = await response.json(); body.items = null;
    await route.fulfill({ response, json: body });
  });
  await page.route(`**/observation-targets/${first}/health/server-configuration-changes?*`, async route => {
    const response = await route.fetch(), body = await response.json(); body.fromUtc = '2026-09-26T08:00:00.000001Z';
    await route.fulfill({ response, json: body });
  });
  await page.goto(`${href('health')}&healthSection=databases`); await revealHealthSupport(page); await revealDashboardSupport(page);
  await expect(page.getByRole('region', { name: 'Database option changes', exact: true })).toContainText('Option history is unavailable.');
  await page.getByRole('tab', { name: 'Dashboard', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Server configuration changes', exact: true })).toContainText('Configuration history is unavailable or changed while paging.');
  await expect(page.getByRole('region', { name: 'Investigation timeline', exact: true })).not.toContainText('Server configuration detected');
  expect(crashes).toEqual([]);
});

async function queryRowFixture(page, state = 'normal') {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'queryRowTrendFixture', value: '1', url: origin }, { name: 'queryRowTrendState', value: state, url: origin }]);
}
const runtimeTrendRows = page => page.getByRole('region', { name: 'Ranked query observations', exact: true }).getByRole('group', { name: /^Ranking trend:/ });
const waitTrendRows = page => page.getByRole('region', { name: 'Query Store wait ranking', exact: true }).getByRole('group', { name: /^Ranking trend:/ });
const cacheTrendRows = page => page.getByRole('region', { name: 'Plan cache interval ranking', exact: true }).getByRole('group', { name: /^Ranking trend:/ });
const trendRequests = request => /\/history\/[0-9a-f]{64}\/plans\/[0-9a-f]{64}\/trend$/.test(new URL(request.url()).pathname);
const protectedQueryRequest = request => /\/runs\/.*(?:\/text|\/content)$/.test(new URL(request.url()).pathname);

test('ranking row trends load only visible plans with two concurrent reads and shared runtime wait evidence', async ({ page }) => {
  await queryRowFixture(page);
  await page.context().addCookies([{ name: 'queryRowTrendCount', value: '35', url: origin }]);
  await page.setViewportSize({ width: 1440, height: 1000 });
  const requests = [], protectedReads = [];
  page.on('request', request => { if (trendRequests(request)) requests.push(request.url()); if (protectedQueryRequest(request)) protectedReads.push(request.url()); });
  let release, active = 0, maximumActive = 0;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/query-performance/databases/*/history/*/plans/*/trend?*', async route => {
    active++; maximumActive = Math.max(maximumActive, active);
    const response = await route.fetch(); await gate;
    active--; await route.fulfill({ response });
  });
  await page.goto(href('queries'));
  await expect(runtimeTrendRows(page)).toHaveCount(35);
  await runtimeTrendRows(page).first().scrollIntoViewIfNeeded();
  await expect.poll(() => requests.length).toBe(2);
  expect(requests.length).toBeLessThan(35);
  await expect(runtimeTrendRows(page).nth(34)).toContainText('Trend loads when visible.');
  release();
  // Make the bounded current page visible; every loaded plan can obtain evidence.
  await page.setViewportSize({ width: 1440, height: 20000 });
  await expect.poll(() => requests.length).toBe(35);
  await expect(runtimeTrendRows(page).nth(34).getByRole('img')).toBeVisible();
  await expect(waitTrendRows(page).first().getByRole('img')).toBeVisible();
  expect(maximumActive).toBe(2);
  expect(new Set(requests).size).toBe(35);
  expect(requests.every(value => { const query = new URL(value).searchParams; return query.get('fromUtc') === from && query.get('toUtc') === to; })).toBe(true);
  expect(protectedReads).toEqual([]);
});

test('ranking row trends expose partial hourly sums and gap reset coverage then allow unavailable evidence to retry', async ({ page }) => {
  await queryRowFixture(page, 'partial');
  const protectedReads = []; page.on('request', request => { if (protectedQueryRequest(request)) protectedReads.push(request.url()); });
  await page.goto(href('queries'));
  let row = runtimeTrendRows(page).first();
  await row.scrollIntoViewIfNeeded();
  await expect(row.locator('svg circle')).toHaveCount(2);
  await expect(row.locator('svg path')).toHaveCount(0);
  await row.getByText('Trend values and coverage', { exact: true }).click();
  await expect(row).toContainText('2 comparable groups; 4 excluded groups; 1 missing hourly buckets.');
  await expect(row).toContainText('No observed groups; gap');
  await expect(row).toContainText('120'); await expect(row).toContainText('240');
  const waits = waitTrendRows(page).first();
  await waits.scrollIntoViewIfNeeded();
  await expect(waits.locator('svg circle')).toHaveCount(2);
  await waits.getByText('Trend values and coverage', { exact: true }).click();
  await expect(waits).toContainText('All-category waits ms/hour bucket');
  await expect(waits).toContainText('100'); await expect(waits).toContainText('220');
  await page.context().addCookies([{ name: 'queryRowTrendState', value: 'unavailable', url: origin }]);
  await page.reload(); row = runtimeTrendRows(page).first();
  await row.scrollIntoViewIfNeeded();
  await expect(row).toContainText('Row trend is unavailable.');
  await expect(row.getByRole('img')).toHaveCount(0);
  await page.context().addCookies([{ name: 'queryRowTrendState', value: 'normal', url: origin }]);
  await row.getByRole('button', { name: 'Retry row trend', exact: true }).click();
  await expect(row.locator('svg circle')).toHaveCount(2);
  expect(protectedReads).toEqual([]);
});

test('ranking row trends show cache interval rates with reset gaps and inspect the exact incarnation', async ({ page }) => {
  await queryRowFixture(page);
  const reads = [], protectedReads = [];
  page.on('request', request => {
    if (/\/query-performance\/cache\/databases\/.*\/history\//.test(request.url())) reads.push(request.url());
    if (protectedQueryRequest(request)) protectedReads.push(request.url());
  });
  await page.goto(`${href('queries')}&queryView=cache`);
  const row = cacheTrendRows(page).first(); await row.scrollIntoViewIfNeeded();
  await expect(row.locator('svg path')).toHaveCount(2);
  await expect(row.locator('svg circle')).toHaveCount(4);
  const disclosure = row.getByText('Trend values and coverage', { exact: true });
  await disclosure.focus(); await disclosure.press('Enter');
  await expect(disclosure).toBeFocused();
  await expect(row).toContainText('CPU ms/s');
  await expect(row).toContainText('2 plotted qualified intervals; 2 unavailable comparisons; 0 invalid intervals. Newest 4 of 4 rows');
  await expect(row).toContainText('reset'); await expect(row).toContainText('stale'); await expect(row).toContainText('partial capture');
  expect(reads.every(value => new URL(value).searchParams.get('limit') === '100' && !new URL(value).searchParams.has('cursor'))).toBe(true);
  await row.getByRole('button', { name: 'Inspect trend', exact: true }).click();
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ target: first, databaseId: '5', queryView: 'cache', cacheDatabaseId: '5', cacheQuery: '000000001000' + '0'.repeat(52), cachePlan: '000000002000' + '0'.repeat(52), cacheIncarnation: '000000003000' + '0'.repeat(52) });
  await assertHistoricalScope(page); expect(protectedReads).toEqual([]);
});

test('ranking row trends cannot publish an old metric after a delayed read', async ({ page }) => {
  await queryRowFixture(page);
  let release, pending = false, firstRead = true;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/query-performance/databases/*/history/*/plans/*/trend?*', async route => {
    const response = await route.fetch();
    if (firstRead) { firstRead = false; pending = true; await gate; }
    await route.fulfill({ response });
  });
  await page.goto(href('queries'));
  await runtimeTrendRows(page).first().scrollIntoViewIfNeeded();
  await expect.poll(() => pending).toBe(true);
  await page.getByRole('combobox', { name: 'Rank by', exact: true }).selectOption('logical_reads');
  const row = runtimeTrendRows(page).first(); await row.scrollIntoViewIfNeeded();
  await expect(row.getByRole('img')).toHaveAccessibleName(/Logical reads\/hour bucket/);
  await row.getByText('Trend values and coverage', { exact: true }).click();
  await expect(row).toContainText('4000'); await expect(row).toContainText('7000');
  release();
  await expect(row).toContainText('Logical reads/hour bucket');
  await expect(row).not.toContainText('CPU ms/hour bucket');
  await expect.poll(() => scope(page).get('queryMetric')).toBe('logical_reads');
});

test('ranking row trends discard delayed old target and window values', async ({ page }) => {
  await queryRowFixture(page);
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/query-performance/databases/*/history/*/plans/*/trend?*`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.goto(href('queries'));
  await runtimeTrendRows(page).first().scrollIntoViewIfNeeded();
  await expect.poll(() => pending).toBe(true);
  const nextFrom = '2026-09-26T09:00:00.000Z', nextTo = '2026-09-26T11:00:00.000Z';
  await page.evaluate(hash => { location.hash = hash; }, `#/queries?${new URLSearchParams({ target: second, range: 'custom', from: nextFrom, to: nextTo })}`);
  const row = runtimeTrendRows(page).first(); await row.scrollIntoViewIfNeeded();
  await expect(row.locator('svg circle')).toHaveCount(1);
  await expect(row).toContainText('One point; no trend line');
  await row.getByText('Trend values and coverage', { exact: true }).click();
  await expect(row).toContainText('1120');
  release();
  await expect(row).toContainText('1120');
  await expect(row.locator('svg circle')).toHaveCount(1);
  await assertHistoricalScope(page, second, nextFrom, nextTo);
});

const selectedDeadlockIds = ['33333333-3333-4333-8333-333333333333', '33333333-3333-4333-8333-333333333334', '33333333-3333-4333-8333-333333333335'];
const selectedDeadlockTimes = ['2026-09-26T11:00:00.123456Z', '2026-09-26T10:00:00.654321Z', '2026-09-26T09:00:00.000001Z'];
const deadlockSelectionHref = (index, target = first) => `${href('deadlocks', target)}&${new URLSearchParams({ deadlockEvent: selectedDeadlockIds[index], deadlockAt: selectedDeadlockTimes[index], databaseId: '6' })}`;
const retainedDeadlockDetail = page => page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
const retainedDeadlockEvents = page => page.getByRole('complementary', { name: 'Captured deadlock events', exact: true });
async function deadlockUrlFixture(page) {
  await databaseFixture(page); await page.context().addCookies([{ name: 'deadlockSelectionFixture', value: '1', url: origin }]);
}

test('deadlock URL selection preserves exact event time and loaded row focus through reload back and forward', async ({ page }) => {
  await deadlockUrlFixture(page);
  await page.goto(`${href('deadlocks')}&databaseId=6`);
  const events = retainedDeadlockEvents(page), detail = retainedDeadlockDetail(page);
  await expect(events.locator('button.event-card')).toHaveCount(25);
  const secondEvent = events.locator('button.event-card').nth(1);
  await secondEvent.focus(); await secondEvent.press('Enter');
  await expect(secondEvent).toBeFocused();
  await expect(secondEvent).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ deadlockEvent: selectedDeadlockIds[1], deadlockAt: selectedDeadlockTimes[1], databaseId: '6' });
  await expect(detail.getByRole('table')).toContainText('81');
  await page.reload(); await expect(detail.getByRole('table')).toContainText('81');
  await expect(events.locator('button.event-card').nth(1)).toHaveAttribute('aria-pressed', 'true');
  await events.locator('button.event-card').first().click();
  await expect.poll(() => scope(page).get('deadlockEvent')).toBe(selectedDeadlockIds[0]);
  await page.goBack(); await expect(detail.getByRole('table')).toContainText('81');
  await expect.poll(() => scope(page).get('deadlockAt')).toBe(selectedDeadlockTimes[1]);
  await page.goForward(); await expect(detail.getByRole('table')).toContainText('51');
  await assertHistoricalScope(page); await assertDatabaseScope(page, 6);
});

test('deadlock URL restoration reads an exact off-page event without substituting the first listed event', async ({ page }) => {
  await deadlockUrlFixture(page);
  const lists = [], detailReads = [];
  page.on('request', request => { const url = new URL(request.url()); if (url.pathname.endsWith('/deadlocks')) lists.push(url); else if (url.pathname.endsWith('/deadlocks/' + selectedDeadlockIds[2])) detailReads.push(url); });
  await page.goto(deadlockSelectionHref(2));
  const detail = retainedDeadlockDetail(page), events = retainedDeadlockEvents(page);
  await expect(detail.getByRole('table')).toContainText('91');
  await expect(page.getByText('The requested deadlock is not on the loaded event page. Its retained detail is shown without selecting another event.', { exact: true })).toBeVisible();
  await expect(events.locator('button[aria-pressed=true]')).toHaveCount(0);
  expect(lists).toHaveLength(1); expect(lists[0].searchParams.get('cursor')).toBe(null);
  expect(detailReads).toHaveLength(1);
  await events.getByRole('button', { name: 'Next', exact: true }).click();
  await expect(events.locator('button.event-card')).toHaveCount(1);
  await expect(events.locator('button.event-card')).toHaveAttribute('aria-pressed', 'true');
  await expect(detail.getByRole('table')).toContainText('91');
  await expect.poll(() => scope(page).get('deadlockAt')).toBe(selectedDeadlockTimes[2]);
});

test('missing invalid and mismatched deadlock URL identities never select a different event', async ({ page }) => {
  await deadlockUrlFixture(page);
  const unknown = 'ffffffff-ffff-4fff-8fff-ffffffffffff';
  await page.goto(`${href('deadlocks')}&${new URLSearchParams({ deadlockEvent: unknown, deadlockAt: selectedDeadlockTimes[0] })}`);
  const unavailable = 'The requested deadlock detail is unavailable. It may have expired or access may have changed. No other event has been selected.';
  await expect(page.getByRole('alert').filter({ hasText: unavailable })).toBeVisible();
  await expect(retainedDeadlockDetail(page).getByRole('table')).toHaveCount(0);
  await expect(retainedDeadlockEvents(page).locator('button[aria-pressed=true]')).toHaveCount(0);
  await page.goto(deadlockSelectionHref(0, second));
  await expect(page.getByRole('alert').filter({ hasText: unavailable })).toBeVisible();
  await expect(retainedDeadlockDetail(page).getByRole('table')).toHaveCount(0);
  await page.goto(`${href('deadlocks')}&deadlockEvent=${selectedDeadlockIds[0]}`);
  await expect(page.getByText('The requested deadlock selection is invalid or contradicts the workspace context. Clear the deadlock selection to continue.', { exact: true })).toBeVisible();
  await expect(retainedDeadlockDetail(page).getByRole('img')).toHaveCount(0);
  await page.route(`**/deadlocks/${selectedDeadlockIds[0]}`, async route => {
    const response = await route.fetch(), body = await response.json(); body.summary.occurredAtUtc = '2026-09-26T11:00:00.123457Z';
    await route.fulfill({ response, json: body });
  });
  await page.goto(deadlockSelectionHref(0));
  await expect(page.getByRole('alert').filter({ hasText: unavailable })).toBeVisible();
  await expect(retainedDeadlockDetail(page).getByRole('table')).toHaveCount(0);
  await page.unroute(`**/deadlocks/${selectedDeadlockIds[0]}`);
  await page.getByRole('button', { name: 'Clear deadlock selection', exact: true }).click();
  await expect.poll(() => scope(page).get('deadlockEvent')).toBe(null);
  await expect.poll(() => scope(page).get('deadlockAt')).toBe(null);
  await expect(retainedDeadlockDetail(page).getByRole('table')).toContainText('51');
});

test('delayed deadlock restoration cannot replace the selected event after target and window navigation', async ({ page }) => {
  await deadlockUrlFixture(page);
  let release, pending = false; const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/deadlocks/${selectedDeadlockIds[0]}`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.goto(deadlockSelectionHref(0)); await expect.poll(() => pending).toBe(true);
  const nextFrom = '2026-09-26T09:30:00.000Z', nextTo = '2026-09-26T11:00:00.000Z';
  await page.evaluate(hash => { location.hash = hash; }, `#/deadlocks?${new URLSearchParams({ target: second, range: 'custom', from: nextFrom, to: nextTo, deadlockEvent: '33333333-3333-4333-8333-333333333344', deadlockAt: selectedDeadlockTimes[1] })}`);
  const detail = retainedDeadlockDetail(page);
  await expect(detail.getByRole('table')).toContainText('181');
  release(); await expect(detail.getByRole('table')).toContainText('181');
  await expect(detail.getByRole('table')).not.toContainText('51');
  await assertHistoricalScope(page, second, nextFrom, nextTo);
});

const adminSelections = [
  { page: 'rules', parameter: 'ruleId', prefix: '66666666-6666-4666-8666-', list: 'Alert rule list', detail: 'SYNTHETIC rule 2' },
  { page: 'maintenance', parameter: 'windowId', prefix: '77777777-7777-4777-8777-', list: 'Configured maintenance windows', detail: 'Maintenance window' },
  { page: 'destinations', parameter: 'destinationId', prefix: '88888888-8888-4888-8888-', list: 'Configured destinations', detail: 'Windows Event Log' },
];
for (const admin of adminSelections) {
  test(`exact ${admin.page} administration IDs restore across bounded pages reload and history without mutations`, async ({ page }) => {
    await databaseFixture(page); await page.context().addCookies([{ name: 'adminSelectionFixture', value: '1', url: origin }]);
    const mutations = [], reads = [];
    page.on('request', request => { if (!['GET', 'HEAD'].includes(request.method())) mutations.push(request.url()); if (new URL(request.url()).pathname.endsWith('/alerts/' + admin.page)) reads.push(request.url()); });
    const id = admin.prefix + '000000000002';
    await page.goto(`${href(admin.page)}&databaseId=6&${admin.parameter}=${id.toUpperCase()}`);
    const detail = page.getByRole('complementary', { name: admin.detail, exact: true });
    await expect(detail).toContainText(id);
    const list = page.getByRole('region', { name: admin.list, exact: true });
    await expect(list.locator('button.table-link')).toHaveCount(2);
    expect(reads).toHaveLength(2);
    const firstItem = list.locator('button.table-link').first();
    await firstItem.focus(); await firstItem.press('Enter');
    await expect(firstItem).toBeFocused(); await expect(firstItem).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(() => scope(page).get(admin.parameter)).toBe(admin.prefix + '000000000001');
    await page.goBack(); await expect(detail).toContainText(id);
    await page.goForward(); await expect(list.locator('button.table-link').first()).toHaveAttribute('aria-pressed', 'true');
    await page.goBack(); await page.reload(); await expect(detail).toContainText(id);
    await page.goto(`${href(admin.page)}&${admin.parameter}=${admin.prefix}000000000999`);
    await expect(page.getByText('The selected administration item is unavailable on this server. It may have been removed or fall outside your access. Select another item.', { exact: true })).toBeVisible();
    await expect(page.locator('.detail-pane').getByRole('heading')).toHaveCount(0);
    await expect(list.locator('button[aria-pressed=true]')).toHaveCount(0);
    await page.goto(`${href(admin.page)}&${admin.parameter}=${id}&${admin.parameter}=${admin.prefix}000000000001`);
    await expect(page.getByText('The administration selection in this URL is invalid. Select an item from the list.', { exact: true })).toBeVisible();
    await expect(page.locator('.detail-pane').getByRole('heading')).toHaveCount(0);
    const wrongParameter = adminSelections.find(candidate => candidate.parameter !== admin.parameter).parameter;
    await page.goto(`${href(admin.page)}&${wrongParameter}=${id}`);
    await expect(page.getByText('The administration selection in this URL is invalid. Select an item from the list.', { exact: true })).toBeVisible();
    await expect(page.locator('.detail-pane').getByRole('heading')).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
    await expect.poll(() => scope(page).get(admin.parameter)).toBe(null);
    await assertHistoricalScope(page, second); expect(mutations).toEqual([]);
  });
}

test('deadlock exact microsecond list bounds and focused event survive a labelled same-context refresh', async ({ page }) => {
  await deadlockUrlFixture(page);
  const preciseFrom = '2026-09-26T08:00:00.0000010Z', preciseTo = '2026-09-26T12:00:00.0000010Z';
  const lists = []; page.on('request', request => { const url = new URL(request.url()); if (url.pathname.endsWith('/deadlocks')) lists.push(url); });
  await page.goto(`/#/deadlocks?${new URLSearchParams({ target: first, range: 'custom', from: preciseFrom, to: preciseTo, deadlockEvent: selectedDeadlockIds[0], deadlockAt: selectedDeadlockTimes[0] })}`);
  const events = retainedDeadlockEvents(page), button = events.locator('button.event-card').first();
  await expect(retainedDeadlockDetail(page).getByRole('table')).toContainText('51');
  expect(lists[0].searchParams.get('fromUtc')).toBe('2026-09-26T08:00:00.000001Z');
  expect(lists[0].searchParams.get('toUtc')).toBe('2026-09-26T12:00:00.000001Z');
  const element = await button.elementHandle(); await button.focus();
  let release, pending = false; const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/deadlocks?*`, async route => { const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response }); });
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).evaluate(element => element.click());
  await expect.poll(() => pending).toBe(true);
  await expect(button).toBeFocused(); expect(await element.evaluate(element => element.isConnected)).toBe(true);
  await expect(page.getByRole('status').filter({ hasText: 'Reading deadlock evidence; received event pages remain displayed...' })).toBeVisible();
  release(); await expect(events).toHaveAttribute('aria-busy', 'false');
  await expect(button).toBeFocused(); expect(await element.evaluate(element => element.isConnected)).toBe(true);
  await expect.poll(() => scope(page).get('deadlockAt')).toBe(selectedDeadlockTimes[0]);
  await assertHistoricalScope(page, first, preciseFrom, preciseTo);
});

test('bounded administration restoration stops at five pages and rejects a repeated cursor without selecting a fallback', async ({ page }) => {
  await fixture(page); await page.context().addCookies([{ name: 'adminSelectionFixture', value: '1', url: origin }]);
  const desired = '66666666-6666-4666-8666-000000000999';
  let mode = 'bounded'; const reads = [];
  await page.route(`**/observation-targets/${first}/alerts/rules?*`, async route => {
    const url = new URL(route.request().url()), cursor = url.searchParams.get('cursor');
    const index = cursor ? Number(Buffer.from(cursor, 'base64').toString('utf8').split(':')[1]) : 0;
    reads.push({ mode, index }); url.searchParams.delete('cursor');
    const response = await route.fetch({ url: url.toString() }), body = await response.json();
    body.items[0].ruleId = mode === 'bounded' && index === 5 ? desired : '66666666-6666-4666-8666-' + String(index + 1).padStart(12, '0');
    body.items[0].name = `SYNTHETIC bounded rule ${index + 1}`;
    body.hasMore = mode === 'cyclic' || index < 5;
    body.nextCursor = body.hasMore ? Buffer.from('page:' + (mode === 'cyclic' ? 1 : index + 1)).toString('base64') : null;
    await route.fulfill({ response, json: body });
  });
  await page.goto(`${href('rules')}&ruleId=${desired}`);
  await expect(page.getByText('The selected administration item is not in the loaded pages. Load more to continue searching, or select another item.', { exact: true })).toBeVisible();
  expect(reads).toHaveLength(5); await expect(page.locator('.detail-pane').getByRole('heading')).toHaveCount(0);
  await page.getByRole('region', { name: 'Alert rule list', exact: true }).getByRole('button', { name: 'Load more', exact: true }).click();
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC bounded rule 6', exact: true })).toContainText(desired);
  expect(reads).toHaveLength(6);
  mode = 'cyclic'; await page.reload();
  await expect(page.getByRole('alert').filter({ hasText: 'The listing repeated a page. Refresh to load it again.' })).toBeVisible();
  expect(reads.filter(item => item.mode === 'cyclic')).toHaveLength(2);
  await expect(page.locator('.detail-pane').getByRole('heading')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Alert rule list', exact: true }).locator('button[aria-pressed=true]')).toHaveCount(0);
});

test('destination history changes close the old editor and a delayed save cannot rewrite the new selection', async ({ page }) => {
  await fixture(page); await page.context().addCookies([{ name: 'adminSelectionFixture', value: '1', url: origin }]);
  const oldId = '88888888-8888-4888-8888-000000000002', nextId = '88888888-8888-4888-8888-000000000001';
  await page.goto(`${href('destinations')}&destinationId=${oldId}`);
  await page.getByRole('complementary', { name: 'Windows Event Log', exact: true }).getByRole('button', { name: 'Manage destination', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Manage destination', exact: true })).toBeVisible();
  let release, command; const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/alerts/destinations/${oldId}`, async route => { command = route.request().postDataJSON(); await gate; await route.fulfill({ status: 204 }); });
  await page.getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect.poll(() => Boolean(command)).toBe(true);
  await page.evaluate(hash => { location.hash = hash; }, `${href('destinations').substring(1)}&destinationId=${nextId}`);
  await expect(page.getByRole('heading', { name: 'Manage destination', exact: true })).toHaveCount(0);
  await expect(page.getByRole('complementary', { name: 'HTTPS webhook', exact: true })).toContainText(nextId);
  release(); await expect.poll(() => scope(page).get('destinationId')).toBe(nextId);
  await expect(page.getByText('Destination updated. Approval is required for the new revision.', { exact: true })).toHaveCount(0);
  await page.getByRole('complementary', { name: 'HTTPS webhook', exact: true }).getByRole('button', { name: 'Manage destination', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Manage destination', exact: true })).toBeVisible();
  await page.evaluate(hash => { location.hash = hash; }, `${href('destinations').substring(1)}&destinationId=${nextId}&destinationId=${oldId}`);
  await expect(page.getByRole('heading', { name: 'Manage destination', exact: true })).toHaveCount(0);
  await expect(page.getByText('The administration selection in this URL is invalid. Select an item from the list.', { exact: true })).toBeVisible();
});

test('quiet dashboard and Resources evidence disclosures remain keyboard accessible and surface incomplete references', async ({ page }) => {
  await fixture(page, 'admin');
  await page.context().addCookies([{ name: 'baselineKpiFixture', value: '1', url: origin }, { name: 'resourceInspectionFixture', value: '1', url: origin }]);
  for (const [surface, label] of [['health', 'Dashboard evidence status'], ['resources', 'Resources evidence status']]) {
    await page.goto(`${href(surface)}&healthSection=summary`);
    const footer = page.getByRole('region', { name: label, exact: true });
    await expect(footer).toContainText('0 reported collection gaps');
    await expect(footer.locator('.compact-evidence-action')).toHaveCount(0);
    const baseline = footer.getByText('Baseline reference details', { exact: true });
    await expect(baseline.locator('xpath=parent::details')).not.toHaveAttribute('open', '');
    await baseline.focus(); await baseline.press('Enter'); await expect(baseline).toBeFocused();
    await expect(footer.locator('.compact-evidence-content').filter({ hasText: 'SQL p10' })).toBeVisible();
    const chart = page.getByText('SQL scheduler CPU evidence: observed', { exact: true });
    await chart.focus(); await chart.press('Enter'); await expect(chart).toBeFocused();
    await expect(chart.locator('xpath=parent::details')).toContainText('2 observed of 2 loaded sample buckets');
    await expect(chart.locator('xpath=parent::details')).toContainText('They do not establish continuous coverage, freshness, or a health verdict.');
    await assertHistoricalScope(page);
  }
  await page.context().addCookies([{ name: 'dashboardBaselineState', value: 'partial', url: origin }]);
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  const footer = page.getByRole('region', { name: 'Resources evidence status', exact: true });
  await expect(footer.locator('.compact-evidence-action').filter({ hasText: 'Partial history remains labelled.' })).toBeVisible();
});

async function openInvestigationWorkspace(page) {
  const summary = page.locator('summary').filter({ hasText: /^Investigation workspace$/ });
  await expect(summary).toBeVisible();
  if (await summary.locator('..').getAttribute('open') === null) {
    await summary.focus(); await page.keyboard.press('Enter');
  }
  await expect(summary.locator('..')).toHaveAttribute('open', '');
}
