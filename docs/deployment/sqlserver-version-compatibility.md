# SQL Server 2016 and newer compatibility checks

The active minimum is SQL Server 2016 (major 13). Explicit engine selections are 2016 (13), 2017 (14), 2019 (15), 2022 (16), and 2025 (17). SQL Server 2014 assets and migration 0373 remain historical inputs; production discovery rejects major 12. Database compatibility level does not change the native engine version.

Apply forward migration `0374_sqlserver_2016_2017_collector_contracts.sql`, then deploy Collector and API from the same source revision. It binds eight exact core/activity contracts and inventory activation digests, preserves schedules and evidence schemas, and rejects prior digest drift atomically. Historical migrations and release receipts remain immutable.

The additive 2016/2017 bundle provides engine counters (including scheduler CPU), database inventory/files, sessions/requests, server waits, blocking, and protected live snapshots. Advanced collectors retain their separate version qualification limits; Query Store, deadlock, host, operational-health, and replication coverage is not established by this bundle. Missing or unsupported evidence remains explicit. See the [support matrix](../architecture/support-matrix.md).

Use actual Windows engines with trusted TLS certificates and a non-sysadmin monitoring identity. SQL 2016 and 2017 use `VIEW SERVER STATE`, as does 2019; 2022 and 2025 retain performance-state permissions. The offline permission generator rejects versions below 13 and unqualified advanced options on 13/14. Optional logical-file metadata requires `VIEW ANY DEFINITION`. Register each exact endpoint and certificate name through server settings, then rediscover its capabilities.

Run the bounded native source verification under the intended monitoring identity:

```powershell
./tools/test-sqlserver-version-compatibility.ps1 -HostName sqlcompat.lab.example -Port 51416 -ExpectedMajor 13
./tools/test-sqlserver-version-compatibility.ps1 -HostName sqlcompat.lab.example -Port 51417 -ExpectedMajor 14
./tools/test-sqlserver-version-compatibility.ps1 -HostName sqlcompat.lab.example -Port 51419 -ExpectedMajor 15
```

The runner verifies actual engine identity, permissions, seven bounded sources, and protected snapshot metadata without loading SQL text. Missing servers, wrong majors, denied permissions, and invalid certificates fail. Its receipt is a source smoke result; full-stack acceptance additionally requires the collector → PostgreSQL → API → dashboard path, coherent retained snapshots and exact session identities, bounded blocking with rollback, deliberate SQL-text access and revocation, and least-privilege checks against the final build inputs.

Windows Server 2019 supports all three older native engines; Windows Server 2025 does not support SQL Server 2016/2017. Provision them in a separate Windows Server 2019 lab and use SQL 2016 SP3 plus applicable servicing updates. [Microsoft's Windows/SQL support matrix](https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/install/windows/use-sql-server-in-windows).
