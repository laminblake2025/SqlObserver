# MCP metric discovery

`list_metric_catalog` exposes the ten enabled metric definitions already accepted by metric-series, comparison, and forecast queries. It returns catalog version/checksum, metric key, display name, unit, source, aggregation, and allowed dimension keys. The list is deterministic and has no arguments, cursor, or pagination. Definition membership does not imply that a particular instance has collected samples.

An active Viewer, Operator, or TargetAdministrator can read the embedded definitions, including users with target-scoped grants. No instance or repository read is involved. Subsequent metric queries continue to enforce authorization on their selected target. Disabled, unauthenticated, unbound, and nonread-role callers are denied. Every invocation uses the terminal audit boundary; a failed audit withholds the result. The catalog audit has no target identifier, including when a caller supplies a rejected `instanceId` argument.

The application owns authorization and the typed metadata projection. MCP adds explicit field mapping and closed input/output schemas. Existing metric-query input enums remain derived from the same catalog.

## Compatibility and verification

The tool inventory now contains 26 entries. The approved catalog digest is `5A96BDA0790C7D0B326CB5A10C7B33D773026D0BB15A21C0A676ED3322BF1AA8`; server and stdio bridge identities and release pins were updated together. Deploy the matching bridge and server because their identity check intentionally rejects a mismatched catalog.

Seventeen new checks initially produced 16 failures and one passing audit-withholding control. After implementation, 24 focused checks passed, covering the new route, actual group claims with restricted grants, metadata/schema parity, denied and invalid calls, every existing application route, SDK discovery on both supported protocols, and the approved catalog identity. Thirteen further affected transport checks passed, including real stdio forwarding, exact protocol transcripts, current/downlevel negotiation, and identity checks. The independent read-only review found no material issue.

Asset verification passed: 25 manifests, 250 direct pins, 37 nested contract pins, and 41 SBOM inputs. No database migration, full Local validation, full MCP suite, or broad CI run was needed for this metadata-only change. Native Windows authentication and SQL Server release qualification are not claimed.

Incident discovery and all-tools PostgreSQL/native certification remain open work.
