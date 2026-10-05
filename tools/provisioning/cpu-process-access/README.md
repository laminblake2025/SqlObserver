# CPU process access provisioner — reviewable lab proposal

This is a separate administrative deployment tool, **not part of the Collector**.
It is not installed or production-qualified by adding these files. An explicit
installation approval is required before `Install -Apply`. The existing temporary
qualification lease does not authorize this persistent policy.

The policy grants exactly `PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`
(`0x101000`) to one approved, separate local/domain account or gMSA SID on each
verified process generation of one approved SQL service. It changes no SQL grants,
account memberships, user rights, SQL service ACL, SQL startup settings, or
Collector identity. It does not start, stop or restart SQL or the Collector.

## Deployment model and trust

An administrator installs a SYSTEM scheduled task named
`\SqlObserverCpuProcessAccess`. It runs at startup and every minute, using a fixed
entry script and no caller-supplied arguments. Task Scheduler limits execution to
one minute and ignores overlapping instances. A protected exclusive lock also
serializes manual operations. Scheduling is eventual: CPU remains unavailable
between SQL replacement and successful reconciliation; no uninterrupted-coverage
claim is made. A hung direct/manual invocation is not covered by Task Scheduler's
deadline, so use the task for unattended execution.

`%ProgramData%\SqlObserverCpuProcessAccess` contains fixed scripts/source, policy,
hash manifest, write-ahead ownership record, coordination lock, and latest receipt.
Only Administrators and SYSTEM receive access. The task DACL has the same two
principals. Reparse paths and unexpected owners/ACLs are refused. Install refuses
existing directories/tasks instead of adopting them. Only the latest receipt is
retained; capture receipts separately for a qualification campaign.

SYSTEM is still a privileged trust boundary. The narrow API surface does not
make a compromised SYSTEM process low privilege. Each helper invocation removes
`SeDebugPrivilege` from its own process token before opening SQL; it never enables
debug privilege or takes process ownership. Its process handle requests
`READ_CONTROL | WRITE_DAC | 0x101000` for writes and omits `WRITE_DAC` for inspection.
SQL token identity is read using `TOKEN_QUERY`; no token modification is attempted.

All invocations must use a **fresh 64-bit Windows PowerShell 5.1 process**. Do not
dot-source or invoke the lifecycle script inside a long-lived administrative
PowerShell session: debug-privilege removal affects that host process token and
cannot be undone there. A scheduled entry script handles `-Confirm:$false` inside
PowerShell, avoiding the Windows PowerShell 5.1 `-File` switch-value limitation.

The policy pins SQL service name, full local executable path and SHA-256, SQL
service-account SID, Collector service name and monitoring account SID. It checks
the configured SQL and Collector accounts, SCM's running own-process PID, held
process creation time, actual image path and actual SQL process TokenUser SID.
The executable is opened without write/delete sharing while hashing and applying.
The process identity is rechecked before and after the guarded DACL write. This
is an executable-file pin, not attestation of SQL's entire loaded code or DLLs.

The initial slice deliberately uses the dedicated **account SID**, matching current
native qualification. This grants the rights to other processes using that account
as well. Service-SID-only grants require qualification under the actual Collector
and child-process tokens; the temporary native-runner service is not equivalent.

## Lifecycle

Review and stage all three runtime files from one immutable snapshot in an
administrator-controlled directory: `CpuProcessAccess.ps1`,
`Invoke-ScheduledReconcile.ps1`, and `ProcessAccessProvisioner.cs`. Review their
hashes before starting an elevated fresh process. Do not execute mutable or
untrusted installation source as administrator.

Supply independently reviewed values; the examples deliberately have placeholders.
The image hash and SIDs are not guessed or derived from test output to bypass a
failed identity check.

```powershell
# Preview only: does not create files/tasks or change the process DACL.
powershell.exe -NoLogo -NoProfile -File .\CpuProcessAccess.ps1 -Action Install `
  -SqlService MSSQLSERVER -SqlImagePath '<full reviewed sqlservr.exe path>' `
  -SqlImageSha256 '<lowercase SHA-256>' -SqlAccountSid '<SQL account SID>' `
  -CollectorService SqlObserverCollector -MonitorSid '<monitor account SID>'

# Only after approval: repeat the exact reviewed Install invocation with -Apply.
# SupportsShouldProcess also supports -WhatIf and confirmation.

