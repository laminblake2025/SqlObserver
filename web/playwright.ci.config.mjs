import { defineConfig, devices } from '@playwright/test';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// Each browser suite must use its own synthetic fixture origin. The native
// collector journey has an explicit SQL Server/PostgreSQL qualification runner.
const root = dirname(fileURLToPath(import.meta.url));
const fixturePorts = new Set([4321, 4432, 4323, 4324, 4325, 4326, 4327, 4331, 4332, 4333, 4334, 4335, 4336, 4337, 4338, 4358]);
const groups = new Map();
for (const name of readdirSync(join(root, 'browser-tests')).filter(name => name.endsWith('.spec.mjs')).sort()) {
  if (name === 'redesign-native.spec.mjs') continue;
  const source = readFileSync(join(root, 'browser-tests', name), 'utf8');
  const ports = [...new Set([...source.matchAll(/http:\/\/127\.0\.0\.1:(\d+)/gu)].map(match => Number(match[1])))];
  if (ports.length > 1) throw new Error(`Browser suite has ambiguous fixture origins: ${name}`);
  const port = ports[0] ?? 4321;
  if (!fixturePorts.has(port)) throw new Error(`Browser suite has an unregistered fixture origin: ${name}`);
  if (!groups.has(port)) groups.set(port, []);
  groups.get(port).push(name);
}

export default defineConfig({
  testDir: './browser-tests', fullyParallel: true, workers: 1, retries: 0, timeout: 45_000,
  outputDir: '../artifacts/web-browser/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/web-browser/results.json' }],
    ['html', { outputFolder: '../artifacts/web-browser/report', open: 'never' }]],
  use: { locale: 'en-US', timezoneId: 'America/New_York', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [...groups].map(([port, testMatch]) => ({
    name: `chromium-${port}`, testMatch,
    use: { ...devices['Desktop Chrome'], baseURL: `http://127.0.0.1:${port}` },
  })),
  webServer: [...groups.keys()].map(port => ({
    command: 'node tools/overview-fixture-server.mjs', url: `http://127.0.0.1:${port}`,
    reuseExistingServer: false, env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: String(port) }, timeout: 30_000,
  })),
});
