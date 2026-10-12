import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { alpha, beta, at, cursor, collector, database, evidence, logicalFile } from './healthFixture.mjs';
import { alertLifecycleHistoryBackend, lifecycleFixtureBinding } from '../tools/alert-lifecycle-history-fixture.mjs';
import { serverLifecycleHistoryBackend, serverLifecycleFixtureBinding } from '../tools/server-lifecycle-history-fixture.mjs';
import { activityFixture } from './activityFixture.mjs';

const sessions = new WeakMap();
const panel = page => page.getByRole('region', { name: /^(Server summary for|Server diagnostics for)/ });
const inventory = page => page.getByRole('region', { name: 'Database health', exact: true });
const files = page => page.getByRole('region', { name: 'Logical-file health', exact: true });
const fact = (container, label) => container.locator('dl > div').filter({ has: container.page().getByText(label, { exact: true }) }).locator('dd');
const json = (route, value) => route.fulfill({ status: 200, contentType: 'application/json',
  headers: { 'Cache-Control': 'no-store' }, body: JSON.stringify(value) });

test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], requests: [], fixtures: new Map([[alpha, evidence(alpha)], [beta, evidence(beta)]]), intercept: undefined };
  const lifecycle = alertLifecycleHistoryBackend({ count: 0, cutoffUtc: at });
  const serverLifecycle = serverLifecycleHistoryBackend({ count: 0, cutoffUtc: at });
  sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== 'http://127.0.0.1:4328') {
      session.unexpected.push(`${request.method()} ${url.href}`); return route.abort();
    }
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    session.requests.push(`${url.pathname}${url.search}`);
    if (request.method() !== 'GET') {
      session.unexpected.push(`${request.method()} ${url.href}`); return route.abort();
    }
    const blocking = /^\/api\/v1\/observation-targets\/([^/]+)\/activity\/blocking\/current$/.exec(url.pathname);
    if (blocking && session.fixtures.has(blocking[1]) && url.searchParams.toString() === 'limit=256')
      return json(route, activityFixture(blocking[1])['blocking/current']);
    if (/^\/api\/v1\/observation-targets\/[^/]+\/lifecycle\/history$/.test(url.pathname)) {
      const item = serverLifecycleFixtureBinding(url);
      if (session.fixtures.has(item.binding.targetId)) return json(route, serverLifecycle.read(item.binding, item.cursor));
    }
    // The real dashboard receives an authorized native empty snapshot. Health
    // remains mounted; unrelated chart/timeline/agent sources are not needed.
    if (url.pathname === '/api/v1/overview' && [alpha, beta].includes(url.searchParams.get('targetId'))) {
      const denied = session.overviewDenied && (session.overviewDenied.period === 'primary'
        || url.searchParams.get('fromUtc') !== '2026-01-15T12:00:00.000Z');
      if (denied) return route.fulfill({ status: session.overviewDenied.status, contentType: 'text/plain', body: 'PRIVATE_OVERVIEW_DENIAL' });
      return json(route, { targetId: url.searchParams.get('targetId'), fromUtc: url.searchParams.get('fromUtc'),
        toUtc: url.searchParams.get('toUtc'), refreshedAtUtc: at, targets: [], excludedTargets: 0, evidence: [] });
    }
    // Retained alert history is independent of the empty Overview snapshot.
    // Exercise its native empty receipt without allowing any backend traffic.
    if (/^\/api\/v1\/observation-targets\/[^/]+\/alerts\/history$/.test(url.pathname)) {
      const item = lifecycleFixtureBinding(url);
      if (session.fixtures.has(item.binding.targetId)) return json(route, lifecycle.read(item.binding, item.cursor));
    }
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/health(?:\/(databases|files|database-option-changes|server-configuration-snapshot|server-configuration-changes))?$/.exec(url.pathname);
    const fixture = match && session.fixtures.get(match[1]);
    if (!fixture) {
      session.unexpected.push(`${request.method()} ${url.href}`); return route.abort();
    }
    if (session.intercept && await session.intercept(route, url, match[1], match[2])) return;
    switch (match[2]) {
      case undefined: return json(route, fixture.target);
      case 'databases': return json(route, fixture.databases);
      case 'files': return json(route, fixture.files);
      case 'database-option-changes': return json(route, { targetId: match[1], fromUtc: url.searchParams.get('fromUtc'),
        toUtc: url.searchParams.get('toUtc'), semantics: 'detected_between_consecutive_complete_inventory_runs', items: [] });
      case 'server-configuration-changes': return json(route, { targetId: match[1], targetRevision: 1, publicationGeneration: 0,
        fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'), coverage: 'never_collected', items: [], nextCursor: null });
      case 'server-configuration-snapshot': return json(route, { targetId: match[1], targetRevision: 1, runId: null,
        observedAtUtc: null, serverMajorVersion: null, collectorEnabled: false, lastOutcome: null,
        coverage: 'never_collected', items: [], nextCursor: null });
      default: throw new Error('Unregistered native health response');
    }
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});

