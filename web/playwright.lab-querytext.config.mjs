import { defineConfig } from '@playwright/test';
import lab from './playwright.lab.config.mjs';
if (!/^[0-9a-f]{32}$/.test(process.env.SQLOBSERVER_QUERYTEXT_VERIFICATION_RUN ?? ''))
  throw new Error('Set SQLOBSERVER_QUERYTEXT_VERIFICATION_RUN to the active, bounded native SQL probe run.');
export default defineConfig({
  ...lab,
  testMatch: ['connected.spec.mjs', 'querytextreader.spec.mjs'],
  reporter: [['list'], ['json', { outputFile: '../.artifacts/lab-api-querytext-20261009/browser-results.json' }]],
  outputDir: '../.artifacts/lab-api-querytext-20261009/browser',
});
