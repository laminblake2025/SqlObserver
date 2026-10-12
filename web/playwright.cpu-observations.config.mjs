import base from './playwright.dashboard-components.config.mjs';
// The caller owns the loopback Vite session, avoiding Windows shell-tree teardown.
export default { ...base, webServer: undefined,
  testMatch: ['CpuObservationHistory.spec.mjs', 'CpuCaptureWindowHistory.spec.mjs'],
  outputDir: '../artifacts/dashboard-cpu-observations-2026-10-05/mounted-results',
  reporter: [['list'], ['json', { outputFile: '../artifacts/dashboard-cpu-observations-2026-10-05/mounted-results.json' }]],
};