test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Unexpected API/external traffic is aborted before the proxy/backend').toEqual([]);
});

async function open(page, section = 'Collection health', query = '') {
  await page.goto(`/component-tests/health.html${query}`);
  await expect(page.getByRole('heading', { name: 'Health panel harness' })).toBeVisible();
  await expect(panel(page).getByRole('tab', { name: 'Collection health' })).toBeVisible();
  if (section !== 'Collection health') await panel(page).getByRole('tab', { name: section, exact: true }).click();
}

async function auditRecovery(page, info, focus, prefix) {
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-retained-pagers/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 390, 768, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await focus.focus(); await expect(focus).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`${prefix}-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}${prefix}-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`${prefix}-${width}`, { path, contentType: 'image/png' });
  }
}

test('discloses native pending, stale and degraded collector evidence with loss and counters', async ({ page }) => {
  const fixture = evidence();
  fixture.target.state = 'degraded';
  fixture.target.collectors = [
    collector('engine.pending', { state: 'pending', reason: 'never_collected', hasVisibilityGap: true,
      lastExecutionOutcome: null, lastSuccessAtUtc: null, lastAttemptAtUtc: null, durationMilliseconds: null }),
    collector('engine.stale', { state: 'stale', reason: 'evidence_stale', hasVisibilityGap: true }),
    collector('engine.loss', { state: 'degraded', reason: 'sample_loss', hasVisibilityGap: true,
      sampleLossKind: 'source_row_limit', minimumLostItems: 3, minimumLostBytes: 12, lossCountIsExact: false,
      sourceRows: 15, responseBytes: 4096, retryCount: 2, circuitState: 'half_open' }),
    collector('engine.exact', { state: 'degraded', reason: 'sample_loss', hasVisibilityGap: true,
      sampleLossKind: 'ingestion_rejection', minimumLostItems: 1, minimumLostBytes: 8, lossCountIsExact: true }),
  ];
  sessions.get(page).fixtures.set(alpha, fixture);
  await open(page);
  const card = id => panel(page).getByRole('article').filter({ has: page.getByRole('heading', { name: id, exact: true }) });
  await expect(card('engine.pending')).toContainText('No data yet');
  await expect(card('engine.pending')).toContainText('Visibility gap: No observation cycle has completed');
  await expect(fact(card('engine.pending'), 'Last success')).toHaveText('Not observed');
  await expect(card('engine.stale')).toContainText('Stale evidence');
  await expect(card('engine.stale')).toContainText('The latest successful evidence is overdue');
  await expect(card('engine.loss')).toContainText('Degraded visibility');
  await expect(fact(card('engine.loss'), 'Sample loss')).toHaveText('source_row_limit: at least 3 items, 12 bytes');
  await expect(fact(card('engine.exact'), 'Sample loss')).toHaveText('ingestion_rejection: 1 items, 8 bytes');
  for (const [label, value] of [['Source rows', '15'], ['Response bytes', '4096'], ['Retries', '2'], ['Circuit', 'Recovery probe']])
    await expect(fact(card('engine.loss'), label)).toHaveText(value);
  await panel(page).getByRole('tab', { name: 'Dashboard', exact: true }).click();
  await panel(page).getByText('All latest core metric samples', { exact: true }).click();
  await expect(panel(page).getByText('No core metric samples are available.', { exact: true })).toBeVisible();
});

test('unavailable collector reasons offer scoped recovery guidance without granting administrator actions', async ({ page }, info) => {
  const fixture = evidence();
  const longId = `engine.${'timeout_visibility_'.repeat(6)}`;
  fixture.target.state = 'unavailable';
  fixture.target.collectors = [
    collector('engine.permissions', { state: 'unavailable', reason: 'permission_denied', hasVisibilityGap: true }),
    collector(longId, { state: 'unavailable', reason: 'timed_out', hasVisibilityGap: true }),
    collector('engine.discovery', { state: 'pending', reason: 'capability_profile_missing', hasVisibilityGap: true }),
    collector('engine.validation', { state: 'unavailable', reason: 'output_invalid', hasVisibilityGap: true }),
    collector('engine.circuit', { state: 'unavailable', reason: 'circuit_open', circuitState: 'open', hasVisibilityGap: true }),
    collector('engine.disabled', { state: 'disabled' }),
  ];
  fixture.databases.items = [database()];
  sessions.get(page).fixtures.set(alpha, fixture);
  await open(page, 'Databases');
  await inventory(page).getByRole('button', { name: 'Use Synthetic inventory as database context', exact: true }).click();
  await panel(page).getByRole('tab', { name: 'Collection health', exact: true }).click();
  const card = id => panel(page).getByRole('article').filter({ has: page.getByRole('heading', { name: id, exact: true }) });
  await expect(card('engine.permissions')).toContainText('collector Windows identity the required read permissions');
  await expect(card('engine.permissions')).toContainText('Target Administrator access unlocks the permission script generator.');
  await expect(panel(page).getByRole('link', { name: 'Generate permission script', exact: true })).toHaveCount(0);
  await expect(card(longId)).toContainText('server connectivity, response time and collector load');
  await expect(card('engine.discovery')).toContainText('current discovery profile');
  await expect(card('engine.validation')).toContainText('output validation diagnostics');
  await expect(card('engine.circuit')).toContainText('collector will probe for recovery');
  await expect(card('engine.disabled')).toContainText('whether this collector should be enabled');
  for (const source of fixture.target.collectors) {
    const link = card(source.collectorId).getByRole('link', { name: 'Review collection health', exact: true });
    const parameters = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
    expect(Object.fromEntries(parameters)).toEqual({ target: alpha, databaseId: '7', range: 'custom',
      from: '2026-01-15T12:00:00.000Z', to: '2026-01-15T13:00:00.000Z', healthSection: 'collection' });
  }
  await auditRecovery(page, info, card('engine.permissions').getByRole('link', { name: 'Review collection health', exact: true }), 'health-recovery');
  expect(sessions.get(page).requests.every(path => !path.includes('/permissions'))).toBe(true);
});

test('the explicit administrator UI fixture exposes the scoped permission destination and partial-metric guidance', async ({ page }, info) => {
  const fixture = evidence();
  fixture.target.collectors = [collector('engine.permissions', { state: 'unavailable', reason: 'permission_denied', hasVisibilityGap: true })];
  fixture.target.coreMetrics = [{ sampleId: '33333333-3333-4333-8333-333333333333', metricId: 'engine.user_connections', observedAtUtc: at, value: 12, dimensions: [] }];
  fixture.databases.items = [database()];
  sessions.get(page).fixtures.set(alpha, fixture);
  await open(page, 'Databases', '?canManageConfiguration=true');
  await inventory(page).getByRole('button', { name: 'Use Synthetic inventory as database context', exact: true }).click();
  await panel(page).getByRole('tab', { name: 'Dashboard', exact: true }).click();
  await expect(panel(page).locator('.health-metric-strip')).toContainText('Unavailable');
  await expect(panel(page).getByText('For unavailable or incomplete core metrics, review', { exact: false })).toBeVisible();
  await expect(panel(page).getByRole('link', { name: 'collection health', exact: true })).toHaveAttribute('href', /healthSection=collection/);
  await panel(page).getByRole('tab', { name: 'Collection health', exact: true }).click();
  const permission = panel(page).getByRole('link', { name: 'Generate permission script', exact: true });
  await expect(permission).toBeVisible();
  await expect(panel(page)).not.toContainText('Target Administrator access unlocks the permission script generator.');
  const href = await permission.getAttribute('href');
  expect(href.split('?')[0]).toBe('#/permissions');
  expect(Object.fromEntries(new URLSearchParams(href.split('?')[1]))).toEqual({ target: alpha, databaseId: '7', range: 'custom',
    from: '2026-01-15T12:00:00.000Z', to: '2026-01-15T13:00:00.000Z' });
  await auditRecovery(page, info, permission, 'health-permission');
  await permission.press('Enter');
  await expect.poll(() => new URL(page.url()).hash).toBe(href);
  expect(sessions.get(page).requests.every(path => !path.includes('/permissions'))).toBe(true);
});

test('empty rows establish absence only for a current succeeded loss-free collection', async ({ page }) => {
  await open(page, 'Databases');
  const absence = async () => {
    await expect(inventory(page)).toContainText('No database rows were reported by a current, loss-free collection.');
    await expect(files(page)).toContainText('No logical-file rows were reported by a current, loss-free collection.');
  };
  await absence();
  // Each independent guard must exclude a completeness claim, even when all
  // other native collector fields look current. Return to baseline between cases.
  for (const guard of [{ state: 'stale' }, { reason: 'evidence_stale' }, { lastExecutionOutcome: 'partial' },
    { hasVisibilityGap: true }, { sampleLossKind: 'visibility_incomplete' }, { minimumLostItems: 0 },
    { minimumLostBytes: 0 }, { lossCountIsExact: false }, { rejectedRows: 1 }]) {
    sessions.get(page).fixtures.set(alpha, evidence(alpha, guard));
    await page.getByRole('button', { name: 'Refresh health evidence' }).click();
    await expect(inventory(page)).toContainText('Database rows cannot be treated as complete for this snapshot.');
    await expect(files(page)).toContainText('Logical-file rows cannot be treated as complete for this snapshot.');
    await expect(panel(page).getByText(/were reported by a current, loss-free collection\./)).toHaveCount(0);
    sessions.get(page).fixtures.set(alpha, evidence());
    await page.getByRole('button', { name: 'Refresh health evidence' }).click();
    await absence();
  }
});

test('keeps exact large file counters, bounded page evidence and database context', async ({ page }) => {
  const fixture = evidence();
  fixture.databases.items = [database(7, 'First database')]; fixture.databases.nextCursor = cursor;
  fixture.files.items = [logicalFile()]; fixture.files.nextCursor = 'more-files';
  sessions.get(page).fixtures.set(alpha, fixture);
  sessions.get(page).intercept = async (route, url, id, kind) => {
    if (kind !== 'databases' || !url.searchParams.has('cursor')) return false;
    expect(url.searchParams.get('limit')).toBe('25'); expect(url.searchParams.get('cursor')).toBe(cursor);
    await json(route, { ...fixture.databases, instanceId: id, items: [database(8, 'Second database')], nextCursor: null }); return true;
  };
  await open(page, 'Databases');
  const file = files(page).getByRole('article');
  for (const [label, value] of [['Size bytes', '9,007,199,254,740,993'], ['Reads', '9,007,199,254,740,993'],
    ['Writes', '9,007,199,254,740,995'], ['Bytes read', '9,007,199,254,740,997'],
    ['Bytes written', '9,007,199,254,740,999'], ['I/O stall ms', '9,007,199,254,741,001']])
    await expect(fact(file, label)).toHaveText(value);
  await expect(files(page)).toContainText('Additional logical files exist; this view shows the bounded first page.');
  await expect(inventory(page).getByRole('button', { name: 'Previous database page' })).toBeDisabled();
  await inventory(page).getByRole('button', { name: 'Next database page' }).click();
  await expect(inventory(page)).toContainText('Page 2 · 1 databases · End of snapshot');
  await expect(inventory(page).getByRole('heading', { name: 'First database' })).toHaveCount(0);
  await expect(inventory(page).getByRole('button', { name: 'Next database page' })).toBeDisabled();
  expect(sessions.get(page).requests).toContain(`/api/v1/observation-targets/${alpha}/health/databases?limit=25&cursor=page%2B%2F%3Dtwo`);
  await inventory(page).getByRole('button', { name: 'Use Second database as database context' }).click();
  await expect(page.getByLabel('Selected database context')).toHaveText('8');
  await expect(files(page)).toContainText('Showing 0 of 1 loaded logical files for database ID 8. Other files may be on another page.');
  await expect(files(page)).toContainText('No logical-file rows for this database are present in the bounded first page.');
  await inventory(page).getByRole('button', { name: 'Clear database context' }).click();
  // A changed database context starts a fresh first page, rather than reusing the old investigated trail.
  await expect(inventory(page).getByRole('button', { name: 'Previous database page' })).toBeDisabled();
  await expect(inventory(page).getByRole('heading', { name: 'First database' })).toBeVisible();
  await expect(inventory(page)).toContainText('Page 1 · 1 databases · More available');
  await expect(files(page).getByRole('heading', { name: 'Synthetic_data' })).toBeVisible();
});

test('sanitizes a denied native response and requires an explicit access retry', async ({ page }) => {
  const secret = 'provider password=synthetic-secret; connectionString=private; <script>window.healthInjected=true</script>';
  let fail = true;
  sessions.get(page).intercept = async (route, _url, _id, kind) => {
    if (kind !== undefined || !fail) return false;
    await route.fulfill({ status: 403, contentType: 'text/html', body: secret }); return true;
  };
  await page.goto('/component-tests/health.html');
  await expect(panel(page).getByRole('alert')).toHaveText('You are not authorized to view health for this target.');
  await expect(panel(page).getByRole('tab')).toHaveCount(3);
  await expect(panel(page).getByRole('tab', { name: 'Collection health', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  expect(await page.evaluate(() => window.healthInjected)).toBeUndefined();
  fail = false;
  await page.getByRole('button', { name: 'Refresh health evidence' }).click();
  await expect(panel(page).getByRole('alert')).toHaveText('You are not authorized to view health for this target.');
  await panel(page).getByRole('button', { name: 'Retry health access', exact: true }).click();
  await expect(panel(page).getByRole('tab', { name: 'Collection health' })).toBeVisible();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
});

test('definitive denied outer Health poll drops all retained inventory and file receipts', async ({ page }) => {
  const fixture = evidence(); fixture.databases.items = [database(7, 'Protected database')]; fixture.files.items = [logicalFile()];
  sessions.get(page).fixtures.set(alpha, fixture); await open(page, 'Databases');
  await expect(inventory(page)).toContainText('Protected database'); await expect(files(page)).toContainText('Synthetic_data');
  sessions.get(page).intercept = async (route, _url, _id, kind) => {
    if (kind !== undefined) return false;
    await route.fulfill({ status: 403, contentType: 'application/json', body: JSON.stringify({ detail: 'SECRET_DENIAL' }) }); return true;
  };
  await page.getByRole('button', { name: 'Refresh health evidence', exact: true }).click();
  await expect(panel(page).getByRole('alert')).toContainText('not authorized');
  await expect(inventory(page)).toHaveCount(0); await expect(files(page)).toHaveCount(0);
  await expect(panel(page)).not.toContainText('Protected database'); await expect(panel(page)).not.toContainText('SECRET_DENIAL');
});

test('preserves the first page on paging failure and allows a sanitized retry', async ({ page }) => {
  const fixture = evidence(); fixture.databases.items = [database(7, 'First database')]; fixture.databases.nextCursor = cursor;
  sessions.get(page).fixtures.set(alpha, fixture);
  let fail = true;
  sessions.get(page).intercept = async (route, url, _id, kind) => {
    if (kind !== 'databases' || !url.searchParams.has('cursor')) return false;
    if (fail) await route.fulfill({ status: 504, contentType: 'text/plain', body: 'provider diagnostic synthetic-secret' });
    else await json(route, { ...fixture.databases, items: [database(8, 'Recovered database')], nextCursor: null });
    return true;
  };
  await open(page, 'Databases');
  await inventory(page).getByRole('button', { name: 'Next database page' }).click();
  await expect(inventory(page).getByRole('alert')).toHaveText('Database page is unavailable or its snapshot changed. The loaded page remains available; return to latest to retry.');
  await expect(inventory(page).getByRole('heading', { name: 'First database' })).toBeVisible();
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  fail = false;
  await inventory(page).getByRole('button', { name: 'Next database page' }).click();
  await expect(inventory(page).getByRole('heading', { name: 'Recovered database' })).toBeVisible();
  await expect(inventory(page).getByRole('alert')).toHaveCount(0);
});

test('aborts the old server request and prevents its late result from replacing the new context', async ({ page }) => {
  let release, started, delivered;
  const held = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  const finished = new Promise(resolve => { delivered = resolve; });
  const old = evidence(); old.target.collectors = [collector('engine.alpha')];
  const next = evidence(beta); next.target.collectors = [collector('engine.beta')];
  sessions.get(page).fixtures.set(beta, next);
  const cancelled = [];
  page.on('requestfailed', request => { if (new URL(request.url()).pathname === `/api/v1/observation-targets/${alpha}/health`) cancelled.push(request.failure()?.errorText); });
  sessions.get(page).intercept = async (route, _url, id, kind) => {
    if (id !== alpha || kind !== undefined) return false;
    started(); await held;
    try { await json(route, old.target); }
    catch (error) { if (!cancelled.length) throw error; }
    finally { delivered(); }
    return true;
  };
  await page.goto('/component-tests/health.html'); await seen;
  await expect(panel(page).getByText('Loading health evidence…', { exact: true })).toBeVisible();
  await page.getByLabel('Selected server').selectOption('beta');
  await expect(panel(page).getByRole('heading', { name: 'engine.beta' })).toBeVisible();
  await expect.poll(() => cancelled.length).toBe(1);
  release(); await finished;
  await expect(panel(page).getByRole('heading', { name: 'Server summary for Beta' })).toBeVisible();
  await expect(panel(page).getByRole('heading', { name: 'engine.beta' })).toBeVisible();
  await expect(panel(page).getByRole('heading', { name: 'engine.alpha' })).toHaveCount(0);
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
});

test('renders provider-controlled names as inert text and uses the real time provider', async ({ page }) => {
  const name = '<img src="https://invalid.example/health" onerror="window.healthInjected=true">';
  const fixture = evidence(); fixture.databases.items = [database(7, name)]; fixture.files.items = [logicalFile(7, name)];
  sessions.get(page).fixtures.set(alpha, fixture);
  await open(page, 'Databases');
  await expect(inventory(page).getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(files(page).getByRole('heading', { name, exact: true })).toBeVisible();
  await expect(panel(page).locator('img, script')).toHaveCount(0);
  expect(await page.evaluate(() => window.healthInjected)).toBeUndefined();
  await expect(fact(inventory(page).getByRole('article'), 'Observed')).toContainText('13:00:00 UTC');
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(fact(inventory(page).getByRole('article'), 'Observed')).toContainText('08:00:00 EST');
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('utc');
  await expect(fact(inventory(page).getByRole('article'), 'Observed')).toContainText('13:00:00 UTC');
});

test('keeps health inventory usable and accessible at 320 and 1440 pixels', async ({ page }, info) => {
  const fixture = evidence(); fixture.databases.items = [database()]; fixture.files.items = [logicalFile()];
  sessions.get(page).fixtures.set(alpha, fixture);
  await open(page, 'Databases');
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-retained-pagers/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(files(page).getByRole('heading', { name: 'Synthetic_data' })).toBeVisible();
    await inventory(page).getByRole('button', { name: 'Use Synthetic inventory as database context' }).focus();
    await expect(inventory(page).getByRole('button', { name: 'Use Synthetic inventory as database context' })).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`health-panel-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}health-panel-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`health-panel-${width}`, { path, contentType: 'image/png' });
  }
});

