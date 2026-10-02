import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { alpha, beta, nextCursor, registeredTarget } from './onboardingFixture.mjs';

const sessions = new WeakMap();
const registration = page => page.getByRole('region', { name: 'Target registration', exact: true });
const servers = page => page.getByRole('region', { name: 'Loaded servers', exact: true });
const submit = page => registration(page).getByRole('button', { name: 'Register target', exact: true });
const callbacks = page => page.getByRole('status', { name: 'Registration callbacks', exact: true });
const json = (route, body) => route.fulfill({ status: 201, contentType: 'application/json',
  headers: { 'Cache-Control': 'no-store' }, body: JSON.stringify(registeredTarget(body)) });
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], requests: [], cancelled: [], respond: undefined };
  sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  page.on('requestfailed', request => {
    if (new URL(request.url()).pathname === '/api/v1/observation-targets') session.cancelled.push(request.url());
  });
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = () => { session.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); };
    if (url.origin !== 'http://127.0.0.1:4328') return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    // Only the production registration mutation is registered. All other API
    // traffic is aborted before Vite's proxy can reach a backend.
    if (request.method() !== 'POST' || url.pathname !== '/api/v1/observation-targets' || url.search) return reject();
    const body = request.postDataJSON();
    session.requests.push(body);
    if (session.respond) return session.respond(route, body);
    return json(route, body);
  });
});

test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Unexpected API/external traffic and runtime errors').toEqual([]);
});

async function open(page, query = '') {
  await page.goto(`/component-tests/onboarding.html${query}`);
  await expect(page.getByRole('heading', { name: 'Onboarding and servers harness', exact: true })).toBeVisible();
}

async function fill(page, key = 'synthetic-sql', name = 'Synthetic SQL') {
  await registration(page).getByRole('textbox', { name: 'Stable key', exact: true }).fill(key);
  await registration(page).getByRole('textbox', { name: 'Display name', exact: true }).fill(name);
  await registration(page).getByRole('textbox', { name: 'Windows host', exact: true }).fill('sql.example.test');
}

function holdRegistration(page) {
  const session = sessions.get(page);
  let release, started, delivered, deliveryError;
  const held = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  const finished = new Promise(resolve => { delivered = resolve; });
  session.respond = async (route, body) => {
    started(); await held;
    try { await json(route, body); }
    catch (error) { deliveryError = error; }
    finally { delivered(); }
  };
  return { seen, async finish(cancelled = false) {
    release(); await finished;
    if (deliveryError && !cancelled) throw deliveryError;
  } };
}

test('discloses integrated service identity and submits mutually exclusive TCP and named-instance bodies', async ({ page }) => {
  await open(page);
  await expect(registration(page)).toContainText('Connections use the Collector Windows service identity.');
  await expect(registration(page)).toContainText('SqlObserver does not ask for or store a SQL login secret.');
  await fill(page);
  await registration(page).getByRole('spinbutton', { name: 'Port', exact: true }).fill('1444');
  await registration(page).getByRole('textbox', { name: /^Certificate host name/ }).fill('certificate.example.test');
  await submit(page).click();
  await expect(callbacks(page)).toHaveText('1');
  const first = sessions.get(page).requests[0];
  expect(first).toEqual({ instanceId: expect.stringMatching(guid), instanceKey: 'synthetic-sql',
    displayName: 'Synthetic SQL', host: 'sql.example.test', tcpPort: 1444, certificateHostName: 'certificate.example.test' });
  await expect(registration(page).getByRole('status')).toHaveText('Target accepted; capability discovery is pending.');
  await expect(registration(page).getByRole('textbox', { name: 'Stable key', exact: true })).toHaveValue('');
  await fill(page, 'named-sql', 'Named SQL');
  await registration(page).getByRole('radio', { name: 'Named instance', exact: true }).check();
  await expect(registration(page).getByRole('spinbutton', { name: 'Port', exact: true })).toHaveCount(0);
  await registration(page).getByRole('textbox', { name: 'Instance name', exact: true }).fill('APP');
  await submit(page).click();
  await expect(callbacks(page)).toHaveText('2');
  expect(sessions.get(page).requests[1]).toEqual({ instanceId: expect.stringMatching(guid), instanceKey: 'named-sql',
    displayName: 'Named SQL', host: 'sql.example.test', namedInstance: 'APP' });
  await expect(registration(page).getByRole('radio', { name: 'TCP port', exact: true })).toBeChecked();
  await expect(registration(page).getByRole('spinbutton', { name: 'Port', exact: true })).toHaveValue('1433');
});

