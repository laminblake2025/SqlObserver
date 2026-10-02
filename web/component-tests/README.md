# Dashboard mounted component checks

This opt-in suite mounts the real `ObservationChart`, `TargetHealthPanel` and `TargetAlertsPanel` under the real `TimeDisplayProvider`, imports the product stylesheet, and serves dedicated HTML/TSX entries through the existing Vite configuration and React plugin. It uses the already installed React, Vite, Playwright and axe dependencies. Harness controls change real props; tests exercise the production transport/parsers with intercepted native synthetic DTOs.

Run from `web` with existing dependencies and the existing Chromium installation:

```powershell
pnpm exec tsc --project tsconfig.component-tests.json --noEmit --pretty false
node --experimental-strip-types --test tests/health-contract.test.mjs tests/alert-contract.test.mjs tests/alert-ui-validation.test.mjs
pnpm exec playwright test --config playwright.dashboard-components.config.mjs
```

The suite starts one Vite server at `127.0.0.1:4328` with strict port ownership, then stops it when Playwright finishes. It does not run a product build, write `dist` or require a database. Chart tests reject API traffic; health/alert tests fulfill only registered API requests locally and abort/report unexpected API or external HTTP requests and browser runtime errors before the Vite proxy/backend is reached. The TypeScript configuration inherits source strict flags and checks all product TS/declarations plus TSX harnesses; browser specs and fixtures remain ES modules like the existing Playwright suites.

Twenty-four behavioral tests cover chart evidence/inspection; collector loss/completeness, exact file counters, database paging, inert names, safe failures and late server requests; and permission/state-gated acknowledgement, stable retries with bodyless 204, filtered/missing selections, pending paging and context-change cancellation. Chart and Health responsive cases check axe, focus and overflow at 320/1440 pixels. Gap checks use browser-computed stroke bounds/length, without reproducing SVG path strings or model calculations. Pointer cancellation uses a browser event because Playwright's mouse API has no native cancellation command. Component permission flags test rendering/actions, not server authorization.

Six original health/alert source-check bundles are replaced with native API/parser and mounted behavior. A smaller health app-wiring/security source constraint remains explicitly named architecture/security lint. Existing pure model tests remain useful and are retained; this suite does not complete migration of unrelated legacy source assertions or replace dashboard integration coverage.

Results live in `artifacts/dashboard-components/`: `results.json`, `native-contract-tests.log`, `browser.log`, `results/` for failure traces/screenshots, and `report/` for HTML/axe attachments. Responsive captures use separate stable `screenshots/observation-chart-{320,1440}.png` and `screenshots/health-panel-{320,1440}.png` paths. These captures are harness evidence and do not replace product gallery images or historical screenshot counts.
