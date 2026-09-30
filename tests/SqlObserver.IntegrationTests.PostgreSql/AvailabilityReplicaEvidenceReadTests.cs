using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Coordination;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed partial class AvailabilityReplicaEvidenceCommitTests
{
    [Fact]
    public async Task V2ReadsPageBothIdentitiesPreserveNullsAndStayOnThePublishedCutoff()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(
            Evidence(work, run, lease), CancellationToken.None)).Status);
        DateTimeOffset cutoff = await ReadClockAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await ScopeAsync(connection, target);
        V2ReadHeader header = await ReadV2HeaderAsync(connection, target, cutoff);
        Assert.Equal(run.Value, header.Run);
        Assert.Equal("retained", header.Status);
        Assert.Equal((short)1, header.State);
        Assert.Equal(work.RepositoryTimeUtc, header.Observed);
        Assert.Equal(4, header.OutputItems);
        Assert.Equal(4L, header.Retained);
        var replicaKeys = new HashSet<string>(StringComparer.Ordinal);
        var databaseKeys = new HashSet<string>(StringComparer.Ordinal);
        byte[]? afterGroup = null, afterReplica = null, afterDatabase = null;
        bool sawUnknownQueue = false, sawZeroQueue = false, sawKnownCommitTime = false, sawUnknownCommitTime = false;
        for (int page = 0; page < 3; page++)
        {
            await using var query = V2PageCommand(connection, false, target, cutoff, header, afterGroup, afterReplica, null);
            await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) break;
            byte[] group = (byte[])reader[0], replica = (byte[])reader[1];
            Assert.True(replicaKeys.Add(Convert.ToHexString(group) + Convert.ToHexString(replica)));
            if (!reader.GetBoolean(3)) Assert.True(reader.IsDBNull(7));
            afterGroup = group; afterReplica = replica;
            int rows = 1;
            while (await reader.ReadAsync()) rows++;
            Assert.InRange(rows, 1, 2); // One retained item plus one lookahead.
        }
        afterGroup = null; afterReplica = null;
        for (int page = 0; page < 3; page++)
        {
            await using var query = V2PageCommand(connection, true, target, cutoff, header, afterGroup, afterReplica, afterDatabase);
            await using NpgsqlDataReader reader = await query.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) break;
            byte[] group = (byte[])reader[0], replica = (byte[])reader[1], db = (byte[])reader[2];
            Assert.True(databaseKeys.Add(Convert.ToHexString(group) + Convert.ToHexString(replica) + Convert.ToHexString(db)));
            if (reader.IsDBNull(11)) sawUnknownQueue = true;
            else if (reader.GetInt64(11) == 0) sawZeroQueue = true;
            if (reader.IsDBNull(15)) sawUnknownCommitTime = true;
            else
            {
                sawKnownCommitTime = true;
                Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(15).Kind);
            }
            afterGroup = group; afterReplica = replica; afterDatabase = db;
        }
        Assert.Equal(2, replicaKeys.Count); Assert.Equal(2, databaseKeys.Count);
        Assert.True(sawUnknownQueue); Assert.True(sawZeroQueue);
        Assert.True(sawKnownCommitTime); Assert.True(sawUnknownCommitTime);
        CollectorDueWorkItem newerWork = await NextDueAsync(database, runtime, target);
        CollectorRunId newerRun = await BeginAsync(runtime, newerWork, lease);
        await runtime.CommitRunAsync(Evidence(newerWork, newerRun, lease, remoteQueue: 99), CancellationToken.None);
        Assert.Equal(run.Value, (await ReadV2HeaderAsync(connection, target, cutoff)).Run);
        DateTimeOffset newerCutoff = await ReadClockAsync(database);
        Assert.Equal(newerRun.Value, (await ReadV2HeaderAsync(connection, target, newerCutoff)).Run);
        await using (var stale = V2PageCommand(connection, true, target, newerCutoff, header, afterGroup, afterReplica, afterDatabase))
            Assert.Equal("40001", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await stale.ExecuteNonQueryAsync())).SqlState);
        await using (var legacy = new NpgsqlCommand("SELECT count(*) FROM reporting.get_latest_m9_run(@target,'availability-groups.health');", connection))
        {
            legacy.Parameters.AddWithValue("target", target);
            Assert.Equal(0L, await legacy.ExecuteScalarAsync());
        }
        await using (var route = database.DataSource.CreateCommand("UPDATE system.collection_run_v2_m9_read_route SET use_v2=true WHERE singleton;"))
            await route.ExecuteNonQueryAsync();
        await using (var legacy = new NpgsqlCommand("SELECT count(*) FROM reporting.get_latest_m9_run(@target,'availability-groups.health');", connection))
        {
            legacy.Parameters.AddWithValue("target", target);
            Assert.Equal(0L, await legacy.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task V2ReadScopeBoundsAndExpiredEvidenceFailClosedWithoutChangingPrivateWriter()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        await runtime.CommitRunAsync(Evidence(work, run, lease), CancellationToken.None);
        DateTimeOffset cutoff = await ReadClockAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() =>
            ReadV2HeaderAsync(connection, target, cutoff))).SqlState);
        await ScopeAsync(connection, Guid.NewGuid());
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() =>
            ReadV2HeaderAsync(connection, target, cutoff))).SqlState);
        await ScopeAsync(connection, target);
        V2ReadHeader header = await ReadV2HeaderAsync(connection, target, cutoff);
        await using (var invalid = V2PageCommand(connection, false, target, cutoff, header, null, new byte[32], null))
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await invalid.ExecuteNonQueryAsync())).SqlState);
        await using (var invalid = V2PageCommand(connection, true, target, cutoff, header, null, null, null))
        {
            invalid.Parameters["limit"].Value = 101;
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await invalid.ExecuteNonQueryAsync())).SqlState);
        }
        await using (var privateTable = new NpgsqlCommand("SELECT count(*) FROM telemetry.availability_replica_evidence;", connection))
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await privateTable.ExecuteScalarAsync())).SqlState);
        await using (var missing = new NpgsqlCommand("SELECT count(*) FROM reporting.get_availability_groups_v2_header(@target,NULL);", connection))
        {
            missing.Parameters.AddWithValue("target", target);
            Assert.Equal("22023", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await missing.ExecuteNonQueryAsync())).SqlState);
        }
        // A retained receipt must disclose a detached data stream, not claim a
        // complete/empty scan. The real retention worker applies its own gates.
        await using (var detach = database.DataSource.CreateCommand("ALTER TABLE telemetry.availability_database_replica_evidence DETACH PARTITION telemetry.ag_database_commit_test;"))
            await detach.ExecuteNonQueryAsync();
        V2ReadHeader expired = await ReadV2HeaderAsync(connection, target, cutoff);
        Assert.Equal("evidence_not_retained", expired.Status);
        Assert.Equal((short)3, expired.State);
        Assert.Equal(2L, expired.Retained);
        Assert.Equal(4, expired.OutputItems);
        await using (var retained = V2PageCommand(connection, false, target, cutoff, expired, null, null, null))
        {
            await using NpgsqlDataReader row = await retained.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.True(await row.ReadAsync());
            Assert.False(await row.ReadAsync());
        }
        await using (var missingStream = V2PageCommand(connection, true, target, cutoff, expired, null, null, null))
            Assert.Null(await missingStream.ExecuteScalarAsync());
        await using (var rollback = database.DataSource.CreateCommand("""
            UPDATE control.collector_schedule SET collector_version=1
             WHERE instance_id=@target AND collector_id='availability-groups.health';
            """))
        {
            rollback.Parameters.AddWithValue("target", target);
            await rollback.ExecuteNonQueryAsync();
        }
        Assert.False((await ReadV2HeaderAsync(connection, target, cutoff)).CollectorEnabled);
        await using NpgsqlDataSource collector = database.CreateCollectorDataSource();
        await using (var denied = collector.CreateCommand("SELECT count(*) FROM reporting.get_availability_groups_v2_header(@target,@at);"))
        {
            denied.Parameters.AddWithValue("target", target); denied.Parameters.AddWithValue("at", cutoff);
            Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(async () =>
                await denied.ExecuteScalarAsync())).SqlState);
        }
    }

    [Fact]
    public async Task V2EmptyWithheldFailedAndSecondaryReceiptsKeepDistinctReadCoverage()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await ScopeAsync(connection, target);
        V2ReadHeader missing = await ReadV2HeaderAsync(connection, target, await ReadClockAsync(database));
        Assert.Null(missing.Run); Assert.Null(missing.Observed); Assert.Equal("not_collected", missing.Status);
        for (int kind = 0; kind < 4; kind++)
        {
            CollectorDueWorkItem work = await NextDueAsync(database, runtime, target);
            CollectorRunId run = await BeginAsync(runtime, work, lease);
            CommitCollectorRunRequest request;
            if (kind < 2)
            {
                var empty = new AvailabilityGroupsV2Snapshot(work.TargetId, work.TargetRevision, run,
                    work.RepositoryTimeUtc, [], [], kind == 0 ? 0 : 2, kind == 1);
                request = Request(work, run, lease, empty,
                    kind == 0 ? CollectorRunOutcome.Succeeded : CollectorRunOutcome.Partial,
                    kind == 0 ? CollectorRunReason.Completed : CollectorRunReason.ResponseByteLimit,
                    kind == 0 ? CollectorLossEvidence.None : new CollectorLossEvidence(CollectorLossKind.ResponseByteLimit, 2, false));
            }
            else if (kind == 2)
            {
                var summary = new CollectorRunSummary(run, work.TargetId, work.TargetRevision, work.CollectorId,
                    2, 2, CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing,
                    TimeSpan.FromMilliseconds(1), 1, new CollectorRunAccounting(0, 0, 0, 0), CollectorLossEvidence.None);
                request = new CommitCollectorRunRequest(work, summary, SqlObserver.Collector.Abstractions.CollectorPayload.Empty,
                    CollectorCircuitSnapshot.Closed(work.RepositoryTimeUtc), lease, Timeout);
            }
            else request = Evidence(work, run, lease, secondary: true);
            Assert.Equal(CollectorRunCommitStatus.Committed, (await runtime.CommitRunAsync(request, CancellationToken.None)).Status);
            DateTimeOffset cutoff = await ReadClockAsync(database);
            V2ReadHeader header = await ReadV2HeaderAsync(connection, target, cutoff);
            Assert.Equal(run.Value, header.Run);
            Assert.Equal(kind switch { 0 => "no_data", 1 => "withheld_by_byte_limit", 2 => "collection_failed", _ => "retained" }, header.Status);
            Assert.Equal(kind switch { 0 => (short)6, 1 => (short)2, 2 => (short)5, _ => (short)3 }, header.State);
            if (kind < 3)
            {
                Assert.Null(header.Observed);
                Assert.Equal(0L, header.Retained);
                await using var page = V2PageCommand(connection, false, target, cutoff, header, null, null, null);
                Assert.Null(await page.ExecuteScalarAsync());
            }
            else
            {
                Assert.Equal(4L, header.Retained);
                await using var page = V2PageCommand(connection, false, target, cutoff, header, null, null, null);
                await using NpgsqlDataReader row = await page.ExecuteReaderAsync();
                Assert.True(await row.ReadAsync());
                Assert.Equal((short)2, row.GetInt16(4));
            }
        }
    }

    [Fact]
    public async Task V2MaximumPageUsesOneLookaheadAndReachesEveryReplicaWithoutDuplicates()
    {
        await using RepositoryTestDatabase database = await PrepareAsync();
        Guid target = await SeedAsync(database);
        var runtime = new PostgreSqlCollectorRuntimeRepositoryPort(database.DataSource);
        var leases = new PostgreSqlWorkerLeasePort(database.DataSource);
        CollectorDueWorkItem work = await DueAsync(runtime, target);
        LeaseAcquisitionResult acquired = await leases.AcquireAsync(new AcquireWorkerLeaseRequest(
            new WorkerLeaseKey($"collector/run/availability-groups.health/{target:N}"),
            new WorkerExecutionId(Guid.NewGuid()), new WorkerLeaseDuration(TimeSpan.FromMinutes(5)), Timeout), CancellationToken.None);
        WorkerLeaseIdentity lease = Assert.IsType<WorkerLease>(acquired.Lease).Identity;
        CollectorRunId run = await BeginAsync(runtime, work, lease);
        string group = new('a', 64), local = new('1', 64);
        AvailabilityReplicaEvidence[] replicas = Enumerable.Range(1, 200).Select(index =>
            new AvailabilityReplicaEvidence(new AvailabilityReplicaEvidenceIdentity(group,
                index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), local, false,
                AvailabilityVisibilityScope.PrimaryAllKnown), true, "SECONDARY", null, "CONNECTED"))
            .Append(new AvailabilityReplicaEvidence(new AvailabilityReplicaEvidenceIdentity(group, local, local,
                true, AvailabilityVisibilityScope.PrimaryAllKnown), true, "PRIMARY", "ONLINE", "CONNECTED")).ToArray();
        var snapshot = new AvailabilityGroupsV2Snapshot(work.TargetId, work.TargetRevision, run,
            work.RepositoryTimeUtc, replicas, [], replicas.Length, false);
        await runtime.CommitRunAsync(Request(work, run, lease, snapshot, CollectorRunOutcome.Succeeded,
            CollectorRunReason.Completed, CollectorLossEvidence.None), CancellationToken.None);
        DateTimeOffset cutoff = await ReadClockAsync(database);
        await using NpgsqlDataSource server = database.CreateServerDataSource();
        await using NpgsqlConnection connection = await server.OpenConnectionAsync();
        await ScopeAsync(connection, target);
        V2ReadHeader header = await ReadV2HeaderAsync(connection, target, cutoff);
        Assert.Equal(201, header.OutputItems);
        byte[]? afterGroup = null, afterReplica = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pageCount = 0;
        for (int page = 0; page < 4; page++)
        {
            await using var command = V2PageCommand(connection, false, target, cutoff, header, afterGroup, afterReplica, null);
            command.Parameters["limit"].Value = 100;
            var items = new List<(byte[] Group, byte[] Replica)>();
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) items.Add(((byte[])reader[0], (byte[])reader[1]));
            Assert.InRange(items.Count, 0, 101);
            if (items.Count == 0) break;
            pageCount++;
            if (items.Count == 101) items.RemoveAt(100);
            foreach (var item in items) Assert.True(seen.Add(Convert.ToHexString(item.Replica)));
            afterGroup = items[^1].Group; afterReplica = items[^1].Replica;
        }
        Assert.Equal(3, pageCount);
        Assert.Equal(201, seen.Count);
        Assert.Equal(replicas.Select(item => item.Identity.ReplicaFingerprint).Order(StringComparer.Ordinal),
            seen.Select(item => item.ToLowerInvariant()).Order(StringComparer.Ordinal));
    }

    private sealed record V2ReadHeader(Guid? Run, long Revision, DateTimeOffset? Observed,
        short State, string Status, int OutputItems, long Retained, bool CollectorEnabled);

    private static async Task<V2ReadHeader> ReadV2HeaderAsync(NpgsqlConnection connection, Guid target, DateTimeOffset cutoff)
    {
        await using var command = new NpgsqlCommand("SELECT * FROM reporting.get_availability_groups_v2_header(@target,@at);", connection);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("at", cutoff);
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        return new V2ReadHeader(row.IsDBNull(0) ? null : row.GetGuid(0), row.GetInt64(1),
            row.IsDBNull(2) ? null : row.GetFieldValue<DateTimeOffset>(2), row.GetInt16(4), row.GetString(5), row.GetInt32(7), row.GetInt64(8), row.GetBoolean(11));
    }

    private static NpgsqlCommand V2PageCommand(NpgsqlConnection connection, bool databaseStream, Guid target,
        DateTimeOffset cutoff, V2ReadHeader header, byte[]? group, byte[]? replica, byte[]? database)
    {
        string sql = databaseStream
            ? "SELECT * FROM reporting.list_availability_database_replica_evidence(@target,@at,@limit,@revision,@run,@group,@replica,@database);"
            : "SELECT * FROM reporting.list_availability_replica_evidence(@target,@at,@limit,@revision,@run,@group,@replica);";
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("target", target); command.Parameters.AddWithValue("at", cutoff);
        command.Parameters.AddWithValue("limit", 1); command.Parameters.AddWithValue("revision", header.Revision);
        command.Parameters.AddWithValue("run", header.Run!.Value);
        command.Parameters.AddWithValue("group", NpgsqlDbType.Bytea, (object?)group ?? DBNull.Value);
        command.Parameters.AddWithValue("replica", NpgsqlDbType.Bytea, (object?)replica ?? DBNull.Value);
        if (databaseStream) command.Parameters.AddWithValue("database", NpgsqlDbType.Bytea, (object?)database ?? DBNull.Value);
        return command;
    }

    private static async Task<DateTimeOffset> ReadClockAsync(RepositoryTestDatabase database)
    {
        await using var command = database.DataSource.CreateCommand("SELECT clock_timestamp();");
        await using NpgsqlDataReader row = await command.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        return row.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task ScopeAsync(NpgsqlConnection connection, Guid target)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,false);", connection);
        command.Parameters.AddWithValue("scope", target.ToString());
        await command.ExecuteNonQueryAsync();
    }
}
