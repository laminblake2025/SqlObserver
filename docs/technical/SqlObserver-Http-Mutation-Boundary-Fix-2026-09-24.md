# HTTP mutation boundary repair

The report body middleware previously ran before authorization and rate limiting.
An unauthenticated malformed or oversized report request could therefore append
an audit row. The report creation endpoint also had no rate-limit policy, and its
trailing-slash form bypassed report-specific body handling.

Authorization now runs before API origin checks, the existing principal mutation
limiter, and body pre-validation. Report creation uses that limiter. Both report
route forms receive the same bounded read, rewind, validation, and terminal audit.
Authentication, origin, and rate-limit rejections never reach report audit storage.
An admitted authenticated request with an invalid report body still produces one
terminal audit; audit failure still prevents the operation.

Windows credentials can accompany browser requests automatically. The seven
manually parsed analytics and retention POST routes previously accepted JSON
carried as `text/plain` or without a Content-Type. They now require
`application/json` (parameters such as `charset=utf-8` are accepted) and return
415 before repository access for other media types. The existing body size and
object-shape limits remain in place.

The shared `/api/v1` middleware rejects unsafe requests whose supplied Origin is
malformed, null, or differs in scheme, host, or effective port from the request
authority. A supplied `Sec-Fetch-Site` must be exactly `same-origin`; conflicting
headers fail closed. This includes typed JSON mutations as well as manually
parsed ones. GET, HEAD, OPTIONS, and routes outside `/api/v1` keep their existing
behavior. Headerless native JSON clients remain supported. Mutation digests are
integrity/replay checks, not CSRF tokens. No cross-origin API contract is introduced.

The regression suite uses the real Server pipeline and application services with
test authentication and recording repository ports. Before production changes,
71 cases produced 46 failures and 25 passes with no skips. All 18 positive controls
passed, covering all seven manual JSON routes for same-origin browsers and native
clients, exact-limit report creation, same-origin Fetch Metadata without Origin,
and a typed policy update. Evidence is in
`artifacts/revamp-http-boundary-red.log` and
`artifacts/revamp-http-boundary-red.trx`.

The expanded suite contains 75 real-pipeline cases and 58 direct middleware
cases, covering malformed or duplicate browser headers, authority syntax,
scheme/host/port mismatch, case and default-port equivalence, IPv6, all unsafe
methods, safe methods, and unrelated protocol paths. It also checks that hostile
report requests never write an audit row.

The first expanded run passed 131 of 133 cases. The two limiter cases exposed a
test-host setup error: `ConfigureAppConfiguration` applied their one-request
limit after Program had read its settings. The factory now uses `UseSetting`,
matching the existing target endpoint tests. Both limiter cases then passed;
the production limiter required no further change. Those two original RED
failures were therefore not independent evidence of a one-request limit breach;
the absent report policy was established from endpoint metadata/source. All
other RED failures reproduced production boundary gaps.

Focused verification commands:

```powershell
dotnet test tests/SqlObserver.ApiContractTests/SqlObserver.ApiContractTests.csproj --configuration Debug --no-restore --filter 'FullyQualifiedName~MutationRequestBoundaryHttpTests|FullyQualifiedName~ApiMutationOriginMiddlewareTests' --logger 'trx;LogFileName=revamp-http-boundary-green.trx' --results-directory artifacts
dotnet test tests/SqlObserver.ApiContractTests/SqlObserver.ApiContractTests.csproj --configuration Debug --no-build --no-restore --filter 'FullyQualifiedName~ReportLimiterRejectsSecondMalformedRequestBeforeAnotherAudit' --logger 'trx;LogFileName=revamp-http-boundary-limiter-green.trx' --results-directory artifacts
```

Evidence is in `artifacts/revamp-http-boundary-green.log`/`.trx` and
`artifacts/revamp-http-boundary-limiter-green.log`/`.trx`. The build passed, all
133 focused cases have passing evidence, and no test was skipped. A fresh,
read-only candidate review found no concrete surviving bypass or regression in
the changed HTTP and PostgreSQL boundaries. It checked direct callers, route
aliases, parser representations, normal client compatibility, and SQL execution
contexts.

Final validation passed:

```powershell
pwsh -NoLogo -NoProfile -File ./tools/validate.ps1 -Profile Local -TestResultsDirectory artifacts/revamp-security-final/test-results
```

All 1,418 selected .NET tests and 147 frontend tests passed with no failures or
skips, including the complete 381-case API suite, 112 security tests, and 77 MCP
contract tests. The solution build, TypeScript check, production web build, asset
verification, and checksum/contract checks passed. Evidence is in
`artifacts/revamp-security-final-local.log` and the per-suite TRX files under
`artifacts/revamp-security-final/test-results`.

This repair does not change the Windows authentication or role policy, the MCP
protocol, or repository schema. It is not deployment or release certification.
