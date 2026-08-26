# Support matrix

## Status labels

- **Initial-release scope**: required for the first supported release, but not yet certified by this scaffold.
- **Planned**: intended future coverage; no current support claim.
- **Development only**: tooling used to build or test, not a monitored production platform.

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
| Monitored database | SQL Server 2014, best effort | Reduced capability set must be explicit; no parity promise |
| Monitored database platform | SQL Server on Linux | Requires authentication, host-metric, packaging, and operational redesign/validation |
| Managed database | Azure SQL Database | Requires service-tier capability and identity matrices |
| Managed database | Azure SQL Managed Instance | Requires a distinct capability and permission matrix |
| Managed database | Amazon RDS for SQL Server | Requires managed-service permission and feature constraints |
| Virtualization | Hyper-V adapter | Host/guest correlation must respect separate credentials and boundaries |
| Virtualization | VMware adapter | Host/guest correlation must respect separate credentials and boundaries |

Planned entries must not silently use the nearest initial-release implementation. Capability discovery fails closed, reports unsupported or degraded status, and chooses only an explicitly tested fallback.

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
