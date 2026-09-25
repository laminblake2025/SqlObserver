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
