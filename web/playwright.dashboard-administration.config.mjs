import { defineConfig, devices } from '@playwright/test';

// Separate administration evidence, against the same built product and synthetic server.
export default defineConfig({
  testDir: './browser-tests',
  testMatch: ['dashboard-administration.spec.mjs'],
  timeout: 30_000,
  workers: 1,
  retries: 0,
  outputDir: '../artifacts/dashboard-administration/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-administration/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-administration/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4323', timezoneId: 'America/New_York', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'node tools/overview-fixture-server.mjs',
    url: 'http://127.0.0.1:4323',
    reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4323' },
    timeout: 15_000,
  },
});
