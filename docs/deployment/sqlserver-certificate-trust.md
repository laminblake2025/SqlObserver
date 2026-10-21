# SQL Server certificate trust exceptions

SQL Observer requires encrypted SQL connections and validates certificates by
default. An administrator can explicitly enable `TrustServerCertificate=true`
for one registered TCP target using its existing certificate. This keeps traffic
encrypted but skips certificate chain and hostname verification for that target.
Windows integrated authentication and the Collector service identity are unchanged.

After registering the target, obtain its `instanceId`, `host`, and `tcpPort` from
`/api/v1/observation-targets/{instanceId}`. Under the existing `SqlObserver` object
in **both** `Server/appsettings.json` and `Collector/appsettings.json` in the
installation's ProgramData directory, add:

```json
"SqlServerCertificateTrustOverrides": [
  {
    "TargetId": "6140bc24-d03d-4b67-b42e-a2863d8a29c5",
    "Host": "sdlc-qa-db03.pentechho.com",
    "TcpPort": 1433
  }
]
```

The example identifies the existing SQL-QA-DB03 test target. Other repositories
must use their own target GUID and exact configured host/port. Preserve all other
configuration, existing trust entries, and file ACLs. The service identities must
have read access only. Named-instance discovery is excluded from this exception;
configure a fixed TCP port. Wildcards and global trust switches are not supported.

Restart `SqlObserverTestServer` and `SqlObserverTestCollector`, then use **Request
rediscovery** in the target settings. The target response now reports
`encryptionMode: mandatory_trust_server_certificate`; target settings display the
exception. Discovery records `transport.tls-trusted-by-operator` instead of
claiming that the certificate was validated. Collection is permitted only while
the exact target/host/port exception remains configured. Endpoint changes revert
to certificate validation automatically. Removing the exception from both files
and restarting both services restores validation; request rediscovery afterward.

Dashboard HTTPS and PostgreSQL VerifyFull TLS continue using their existing trust
configuration. No SQL Server certificate, restart, login, or permission change is
required to enable this client-side option. A successful discovery can still
report separate SQL authentication or capability failures.

## Existing test installation patch

The separately built `SqlObserver-TestPatch-*.zip` updates the known identity-fix
test installation. Extract it to a local directory and run its
`Apply-SqlObserverTestPatch.ps1` from an elevated PowerShell 7.5+ console. Its
defaults select `C:\Program Files\SqlObserver Test` and
`C:\ProgramData\SqlObserverTestIdentityFix`, and enable the example exception in
both configuration files. `-ValidateOnly` performs preflight without changing files
or services. No setup wizard or repository migration is run.

The patch verifies the expected base application/helper and incoming payload,
retains protected backups, stops the two services, replaces application/helper
files, updates only the trust configuration, and restores their previous running
states. It attempts rollback if application fails and reports rollback errors.
Repository data, credentials, keys, certificates, service accounts, and service
registration are retained. Keep the protected backup directory until the patched
deployment is verified. Do not run the full fresh-install wizard against the
existing repository.
