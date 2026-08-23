# M3 onboarding, identity, capabilities, and permissions

## Scope

Milestone 3 adds the first complete monitored-target control-plane slice:

- authenticated and role-scoped observation-target registration, listing, update, retirement, and capability rediscovery;
- a structured SQL Server endpoint model that accepts a host and exactly one named instance or TCP port;
- Windows integrated authentication under the Collector service identity, with mandatory encryption and certificate validation;
- an immutable, bounded capability-collector manifest and fixed read-only SQL assets for SQL Server 2019, 2022, and 2025 on Windows;
- append-only PostgreSQL capability evidence and revision-fenced profile recording;
- an offline, version-aware least-privilege permission-plan generator; and
- expected-denial, timeout, payload-bound, authorization, repository, API, and target-adapter tests.

Registration is intentionally asynchronous. A newly accepted target remains `pending_discovery` until the Collector records a profile for the same target revision while holding the repository lease.

Registration requires a nonempty, client-owned `instanceId`. A client retains that identifier across retries, allowing the repository's existing identity-and-content checks to return the original target for an exact replay while rejecting target-ID or stable-key reuse with different registration content. Responses expose both lifecycle (`pending_discovery`, `active`, `disabled`, or `retired`) and capability state; disabled and retired targets never masquerade as pending discovery.

## Credential and connection boundary

SqlObserver does not accept or persist a SQL password, access token, reusable target secret, raw target connection string, or certificate-validation bypass. The Collector connects as its Windows service identity. Production connections select the `master` database, use a bounded connect timeout, require TLS, and validate the server certificate. A certificate host name may be supplied independently from the network address.

The initial deployment preference is a gMSA so key material and token issuance remain owned by Active Directory and LSASS rather than SqlObserver. Service installation and gMSA/Kerberos certification remain M12 release gates.

## Capability contract

`capability.connection` is passive, parameterless, cancellable, one-row bounded, and non-overlapping per target. It records only normalized evidence: product version, edition, Windows platform, authentication scheme, encryption state, sysadmin membership, allowlisted permission results, duration, byte count, collector/manifest version, and output schema version. Provider error messages and arbitrary target content are not persisted.

The outcome vocabulary is explicit:

- `supported` requires SQL Server 2019/2022/2025 on Windows, Kerberos, encrypted transport, required permission, and a non-sysadmin identity;
- `degraded` identifies a safe but reduced path, including an explicit NTLM authentication fallback;
- `unsupported` identifies an unqualified version, platform, or edition;
- connection, authentication, TLS, and timeout failures use dedicated safe outcomes; and
- unsafe authentication, unencrypted transport, or sysadmin membership is rejected by security policy.

The repository stores append-only attempts, profiles, allowlisted capability reasons, and permission evidence. A profile write is accepted only when both the target revision and the PostgreSQL repository-time worker lease fence still match.

## Permissions

`tools/generate-permissions.ps1` operates offline and emits a reviewable DBA-run SQL script. It never connects to a target. SQL Server 2019 uses `VIEW SERVER STATE`; SQL Server 2022 and 2025 use the supported `##MS_ServerPerformanceStateReader##` role. The generated script validates the exact major version, Windows platform, an existing Windows principal, and non-sysadmin status before applying or removing only the selected M3 permission.

No service code executes generated permission SQL, changes Query Store, creates or changes Extended Events sessions, changes blocked-process settings, or performs any other target mutation.

## Security and operational assumptions

- Windows group membership is mapped to bounded application roles and target scopes by SID. Authentication alone grants no application access.
- Administrative success, conflict, denial, and discovery recording are audited. Authorized repository mutations and their audit records are atomic.
- PostgreSQL repository time determines discovery due work and lease validity; caller clocks do not.
- Legacy M2 identity-only targets remain readable for migration compatibility but are excluded from M3 discovery until a structured endpoint is supplied.
- The API, Collector, and target adapter enforce independent row, byte, body, connection, command, and cancellation limits.
- Registration, update, retirement, and rediscovery share a per-authenticated-principal administrative limiter, partitioned by the authenticated primary SID, then the name-identifier claim, with one fail-closed fallback partition when neither stable identifier exists. Each principal may have at most two concurrent mutations and 30 mutation attempts per 60-second fixed window by default; requests are never queued. Admitted authorization denials still reach the application service and its durable denial audit, but the limiter bounds that work: later rejected attempts stop at admission with a bounded `429` problem response and correlation identifier, and are not represented as completed administrative writes. Deployment overrides are startup-validated and fail closed when outside their documented bounds.

## Qualification boundary

The active test suite supplies functional evidence for the M3 slice on the available development lab. It does not certify the initial support matrix. Windows Server 2022/2025 installation, gMSA/SPN/Kerberos behavior, trusted production TLS, and SQL Server 2019/2022/2025 edition/build matrices remain M12 release gates and must not be inferred from a passing development test.
