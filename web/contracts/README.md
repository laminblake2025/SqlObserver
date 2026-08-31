# Web asset identity contract

`web-asset-manifest.v1.schema.json` defines the local M12 web build identity.
The generated catalog is written to the ignored `web/.artifacts` directory;
it is intentionally not a shipped server manifest and creates no serving,
cache, authentication, CSP, proxy, browser, accessibility, or SignalR claim.

Run `pnpm run assets:generate` after a frontend build to create the deterministic
UTF-8/LF catalog, or `pnpm run assets:verify` to verify an existing catalog.
The verifier hashes every file in `dist`, checks the Vite 8 manifest graph and
`index.html` references, rejects unsafe or ambiguous paths and links, and fails
closed on missing, extra, tampered, un-hashed, or unknown output.

The catalog's `sha256` is the SHA-256 digest of the canonical catalog with its
own digest member omitted. Files are sorted by safe POSIX path and include their
exact byte count, lowercase digest, and closed role vocabulary. Source maps and
the Vite manifest are build metadata; executable and static payloads must carry
an opaque Vite hash token in their filename.

This is implementation evidence only. ADR-0018 remains Proposed; hosting,
browser/accessibility, release, cache policy, and SignalR invalidation remain
explicit downstream gates.
