# Query-content protection boundary

Current status: 2026-10-01. The collector and server use an optional Windows
protection provider for query text and execution plans. Configure
`SqlObserver:QueryContent:ProtectedKeyPath` in both services with the same
machine-DPAPI-protected 32-byte key file. The existing
`tools/lab/provision-live-activity-key.ps1` provisions that file format and
restricts it to the named service identities. Use a **separate key and path**
for query content. Do not replace the key while protected history still needs
to be read. Provision it on the Windows machine hosting those services; a
LocalMachine DPAPI blob is bound to that machine. Cross-machine key distribution
is not qualified by the local test.

The provider fails closed without a usable key, on non-Windows hosts, for broad
file or directory access rules, or for a reparse-point key file or directory. Query text is
bounded to 16 KiB and plan content to 1 MiB before protection. AES-256-GCM
authenticates the target identity and content kind; a keyed SHA-256 fingerprint
scopes repository deduplication to that same target and kind. Ciphertext has a
fresh nonce for each call. The provider verifies the fingerprint after decryption
and rejects tampering or cross-target use.

Bounded Query Store text/plan capture, canonical content references, scoped
retention and audited HTTP readers have been implemented. Query Store drilldowns
open content only on an explicit click for a caller with an operational read
role and QueryTextReader on that same server. Text and XML render as inert
preformatted text. Content stays out of summaries, logs, URLs, exports and MCP
catalog responses.

The staged query-performance v3 collector adds bounded content to the same
cache-counter capture: at most eight entries, two MiB of source UTF-16 and two
MiB of ciphertext, with the per-kind limits above. Counter and content
identities must match the target revision, database incarnation, source epoch,
query, plan, cache entry and capture time. Migration 0322 stores opaque links
with scoped retention; 0323 commits links and runtime evidence atomically with
the leased run outcome. Migration 0324 supplies exact-observation reads that
also require retained counter evidence, current target revision and a completed
canonical run. Missing captures, size limits and key failures have explicit
availability reasons.

The staged v3 producer is available through production dependency injection
with the configured key provider. The production registry still selects query
collector v1, and no v3 catalog grants or schedule activation are installed.
SQL Server 2019/2025 qualification and fenced rollout remain required.

The Windows production key provider has passed an actual SQL Server 2022 to
PostgreSQL 18 round trip for v3. The native source connection is opened under
an owned non-sysadmin lab login; the collector, pinned assets, production
protector, repository and audited service are real. A separately constructed
reader loads the same restricted machine-protected key and opens retained text
and plans. A different valid key produces one unavailable audit and no
plaintext. This is same-machine provider qualification; distinct service
identities and production certificate trust still need deployment qualification.
Evidence: `artifacts/plan-cache-deltas/cache-content-production-qualified-final.trx`
and `cache-content-production-composition.trx`.

Migration 0119 adds an exact monitored target to new protected payload rows.
The repository write request now requires that target, and deduplication only
returns a ciphertext row owned by the same target. An existing fingerprint
owned by another target, or by a historical row without a target, is rejected.
Historical rows remain intact for migration and retention; they cannot become
new query-content references. Migration 0120 established the same target match on
new query-performance content links, while preserving historical links for
reconciliation. Migration 0121 also requires new links to identify a query
row from the same collection run and target. Migration 0122 supplies the
combined canonical commit and leased link writer. Protected payloads are
written before the run transaction, so a failed run can leave an unreferenced
ciphertext row; the scoped orphan-retention path reclaims those rows when no
retained link or lease holds them. The
[M7 execution record](../milestones/M7-query-store-query-performance.md) records
the later plan-reference, retention, retrieval and cache-content work and its
remaining release gates.
