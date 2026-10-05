import base from './playwright.dashboard-components.config.mjs';
// Caller owns the loopback Vite process and its teardown.
export default {...base, webServer:undefined,
 testMatch:['LifecycleCollection.spec.mjs','ServerLifecycleHistory.spec.mjs'],
 outputDir:'../artifacts/dashboard-lifecycle-collection-2026-10-05/mounted-results',
 reporter:[['list'],['json',{outputFile:'../artifacts/dashboard-lifecycle-collection-2026-10-05/mounted-results.json'}]],
};
