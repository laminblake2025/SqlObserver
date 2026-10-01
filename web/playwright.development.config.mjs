import { defineConfig, devices } from '@playwright/test';

// Explicit opt-in: tools/dev-up.ps1 owns the API, Vite and database lifecycle.
// Never substitute the synthetic HTTP fixture for these connected checks.
export default defineConfig({
  testDir: './browser-development-tests',
  timeout: 30_000,
  workers: 1,
  retries: 0,
  outputDir: '../artifacts/development-browser/results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/development-browser/results.json' }],
    ['html', { outputFolder: '../artifacts/development-browser/report', open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:5173', trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
