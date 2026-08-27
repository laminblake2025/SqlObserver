# Lifecycle assessment contracts

These closed JSON schemas describe the M12 foundation's side-effect-free
deployment and PostgreSQL migration assessments. They are safe to serialize:
they contain bounded identifiers, hashes, statuses, and UTC timestamps only;
they never contain paths, SQL, connection strings, credentials, certificates,
or raw exceptions.

The foundation does not ship an installer and does not mutate Windows services,
PostgreSQL, configuration, ACLs, or data. `readyToMutate` is always `false`.
An absent or unaccepted pinned owner policy, missing or duplicate checks, and
any identity/history mismatch fail closed. `checksums.sha256` pins the exact
LF-encoded schema bytes.
