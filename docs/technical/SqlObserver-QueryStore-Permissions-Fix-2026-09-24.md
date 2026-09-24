# Query Store permission generator — 2026-09-24

The offline permission generator now accepts `-QueryStoreDatabase` to add access
for one explicitly selected user database. This addresses the missing database
user that can otherwise produce SQL Server error 916 and cause Query Store
collection to fall back to the plan cache. Error 916 classification was corrected
in the earlier collector checkpoint.

The generated section checks that the database exists and is online, creates a
user for the existing Windows login only when absent, and verifies the existing
or new user's SID, type, and authentication mapping before granting access. It
grants `CONNECT` and the version-appropriate database state permission documented
by [Microsoft](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-query-store-runtime-stats-transact-sql).
No database role, login, Query Store setting, or target execution is added.

The option accepts one trimmed user-database name of at most 128 characters.
System database names and control characters are rejected. Identifier and Unicode
literal escaping preserve brackets, apostrophes, Unicode, and punctuation as
data. Both file and redirected stdout output preserve UTF-8 names; console
encoding is restored after writing. `Remove` with this option is rejected because
the generator has no provenance for existing users or database grants.

The focused offline generator tests produced 11 failures and five passing
controls before the new option. After implementation, 15 passed and the Unicode
case exposed lossy inherited stdout encoding. UTF-8 output fixed that; the test
reader also needed an explicit UTF-8 decoder instead of its inherited OEM page.
The remaining Unicode case then passed. Evidence is under
`artifacts/revamp-query-store-permissions/test-results/`.

Only the affected permission-generator tests ran. No generated SQL was executed,
no live permissions were changed, and no native SQL Server qualification is
claimed. See the [usage and removal guidance](../milestones/M3-onboarding-and-capabilities.md#permissions).
