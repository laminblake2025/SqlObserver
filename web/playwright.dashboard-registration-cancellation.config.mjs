import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './browser-tests', testMatch: ['dashboard-registration-cancellation.spec.mjs'],
  timeout: 30_000, workers: 1, retries: 0,
  outputDir: '../artifacts/dashboard-registration-cancellation/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-registration-cancellation/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-registration-cancellation/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4336', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node tools/overview-fixture-server.mjs', url: 'http://127.0.0.1:4336',
    reuseExistingServer: false, env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4336' }, timeout: 15_000 },
});
