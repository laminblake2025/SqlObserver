# ObservationChart mounted component checks

This opt-in suite mounts the real `ObservationChart` under the real `TimeDisplayProvider`, imports the product stylesheet, and serves a dedicated HTML/TSX entry through the existing Vite configuration and React plugin. It uses the already installed React, Vite, Playwright, and axe dependencies. The harness changes real props and shares controlled crosshair/time-window state between two charts; labeled outputs expose callback results.

Run from `web` with existing dependencies and the existing Chromium installation:

```powershell
pnpm exec tsc --project tsconfig.component-tests.json --noEmit --pretty false
pnpm exec playwright test --config playwright.dashboard-components.config.mjs
```

The suite starts one Vite server at `127.0.0.1:4328` with strict port ownership, then stops it when Playwright finishes. It does not run a product build, write `dist`, use an API fixture, or require a database. Every test aborts and reports attempted API/external HTTP requests and browser runtime errors. The isolated TypeScript configuration inherits the source strict flags and checks the TSX harness plus its imported source modules; the browser spec remains an ES module like the existing Playwright suites.

Eight behavioral tests cover missing versus zero observations; solitary points and visible gaps; reference legends and accessible formatted value tables; shared crosshair callbacks and pointer leave; normalized drag selection, no-op and cancellation; unavailable props and restoration; local/UTC display; and accessibility/overflow at 320 and 1440 pixels. Gap checks use browser-computed rendered stroke bounds and length, without reproducing SVG path strings or the chart model's calculations. Pointer cancellation uses a browser event because Playwright's mouse API has no native cancellation command.

This is complementary component coverage. Existing pure model tests remain useful and are retained; this suite does not complete the migration of unrelated legacy source assertions or replace dashboard integration coverage.

Results live in `artifacts/dashboard-components/`: `results.json`, `results/` for failure traces/screenshots, and `report/` for the HTML report and axe attachments. Responsive captures use separate stable paths in `screenshots/observation-chart-320.png` and `screenshots/observation-chart-1440.png`. These captures are harness evidence and do not replace product gallery images or historical screenshot counts.
