# SQL Agent failure counts

The Overview now counts distinct failed **job outcomes**. A failed step and its
job-outcome row contribute one failed job execution rather than two events.
The affected-job issue uses the same filtered records. Step failures, retries,
and cancellations remain available as diagnostic records, but do not by
themselves establish that a job failed.

The shared classification requires `stepId == 0`, `runStatus == 0`, and a
consistent `Failed` kind. Microsoft documents step zero as a job-history record
and retry as a step-only outcome. [SQL Server job-history reference](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-help-jobhistory-transact-sql?view=sql-server-ver17).

HTTP and MCP expose `isJobOutcome` and `countsAsJobFailure`; MCP also includes
the underlying step and status numbers. The Operations table labels record
scope and whether it counts as a failed job. Its parser accepts the new flags,
checks supplied values against the underlying fields, and accepts older
responses without them as unreported classification. Ship the updated web
assets with the server because older parsers reject unfamiliar Agent fields.

The Overview retains partial coverage when records are truncated or another
page exists. A zero count in a partial page is not proof of no failed jobs.
The source query, persisted observations, fingerprints, first-observed times,
replay digests, and database schema are unchanged. Computed classifications
are added only to the explicit presentation projections.

## Verification and remaining work

Eight new Overview tests first failed on the inflated counts; seven MCP tests
first failed on missing classification fields. The browser parser first
rejected the new fields while its older-response control passed. After the
change, eight Overview, seven MCP, two production-hosted HTTP, and two browser
checks passed. TypeScript checking and the existing asset-pin verifier passed.
The independent boundary review found no material issue. No PostgreSQL, native
SQL Server, full Local validation, or broad CI run was needed for this slice.

Source execution-time collection remains open. `firstObservedAtUtc` still
means when the repository first saw the record. SQL Agent's `run_date` and
`run_time` describe execution start, and they do not carry a UTC offset. A later
change must preserve that distinction while adding source-time metadata.
[Source history columns](https://learn.microsoft.com/en-us/sql/relational-databases/system-tables/dbo-sysjobhistory-transact-sql?view=sql-server-ver17).