test('uses the same client ID on safe 429 failure and retry, then creates a new ID after success', async ({ page }) => {
  const session = sessions.get(page);
  let fail = true;
  session.respond = async (route, body) => {
    if (fail) await route.fulfill({ status: 429, contentType: 'text/html', body: '<script>window.onboardingInjected=true</script> password=synthetic-secret' });
    else await json(route, body);
  };
  await open(page); await fill(page);
  await submit(page).click();
  await expect(registration(page).getByRole('status')).toHaveText('Too many administrative requests. Wait briefly and retry.');
  await expect(callbacks(page)).toHaveText('0');
  await expect(submit(page)).toBeEnabled();
  await expect(registration(page).getByRole('textbox', { name: 'Stable key', exact: true })).toHaveValue('synthetic-sql');
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  expect(await page.evaluate(() => window.onboardingInjected)).toBeUndefined();
  fail = false;
  await submit(page).click(); await expect(callbacks(page)).toHaveText('1');
  expect(session.requests[0].instanceId).toMatch(guid);
  expect(session.requests[1]).toEqual(session.requests[0]);
  await fill(page, 'second-sql', 'Second SQL');
  await submit(page).click(); await expect(callbacks(page)).toHaveText('2');
  expect(session.requests[2].instanceId).toMatch(guid);
  expect(session.requests[2].instanceId).not.toBe(session.requests[1].instanceId);
});

test('rejects foreign, malformed and oversized registration success bodies without callbacks and retries the same identity', async ({ page }) => {
  const session = sessions.get(page);
  let failure = 'foreign';
  session.respond = async (route, body) => {
    if (failure === 'foreign') return route.fulfill({ status: 201, contentType: 'application/json',
      body: JSON.stringify(registeredTarget(body, { instanceId: beta })) });
    if (failure === 'malformed') return route.fulfill({ status: 201, contentType: 'application/json',
      body: '{"provider password=synthetic-secret":' });
    if (failure === 'oversized') return route.fulfill({ status: 201, contentType: 'application/json',
      body: JSON.stringify({ ...registeredTarget(body), padding: ' '.repeat(1024 * 1024) }) });
    return json(route, body);
  };
  await open(page);
  for (const [index, invalid] of ['foreign', 'malformed', 'oversized'].entries()) {
    failure = invalid;
    await fill(page, `retry-${index}`, `Retry server ${index}`);
    await submit(page).click();
    await expect(registration(page).getByRole('status')).toHaveText('SqlObserver target response is invalid. Refresh and retry.');
    await expect(callbacks(page)).toHaveText(String(index));
    await expect(submit(page)).toBeEnabled();
    await expect(registration(page).getByRole('textbox', { name: 'Stable key', exact: true })).toHaveValue(`retry-${index}`);
    await expect(page.locator('body')).not.toContainText('synthetic-secret');
    failure = '';
    await submit(page).click();
    await expect(callbacks(page)).toHaveText(String(index + 1));
    expect(session.requests[index * 2 + 1]).toEqual(session.requests[index * 2]);
  }
});

