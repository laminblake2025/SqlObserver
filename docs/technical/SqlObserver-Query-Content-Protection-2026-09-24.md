# Query-content protection boundary

The collector now has an optional Windows protection provider for query text and
execution plans. Configure `SqlObserver:QueryContent:ProtectedKeyPath` with a
machine-DPAPI-protected 32-byte key file. The existing
`tools/lab/provision-live-activity-key.ps1` provisions that file format and
restricts it to the named service identities. Use a **separate key and path**
for query content. Do not replace the key while protected history still needs
to be read.

The provider fails closed without a usable key, on non-Windows hosts, for broad
file or directory access rules, or for a reparse-point key file or directory. Query text is
bounded to 16 KiB and plan content to 1 MiB before protection. AES-256-GCM
authenticates the target identity and content kind; a keyed SHA-256 fingerprint
scopes repository deduplication to that same target and kind. Ciphertext has a
fresh nonce for each call. The provider verifies the fingerprint after decryption
and rejects tampering or cross-target use.

This only establishes the protection boundary. The query-performance collector
still selects metadata only, and the API/MCP still do not return query text or
plans. Before enabling collection, add bounded source reads, references from
query observations to protected payloads, target-scoped retention, explicit
`QueryTextReader` retrieval with safe audit metadata, and inert rendering. Keep
content out of summaries, logs, URLs, exports, and MCP catalog responses.

The M7 SQL Server collector still emits metadata only. Its repository commit
now accepts a pre-protected `QueryText` reference and writes the content link
inside the canonical run transaction. The combined database operation checks
the lease, target, query row, payload kind, and keyed fingerprint. The link
writer is private to that operation, so it cannot append evidence after a run
commits. A failed link rolls back the run outcome and metadata evidence.
References participate in the replay digest;
a changed reference cannot be replayed as the original run. The metadata JSON
never contains the payload ID or ciphertext. Execution-plan references remain
rejected until links can identify their exact plan.

Migration 0119 adds an exact monitored target to new protected payload rows.
The repository write request now requires that target, and deduplication only
returns a ciphertext row owned by the same target. An existing fingerprint
owned by another target, or by a historical row without a target, is rejected.
Historical rows remain intact for migration and retention; they cannot become
new query-content references. Migration 0120 enforces the same target match on
new query-performance content links, while preserving historical links for
reconciliation. Migration 0121 also requires new links to identify a query
row from the same collection run and target. Migration 0122 supplies the
combined canonical commit and leased link writer. Protected payloads are
written before the run transaction, so a failed run can leave an unreferenced
ciphertext row; retention must
reclaim it. Bounded SQL Server source reads, an authorized and audited reader,
plan identity, and retention are still needed before content collection can
be enabled.
