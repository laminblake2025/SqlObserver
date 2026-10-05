// Explicit native-receipt acceptance, invoked only while its isolated repository is alive.
// No API interception or synthetic CPU responses are installed.
import assert from 'node:assert/strict';
import {mkdir, writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {chromium, expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const required = name => {
  const value = process.env[`SQLOBSERVER_CPU_NATIVE_${name}`];
  assert.ok(value, `Missing native acceptance input ${name}`);
  return value;
};
const origin = new URL(required('API_URL'));
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(origin.hostname), 'Acceptance must use the isolated loopback server');
const target = required('TARGET_ID'), revision = required('TARGET_REVISION'), run = required('RUN_ID');
const from = required('FROM_UTC'), to = required('TO_UTC'), expected = JSON.parse(required('EXPECTED_JSON'));
const stamp = new Date().toISOString().replaceAll(':', '-');
const folder = fileURLToPath(new URL(`../../artifacts/dashboard-native-continuation-2026-10-05/browser-${stamp}/`, import.meta.url));
await mkdir(folder, {recursive: true});
const browser = await chromium.launch();
const context = await browser.newContext({locale: 'en-US', extraHTTPHeaders: {'X-Native-Receipt-Identity': 'viewer'}});
await context.tracing.start({screenshots: true, snapshots: true, sources: true});
const page = await context.newPage(), errors = [], cpuResponses = [];
page.on('pageerror', error => errors.push(error.message));
page.on('response', response => {
  if (new URL(response.url()).pathname.includes('/resources/cpu/observations'))
    cpuResponses.push({url: response.url(), status: response.status()});
});
const params = new URLSearchParams({target, range: 'custom', from, to, cpuObservationRun: run, cpuObservationRevision: revision});
const address = new URL(`/#/resources?${params}`, origin).href;
try {
  const query = new URLSearchParams({targetRevision: revision, fromUtc: from, toUtc: to, sampleTargetRevision: revision});
  const response = await context.request.get(new URL(`/api/v1/observation-targets/${target}/resources/cpu/observations/${run}?${query}`, origin).href);
  assert.equal(response.status(), 200);
  const exactBody = await response.json();
  assert.deepEqual(exactBody.receipt.observation, expected);
  assert.equal(exactBody.receipt.runId, run);
  assert.equal(exactBody.receipt.sampleTargetRevision, Number(revision));
  assert.ok(!/rawBefore|rawAfter|tcpBeforeHelperBytes/u.test(JSON.stringify(exactBody)), 'Private source evidence must not enter HTTP');
  await writeFile(folder + 'exact-public-response.json', JSON.stringify(exactBody, null, 2));
  for (const width of [320, 1440]) {
    await page.setViewportSize({width, height: 1000});
    await page.goto(address, {waitUntil: 'domcontentloaded'});
    const panel = page.getByRole('region', {name: 'CPU component observations', exact: true});
    const selected = panel.getByRole('region', {name: 'Selected CPU observation', exact: true});
    await expect(selected).toContainText(run, {timeout: 20000});
    await expect(selected).toContainText('Target association verified for this observation.');
    for (const [key, label] of [['sqlProcess', 'SQL-process CPU'], ['operatingSystem', 'Target-host CPU']]) {
      const value = expected[key];
      assert.notEqual(value.percent, null);
      const evidence = selected.getByRole('region', {name: `${label} exact evidence`, exact: true});
      await expect(evidence).toContainText(`${value.percent.toLocaleString('en-US', {maximumFractionDigits: 3})}%`);
      await evidence.getByText('Source intervals and calculation details', {exact: true}).click();
      await expect(evidence).toContainText(`${value.fromNative100ns} to ${value.toNative100ns}`);
      await expect(evidence).toContainText(value.observedFromUtc);
      await expect(evidence).toContainText(value.observedToUtc);
      const chart = panel.getByRole('img', {name: `${label} independent observations and gaps`, exact: true});
      await expect(chart.locator('[data-quality]')).toHaveCount(1);
      await expect(chart.locator('[data-cpu-gap]')).toHaveCount(0);
      assert.equal(await chart.locator('polyline').count(), 0);
      await expect(chart).toContainText(`${value.percent}%`);
      await panel.locator('figure').filter({has: page.getByRole('img', {name: `${label} independent observations and gaps`, exact: true})})
        .screenshot({path: folder + `native-${key}-chart-${width}.png`});
      await evidence.screenshot({path: folder + `native-${key}-evidence-${width}.png`});
    }
    await expect(selected).toContainText(`Strict paired calculation eligible: ${expected.exactPairEligible ? 'Yes' : 'No'}`);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth) <= 1, 'Resources must fit the viewport');
    const accessibility = await new AxeBuilder({page}).include('.cpu-observation-history')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
    assert.deepEqual(accessibility.violations, []);
    // Tall element screenshots can include a sticky shell halfway through the image.
    // At narrow widths the smaller chart/evidence captures above preserve readable pixels.
    if (width === 1440) await panel.screenshot({path: folder + `native-cpu-${width}.png`});
    await page.reload({waitUntil: 'domcontentloaded'});
    await expect(selected).toContainText(run);
    await expect(selected).toContainText('Target association verified for this observation.');
    assert.equal(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('cpuObservationRun'), run);
  }
  assert.deepEqual(errors, []);
  assert.ok(cpuResponses.length >= 4);
  assert.ok(cpuResponses.every(response => response.status === 200));
  const result = {passed: true, target, revision, run, widths: [320, 1440], actualCpuHttpResponses: cpuResponses.length,
    noApiMocks: true, nativeObservationReplayedWithoutIdentityOrCounterChanges: true, reloadPreservedExactRun: true,
    scopedWcagAxeViolations: 0,
    scope: 'Recorded native source -> isolated canonical repository -> production HTTP -> built Resources; test authentication only'};
  await writeFile(folder + 'result.json', JSON.stringify(result, null, 2));
  console.log(JSON.stringify({...result, evidenceDirectory: folder}));
} catch (error) {
  await page.screenshot({path: folder + 'failure.png', fullPage: true}).catch(() => {});
  await writeFile(folder + 'failure.json', JSON.stringify({error: String(error), pageErrors: errors, cpuResponses}, null, 2));
  throw error;
} finally {
  await context.tracing.stop({path: folder + 'trace.zip'});
  await browser.close();
}
