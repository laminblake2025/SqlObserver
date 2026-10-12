import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './browser-tests', testMatch: 'redesign.spec.mjs',
  workers: 1, retries: 0, timeout: 45_000,
  outputDir: '../.artifacts/ui-redesign-20261009/browser-results',
  reporter: [['list'], ['json', { outputFile: '../.artifacts/ui-redesign-20261009/browser-results.json' }]],
  use: { baseURL: 'http://127.0.0.1:4358', timezoneId: 'America/New_York', locale: 'en-US', trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node tools/overview-fixture-server.mjs', url: 'http://127.0.0.1:4358', reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4358' }, timeout: 15_000 },
});
