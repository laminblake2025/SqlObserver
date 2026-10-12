# Shared frontend API transport

The frontend routes all 26 feature API modules through `web/src/apiClient.ts`.
Feature modules still validate their own target, revision, cursor and response
schema. This transport replaces their direct `fetch` calls; it does not change
API contracts or add automatic retries to mutations.

## Request and cache behavior

- Requests use local `/api/v1` or `/api/v2` paths, same-origin credentials and
  same-origin mode. Redirects fail rather than silently changing destinations.
- Matching concurrent GETs share one network request. Keys include the full URL,
  normalized request headers, browser cache option, response limit and timeout.
  Each subscriber gets an independent response body and can cancel independently.
  The network request is cancelled when its last subscriber leaves.
- Completed caching requires an explicit server `public, max-age` policy on a
  successful JSON response. Entries expire within five seconds, accounting for
  `Age`. Private, no-store, no-cache and identity-varying responses are excluded;
  request no-store, no-cache and reload options bypass completed caching.
  Sensitive query text, plans and live evidence retain their existing no-store
  settings. In-flight sharing still applies to matching no-store reads.
- Every mutation invalidates reads before dispatch and after completion,
  including uncertain network failures. A 401 or 403 also invalidates reads.
  Reads started before invalidation cannot populate the new cache or acquire
  new subscribers. Existing subscribers retain their own cancellation controls.
- Responses are bounded while streaming, before feature parsing. The default
  limit is 4 MiB and can be configured up to 16 MiB. Existing smaller feature
  limits still apply. The default 30-second deadline covers headers and body;
  callers can configure up to 120 seconds. There are at most 64 outstanding
  transports and 32 completed entries totaling at most 8 MiB of response bodies.

## Validation, 2026-09-30

The selected frontend suite passed **308 tests**, including runtime checks for
shared production readers, independent cancellation, request separation by
window and headers, expiry, invalidation, response bounds, stalled bodies and
outstanding-request limits. Existing feature checks cover both API versions and
their target/cursor validation. `pnpm run build` passed TypeScript checking,
production bundling and generated asset-manifest verification.

The selected suite excludes `m12-license-generator-contract.test.mjs`,
`m12-provenance-generator-contract.test.mjs` and
`m12-sbom-generator-contract.test.mjs`. An earlier full sweep encountered trusted
PowerShell availability and release input-pin/hash failures in those generators.
Those failures are outside this frontend change and remain unresolved. This
validation does not establish a passing full release suite.

No database or SQL Server tests were needed for this transport-only change.
