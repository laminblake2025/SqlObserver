import { defineConfig } from '@playwright/test';
import base from './playwright.dashboard-components.config.mjs';
export default defineConfig({ ...base, outputDir: '../.artifacts/ui-redesign-20261009/component-results', reporter: [['list'], ['json', { outputFile: '../.artifacts/ui-redesign-20261009/component-results.json' }]] });
