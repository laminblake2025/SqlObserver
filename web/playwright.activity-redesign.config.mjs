import { defineConfig } from '@playwright/test';
import base from './playwright.redesign.config.mjs';
export default defineConfig({ ...base, testMatch: 'activity-redesign.spec.mjs', outputDir: '../.artifacts/activity-redesign-20261009/browser', reporter: [['list'], ['json', { outputFile: '../.artifacts/activity-redesign-20261009/browser-results.json' }]] });
