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

The current M7 commit contract is metadata-only. It now rejects an observation
with a `ContentReference` before writing a run, instead of silently discarding
that reference. Enabling content requires a leased, atomic link commit and
retrieval path; changing the collector output alone is insufficient.

Migration 0119 adds an exact monitored target to new protected payload rows.
The repository write request now requires that target, and deduplication only
returns a ciphertext row owned by the same target. An existing fingerprint
owned by another target, or by a historical row without a target, is rejected.
Historical rows remain intact for migration and retention; they cannot become
new query-content references. Migration 0120 enforces the same target match on
new query-performance content links, while preserving historical links for
reconciliation. A leased atomic link commit, query identity binding, and
authorized reader are still needed before collection can be enabled.
