import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './browser-tests', testMatch: ['dashboard-activity-time.spec.mjs'], timeout: 30_000, workers: 1, retries: 0,
  outputDir: '../artifacts/dashboard-query-history/activity-time/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-query-history/activity-time/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-query-history/activity-time/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4338', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node tools/overview-fixture-server.mjs', url: 'http://127.0.0.1:4338', reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4338' }, timeout: 15_000 },
});
