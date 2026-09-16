using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Infrastructure.PostgreSql;

internal sealed class PostgreSqlLiveActivityRepository(NpgsqlDataSource source) : ILiveActivityRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Cursor(Guid Snapshot, int Offset, string Scope);
    private sealed record Envelope(string Fingerprint, string Algorithm, string Key, byte[] Nonce, byte[] Tag, byte[] Ciphertext);

    public async Task<IReadOnlyList<LiveActivityTarget>> TargetsAsync(CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("SELECT instance_id,revision,host_name,instance_name,tcp_port,certificate_host_name,connect_timeout FROM live_activity.targets()");
        command.CommandTimeout = 5;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var targets = new List<LiveActivityTarget>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            targets.Add(new(reader.GetGuid(0), reader.GetInt64(1), new SqlServerConnectionPolicy(
                new SqlServerEndpoint(new SqlServerHostName(reader.GetString(2)), reader.IsDBNull(3) ? null : new SqlServerInstanceName(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4)), new SqlServerConnectTimeout(reader.GetTimeSpan(6)),
                reader.IsDBNull(5) ? null : new SqlServerCertificateHostName(reader.GetString(5)))));
        return targets;
    }

    public async Task CommitAsync(LiveActivityTarget target, WorkerLeaseIdentity lease, LiveActivityCapture capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(capture);
        bool triggered = capture.DeadlockEventId is not null;
        string leasePrefix = triggered ? "collector/live-activity-deadlock/" : "collector/live-activity/";
        if (lease.Key.Value != leasePrefix + target.Id.ToString("D")) throw new ArgumentException("Invalid lease scope.");
        if (!triggered && capture.DeadlockOccurredUtc is not null) throw new ArgumentException("A cadence capture cannot carry deadlock metadata.", nameof(capture));
        if (capture.DeadlockEventId is Guid eventId)
        {
            if (eventId == Guid.Empty || capture.DeadlockOccurredUtc is null || capture.DeadlockOccurredUtc.Value.Offset != TimeSpan.Zero)
                throw new ArgumentException("A triggered capture requires a non-empty deadlock event and UTC occurrence time.", nameof(capture));
        }
        string sql = triggered
            ? "SELECT live_activity.commit_triggered_capture(@target,@revision,@owner,@fence,@id,@observed,@truncated,@rows,@payloads,@databases,@deadlock_event,@deadlock_occurred)"
            : "SELECT live_activity.commit_capture(@target,@revision,@owner,@fence,@id,@observed,@truncated,@rows,@payloads,@databases)";
        await using var command = source.CreateCommand(sql);
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("target", target.Id);
        command.Parameters.AddWithValue("revision", target.Revision);
        command.Parameters.AddWithValue("owner", lease.Owner.Value);
        command.Parameters.AddWithValue("fence", lease.FencingToken.Value);
        command.Parameters.AddWithValue("id", capture.Id);
        command.Parameters.AddWithValue("observed", capture.ObservedUtc);
        command.Parameters.AddWithValue("truncated", capture.Truncated);
        command.Parameters.AddWithValue("rows", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(capture.Rows, Json));
        command.Parameters.AddWithValue("databases", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(capture.Databases ?? [], Json));
        command.Parameters.AddWithValue("payloads", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(capture.Payloads.Select(p => new {
            id = p.Id, @protected = new Envelope(p.Protected.Fingerprint.ToHexString(), p.Protected.ProtectionAlgorithm,
            p.Protected.KeyIdentifier,p.Protected.GetNonce(),p.Protected.GetAuthenticationTag(),p.Protected.GetCiphertext()) }), Json));
        if (capture.DeadlockEventId is Guid triggeredEvent)
        {
            command.Parameters.AddWithValue("deadlock_event", triggeredEvent);
            command.Parameters.AddWithValue("deadlock_occurred", capture.DeadlockOccurredUtc!.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasDeadlockSnapshotAsync(Guid targetId, Guid eventId, CancellationToken cancellationToken)
    {
        if (targetId == Guid.Empty || eventId == Guid.Empty) throw new ArgumentException("Target and event identities are required.");
        await using var command = source.CreateCommand("SELECT live_activity.has_deadlock_snapshot(@target,@event)");
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("target", targetId);
        command.Parameters.AddWithValue("event", eventId);
        return (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) is true;
    }

    public async Task<LiveActivityPage> ReadAsync(LiveActivityRead request, CancellationToken cancellationToken)
    {
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {request.TargetId, request.Filter}, Json))));
        Guid? snapshot = request.SnapshotId;
        int offset = 0;
        if (request.Cursor is not null)
        {
            Cursor cursor;
            try { cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(request.Cursor), Json) ?? throw new JsonException(); }
            catch (Exception ex) when (ex is JsonException or FormatException) { throw new ArgumentException("Invalid activity cursor."); }
            if (cursor.Scope != scope || cursor.Offset is < 1 or > 512 || (snapshot is not null && snapshot != cursor.Snapshot))
                throw new ArgumentException("Activity cursor scope changed.");
            snapshot = cursor.Snapshot;
            offset = cursor.Offset;
        }
        await using var command = source.CreateCommand("SELECT live_activity.read_page(@target,@snapshot,@filter,@offset)");
        command.CommandTimeout = 5;
        command.Parameters.AddWithValue("target", request.TargetId);
        command.Parameters.AddWithValue("snapshot", NpgsqlDbType.Uuid, (object?)snapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("filter", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(request.Filter, Json));
        command.Parameters.AddWithValue("offset", offset);
        string data = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        var page = JsonSerializer.Deserialize<LiveActivityPage>(data, Json)!;
        return page with { Rows = page.Rows.Take(50).ToArray(), NextCursor = page.Rows.Count > 50 && page.SnapshotId is Guid id
            ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(id,offset+50,scope), Json)) : null };
    }

    public async Task<IReadOnlyList<LiveActivitySnapshot>> HistoryAsync(Guid targetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("SELECT id,observed_at,truncated,deadlock_event_id,deadlock_occurred_at FROM live_activity.history(@target,@from,@to)");
        command.CommandTimeout=5;
        command.Parameters.AddWithValue("target",targetId); command.Parameters.AddWithValue("from",fromUtc); command.Parameters.AddWithValue("to",toUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LiveActivitySnapshot>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new(reader.GetGuid(0),reader.GetFieldValue<DateTimeOffset>(1),reader.GetBoolean(2),reader.IsDBNull(3) ? null : reader.GetGuid(3),reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
        return result;
    }

    public async Task<ProtectedSensitivePayload?> QueryAsync(Guid targetId, Guid snapshotId, string identity, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("SELECT live_activity.query_payload(@target,@snapshot,@identity)");
        command.CommandTimeout=5;
        command.Parameters.AddWithValue("target",targetId); command.Parameters.AddWithValue("snapshot",snapshotId); command.Parameters.AddWithValue("identity",identity);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string json) return null;
        var e = JsonSerializer.Deserialize<Envelope>(json,Json)!;
        return new(SensitivePayloadKind.QueryText,new SensitivePayloadFingerprint(Convert.FromHexString(e.Fingerprint)),e.Algorithm,e.Key,e.Nonce,e.Tag,e.Ciphertext);
    }

    public async Task AuditAsync(Guid targetId, Guid snapshotId, string actor, string outcome, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand("SELECT live_activity.audit_query(@target,@snapshot,@actor,@outcome)");
        command.CommandTimeout=5;
        command.Parameters.AddWithValue("target",targetId); command.Parameters.AddWithValue("snapshot",snapshotId);
        command.Parameters.AddWithValue("actor",actor); command.Parameters.AddWithValue("outcome",outcome);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var command=source.CreateCommand("SELECT live_activity.cleanup()"); command.CommandTimeout=5;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailedAsync(Guid targetId,WorkerLeaseIdentity lease,CancellationToken cancellationToken)
    {
        await using var command=source.CreateCommand("SELECT live_activity.failed(@target,@owner,@fence)"); command.CommandTimeout=5;
        command.Parameters.AddWithValue("target",targetId); command.Parameters.AddWithValue("owner",lease.Owner.Value); command.Parameters.AddWithValue("fence",lease.FencingToken.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
