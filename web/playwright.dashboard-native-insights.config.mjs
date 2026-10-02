import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './browser-tests', testMatch: ['dashboard-native-insights.spec.mjs'],
  timeout: 30_000, workers: 1, retries: 0,
  outputDir: '../artifacts/dashboard-native-insights/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-native-insights/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-native-insights/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4326', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node tools/overview-fixture-server.mjs', url: 'http://127.0.0.1:4326', reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4326' }, timeout: 15_000 },
});
