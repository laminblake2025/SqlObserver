# MCP audit append repair

The PostgreSQL functional baseline exposed a real append failure in migration
0015: its actor regular expression uses an unsupported repetition bound. Valid
MCP audit writes fail with SQLSTATE `2201B`. The invocation replay conflict target
also collides with the function's `invocation_id` output variable.

Forward migration `0078_mcp_audit_append_bounds.sql` replaces only the append
function. It checks the existing 1–512 UTF-8 byte bound, surrounding spaces, and
control characters directly, and names the existing primary-key constraint for
replay conflict handling. It retains every immutable replay-field comparison,
the `sqlobserver_migrator` owner, `SECURITY DEFINER`, fixed search path and UTC
setting. Only the Server role receives EXECUTE; existing audit rows, indexes,
table privileges, and append-only trigger remain unchanged.

Apply the verified migration through the normal migration runner before using
MCP. No Server or Collector API changes are required. The migration is
transactional and contains no data rewrite. If deployment fails, the runner rolls
back that migration; correct it with a new forward migration rather than editing
0078 or restoring the broken append function. MCP continues to fail closed when
required audit persistence fails.

The integration regression cases cover ASCII and multibyte actor limits, invalid
actors with zero inserted rows, same-record replay, divergent replay, owner and
privilege boundaries, and upgrade from migration 0077 with preservation of an
existing row and its receipt. The old role-denial test mistakenly expected
`has_function_privilege` to throw; it now executes the actual append as Collector
and Auditor and requires PostgreSQL permission denial.

Before the migration, the focused suite recorded nine failures and three passes
in `artifacts/revamp-mcp-audit/mcp-audit-before.trx`. The pre-existing baseline
append failure is also captured in `artifacts/revamp-postgres-baseline.trx`.

After migration 0078, the 13 live MCP audit cases and three migration-assessment
checks passed with no skips (`artifacts/revamp-mcp-audit/mcp-audit-fixed.trx`). The
reports certification producer's `-ContractOnly` check also passed against the
updated migration-manifest and contract pins. These checks validate the repair;
they do not constitute release certification.

The complete functional PostgreSQL selection then passed 178 tests with no
failures or skips in 4 minutes 50 seconds. Evidence is saved in
`artifacts/revamp-mcp-audit/postgresql-functional-fixed.trx` and
`artifacts/revamp-postgresql-functional-fixed.log`. The command was:

```powershell
dotnet test tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj --configuration Release --no-build --no-restore --filter 'Category=RequiresPostgreSql&Category!=RequiresM12ReportsRelease&Category!=RequiresM12ObservabilityRelease' --logger 'trx;LogFileName=postgresql-functional-fixed.trx' --results-directory artifacts/revamp-mcp-audit
```