test('embedded navigation and independent dashboard remain available when the latest health snapshot fails', async ({ page }) => {
  sessions.get(page).intercept = async (route, _url, _id, kind) => {
    if (kind !== undefined) return false;
    await route.fulfill({ status: 503, contentType: 'application/json', body: '{"detail":"PRIVATE_HEALTH_ERROR"}' }); return true;
  };
  await open(page, 'Dashboard', '?embedded=true');
  await panel(page).getByText('Latest health, databases & configuration', { exact:true }).click();
  await expect(panel(page).getByRole('heading', { name: 'Server diagnostics for Alpha', exact: true })).toHaveCount(1);
  await expect(page.getByRole('heading', { name: 'Server summary for Alpha', exact: true })).toHaveCount(0);
  await expect(panel(page).getByRole('button', { name: 'Close', exact: true })).toHaveCount(0);
  const navigation = panel(page).getByRole('tablist', { name: 'Server summary sections', exact: true });
  await expect(navigation).toBeVisible();
  await expect(panel(page).getByRole('alert').filter({ hasText: 'The health request failed safely.' })).toBeVisible();
  await expect(panel(page).getByText('No latest core and collector health snapshot has been received for this server. Independent database, file and history sources can still be available; missing evidence is not an all-clear signal.', { exact: true })).toBeVisible();
  await expect(panel(page).locator('.health-dashboard-pane')).toBeVisible();
  await navigation.getByRole('tab', { name: 'Databases', exact: true }).click();
  await expect(panel(page).locator('.health-dashboard-pane')).toBeHidden();
  await expect(navigation).toBeVisible();
  await navigation.getByRole('tab', { name: 'Collection health', exact: true }).click();
  await expect(navigation.getByRole('tab', { name: 'Collection health', exact: true })).toHaveAttribute('aria-selected', 'true');
  await navigation.getByRole('tab', { name: 'Dashboard', exact: true }).click();
  await expect(panel(page).locator('.health-dashboard-pane')).toBeVisible();
  expect(sessions.get(page).requests.filter(path => path.startsWith('/api/v1/overview?'))).toHaveLength(1);
  expect(sessions.get(page).requests.filter(path => path.endsWith('/health'))).toHaveLength(1);
  await expect(page.locator('body')).not.toContainText('PRIVATE_HEALTH_ERROR');
});

