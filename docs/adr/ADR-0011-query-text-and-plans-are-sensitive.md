# ADR-0011: Query text and plans are sensitive

- Status: Accepted
- Date: 2026-08-23

## Context

Query text, comments, execution plans, object names, errors, job steps, and XML can reveal business logic, schema, identifiers, literal values, credentials accidentally embedded by applications, personal data, and infrastructure details. They can also contain attacker-controlled markup, formula prefixes, prompt-like instructions, or payloads intended to exhaust parsers and renderers. Treating them as ordinary metrics would spread sensitive content into logs, caches, alerts, exports, telemetry, and MCP audit records.

## Decision

Classify query text and plans as sensitive, untrusted diagnostic content. Apply the same default handling to SQL comments, object names, job steps, server errors, deadlock XML, and related captured XML.

Collect only when a product use case and target capability justify it. Deduplicate payloads, use non-semantic internal identifiers/digests, encrypt/protect storage according to deployment policy, restrict fields and targets through server-side RBAC, and apply explicit retention. Never place unrestricted content in logs, traces, metric labels, URLs, alert notifications, or audit summaries. Audit access using safe identifiers and bounded parameter metadata rather than returned content.

The deduplication fingerprint is a deterministic, externally computed cryptographic
digest of the unprotected content, scoped by payload kind. Randomized encryption may
legitimately produce different nonce, authentication-tag, and ciphertext bytes for the
same fingerprint. The repository therefore uses immutable first-committed-write
semantics: a later kind/fingerprint match returns the original internal identifier and
never overwrites its protected bytes or key metadata. The repository cannot safely
distinguish an exact retry from a cryptographic fingerprint collision without comparing
plaintext, so collision investigation belongs outside this persistence boundary.

Treat content as inert data. Use safe XML parsers with DTDs, external entities, and network resolution disabled. Bound input bytes, decompression, depth, parse time, result rows, and response bytes. Encode for the output context, sanitize any derived visual form, prevent spreadsheet-formula execution in exports, and never use captured content as a command, template, log format, HTML, or instruction to an MCP/AI client.

APIs and MCP expose only the minimum authorized projection. List/summary operations prefer metadata and identifiers; content-bearing retrieval is explicit, bounded, attributable, and separately authorized where policy requires it.

## Consequences

- Some diagnostic workflows require extra authorization and cannot put full text into convenient notifications or traces.
- Deduplication reduces repeated exposure and storage but requires reference cleanup, collision-safe identity, and authorization-aware caching.
- Redaction is defense in depth, not a promise that arbitrary SQL can be reliably anonymized; retention and access control remain essential.
- Security tests need malicious markup/XML, oversized/compressed content, secrets, prompt injection, log forging, and export-formula fixtures.
- MCP behavior remains read-only but can still disclose sensitive data, so [ADR-0006](ADR-0006-read-only-mcp.md) limits and audit apply.
