# Isolated PostgreSQL wall-clock qualification

This image and fixture are test infrastructure for native retention/expiry
qualification. PostgreSQL sees actual advancing UTC plus a container-local,
whole-day offset. Tests still use genuine `clock_timestamp()`, canonical
Begin/Commit producers, repository policy and retention/recovery functions.
There are no timestamp edits or production clock-function replacements.

Build explicitly from the repository root, before running tests:

```powershell
docker build --platform linux/amd64 --tag sqlobserver-postgres-isolated-clock:18.4-v2 tools/qualification/isolated-clock-postgresql
$env:SQLOBSERVER_ISOLATED_CLOCK_POSTGRES_IMAGE = 'sqlobserver-postgres-isolated-clock:18.4-v2'
```

The qualification lead must preserve the Docker build log and resulting image
ID alongside the final consumed-source inventory. A digest reference to the
built image may also be supplied. The fixture uses `PullPolicy.Never`; it fails
when the setting/image is absent and never downloads or installs dependencies
during a test. The Local/Release repository connection settings are ignored.
Do not use this image for development, production or an external database.

The two build stages use the existing exact PostgreSQL 18.4 bookworm digest.
The runtime package is `libfaketime:amd64=0.9.10-2.1` from Debian's signed APT
indexes; package/version/architecture and successful signature verification
must remain in the build record. No unauthenticated package option is used.
The builder installs the bookworm GCC metapackage `4:12.2.0-3` and libc headers;
the final stage contains neither compiler nor source. If a pinned package is
unavailable, the build fails instead of selecting another version.

The container starts directly as the postgres user. Its clock directory/file
are owned by that user, so the pinned library's private semaphore is created
and reopened under the same identity. Stock root-to-postgres re-exec under
the preload fails permissions and is intentionally avoided.

The image deliberately supports **linux/amd64 only**. Its probe performs direct
x86_64 Linux clock syscalls, bypassing preload interposition, to compare the
kernel clocks with the libc clocks observed by PostgreSQL. The preload has no
host-clock capability and no host clock-file mount. It sets:

- `LD_PRELOAD=/usr/lib/x86_64-linux-gnu/faketime/libfaketime.so.1`
- `FAKETIME_TIMESTAMP_FILE=/opt/sqlobserver-clock/clock-offset.txt`
- `FAKETIME_DONT_FAKE_MONOTONIC=1`
- `FAKETIME_NO_CACHE=1`
- `NO_FAKE_STAT=1`

Do not set `FAKETIME`: it takes precedence over the reloadable timestamp file.
The clock file starts at `+0d`; updates atomically replace it with `+Nd`.
`clock-control set-days N` accepts only a canonical integer greater than the
existing value, with a cumulative ceiling of 36500 days. No arbitrary path or
shell command is accepted. The fixture is the only caller of this fixed
executable in its newly created container.

Use `[Collection(IsolatedClockPostgreSqlCollectionDefinition.Name)]` and inject
`IsolatedClockPostgreSqlFixture`. The collection disables parallelization.
`CreateDatabaseAsync(CancellationToken)` returns the existing
`RepositoryTestDatabase`: migrate and seed it through the usual native workflow,
then use its `ConnectionString`/`DataSource`, `CreateServerDataSource()` and
`CreateCollectorDataSource()` with the existing role/scope contracts.

`AdvanceAsync(TimeSpan.FromDays(n), CancellationToken)` accepts only positive
whole days. Advances are cumulative across this fixture's collection, so every
test must establish its baseline using native `clock_timestamp()` and ensure
current-date partitions through supported repository helpers. There is no reset
or backwards operation. Dispose each test database before fixture disposal.

Each advance verifies file/preload/kernel clock agreement, the same already-open
PostgreSQL backend's clock reload, host wall time against host monotonic elapsed
time, and unchanged container monotonic time against direct kernel readings.
An uncertain or failed verification poisons the fixture so later tests cannot
silently use an unverified offset. No offset changes should overlap producers,
lease workers, repository reads or database creation: advance only between
explicit phases, then acquire fresh native leases at the new repository time.

Sources for the package and reload/monotonic behavior:
[Debian package/version](https://packages.debian.org/bookworm/libfaketime),
[amd64 library paths](https://packages.debian.org/bookworm/amd64/libfaketime/filelist),
[libfaketime 0.9.10 documentation](https://github.com/wolfcw/libfaketime/blob/v0.9.10/README),
[versioned clock/cache implementation](https://github.com/wolfcw/libfaketime/blob/v0.9.10/src/libfaketime.c).
