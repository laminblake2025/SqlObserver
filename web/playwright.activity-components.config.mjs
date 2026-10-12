import { defineConfig } from '@playwright/test';
import base from './playwright.dashboard-components.config.mjs';
export default defineConfig({ ...base, testMatch: ['LiveSessionsAccess.spec.mjs', 'ActivityPanel.spec.mjs', 'IndependentBlocking.spec.mjs', 'HeadBlocker.spec.mjs'], outputDir: '../.artifacts/activity-redesign-20261009/components', reporter: [['list'], ['json', { outputFile: '../.artifacts/activity-redesign-20261009/component-results.json' }]] });
