# SQL Observer lab test package

This ZIP is a versioned, unsigned Windows x64 test build. It contains the API, Collector, dashboard, and lab migration utility from the commit named in `package-manifest.json`. It is not an MSI, does not install services, and does not establish production or native SQL Server acceptance. PostgreSQL migrations are embedded in the migration utility; the shipped checksum catalog is provided for inspection.

## Prerequisites and verification

Use an isolated Windows Server 2022/2025 x64 test host with the .NET 10 ASP.NET Core runtime, PowerShell 7.5+, and PostgreSQL 18. A development SDK or Node.js is not required on the destination. SQL Server targets require Windows authentication, a non-sysadmin monitoring identity, the version-specific grants, and trusted TLS. The minimum engine is SQL Server 2016; SQL Server 2014 is excluded. SQL Server 2016/2017 targets run on a separate compatible Windows host and provide the bounded core/activity coverage described in [version compatibility](sqlserver-version-compatibility.md).

Before extraction, compare the ZIP SHA-256 against the independently retained build receipt. Run the verifier from the trusted source checkout:

```powershell
./tools/test-lab-package.ps1 -ArchivePath 'C:\Transfers\SqlObserver-lab-win-x64-COMMIT.zip' -ExpectedArchiveSha256 'EXPECTED_64_HEX_DIGEST'
```

The verifier checks bounded ZIP members, traversal, duplicate paths, prohibited secret files, the complete payload inventory, and checksums without extracting or executing the payload. Extract to a fresh versioned directory, such as `C:\SqlObserverLab\Candidates\COMMIT`. Then recheck the extracted directory with `Tools\test-lab-package.ps1 -PackageDirectory 'C:\SqlObserverLab\Candidates\COMMIT'`. Checksums identify the build contents; they are not a signing or publisher-identity guarantee.

## Prepare a separate repository

Use **`sqlobserver_lab`** for this first deployment. Preserve the current `sqlobserver` repository, service configuration, keys, and port 5443. Do not rewrite an existing migration ledger or run the migration utility against a repository with drift.

Follow [the single-host lab guide](lab-single-host.md) to provision the bootstrap account, separate runtime accounts/roles, fixed NOLOGIN expiry owners, and the run dependency reader. In the repository-creation command, substitute `sqlobserver_lab` for `sqlobserver`; leave the approved role and permission boundaries intact. Create the database only if it does not already exist; inspect any existing database before proceeding. PostgreSQL is local at `127.0.0.1:5432` for this bounded migration utility.

Run the following under the authorized bootstrap operator after the guide's prerequisites are complete:

```powershell
& 'C:\SqlObserverLab\Candidates\COMMIT\MigrationHost\SqlObserver.Cli.exe' postgres migrate --lab-loopback --allow-loopback-cleartext --database sqlobserver_lab --username sqlobserver_bootstrap
```

The password is entered interactively with hidden input. It is not accepted as an argument or environment variable. Inspect the sanitized JSON result, resolve any failure, and repeat successful batches until every migration in `Docs\repository-migrations.sha256` is applied and checksum-verified. The loopback cleartext exception belongs only to this explicit lab migration command. API/Collector connection configuration must retain the runtime transport policy.

## Configure and launch

1. Copy `ConfigTemplates\server.example.json` and `collector.example.json` to separate, ACL-restricted directories outside the package, naming each copy `appsettings.json`. Replace every `__SET_...__` placeholder. Point both connections at `sqlobserver_lab`, using distinct least-privilege PostgreSQL accounts.
2. Set the same 32-byte identity fingerprint key (64 hexadecimal characters) for Server and Collector. Keep credentials and keys outside the package and source repository. Grant each process access only to its own configuration and required key material.
3. Set Server `SqlObserver:WebRootPath` to the absolute `Web` directory of this package. Keep that directory read-only to the Server identity. Configure the trusted certificate and grant its private-key access to the Server identity. Use the candidate API port **5444** so the existing deployment remains available at 5443.
4. Configure a dedicated Windows lab administrator group. For initial target registration only, explicitly choose the necessary global Target Administrator binding (`AllTargets: true`, empty `TargetIds`); after registration, narrow the binding to the returned test target IDs. The supplied template uses scoped placeholders and is intentionally unusable until configured. Authentication remains Negotiate; there is no anonymous or caller-header authentication mode in this package.
5. Run the two console hosts in separate operator sessions under their intended Windows identities, using their external content roots. The Collector identity must have the SQL monitoring grants and must not be SQL Server sysadmin. If integrating with existing service management later, retain the documented identity/ACL boundaries; this ZIP does not create or reconfigure Windows services.

```powershell
& 'C:\SqlObserverLab\Candidates\COMMIT\Server\SqlObserver.Server.exe' --contentRoot 'C:\SqlObserverLab\Config-Lab\Server'
& 'C:\SqlObserverLab\Candidates\COMMIT\Collector\SqlObserver.Collector.exe' --contentRoot 'C:\SqlObserverLab\Config-Lab\Collector'
```

Use a browser with authorized Windows credentials at `https://CERTIFICATE-DNS-NAME:5444/`. `/health` confirms that the API responds; `/ready` verifies repository compatibility. Neither endpoint establishes SQL target health. Register the intended SQL Server only after these gates pass, then verify fresh discovery and collection evidence.

## Sensitive SQL and acceptance

Sensitive content remains unavailable while the protection paths are empty. To test captured SQL, provision the machine-protected key on the destination host using `Tools\provision-live-activity-key.ps1`, naming exactly the Server and Collector identities, and configure the same path in both `SqlObserver:LiveActivity:ProtectedKeyPath` settings. Never overwrite a protection key used by retained evidence. Grant the application `QueryTextReader` role explicitly for the test target and verify both allowed and denied reads. Query-performance text/plan protection has its separate `SqlObserver:QueryContent:ProtectedKeyPath` setting and requires its own reviewed key setup.

Validate the complete collector → PostgreSQL → API → dashboard path: freshness, database/file evidence, exact session captures, blocking relationships, deep links, capture saves, permission revocation, and visible missing/unsupported evidence. Use only bounded, authorized lab workloads with rollback. Test each native engine being claimed, including SQL Server 2016, 2017, and 2019; a database compatibility level does not replace native validation. Keep the sanitized qualification receipt and its package/source hashes beside the build receipt; see [version compatibility](sqlserver-version-compatibility.md). No MCP stdio bridge is included.

## Rollback and evidence

For this side-by-side test, stop only the new console processes, return to the old port 5443 URL, and retain `sqlobserver_lab` for investigation. Do not drop or overwrite the old database, configuration, binaries, or keys. Record the package/commit hashes, host identity, sanitized readiness results, migration results, and target/version evidence. These are lab deployment receipts, not production certification.

## Rebuild from source

From a clean committed checkout on Windows x64 with the pinned SDK, Node, and pnpm toolchains, run `./tools/build-lab-package.ps1`. It publishes Release binaries, builds and verifies the dashboard, checks apphost architecture, runs only migration `--help`, verifies the ZIP, and writes a build receipt under ignored `artifacts/lab-packages/`. It never installs services or connects to a database.
