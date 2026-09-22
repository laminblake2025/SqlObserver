import { test, expect } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import { mockEvidence, routeFor } from './fixtures.mjs';

for (const fleetSize of [1, 5, 10]) {
  test(`browser fixture benchmark: ${fleetSize} targets`, async ({ browser }, testInfo) => {
    const samples = [];
    // Five independent contexts include initial rendering, not a hot component update.
    for (let iteration = 0; iteration < 5; iteration++) {
      const context = await browser.newContext();
      try {
        const page = await context.newPage();
        const fixture = await mockEvidence(page, { count: fleetSize });
        const start = performance.now();
        await page.goto(`${testInfo.project.use.baseURL ?? 'http://127.0.0.1:5173'}${routeFor('overview', '')}`);
        await expect(page.getByRole('table').locator('tbody tr')).toHaveCount(fleetSize);
        const firstUsableMs = performance.now() - start;
        const interactionStart = performance.now();
        await page.getByLabel('Search loaded servers').fill('Fixture SQL 1');
        await expect(page.getByRole('table').locator('tbody tr')).toHaveCount(fleetSize === 10 ? 2 : 1);
        const filterMs = performance.now() - interactionStart;
        const cdp = await context.newCDPSession(page);
        await cdp.send('Performance.enable');
        const { metrics } = await cdp.send('Performance.getMetrics');
        const heap = metrics.find(metric => metric.name === 'JSHeapUsedSize')?.value ?? null;
        samples.push({ firstUsableMs, filterMs, requests: fixture.requests.length, domElements: await page.locator('*').count(), jsHeapBytes: heap });
      } finally { await context.close(); }
    }
    const percentile = (key, fraction) => [...samples.map(s => s[key])].sort((a, b) => a - b)[Math.ceil(samples.length * fraction) - 1];
    const result = { kind: 'synthetic-browser-benchmark', fleetSize, measuredAtUtc: new Date().toISOString(),
      apiLatency: 'not measured: API responses are fixtures', collectionLag: 'not measured', repositoryLoad: 'not measured',
      firstUsableP50Ms: percentile('firstUsableMs', .5), firstUsableP95Ms: percentile('firstUsableMs', .95), filterP95Ms: percentile('filterMs', .95), samples };
    await mkdir('.artifacts/browser-benchmark', { recursive: true });
    await writeFile(`.artifacts/browser-benchmark/fleet-${fleetSize}.json`, JSON.stringify(result, null, 2) + '\n');
    console.log(JSON.stringify({ fleetSize, firstUsableP50Ms: result.firstUsableP50Ms, firstUsableP95Ms: result.firstUsableP95Ms, filterP95Ms: result.filterP95Ms }));
  });
}
