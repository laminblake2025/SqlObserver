# Installer assets

WiX packaging and PostgreSQL installation or integration assets remain reserved
for a later M12 package. The current foundation contains only closed assessment
contracts under `contracts/`; it does not create an MSI/Burn package or install,
register, start, stop, or uninstall services. It does not provision PostgreSQL,
apply migrations, write configuration/ACLs, manage credentials/certificates, or
purge data.

The application can perform a bounded, read-only migration-history assessment
using the exact embedded migration catalog. Assessment output is safe for logs
and JSON (bounded IDs, hashes, statuses, and UTC timestamps only) and always
reports `readyToMutate: false` until an accepted pinned owner policy and the
remaining lifecycle decisions/evidence exist. Local foundation checks are not
installer, runtime, support, or release evidence; ADR-0016 remains Proposed.