# After installation, run only the protected installed script.
powershell.exe -NoLogo -NoProfile -File "$env:ProgramData\SqlObserverCpuProcessAccess\CpuProcessAccess.ps1" -Action Inspect
powershell.exe -NoLogo -NoProfile -File "$env:ProgramData\SqlObserverCpuProcessAccess\CpuProcessAccess.ps1" -Action Reconcile
powershell.exe -NoLogo -NoProfile -File "$env:ProgramData\SqlObserverCpuProcessAccess\CpuProcessAccess.ps1" -Action Remove
```

All lifecycle actions default to no process DACL mutation. `Reconcile -Apply` and
`Remove -Apply` additionally require ShouldProcess approval. Install preview
validates policy syntax, configured service accounts and current executable hash;
it **does not prove live SQL token identity or process access**, which are checked
by reconciliation. Inspect/Reconcile preview may create the private coordination
lock, and remove debug privilege from their fresh host token. They do not grant
process rights or write ownership/receipt state.

Installation registers the task disabled, sets/verifies its DACL and fixed
configuration, then enables it. First reconciliation is scheduled within a minute;
installation itself is not proof that reconciliation succeeded. On partial install
failure, retain the directory and inspect the task; do not delete an unknown task
or use a force/adopt switch (none is provided).

## Ownership, failure and removal

Before writing the DACL, reconciliation atomically persists and reads back a
private record binding the policy hash, PID, creation time, original DACL, exact
derived proposal and phase. Existing monitor ACEs are never adopted. Unsupported,
null or noncanonical ACLs are refused. Original ACE bytes/order are preserved.

On the same process generation, exact postimage is idempotent success. An exact
preimage may retry only while the record is still write-ahead/pending. Once marked
applied, removal or any DACL drift refuses regrant. A crash after mutation but
before the applied marker is handled using the exact postimage. A pending record
cannot distinguish a crash-before-write from an administrator recreating that
exact preimage; disable the task before administrative ACL maintenance.

A replacement SQL process receives a new record only when the previous generation
is proven exited (including PID reuse detected through creation time). Live or
unverifiable old processes refuse replacement. No record is applied by PID alone.

`Remove -Apply` first disables/stops the owned task, waits up to ten seconds for it
to stop, then removes only one exact, uniquely matching owned monitor ACE from the
**current** DACL. It preserves unrelated current ACEs and flags. Duplicate, changed
or unverifiable monitor ACEs refuse removal. If the original process has exited,
its ACE has no surviving process object; a replacement is not altered using the
old record. It then unregisters the task and retains private files and receipts.
There is no recursive-delete or evidence-purge operation.

Detected DACL drift before a write is refused, but Windows offers no atomic DACL
compare-and-swap. Another administrator can race the final check/write or remove
and recreate an identical ACE; byte identity and the ownership log do not prove
exclusive historical authorship. Coordinate ACL administration and disable this
task first. Removing the ACE does not revoke previously opened handles.

Image/account/hash drift also refuses removal. **Remove before SQL upgrade/account
changes**, then review a new policy separately. On refusal, the task remains
disabled and evidence remains; an administrator must resolve the exact current
identity/ACL state. There is no guessed forced restore or broad fallback. Files
are retained intentionally, so reinstallation requires separately reviewed archival
of the retired installation; this slice has no automatic policy-upgrade path.

## Verification and remaining native gates

```powershell
powershell.exe -NoLogo -NoProfile -NonInteractive -File .\Test-Provisioner.ps1
```

The pure managed suite compiles both C# files in Windows PowerShell 5.1, parses the
lifecycle script, and exercises exact ACE insertion/removal, preservation during
unrelated drift, ambiguity/refusal, phase/crash recovery, and binary-record policy
binding. It makes no native process calls, installs no tasks, and writes no process
DACL. Temporary ordinary test files are removed afterward.

Still required before durable provisioning is claimed: approved isolated install;
task/action/ACL readback; actual SYSTEM reconciliation without debug privilege;
actual Collector access and unchanged low-privilege identity; SQL restart and helper
restart recovery; visible denial during the gap; drift refusals; removal preserving
unrelated ACEs; task deadline/crash recovery and cleanup. A controlled SQL restart
needs separate maintenance authorization. Collection opt-in and product admission
remain independent decisions.

Microsoft references: [process rights and default security](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights),
[service-object rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights),
[SetSecurityInfo](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-setsecurityinfo),
[SQL service identity isolation](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/configure-windows-service-accounts-and-permissions?view=sql-server-ver17).
