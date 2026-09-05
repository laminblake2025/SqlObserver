# Synthetic database validation — 2026-09-05

This phase runs real SQL Server workloads on the authorized disposable WIN-QNGOV5GDM24 VM. The dashboard is connected to SQL Server 2025 at the existing registered target dfb2b72c-4ad4-4765-9523-528c798482f0; these are synthetic databases on a real SQL engine, not browser mocks.

## Fixtures and execution

See tools/lab/README.md for repeatable setup and execution. SqlObserverLabSales (database 5) contains 122,004 synthetic rows and enables Query Store. SqlObserverLabWarehouse (database 6) contains 80,000 inventory rows and deliberately disables Query Store to exercise plan-cache fallback.

Final all-scenario run: C:\SqlObserverLab\Workloads\20260905-071333-7e8fc0, 07:13:33–07:17:34 UTC. All seven SQLCMD processes exited 0. Earlier attempts exposed an Agent startup race and partially configured job; the runner and job setup were repaired and the complete run repeated. SQL Agent job history contains the intended error 51042, and the deadlock victim script records expected error 1205. Both compressed COPY_ONLY backups passed RESTORE VERIFYONLY. All four lock rows remained Value=0 after rollback. Backup verification is not a full restore/recovery drill.

## Implementation repairs

- Corrected Windows msdb user verification in the permission generator and applied its generated least-privilege grants.
- Corrected Extended Events catalog column names, bounded the file read to a single scan, and fixed sequential-reader column ordering.
- Joined Query Store runtime intervals for valid start/end timestamps, normalized SQL timestamps to PostgreSQL microsecond precision, aggregated duplicate plan-cache identities, and excluded plan-cache rows for databases already handled by Query Store. Per-database accounting retains its actual source.
- Preserved M9 history permission probes and their evidence in capability v3 profiles. Fixed forward-only reads for backups, Agent, TempDB and availability-group readers.
- Migration 26 updates exact M6/M7 catalog asset pins transactionally and restores the append-only trigger before commit. Matching C# runtime pins were updated. Historical run hashes remain intact.
- Migration 27 repairs quoted JSON column binding in populated M9 persistence, excludes failure payloads from snapshot inserts, and removes deadlock conflict-target ambiguity.
- Migration 28 restores prior partial-result combinations in shared outcome constraints that migration 13 had dropped. Valid partial query evidence now persists.
- Migration 29 repairs operational payload-kind binding for hyphenated collector IDs, allowing SQL Agent failures to persist.

Applied migration hashes (immutable):

| Migration | SHA-256 |
|---|---|
| 26 | 1f0daf862917c9b8d343658c8b8ae0a830a785e9b35d58379bceb7b1b267284b |
| 27 | c7028ba20a7ecd720307d8b8685be417b7bd9dff2394f522b5894b86795e9c46 |
| 28 | 33caca480a622481e079f6fd6ca6aea0625bec07b110b5cbd2dc138dd860d5b1 |
| 29 | e745c1e5830956480a1ce3c39094eea064c47dd9c2e6451ee2768bc889d64fac |

Collector deployment: C:\SqlObserverLab\Collector-workload-v9-20260905. Existing external service configuration and validated TLS remain in use. No recurring workload or job schedule was installed.

## Browser evidence

Validation used the actual dashboard at https://win-qngov5gdm24:5443/ through browser automation on Astra High.

- Health showed both synthetic databases ONLINE with collected file I/O.
- Activity captured the deliberate LCK_M_X blocking during the first run, including the waiting and blocking sessions. A later post-run Activity request failed safely; clearing/history rendering is not marked passed.
- Deadlock detail displayed both participants, the victim, and the two opposing key/X relationships. The read-only collector subsequently returned exactly two events from the two runs.
- Query Store ranking and query detail passed after migration 28. The selected Sales observation showed CPU 7,608 ms, duration 7,868 ms, 446 executions and 198,024 logical reads, with plan identity and a bounded history point.
- Selecting the plan-cache source showed four Warehouse observations, including CPU 19,503 ms. Source selection reset the previous query detail correctly.
- Operations passed at 07:46 UTC: two complete backup records (COPY_ONLY, checksums, not damaged), four complete Agent failure records including error 51042, and nine healthy TempDB file records. Backup UTC finish is correctly unavailable because SQL source timezone is not established; source local finish is retained.

Evidence is retained under TestResults/workload-phase: workload run logs, rollback-and-job-verification.txt, collector-probe-final.txt, queries-populated.txt, query-detail.txt, plan-cache-populated.txt and screenshots. The direct collector probe is an administrator-run, read-only diagnostic; browser results separately establish persistence through the deployed service.

## Scope and remaining limitations

The project is not release-complete. Query coverage is explicitly partial/truncated; query text and plan content remain unavailable. Plan-cache fallback state labels currently report unsupported/read_failure rather than the fixture's disabled Query Store state. Health still has local-time formatting under a UTC shell label. Narrow browser tables require horizontal scrolling. Activity post-run failure and existing M10 host/replication persistence errors need another repair phase. Availability Groups, replication topology, sustained capacity, full recovery, external notifications and formal release certification were not validated by this two-database fixture.

## Automated validation

The Local profile before the final capability-validator adjustment and migration 29 passed: 1,062 .NET tests and 94 frontend tests, including migration checksum, contract, typecheck and web build checks. After the last capability validator adjustment, the complete affected suites passed again: 368 unit tests (including both legacy and expanded v3 permission profiles) and 101 SQL Server integration-contract tests. The normal Local profile excludes external release-certification lanes; live VM/browser evidence above supplements it and is not release certification.

Two diagnostic test invocations were unsuitable: one omitted the release-lane exclusion and correctly failed for a missing certification-case environment variable; an isolated-artifact invocation broke tests that assume the standard repository output layout. The corrected normal-layout invocation passed. Initial migration 28 packaging was rejected before application because of CRLF; its unapplied bytes were normalized to LF and it then applied successfully. No already-applied migration was rewritten.

The saved profile at 07:39:45 UTC confirms validated encryption, non-sysadmin collection, and granted database-scoped SELECT on backupset and sysjobhistory. Discovery is still degraded because this lab uses NTLM fallback.

After migration 29, all 57 environment-independent PostgreSQL tests and all eight report certification/pin tests passed. Final operational browser evidence is operations-populated.txt and screenshots/operations-populated.jpg. The collector remains running on v9. Workload processes have finished; databases remain connected for future runs.
