import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './browser-tests', testMatch: ['dashboard-server-management.spec.mjs'],
  timeout: 30_000, workers: 1, retries: 0,
  outputDir: '../artifacts/dashboard-server-management/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-server-management/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-server-management/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4332', locale: 'en-US', timezoneId: 'America/New_York',
    trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node tools/overview-fixture-server.mjs', url: 'http://127.0.0.1:4332',
    reuseExistingServer: false, env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4332' }, timeout: 15_000 },
});
