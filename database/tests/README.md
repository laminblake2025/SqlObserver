# PostgreSQL M10 tests

`m10_migration_static.ps1` validates migration invariants and the append-only
checksum entry without requiring a server. `m10_postgres_docker.ps1` probes
Docker and explicitly reports `UNAVAILABLE` when Docker is not installed or
its daemon cannot be reached; environments with Docker should run the normal
repository migration runner followed by `m10_postgres_assertions.sql` and
exercise the listed fresh/upgrade, RLS, replay, pruning, backfill, and
retention assertions.
