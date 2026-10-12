import base from './playwright.dashboard-refactor.config.mjs';
// The caller owns the loopback fixture session and builds the actual Web assets first.
export default { ...base, webServer: undefined,
  testMatch: ['dashboard-cpu-observations.spec.mjs', 'dashboard-cpu-capture-history.spec.mjs'],
  outputDir: '../artifacts/dashboard-cpu-observations-2026-10-05/workspace-results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-cpu-observations-2026-10-05/workspace-results.json' }]],
};
