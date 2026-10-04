import { defineConfig, devices } from '@playwright/test';

// Tests run against the final built assets and explicitly synthetic loopback data.
export default defineConfig({
  testDir: './browser-tests',
  testMatch: ['dashboard-refactor.spec.mjs', 'dashboard-accessibility.spec.mjs', 'dashboard-chart-access.spec.mjs', 'dashboard-file-window-history.spec.mjs', 'dashboard-cpu-capture-history.spec.mjs'],
  timeout: 30_000,
  workers: 1,
  retries: 0,
  outputDir: '../artifacts/dashboard-refactor/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-refactor/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-refactor/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4322', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'node tools/overview-fixture-server.mjs',
    url: 'http://127.0.0.1:4322',
    reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4322' },
    timeout: 15_000,
  },
});
