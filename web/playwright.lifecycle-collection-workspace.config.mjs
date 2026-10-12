import base from './playwright.dashboard-refactor.config.mjs';
export default {...base,webServer:undefined,
 testMatch:['dashboard-lifecycle-collection.spec.mjs','dashboard-refactor.spec.mjs','dashboard-native-insights.spec.mjs'],
 grep:/dashboard reader|dashboard administrator|dashboard baseline|partial dashboard|missing-hour|a delayed old-server baseline|a delayed old-window baseline|Investigate first|health check workflow/u,
 outputDir:'../artifacts/dashboard-lifecycle-collection-2026-10-05/workspace-results',
 reporter:[['list'],['json',{outputFile:'../artifacts/dashboard-lifecycle-collection-2026-10-05/workspace-results.json'}]],
};