test('switching sections preserves the mounted dashboard disclosure and owned source receipts', async ({ page }) => {
  await open(page, 'Dashboard');
  await panel(page).getByText('Retained alerts, server lifecycle & SQL Agent evidence', { exact:true }).click();
  const summary = panel(page).locator('summary').filter({ hasText: /^Browse alert lifecycle events$/ });
  await expect(summary).toBeVisible();
  await summary.focus(); await page.keyboard.press('Enter');
  await expect(summary.locator('..')).toHaveAttribute('open', '');
  const reads = sessions.get(page).requests.filter(path => path.includes('/alerts/history')).length;
  const tab = name => panel(page).getByRole('tab', { name, exact: true });
  await tab('Collection health').click(); await expect(summary).toBeHidden();
  await tab('Dashboard').click(); await expect(summary).toBeVisible();
  await expect(summary.locator('..')).toHaveAttribute('open', '');
  expect(sessions.get(page).requests.filter(path => path.includes('/alerts/history'))).toHaveLength(reads);
});

for (const unavailable of ['databases', 'files']) test(`a ${unavailable} outage keeps core and independently readable change history visible`, async ({ page }) => {
  const fixture = evidence(); fixture.databases.items = [database(7, 'Independent database')]; fixture.files.items = [logicalFile()];
  sessions.get(page).fixtures.set(alpha, fixture);
  sessions.get(page).intercept = async (route, _url, _id, kind) => {
    if (kind !== unavailable) return false;
    await route.fulfill({ status: 503, contentType: 'text/plain', body: 'SECRET_PARTIAL_FAILURE' }); return true;
  };
  await open(page, 'Dashboard');
  await expect(panel(page).locator('.health-summary')).toContainText('Current evidence');
  await expect(panel(page).getByRole('region', { name: 'Server configuration changes', exact: true })).toBeVisible();
  await expect(panel(page).getByRole('alert').filter({ hasText: 'The health request failed safely.' })).toBeVisible();
  await panel(page).getByRole('tab', { name: 'Databases', exact: true }).click();
  await expect(panel(page).getByRole('region', { name: 'Database option changes', exact: true })).toBeVisible();
  if (unavailable === 'files') {
    await expect(inventory(page)).toContainText('Independent database'); await expect(files(page)).toHaveCount(0);
  } else {
    await expect(files(page)).toContainText('Synthetic_data'); await expect(inventory(page)).toHaveCount(0);
  }
  await expect(panel(page)).not.toContainText('SECRET_PARTIAL_FAILURE');
});