test('disables pending submission and guards duplicate submit events until one callback completes', async ({ page }) => {
  const held = holdRegistration(page);
  await open(page); await fill(page);
  await submit(page).click(); await held.seen;
  await expect(registration(page).getByRole('button', { name: 'Registering…', exact: true })).toBeDisabled();
  // requestSubmit retains native form validation and exercises the active-request
  // guard even if a second submission event is delivered while the button is disabled.
  await registration(page).locator('form').evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
  await expect(callbacks(page)).toHaveText('0');
  await held.finish();
  await expect(callbacks(page)).toHaveText('1');
  expect(sessions.get(page).requests).toHaveLength(1);
  await expect(submit(page)).toBeEnabled();
});

test('aborts registration on unmount and refuses a late success callback', async ({ page }) => {
  const session = sessions.get(page), held = holdRegistration(page);
  await open(page); await fill(page);
  await submit(page).click(); await held.seen;
  await page.getByRole('button', { name: 'Hide registration', exact: true }).click();
  await expect(registration(page)).toContainText('Registration unmounted');
  await expect.poll(() => session.cancelled.length).toBe(1);
  await held.finish(true);
  await expect(callbacks(page)).toHaveText('0');
  await expect(page.getByRole('status', { name: 'Last registered identity', exact: true })).toHaveText('None');
  await page.getByRole('button', { name: 'Show registration', exact: true }).click();
  await expect(submit(page)).toBeEnabled();
  await expect(registration(page).getByRole('status')).toHaveCount(0);
  expect(session.requests).toHaveLength(1);
});

test('native browser validation blocks missing fields, invalid stable keys and out-of-range ports before API submission', async ({ page }) => {
  await open(page);
  const key = registration(page).getByRole('textbox', { name: 'Stable key', exact: true });
  await submit(page).click();
  expect(await key.evaluate(input => input.validity.valueMissing)).toBe(true);
  await fill(page, 'INVALID KEY');
  await submit(page).click();
  expect(await key.evaluate(input => input.validity.patternMismatch)).toBe(true);
  await key.fill('valid-key');
  const port = registration(page).getByRole('spinbutton', { name: 'Port', exact: true });
  await port.fill('65536'); await submit(page).click();
  expect(await port.evaluate(input => input.validity.rangeOverflow)).toBe(true);
  await port.fill('0'); await submit(page).click();
  expect(await port.evaluate(input => input.validity.rangeUnderflow)).toBe(true);
  await registration(page).getByRole('radio', { name: 'Named instance', exact: true }).check();
  const instance = registration(page).getByRole('textbox', { name: 'Instance name', exact: true });
  await submit(page).click();
  expect(await instance.evaluate(input => input.validity.valueMissing)).toBe(true);
  expect(sessions.get(page).requests).toEqual([]);
  await instance.fill('VALID'); await submit(page).click();
  await expect(callbacks(page)).toHaveText('1');
});

