import { defineConfig, devices } from '@playwright/test';

// Mount real components directly through Vite; no product build or API fixture.
export default defineConfig({
  testDir: './component-tests', testMatch: '**/*.spec.mjs',
  timeout: 30_000, workers: 1, retries: 0,
  outputDir: '../artifacts/dashboard-components/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-components/results.json' }],
    ['html', { outputFolder: '../artifacts/dashboard-components/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:4328', locale: 'en-US', timezoneId: 'America/New_York',
    trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: { command: 'node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 4328 --strictPort',
    url: 'http://127.0.0.1:4328/component-tests/index.html', reuseExistingServer: false, timeout: 20_000 },
});
