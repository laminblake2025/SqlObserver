import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './browser-tests',
  testIgnore: ['dashboard-refactor.spec.mjs', 'dashboard-accessibility.spec.mjs', 'dashboard-administration.spec.mjs', 'dashboard-evidence-contracts.spec.mjs', 'dashboard-workflow-completion.spec.mjs', 'dashboard-native-insights.spec.mjs'],
  timeout: 30_000,
  workers: 1,
  retries: 0,
  outputDir: '../artifacts/web-browser/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/web-browser/results.json' }],
    ['html', { outputFolder: '../artifacts/web-browser/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4321', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'pnpm run fixture:overview', url: 'http://127.0.0.1:4321', reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4321' }, timeout: 15_000,
  },
});
