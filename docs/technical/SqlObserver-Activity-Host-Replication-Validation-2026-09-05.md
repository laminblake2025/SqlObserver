# Activity history and host/replication reporting validation — 2026-09-05

Implemented and deployed on the disposable WIN-QNGOV5GDM24 lab. This is a validated implementation phase, not a claim that the entire project is release-certified.

## Delivered behavior

- Activity sections load independently. A failed request no longer discards successful sessions, waits, current blocking, or history. Cancellation still aborts the whole load.
- Blocking history includes observation timestamps and a bounded Last hour / Last 6 hours / Last 24 hours selector. Each section retains its existing 25-row page bound.
- Explicit host capability flags are accepted consistently by the API, repository validator, and database. The host registration transaction now closes its result reader before committing.
- The Windows helper uses a fixed absolute executable path and embeds only an allow-listed numeric selector in its encoded script. Fixed counter queries run concurrently under one owned cancellation/termination boundary. Logical drive identities match performance-counter identities, and committed memory uses the actual Windows committed-bytes counter.
- Host and replication commits now complete their canonical outcome and schedule atomically. Failure outcomes retain evidence gaps; replay adds no duplicate rows; expired/released ownership cannot write.
- Replication payloads use their dedicated validator. Reduced visibility persists with the explicit `visibility_incomplete` reason. A null visibility-gap field is not interpreted as a real gap.
- Omitted analytics snapshot times are accepted; explicit non-UTC snapshots remain rejected. The overview CPU chart now reads the measured series.
- Host metric dimensions are returned as JSON objects. The frontend accepts zero-offset PostgreSQL UTC timestamps and the system's opaque host GUIDs. Replication coverage gaps display their reason and unavailable values. Scalar evidence values remain readable in horizontally scrollable tables.

## Lab state

Dashboard: https://win-qngov5gdm24:5443/

- Target: `dfb2b72c-4ad4-4765-9523-528c798482f0`, revision 1.
- Host: `6b641784-45e4-7e6a-e059-228b6034df6d`, binding/profile revision 1.
- Probed profile: Windows 10.0.26100, 12 logical processors, 8,488,157,184 bytes physical memory; CPU, memory, logical-volume and disk-latency capabilities enabled.
- Collector service account was added to Windows **Performance Monitor Users** and its service restarted. It was not made an administrator.
- Scheduled service collection began persisting host metrics at **2026-09-05T12:27:36.020220Z**, before the separate administrator-run lifecycle probe. One host sample produces eight metric rows for this one-volume VM.
- Example service observations: CPU 1.514379%, available memory 4,017,496,064 bytes, committed memory 6,108,192,768 bytes, volume free space 8,320,315,392 bytes, total space 68,375,425,024 bytes.
- Replication now completes at its normal cadence with `partial / visibility_incomplete`; no distribution database is bound. The UI explicitly reports that topology health and queue values are unavailable.
- The Sales and Warehouse synthetic databases from the prior phase remain available.

Live deployment directories:

- `C:\SqlObserverLab\Server-activity-host-v5-20260905`
- `C:\SqlObserverLab\Collector-activity-host-v4-20260905`
- `C:\SqlObserverLab\Web-activity-host-v3-20260905`

External configuration remains under `C:\SqlObserverLab\Config-16c5565`. Credentials are not recorded here.

## Database migrations

Migrations 30–34 were applied through the migration CLI and pinned in the catalog and certification dependencies. Already-applied files were not edited.

| Migration | Purpose |
| --- | --- |
| 0030 | Canonical M10 collection lifecycle and replication gap binding |
| 0031 | Explicit host metric capability flags |
| 0032 | Qualified host binding column references |
| 0033 | Use the explicit host profile identity constraint |
| 0034 | Permit the replication visibility-incomplete gap reason |

## Validation

- Local validation profile passed: **1,072 .NET tests**, migration/catalog pins, frontend checks and build. External release certification lanes are intentionally outside that profile.
- Final frontend suite: **99 passed**. Includes partial activity failures, cancellation, 24-hour bounds, actual host identity shape, UTC offset handling, and replication visibility gaps.
- Targeted host/replication regressions: **19 passed**. Includes concurrent fixed-query startup and a real parser-produced replication envelope passing the persistence validator.
- Final analytics snapshot regression suite: **15 passed**, including omitted, explicit UTC, and rejected non-UTC snapshot timestamps.
- Analytics API contracts: **18 passed**, including known, unknown, fractional, string and null host capability flags.
- Final PostgreSQL Local lanes after the dimension projection fix: **57 passed**.
- A broader standalone PostgreSQL invocation accidentally included four external certification tests; those could not initialize because Docker/release configuration was unavailable. The exact Local filter subsequently passed. This does not replace release certification.
- Live commit/replay/fencing probe on the VM:
  - Replication: Partial, first commit inserted 1 row; replay inserted 0; commit after lease release returned LeaseLost.
  - Host: Succeeded, first commit inserted 8 rows; replay inserted 0; commit after lease release returned LeaseLost.
- Browser validation on Astra High: the overview CPU chart rendered 15 observations from 12:27:36 to 12:42:13 UTC; real host metrics rendered; replication coverage and unavailable values rendered; six-hour blocking history showed the nine observations of session 82 blocked by 67 around 11:31–11:33 UTC, while current blocking was empty and current evidence succeeded.
- `git diff --check` passed; an existing line-ending warning is unrelated to this phase.

Evidence is saved under `TestResults/activity-host-phase/`: `validate-local.txt`, `web-tests-final.txt`, `m10-regression-tests.txt`, `m10-api-tests.txt`, `postgresql-local-final.txt`, `commit-replay-fence.txt`, API JSONL captures, and `browser-*.txt/png` captures.

## Remaining work

- Configure a real publisher/distributor/subscriber fixture to validate replicated commands, latency, agent failure and recovery. Current validation proves honest reporting of an unconfigured topology, not functioning replication.
- History reads remain bounded; the selector does not remove the 25-row cap or add deeper history pagination.
- Sustained-load, additional SQL versions/topologies, installer, signing and the remaining release certification lanes are still separate project work.

## Follow-up deployment

The real replication fixture, stopped-agent/backlog/recovery validation, revision-safe host history and shared Overview deployment are now covered by [Replication and Overview validation](SqlObserver-Replication-Overview-Validation-2026-09-05.md). That report supersedes the unconfigured-replication status above.