test('renders lifecycle and discovery states, searches only the loaded page, and exposes server navigation and paging', async ({ page }) => {
  await open(page);
  const fleet = servers(page), table = fleet.getByRole('table');
  await expect(table.getByRole('columnheader', { name: 'Lifecycle', exact: true })).toBeVisible();
  await expect(table.getByRole('row').filter({ hasText: 'Alpha pending' })).toContainText('pending discovery');
  for (const [name, status] of [['Beta supported', 'Supported'], ['Delta degraded', 'Degraded visibility'],
    ['Epsilon unsupported', 'Unsupported'], ['Gamma unreachable', 'Unreachable'], ['Zeta disabled', 'Disabled'], ['Zulu retired', 'Retired']])
    await expect(table.getByRole('row').filter({ hasText: name }).getByRole('cell', { name: status, exact: true })).toBeVisible();
  for (const [label, value] of [['Registered targets', '7'], ['Active lifecycle', '4'], ['Discovery pending', '1'], ['Visibility concerns', '3']])
    await expect(fleet.locator('.kpi').filter({ has: page.getByText(label, { exact: true }) }).locator('strong')).toHaveText(value);
  await expect(fleet).toContainText('discovery is not collection health');
  await expect(table.getByRole('row').filter({ hasText: 'Beta supported' })).toContainText('Loading / unavailable');
  const pending = fleet.locator('article').filter({ has: page.getByRole('link', { name: 'Alpha pending', exact: true }) });
  await pending.getByText('Discovery evidence', { exact: true }).click();
  await expect(pending).toContainText('Synthetic pending discovery evidence.');
  const search = fleet.getByRole('textbox', { name: 'Search loaded servers', exact: true });
  await search.fill('SQL-2.EXAMPLE.TEST');
  await expect(table.locator('caption')).toHaveText('1 of 7 loaded targets · sorted by name');
  const betaRow = table.getByRole('row').filter({ hasText: 'Beta supported' });
  await expect(betaRow).toContainText('sql-2.example.test\\APP');
  await expect(betaRow.getByRole('link', { name: 'Beta supported', exact: true })).toHaveAttribute('href', `#/health?target=${beta}`);
  await betaRow.getByRole('link', { name: 'Beta supported', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`#/health\\?target=${beta}$`));
  await expect(betaRow.getByRole('link', { name: 'Unavailable', exact: true })).toHaveAttribute('href', `#/alerts?target=${beta}`);
  await fleet.getByRole('combobox', { name: 'Lifecycle filter', exact: true }).selectOption('disabled');
  await expect(fleet.getByText('No servers match this loaded page.', { exact: true })).toBeVisible();
  await search.fill('');
  await expect(table.getByRole('link', { name: 'Zeta disabled', exact: true })).toBeVisible();
  await expect(table.locator('caption')).toHaveText('1 of 7 loaded targets · sorted by name');
  await fleet.getByRole('combobox', { name: 'Lifecycle filter', exact: true }).selectOption('');
  await expect(fleet.getByRole('button', { name: 'First page', exact: true })).toBeDisabled();
  await fleet.getByRole('button', { name: 'Next page', exact: true }).click();
  await expect(page.getByRole('status', { name: 'Server page cursor', exact: true })).toHaveText(nextCursor);
  await expect(table.getByRole('link', { name: 'Next page server', exact: true })).toBeVisible();
  await expect(fleet.getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
  await fleet.getByRole('button', { name: 'First page', exact: true }).click();
  await expect(table.locator('caption')).toHaveText('7 of 7 loaded targets · sorted by name');
  expect(sessions.get(page).requests).toEqual([]);
});

test('renders provider-controlled server names and discovery reasons as inert text', async ({ page }) => {
  await open(page, '?scenario=inert');
  const name = '<img src="https://invalid.example/onboarding" onerror="window.onboardingInjected=true">';
  const fleet = servers(page);
  await expect(fleet.getByRole('table').getByRole('link', { name, exact: true })).toBeVisible();
  const attention = fleet.locator('article').filter({ has: page.getByRole('link', { name, exact: true }) });
  await attention.getByText('Discovery evidence', { exact: true }).click();
  await expect(attention.getByRole('listitem')).toHaveText('<script>window.onboardingInjected=true</script>');
  await expect(fleet.locator('img, script')).toHaveCount(0);
  expect(await page.evaluate(() => window.onboardingInjected)).toBeUndefined();
  await expect(fleet.getByRole('table').getByRole('link', { name, exact: true })).toHaveAttribute('href', `#/health?target=${alpha}`);
});

test('keeps onboarding usable and accessible at 320 and 1440 pixels with fresh screenshots', async ({ page }, info) => {
  await open(page);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await registration(page).getByRole('textbox', { name: 'Stable key', exact: true }).focus();
    await expect(registration(page).getByRole('textbox', { name: 'Stable key', exact: true })).toBeFocused();
    await expect(submit(page)).toBeVisible();
    expect(await registration(page).evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).include('section[aria-label="Target registration"]')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`onboarding-panel-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}onboarding-panel-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`onboarding-panel-${width}`, { path, contentType: 'image/png' });
  }
});
