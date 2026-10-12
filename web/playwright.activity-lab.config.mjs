import { defineConfig } from '@playwright/test';
import base from './playwright.lab.config.mjs';
export default defineConfig({ ...base, testMatch: 'activity-redesign.spec.mjs', timeout: 70_000, reporter: [['list'], ['json', { outputFile: '../.artifacts/activity-redesign-20261009/native/browser-results.json' }]], outputDir: '../.artifacts/activity-redesign-20261009/native/browser' });
