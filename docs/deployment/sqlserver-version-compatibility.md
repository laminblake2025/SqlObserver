# SQL Server 2014 / 2019 compatibility checks

The current code recognizes SQL Server 2014 (12) and retains SQL Server 2019 (15). SQL Server 2014 has the reduced capability set listed in the [support matrix](../architecture/support-matrix.md). Neither version is newly native-certified by these source changes.

Deploy the rebuilt Collector and API from the same source revision. Existing database migrations and API shapes do not change. Older lab deployments do not gain compatibility merely because the local dashboard assets were rebuilt.

Provision a pre-existing Windows monitoring login using the offline permission generator, with major `12` or `15` as appropriate. Review and execute the generated SQL independently as the authorized DBA. Select logical-file metadata explicitly if file collection is needed. For 2014, Query Store and replication options are rejected and msdb history grants are omitted.

The native smoke test requires the logical-file grant as well as base monitoring. Use a SQL Server 2014 build with TLS 1.2 support (included in SP2/SP3), a trusted server certificate, and a compatible Windows TLS configuration. The product retains mandatory encryption and certificate validation. See [Microsoft's TLS compatibility guidance](https://learn.microsoft.com/en-us/troubleshoot/sql/database-engine/connect/tls-1-2-support-microsoft-sql-server).

Use the server's existing settings surface to register its DNS name and instance/port, validated certificate name, and collector identity, then request capability rediscovery. Require verified Windows authentication and TLS, a non-sysadmin collector, and the required version-specific permission evidence. SQL Server 2014 must appear as Windows, major 12; SQL Server 2019 as major 15. Authentication/transport failures or permission denials remain diagnostic failures rather than a version fallback.

For a bounded, passive native source smoke test under the intended monitoring identity:

```powershell
./tools/test-sqlserver-version-compatibility.ps1 -HostName sql2014.lab.example -Port 1433 -ExpectedMajor 12
./tools/test-sqlserver-version-compatibility.ps1 -HostName sql2019.lab.example -Port 1433 -ExpectedMajor 15
```

The test reads the compiled, checksum-verified assets through the product's strict integrated connection factory. It checks actual major version, required permissions, seven core/activity sources, and protected live-session metadata without loading SQL text. It does not create workloads, change permissions/configuration, or permit trusted-certificate bypass. An absent server, wrong version, denied permission, or invalid certificate fails. Its receipt is a native source smoke result, not release or full-stack acceptance.

Then verify repository ingestion and dashboard reads from that exact target: fresh health samples, inventory/file evidence, snapshots with session counts and lifetimes, blocking relationships, and deliberate Query Text Reader access. Capture bounded blocking activity only in an authorized isolated lab, with rollback. Confirm unknown scheduler CPU and unsupported collectors remain explicit on 2014. Repeat permission denial/revocation and least-privilege checks. Save receipts against the final build inputs before claiming native acceptance.
