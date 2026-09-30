# Native availability-group v2 qualification

This opt-in test reads a stable, joined availability group from its primary and
secondary through the production discovery and collector paths. It never creates
databases, grants permissions, changes AG state or activates the repository rollout.
It is excluded from ordinary Local and Release validation sweeps.

As of 2026-09-30, the test builds with no warnings or errors. The 56 focused
checks for configuration, collector permission gating, migration preambles,
PostgreSQL upgrade/rollout behavior and dependent release pins pass. Native
primary/secondary qualification has not run: the available single SQL instance
does not provide an AG pair with trusted TLS. These checks are not native evidence.

Prepare two Windows SQL Server replicas with at least one joined database in one
shared AG. Use the same major version on both replicas: 15 (2019), 16 (2022) or
17 (2025). Run under a Windows monitoring identity with validated TLS certificates
and read permissions on both replicas. For 2019 the AG v2 permissions are
`VIEW SERVER STATE`, `VIEW ANY DEFINITION` and `VIEW ANY DATABASE`; for 2022/2025
they are `VIEW SERVER PERFORMANCE STATE` and `VIEW ANY DATABASE`. Discovery also
requires the existing capability collector's permission contract. The test rejects
sysadmin, CONTROL SERVER, ALTER ANY AVAILABILITY GROUP and ALTER ANY LOGIN.

Use direct `tcp:host,port` replica endpoints and `master`, with Windows Integrated
Security and explicit `Encrypt=true` or `Strict`. Certificate bypass, SQL credentials,
ReadOnly routing, failover options and MARS are rejected. A listener must not be used:
the local replica roles are checked by the actual collector output. No connection
strings or source AG, replica or database names are written to test output.

```powershell
$env:SQLOBSERVER_AG_PRIMARY_SQLSERVER = 'Server=tcp:PRIMARY_FQDN,1433;Initial Catalog=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false'
$env:SQLOBSERVER_AG_SECONDARY_SQLSERVER = 'Server=tcp:SECONDARY_FQDN,1433;Initial Catalog=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=false'
$env:SQLOBSERVER_AG_EXPECTED_MAJOR = '15'
dotnet test tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj --filter 'Category=RequiresAvailabilityGroup' --logger 'trx;LogFileName=ag-v2-native.trx'
```

Repeat separately for all three supported major versions and retain each TRX with
the lab's version, topology and monitoring principal grant inventory. Missing lab
variables or unsupported configuration fail an explicitly selected test.

The test checks paired replica and database identities, native primary visibility,
secondary local-only observations, unknown remote fields, bounded accounting and
unconverted SQL-local commit timestamps. A shared identity and fixed fingerprint
key are used only inside the test to compare observation locations. Production
fingerprints remain target-specific.

This is a normal joined-pair qualification, not fault-injection coverage. Suspended
data movement, disconnected replicas, resolving roles, failover, Basic AG restrictions,
permission revocation and source truncation still require a separate controlled lab
matrix. Synthetic tests exercise those output boundaries. Do not activate AG v2
solely because synthetic tests or this test's compilation pass.

The SQL Server 2019 metadata requirement follows Microsoft's
[sys.availability_groups permissions](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-availability-groups-transact-sql?view=sql-server-ver17).
Primary versus secondary visibility follows
[sys.dm_hadr_availability_replica_states](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-views/sys-dm-hadr-availability-replica-states-transact-sql?view=sql-server-ver17).
