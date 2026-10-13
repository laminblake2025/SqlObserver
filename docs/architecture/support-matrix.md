# Support matrix

## Status labels

- **Initial-release scope**: required for the first supported release, but not yet certified by this scaffold.
- **Planned**: intended future coverage; no current support claim.
- **Development only**: tooling used to build or test, not a monitored production platform.
- **Implemented, qualification pending**: version-specific application code is available; native and release certification remain outstanding.

No platform is production-supported before M12 certification. A platform becomes supported only after its compatibility, security, installation, upgrade, performance, and recovery gates are complete and release documentation says so. M2/M3 integration tests are development evidence, not a support claim.

## Initial-release scope

| Area | Initial-release entry | Current status | Notes |
| --- | --- | --- | --- |
| Application host | Windows Server 2022 and 2025 | Initial-release scope | Separately deployable Windows-hosted .NET processes |
| Application repository | PostgreSQL 18.x | Initial-release scope | Stores SqlObserver data; it is not a monitored engine |
| Monitored database | SQL Server 2019 on Windows | Initial-release scope | Uses version-aware least-privilege grants |
| Monitored database | SQL Server 2022 on Windows | Initial-release scope | Prefer supported performance-reader roles and performance-state permissions |
| Monitored database | SQL Server 2025 on Windows | Initial-release scope | Prefer supported performance-reader roles and performance-state permissions |
| Browser | Microsoft Edge | Initial-release scope | Current stable release at validation time |
| Browser | Google Chrome | Initial-release scope | Current stable release at validation time |
| Web authentication | Windows Integrated Authentication | Initial-release scope | Application RBAC is additionally required |
| Backend/services | .NET 10 LTS | Initial-release scope | Nullable, analyzers, and warnings-as-errors |
| Web client | React and strict TypeScript | Initial-release scope | Scaffold dependencies are exactly pinned and locked; production support is not yet certified |
| Application build host | Windows development workstation | Development only | Must provide the pinned toolchains; not a production-host support claim |

## Planned platform scope

| Area | Planned entry | Qualification note |
| --- | --- | --- |
| Monitored database | SQL Server 2016 and 2017 | Requires permission fallbacks, DMV/query variants, and a full compatibility suite |
| Monitored database platform | SQL Server on Linux | Requires authentication, host-metric, packaging, and operational redesign/validation |
| Managed database | Azure SQL Database | Requires service-tier capability and identity matrices |
| Managed database | Azure SQL Managed Instance | Requires a distinct capability and permission matrix |
| Managed database | Amazon RDS for SQL Server | Requires managed-service permission and feature constraints |
| Virtualization | Hyper-V adapter | Host/guest correlation must respect separate credentials and boundaries |
| Virtualization | VMware adapter | Host/guest correlation must respect separate credentials and boundaries |

Planned entries must not silently use the nearest initial-release implementation. Capability discovery fails closed, reports unsupported or degraded status, and chooses only an explicitly tested fallback.

## SQL Server 2014 and 2019 implementation

SQL Server 2019 (major 15) retains its existing collector variants and `VIEW SERVER STATE` permission plan. Its full initial-release scope remains subject to native certification.

SQL Server 2014 (major 12) on Windows is **implemented, qualification pending**, with a reduced, passive capability set:

| Surface | SQL Server 2014 behavior |
| --- | --- |
| Engine health | Batch/compilation counters, memory, PLE, connections, grants, runnable tasks, scheduler count, and startup epoch |
| Database inventory / files | Database state and log reuse wait, logical file metadata and cumulative file I/O |
| Activity | All captured sessions and requests, waits, blocking, exact session lifetimes, and protected captured SQL |
| Scheduler CPU rate | Unavailable; the missing scheduler counter is omitted, never reported as zero |
| Query Store / query-performance history | Unavailable; no newer Query Store objects or permissions are used |
| Deadlock, host, operational-health, replication collectors | Unavailable in this compatibility bundle |

The additive bundle is checksum-pinned independently of the historical 2019/2022/2025 assets. Runtime core/activity catalogs compose both digests. Version selection is explicit: 2016/2017 remain unsupported, even though they lie between the stored range bounds. Public APIs, telemetry output contracts, and persisted schemas are unchanged.

The 2014 variants avoid [scheduler CPU counters introduced in SQL Server 2016](https://learn.microsoft.com/en-us/sql/relational-databases/system-dynamic-management-objects/sys-dm-os-schedulers-transact-sql) and [DATEDIFF_BIG](https://learn.microsoft.com/en-us/sql/t-sql/functions/datediff-big-transact-sql). [Query Store starts with SQL Server 2016](https://learn.microsoft.com/en-us/sql/relational-databases/performance/monitoring-performance-by-using-the-query-store), so it remains unavailable on 2014.

Generate a reviewed permission script with `tools/generate-permissions.ps1 -SqlServerMajorVersion 12 -Principal 'DOMAIN\collector'` or the server administration permission page. Base grants are `VIEW SERVER STATE` and `VIEW ANY DATABASE`; optional logical-file visibility adds `VIEW ANY DEFINITION`. The 2014 plan omits unused msdb history grants and rejects Query Store and replication options. SQL Server 2014's Windows identity is established only for boxed engine editions 2/3/4; newer versions continue to use their platform evidence. TLS certificate validation, integrated authentication, non-sysadmin collection, query-text RBAC, and capture bounds remain required.

Native acceptance requires actual 2014 and 2019 instances with the intended Windows collector identity and trusted TLS, followed by the collector → PostgreSQL → API → dashboard journey. Local unit, source-contract, browser, and permission-parity tests do not substitute for that qualification. See [version compatibility checks](../deployment/sqlserver-version-compatibility.md).

## Compatibility dimensions

Promotion to supported requires evidence across:

- clean install, upgrade, rollback/recovery, uninstall, and service identity configuration;
- Windows, .NET, PostgreSQL, SQL Server edition/build, browser, collation, locale, and time-zone combinations relevant to the supported entry;
- least-privilege success and expected-denial tests;
- TLS, Windows authentication, gMSA/Kerberos, and application RBAC;
- every enabled collector's capability, documented interface, bounded failure behavior, and fallback;
- repository migration, partition, ingestion, retention, backup, and restore behavior;
- API/MCP contract, security, payload-limit, and cancellation behavior;
- sustained-load, long-duration, failure-recovery, and clock-skew testing;
- accessibility and browser compatibility for supported user workflows.

All storage and protocol timestamps remain UTC regardless of host or browser time zone.

## Explicit exclusions before release certification

M10 analytics and M11 read-only MCP are locally implemented with target-scoped
application-service reads, repository-time pagination, and terminal audit. They
are not live Kerberos/SPN delegation, trusted production TLS, Docker,
sustained-load, upgrade, installer, or release certification.

M3 can discover a bounded capability profile with Windows integrated authentication and validated TLS, but it does not certify any entry above, install a gMSA-backed Windows service, provision trusted certificates/SPNs, or create a production database. M11 exposes MCP locally, but live identity delegation and transport qualification remain M12. Passing milestone validation is not support certification.
## M9 operational health

Backups, SQL Agent history, TempDB, and Availability Groups use passive read-only SQL with fixed execution bounds. SQL Server Agent is unsupported on Express; Availability Groups require HADR and non-Express editions. Live SQL Server and PostgreSQL certification is environment-dependent.
