import { defineConfig, devices } from '@playwright/test';
import { readFileSync } from 'node:fs';

const selection = JSON.parse(readFileSync(new URL('../artifacts/dashboard-acceptance-2026-10-07/journey-selection.json', import.meta.url), 'utf8'));
const attempt = process.env.DASHBOARD_ACCEPTANCE_ATTEMPT ?? 'attempt1';
if (!/^attempt[1-9][0-9]*$/.test(attempt) || selection.fixtureOrigin !== 'http://127.0.0.1:4322') throw new Error('Acceptance binding invalid');
const titles = selection.groups.flatMap(group => group.titles);
if (titles.length !== selection.expectedCases || titles.length > 60 || new Set(titles).size !== titles.length) throw new Error('Acceptance selection invalid');
const escape = value => [...value].map(char => '.^$|?*+()[]{}'.includes(char) || char.charCodeAt(0) === 92 ? String.fromCharCode(92) + char : char).join('');
const grep = new RegExp(`(?:${titles.map(escape).join('|')})$`);
for (const title of [...titles, `literal.+()[]${String.fromCharCode(92)}`]) {
  const literal = new RegExp(`^${escape(title)}$`);
  if (!literal.test(title) || literal.test(`${title}extra`)) throw new Error('Acceptance literal title escaping invalid');
}
const artifactRoot = `../artifacts/dashboard-acceptance-2026-10-07/`;
const outputDir = `${artifactRoot}results-${attempt}`;
const reportJson = `${artifactRoot}results-${attempt}.json`;
const reportHtml = `${artifactRoot}report-${attempt}`;
// The lead's launch wrapper checks fresh output paths before Playwright starts.
// Config modules are also loaded in workers, after the runner creates output.
export default defineConfig({
  testDir: './browser-tests',
  testMatch: selection.groups.map(group => group.file),
  grep,
  timeout: 60_000,
  globalTimeout: 30 * 60_000,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  outputDir,
  reporter: [['list'], ['json', { outputFile: reportJson }], ['html', { outputFolder: reportHtml, open: 'never' }]],
  use: { baseURL: selection.fixtureOrigin, trace: 'retain-on-failure', screenshot: 'only-on-failure' },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: {
    command: 'node tools/overview-fixture-server.mjs',
    url: selection.fixtureOrigin,
    reuseExistingServer: false,
    env: { SQLOBSERVER_OVERVIEW_FIXTURE_PORT: '4322' },
    timeout: 15_000,
  },
});
