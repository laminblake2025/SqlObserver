import { defineConfig } from '@playwright/test';
const port = Number(process.env.SQLOBSERVER_LAB_DASHBOARD_PORT ?? 4322);
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid loopback lab port.');
export default defineConfig({
  testDir: './browser-lab-tests', testMatch: 'connected.spec.mjs', workers: 1, retries: 0, timeout: 60_000,
  reporter: [['list'], ['json', { outputFile: '../.artifacts/lab-dashboard-20261009/browser-results.json' }]],
  outputDir: '../.artifacts/lab-dashboard-20261009/browser',
  use: { baseURL: `http://localhost:${port}`, viewport: { width: 1600, height: 1000 },
    launchOptions: { args: ['--auth-server-allowlist=localhost,127.0.0.1'] } },
});
