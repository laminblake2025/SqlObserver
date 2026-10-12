# SQL-reported volume window history

This Resources dashboard slice adds selected-window history from retained canonical volume observations. It does not change collection or claim physical-volume incarnation evidence that the source does not retain. Latest snapshot and forecast remain separate Live evidence.

## Identity, time and comparison

Each history identity is the exact `(sampleTargetRevision, volumeKey)` pair, scoped to the target. The keyed source includes target revision, physical node and identity kind; changes never merge with another key or revision. Mount-point keys do not prove volume replacement continuity. Unknown file keys include the run and cannot form a cross-run trend. Public APIs never expose paths, raw node/volume identities or fingerprint inputs.

The selected half-open window uses **collection completion UTC**. Actual SQL observation UTC and ingestion UTC are retained alongside every received sample. Charts use completion time and disclose this basis. There is no interpolation to window edges, no historical forecast and no missing-to-zero conversion. Previous equal-duration windows are independent reads of the same exact identity, with separate coverage. Comparison is eligible only for volume-ID identity, complete received evidence, known unchanged total capacity and sufficient observations in both windows. Capacity changes and unknown identity are disclosed; no physical incarnation guarantee is inferred from a repeated key.

Readers may select an older sample revision explicitly while the current target revision remains a request fence. Retention is the native policy, currently 30 days by default; unsupported or expired evidence is unavailable. Gaps include unsuccessful/lossy runs, missing selected-volume rows, inconsistent stored counts, unknown capacity and elapsed collection gaps. A successful scan without the selected identity means absence at that scan, not zero capacity or a measured replacement.

Exact history emits actual runs of the selected sample revision only. Other revisions remain part of the complete manifest. A window spanning revisions, or containing another revision but none of the selected revision, discloses `revision_boundary` and `hasVisibilityGap=true`; it neither fabricates samples nor permits comparison across the boundary. A future SQL source observation beyond the repository cutoff is disclosed as `source_clock_skew`, rather than silently replacing its time with completion time.

## Native read and HTTP contract

Same-target Viewer, Operator or Target Administrator authorization applies on every request. Responses are private/no-store. Native reads use canonical `collection_run_identity`/partitioned run/outcome relations, not sealed original mirrors. Window duration is at most 31 days, completion cutoff is server-derived, and reads are bounded to at most 10,000 completed runs, 1,000 distinct revision/key identities and 250,000 retained observations. Overflow is an explicit refusal asking for a narrower window, never a truncated healthy result. Native statement work uses a five-second budget or the caller's shorter remaining deadline.

Routes under `/api/v1/observation-targets/{instanceId}/resources/volumes`:

- `GET /history?targetRevision=N&fromUtc=UTC&toUtc=UTC&limit=25&cursor=...`: window identity catalog, with native ordering `(sampleTargetRevision,volumeKey)`.
- `GET /{volumeKey}/history?targetRevision=N&sampleTargetRevision=N&fromUtc=UTC&toUtc=UTC&limit=250&cursor=...`: exact selected identity and per-run evidence, ordered `(completedAtUtc,runId)`.

Common page header: `instanceId`, `targetRevision` (current fence), `fromUtc`, `toUtc`, `cutoffUtc`, `readEpoch` (server digest), `repositoryTimeUtc`, `state`, `reason`, `coverage`, `items`, `nextCursor`. Exact-history header additionally includes `sampleTargetRevision`, `volumeKey`, `identityKind` (nullable when unavailable). UTC is microsecond aligned; all byte capacities/deltas are decimal Int64 strings or null. No caller may supply cutoff, digest, authority or native anchor outside a protected cursor.

Coverage: `runCount`, `completeRunCount`, `gapRunCount`, `retainedObservationCount`, `retentionDays` (nullable if unavailable), `hasVisibilityGap`. Coverage describes retained completed runs, not proof that every scheduled collection occurred.

Catalog item: `sampleTargetRevision`, `volumeKey`, `identityKind`, `observationCount`, `firstCompletedAtUtc`, `lastCompletedAtUtc`, `firstTotalBytes`, `firstAvailableBytes`, `lastTotalBytes`, `lastAvailableBytes`, `comparisonState`, `availableBytesDelta` (nullable), `totalBytesDelta` (nullable).

Exact-history item: `runId`, `completedAtUtc`, `observedAtUtc` (nullable), `collectedAtUtc` (nullable), `outcome`, `lossKind`, `minimumLostItems`, `state`, `reason`, `mappedFileCount` (nullable), `totalBytes` (nullable), `availableBytes` (nullable). Failed/absent/unknown rows retain explicit nulls. Counts and state must agree; missing stored evidence cannot be called complete.

A native content digest binds the complete bounded selected-window run/outcome/observation manifest, including rows not on the current page. Continuations bind target/current revision, window, source cutoff, selected identity (if any), epoch, limit and keyset anchor, expire after 15 minutes and reject tampering or divergent context. Recompute the manifest atomically on every page: late commits, retention or drift invalidate unread continuations rather than silently changing membership. A repeatable-read repository transaction keeps headers, coverage and items coherent.

## UI and qualification

Keep the current latest table distinct. Window history lists received revision/key identities and restores an exact selection from Resources URLs even when absent from latest inventory. Never substitute another identity. Follow the resolved global window/comparison/crosshair, preserve compatible selections on time changes, and use explicit bounded paging. At most 40 pages/1,000 catalog identities and 40 pages/10,000 history events are retained. Unread continuations can expire; already-read evidence remains labelled retained.

Context generation includes target/current revision, selected sample revision/key, window and comparison. Abort and fence late reads across context, ABA, role denial and unmount changes. Transient retry/cancellation retains same-context received evidence; true 401/403 purges protected data. History charts break on missing/unknown/loss/capacity changes or unexplained time gaps and show actual received gauges, not rollup averages. Missing comparison never substitutes current values.

Three owners implement backend contracts/HTTP, native storage/repository, and UI/theme/interactions; the lead integrates shared selection/App seams and final evidence. Required owning checks cover canonical post-seal source reads, revisions/epochs, protected cursor binding/expiry/drift, paging, exact bytes/nulls, mixed grants, outcome gaps, overflow/retention and production HTTP. Mounted and built-App checks cover Rewind/Live separation, exact URLs, cancellation/retry/denial, comparison eligibility, shared time interactions, dark theme, keyboard/axe and four widths. Screenshots and case/input hashes will bind the final local commit.

## Delivered qualification

Final evidence: gallery (local output: `artifacts/dashboard-volume-window-history/index.html`), cases, hashes and provenance (local output: `artifacts/dashboard-volume-window-history/completion.json`). 72 affected .NET checks, 45 native web checks and 131 browser checks pass against frozen final inputs. Native/HTTP qualification uses isolated PostgreSQL 18.4; browser evidence is synthetic. Eight captures cover 320/390/768/1440. No collector or physical-incarnation certification is claimed.

Elapsed-gap classification uses the currently configured volume schedule (twice its interval, at least five minutes; ten-minute fallback); it is a conservative visibility signal, not reconstructed historical scheduler accounting. The UI additionally breaks lines over ten minutes without changing native coverage.
