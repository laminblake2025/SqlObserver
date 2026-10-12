import { defineConfig } from '@playwright/test';
import base from './playwright.dashboard-activity-time.config.mjs';
export default defineConfig({ ...base, testMatch: ['dashboard-activity-time.spec.mjs', 'dashboard-activity-calendar.spec.mjs'], use: { ...base.use, timezoneId: 'America/New_York' }, outputDir: '../.artifacts/activity-redesign-20261009/history', reporter: [['list'], ['json', { outputFile: '../.artifacts/activity-redesign-20261009/history-results.json' }]] });
