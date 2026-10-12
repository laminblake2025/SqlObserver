# MCP incident discovery

`list_incidents` makes incident evidence discoverable through MCP. It returns
thread IDs, opening times, and generation counts/latest observation times for
one instance. Pass a returned `threadId` to `get_incident_evidence` for that
same instance. The listing contains no summary JSON or evidence payloads.

The opening-time window defaults to the last 24 hours and allows at most 31
days. Pages contain up to 100 items, ordered by opening time and thread UUID.
Incidents opened before the selected window are excluded even if still active.
Zero visible generations produce a zero count and an explicit null latest time.
Current open/closed state is not presented as historical state.

Viewer, Operator, or TargetAdministrator access must apply to the requested
instance. Mixed grants do not let an Auditor-only target inherit read access
from another target. All calls use the existing terminal audit boundary; audit
failure withholds incident identities.

## Paging and database change

Forward migration 0086 adds a publication revision per instance/target revision,
seeded for existing incident history. Thread and generation mutations advance
it transactionally. The metadata reader holds a shared lock on that revision
while reading a page. A changed revision on a subsequent call produces the
fixed `cursor_stale` error, instructing the client to restart without a cursor.
Writes to another instance do not invalidate the listing.

The signed continuation carries the complete ordering key, effective window,
target revision, observation cutoff, and publication revision. Backdated inserts
cannot silently create gaps between pages. Observation cutoffs filter thread
opening and generation observation timestamps; they are not ingestion-time
watermarks. Evidence retrieval takes its own snapshot and can include later
generations. When cursor signing is unavailable, the first page still reports
`hasMore`, with no unusable continuation token.

The new PostgreSQL table has forced row-level security and no application-role
table grants. Server-only functions validate transaction-local target scope;
they are owned by the migrator, use a fixed search path and UTC, and deny PUBLIC,
collector, and auditor execution. Existing incident/evidence APIs are retained.

## Compatibility and verification

The catalog now contains 27 tools. The approved digest is
`984319BD896C532E6E4B942334318FF5AB00E768677824023CCCEA180BF1DC2C`.
Deploy migration 0086 and the matching server/stdio bridge; the bridge rejects
a mismatched catalog identity. Catalog and release asset pins were updated.

The initial MCP regression run produced 15 missing-tool failures and one passing
audit-withholding control. After implementation and two existing fixture updates,
42 distinct focused MCP checks passed, including the 18 new cases, SDK discovery,
all application routes, output schemas, signed/unsigned paging, mixed grants,
stale-cursor audit, and actual stdio forwarding/transcripts. Independent review
identified an undocumented generation-count ceiling; it was removed, with two
focused checks confirming the nonnegative 64-bit count contract.

Nine PostgreSQL 18 checks passed: server-only permissions and target scope;
101 equal-time threads without duplicates or gaps; observation cutoff and
list-to-evidence behavior; backdated writes between pages; a transaction begun
before the first page with deterministic lock-blocking verification; an absent
publication fence; mutation invalidation; SQL bounds; and the 85-to-86 upgrade
preserving existing history. Asset verification passed for 25 manifests,
251 direct pins, 37 nested pins, and 41 SBOM inputs.

No full Local validation, full MCP/PostgreSQL suite, broad CI run, or native
SQL Server certification was needed for this slice. All-tools PostgreSQL/MCP
pipeline and native authentication qualification remain separate open work.
