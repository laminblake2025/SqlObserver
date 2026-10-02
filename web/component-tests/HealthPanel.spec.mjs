import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { alpha, beta, at, cursor, collector, database, evidence, logicalFile } from './healthFixture.mjs';

const sessions = new WeakMap();
const panel = page => page.getByRole('region', { name: /^Server summary for/ });
const inventory = page => page.getByRole('region', { name: 'Database health', exact: true });
const files = page => page.getByRole('region', { name: 'Logical-file health', exact: true });
const fact = (container, label) => container.locator('dl > div').filter({ has: container.page().getByText(label, { exact: true }) }).locator('dd');
const json = (route, value) => route.fulfill({ status: 200, contentType: 'application/json',
  headers: { 'Cache-Control': 'no-store' }, body: JSON.stringify(value) });

test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], requests: [], fixtures: new Map([[alpha, evidence(alpha)], [beta, evidence(beta)]]), intercept: undefined };
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
    // The real dashboard receives an authorized native empty snapshot. Health
    // remains mounted; unrelated chart/timeline/agent sources are not needed.
    if (url.pathname === '/api/v1/overview' && [alpha, beta].includes(url.searchParams.get('targetId'))) {
      return json(route, { targetId: url.searchParams.get('targetId'), fromUtc: url.searchParams.get('fromUtc'),
        toUtc: url.searchParams.get('toUtc'), refreshedAtUtc: at, targets: [], excludedTargets: 0, evidence: [] });
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

async function open(page, section = 'Collection health') {
  await page.goto('/component-tests/health.html');
  await expect(page.getByRole('heading', { name: 'Health panel harness' })).toBeVisible();
  await expect(panel(page).getByRole('tab', { name: 'Collection health' })).toBeVisible();
  if (section !== 'Collection health') await panel(page).getByRole('tab', { name: section, exact: true }).click();
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
  await panel(page).getByRole('tab', { name: 'Summary', exact: true }).click();
  await panel(page).getByText('All latest core metric samples', { exact: true }).click();
  await expect(panel(page).getByText('No core metric samples are available.', { exact: true })).toBeVisible();
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
  await inventory(page).getByRole('button', { name: 'Previous database page' }).click();
  await expect(inventory(page).getByRole('heading', { name: 'First database' })).toBeVisible();
  await expect(inventory(page)).toContainText('Page 1 · 1 databases · More available');
  await expect(files(page).getByRole('heading', { name: 'Synthetic_data' })).toBeVisible();
});

test('sanitizes a failed native response and retries through an external refresh', async ({ page }) => {
  const secret = 'provider password=synthetic-secret; connectionString=private; <script>window.healthInjected=true</script>';
  let fail = true;
  sessions.get(page).intercept = async (route, _url, _id, kind) => {
    if (kind !== undefined || !fail) return false;
    await route.fulfill({ status: 403, contentType: 'text/html', body: secret }); return true;
  };
  await page.goto('/component-tests/health.html');
  await expect(panel(page).getByRole('alert')).toHaveText('You are not authorized to view health for this target.');
  await expect(panel(page).getByRole('tab')).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  expect(await page.evaluate(() => window.healthInjected)).toBeUndefined();
  fail = false;
  await page.getByRole('button', { name: 'Refresh health evidence' }).click();
  await expect(panel(page).getByRole('tab', { name: 'Collection health' })).toBeVisible();
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
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
  await expect(inventory(page).getByRole('alert')).toHaveText('Database page is unavailable. Try again.');
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
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
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
