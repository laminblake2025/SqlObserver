# Offline read-performance test patch

Migration 0380 improves recent completed-collection lookup for health, sessions,
requests, blocking and waits. It preserves historical fallback, exact snapshot
reads and cursor behavior. The migration is immutable and does not delete
telemetry or change the protected partitioned-read rollout setting.

`tools/build-offline-read-patch.ps1` builds an unsigned, database-specific patch
from the reviewed base-package manifest and the exact 379-row installed migration
history. Supply `-RepositoryDatabase` when the installed repository differs from
the default test database. The generated package verifies that exact database
name; a package for another repository must be rebuilt rather than edited.

The packaged `Invoke-SqlObserverReadPatch.ps1` defaults to compatibility checks.
Its explicit `-Apply` mode verifies package hashes and the full migration ledger,
preserves TLS certificate and hostname validation, stops the test services,
creates a private database backup, and applies the migration and ledger entry in
one transaction. It restores the services' original running/stopped states.
An already-recorded, matching migration 0380 is verified without repeating DDL.
Application binaries, identities, credentials and target configuration are not
replaced by this database patch.

Follow the README generated inside the package for installation and recovery.
A readable backup archive is checked automatically; a full restore rehearsal is
separate. After a committed patch, do not rewind the migration ledger or run an
older migration host that recognizes only migrations through 0379. Recovery
requires validating a restore into a separate database before a reviewed cutover.

## Deployment checkpoint

On November 14, 2026, the maintainer confirmed successful patch installation and
that the application was running correctly in the test environment. This is
operator confirmation; an installed build identifier and migration-ledger receipt
were not supplied with it. It does not establish fresh-install, rollback, restore,
sustained-load or MCP qualification, and does not change M12 certification status.

The next checkpoint is to record the deployed package identity and migration
ledger, run a sustained monitoring pilot, and verify MCP end to end separately.