test('a pending file read does not hold successfully validated core or database evidence hostage', async ({ page }) => {
  const fixture = evidence(); fixture.databases.items = [database(7, 'Immediate database')]; sessions.get(page).fixtures.set(alpha, fixture);
  let announce, release;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { release = resolve; });
  sessions.get(page).intercept = async (route, _url, _id, kind) => {
    if (kind !== 'files') return false; announce(); await gate; await json(route, fixture.files); return true;
  };
  await open(page, 'Databases'); await started;
  await expect(inventory(page)).toContainText('Immediate database');
  await expect(files(page)).toHaveCount(0);
  await panel(page).getByRole('tab', { name: 'Collection health', exact: true }).click();
  await expect(panel(page).getByRole('article').filter({ has: page.getByRole('heading', { name: 'inventory.databases', exact: true }) })).toBeVisible();
  release();
  await panel(page).getByRole('tab', { name: 'Databases', exact: true }).click(); await expect(files(page)).toBeVisible();
});

for (const status of [401, 403]) test(`file HTTP${status} has priority over core503 and aborts a held peer without automatic recovery`, async ({ page }) => {
  const session = sessions.get(page); let announce, release, finished, heldRequest;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { release = resolve; });
  const ended = new Promise(resolve => { finished = resolve; });
  session.intercept = async (route, _url, _id, kind) => {
    if (kind === 'databases') {
      heldRequest = route.request(); announce(); await gate;
      try { await json(route, evidence().databases); } catch { expect(heldRequest.failure()?.errorText).toContain('ERR_ABORTED'); }
      finished(); return true;
    }
    if (kind === 'files') { await started; await route.fulfill({ status, contentType: 'text/plain', body: 'SECRET_DENIAL_PRIORITY' }); return true; }
    if (kind === undefined) { await route.fulfill({ status: 503 }); return true; }
    return false;
  };
  await open(page, 'Databases'); await started;
  await expect(panel(page).getByRole('button', { name: 'Retry health access', exact: true })).toBeVisible();
  await expect.poll(() => heldRequest.failure()?.errorText).toContain('ERR_ABORTED');
  await expect(inventory(page)).toHaveCount(0); await expect(files(page)).toHaveCount(0);
  await expect(panel(page).getByRole('region', { name: 'Database option changes', exact: true })).toHaveCount(0);
  release(); await ended;
  const reads = session.requests.length;
  await page.getByRole('button', { name: 'Refresh health evidence', exact: true }).click();
  await expect(panel(page).getByRole('button', { name: 'Retry health access', exact: true })).toBeVisible();
  expect(session.requests).toHaveLength(reads);
  session.intercept = undefined;
  await panel(page).getByRole('button', { name: 'Retry health access', exact: true }).click();
  await expect(inventory(page)).toBeVisible();
  await expect(panel(page).getByRole('button', { name: 'Retry health access', exact: true })).toHaveCount(0);
  await expect(panel(page)).not.toContainText('SECRET_DENIAL_PRIORITY');
});

