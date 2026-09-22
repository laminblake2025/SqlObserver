import { defineConfig } from '@playwright/test';
import base from './playwright.config.mjs';

export default defineConfig({
  ...base,
  testMatch: '**/*.benchmark.mjs',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  outputDir: '.artifacts/browser-benchmark',
  use: { ...base.use, trace: 'off', screenshot: 'off' },
});
