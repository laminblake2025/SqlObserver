import { defineConfig, devices } from '@playwright/test';

// Start the opt-in NativeRedesign .NET journey first; it owns and cleans up the isolated API/database.
export default defineConfig({
  testDir: './browser-tests', testMatch: 'redesign-native.spec.mjs', workers: 1, retries: 0, timeout: 60_000,
  outputDir: '../.artifacts/ui-redesign-20261009/native/browser-results',
  reporter: [['list'], ['json', { outputFile: '../.artifacts/ui-redesign-20261009/native/browser-results.json' }]],
  use: { baseURL: 'http://127.0.0.1:4359', extraHTTPHeaders: { 'X-Redesign-Lab': 'viewer' }, viewport: { width: 1600, height: 1000 }, timezoneId: 'America/New_York', trace: 'retain-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1600, height: 1000 } } }],
});