test('independent option-history denial clears all retained health siblings until explicit access recovery', async ({ page }) => {
  const fixture = evidence(); fixture.databases.items = [database(7, 'Protected independent database')]; fixture.files.items = [logicalFile()];
  const session = sessions.get(page); session.fixtures.set(alpha, fixture); await open(page, 'Databases');
  await expect(inventory(page)).toContainText('Protected independent database');
  await panel(page).getByRole('tab', { name: 'Dashboard', exact: true }).click();
  session.intercept = async (route, _url, _id, kind) => {
    if (kind !== 'database-option-changes') return false;
    await route.fulfill({ status: 403, contentType: 'text/plain', body: 'PRIVATE_OPTIONS' }); return true;
  };
  await panel(page).getByRole('tab', { name: 'Databases', exact: true }).click();
  await expect(panel(page).getByRole('button', { name: 'Retry health access', exact: true })).toBeVisible();
  await expect(inventory(page)).toHaveCount(0); await expect(files(page)).toHaveCount(0);
  await expect(panel(page)).not.toContainText('Protected independent database');
  await expect(panel(page)).not.toContainText('PRIVATE_OPTIONS');
});

for (const period of ['primary', 'comparison']) for (const status of [401, 403])
  test(`${period} analytics HTTP${status} clears protected health siblings while ordinary source failures remain independent`, async ({ page }) => {
    const fixture = evidence(); fixture.databases.items = [database(7, 'Protected analytics sibling')]; fixture.files.items = [logicalFile()];
    const session = sessions.get(page); session.fixtures.set(alpha, fixture);
    await open(page, 'Databases', `?navigation=true#/health?${new URLSearchParams({ target: alpha, range: 'custom',
      from: '2026-01-15T12:00:00.000Z', to: '2026-01-15T13:00:00.000Z', compare: '1', healthSection: 'databases' })}`);
    await expect(inventory(page)).toContainText('Protected analytics sibling');
    session.overviewDenied = { period, status };
    await page.getByRole('button', { name: 'Refresh health evidence', exact: true }).click();
    await expect(panel(page).getByRole('button', { name: 'Retry health access', exact: true })).toBeVisible();
    await expect(inventory(page)).toHaveCount(0); await expect(files(page)).toHaveCount(0);
    await expect(panel(page).locator('.server-dashboard')).toHaveCount(0);
    await expect(panel(page)).not.toContainText('Protected analytics sibling');
    await expect(panel(page)).not.toContainText('PRIVATE_OVERVIEW_DENIAL');
    const reads = session.requests.length;
    await page.getByRole('button', { name: 'Refresh health evidence', exact: true }).click();
    await expect(panel(page).getByRole('button', { name: 'Retry health access', exact: true })).toBeVisible();
    expect(session.requests).toHaveLength(reads);
    session.overviewDenied = undefined;
    await panel(page).getByRole('button', { name: 'Retry health access', exact: true }).click();
    await expect(inventory(page)).toContainText('Protected analytics sibling');
  });
