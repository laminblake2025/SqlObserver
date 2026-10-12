# SQL Agent source execution start

SQL Agent failure, retry, and cancellation records now carry the execution start
reported by `msdb.dbo.sysjobhistory.run_date` and `run_time`. The three supported
SQL Server asset variants append those integer columns to the existing bounded
passive query. Valid values become `sourceLocalStart` in the exact form
`yyyy-MM-ddTHH:mm:ss`. The value has no UTC offset or time zone. Missing and
invalid source values remain null; no UTC conversion or current server offset
is inferred. [SQL Server job-history columns](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/dbo-sysjobhistory-transact-sql?view=sql-server-ver17).

`firstObservedAtUtc` continues to mean when the repository first saw an event.
It remains the UTC window, ordering, and cursor basis. The failure fingerprint
and job-versus-step classification are unchanged. HTTP, MCP, and the Operations
screen display the source start separately and label it as the server's local
clock. MCP includes an explicit null and an offset-free output schema; the web
parser accepts older responses with no source field and rejects malformed or
offset-bearing values.

Forward migration 0087 adds the nullable timestamp to **per-run occurrences**.
The immutable event row and its first-observed time stay intact when a later
scan finds an execution start for the same failure. The migration validates the
new optional JSON field before committing, exposes a versioned scoped reader,
and advances the four collectors that share the M9 asset bundle under the
existing append-only registry guard. A null source value keeps the legacy JSON
shape and completion digest; an altered known value fails replay by digest.
Known values add eight estimated response bytes and eight persisted bytes per
new occurrence. Deploy the migration, collector assets, server, and web client
together because the prior web parser rejects an unfamiliar Agent field.

## Focused verification

The collector regression first failed for six valid execution starts, then
passed 22 cases covering valid, invalid, null, daylight-saving ambiguous and
nonexistent wall-clock times, and the stable fingerprint. Four PostgreSQL
integration tests passed against PostgreSQL 18.4 in disposable containers:
86→87 legacy replay and re-observation, known/null round trips and divergent
replay, malformed JSON rollback, and reader access and bounds. Two historical
bundle-upgrade tests (79→latest and 84→85→latest) also passed. Nine MCP
classification/source-time tests, two production-hosted HTTP tests, one focused
web parser test, TypeScript checking, and the pin verifier passed. The verifier
checked 25 manifests, 252 direct pins, 37 nested contract pins, and 41 SBOM
inputs. The MCP catalog digest was reapproved after its tool description
changed. No full test suite or broad CI run was needed for this slice.

The three native SQL Server query variants were checked statically and by the
collector's fake-row tests. They have not run against the lab instance; the
separate passive-test package upload still awaits authorization.
