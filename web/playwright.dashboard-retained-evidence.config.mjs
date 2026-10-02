import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './browser-tests', testMatch: ['dashboard-retained-evidence.spec.mjs'],
  timeout: 30_000, workers: 1, retries: 0,
  outputDir: '../artifacts/dashboard-retained-evidence/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-retained-evidence/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-retained-evidence/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4327', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node tools/overview-fixture-server.mjs', url: 'http://127.0.0.1:4327', reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4327' }, timeout: 15_000 },
});
