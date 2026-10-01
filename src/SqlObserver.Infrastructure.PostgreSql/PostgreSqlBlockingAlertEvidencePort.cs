using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

public sealed class PostgreSqlBlockingAlertEvidencePort(NpgsqlDataSource dataSource) : IBlockingAlertEvidencePort
{
    public async ValueTask<BlockingAlertEvidence> ReadAsync(MonitoredInstanceId targetId,long targetRevision,
        WorkerLeaseIdentity lease,RepositoryCallTimeout timeout,CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetId); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRevision);
        ArgumentNullException.ThrowIfNull(lease); ArgumentNullException.ThrowIfNull(timeout);
        if (lease.Key.Value!="alerts/evaluation/reconcile") throw new ArgumentException("The alert coordinator lease is required.",nameof(lease));
        var bounded=new RepositoryCallTimeout(timeout.Value<TimeSpan.FromSeconds(5)?timeout.Value:TimeSpan.FromSeconds(5));
        using CancellationTokenSource deadline=PostgreSqlRuntimeSupport.CreateTimeoutScope(bounded,cancellationToken);
        try
        {
            await using NpgsqlConnection connection=await dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction=await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await PostgreSqlRuntimeSupport.ConfigureTransactionAsync(connection,transaction,bounded,deadline.Token).ConfigureAwait(false);
            await using (var scope=new NpgsqlCommand("SELECT set_config('sqlobserver.target_scope',@scope,true);",connection,transaction))
            {
                scope.CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded);
                scope.Parameters.AddWithValue("scope",targetId.Value.ToString("D")); await scope.ExecuteNonQueryAsync(deadline.Token).ConfigureAwait(false);
            }
            await using var command=new NpgsqlCommand("SELECT read_at,payload,source_digest FROM alerting.read_blocking_alert_evidence(@target,@revision,@owner,@fencing);",connection,transaction)
                {CommandTimeout=PostgreSqlRuntimeSupport.GetCommandTimeoutSeconds(bounded)};
            command.Parameters.AddWithValue("target",targetId.Value); command.Parameters.AddWithValue("revision",targetRevision);
            command.Parameters.AddWithValue("owner",lease.Owner.Value); command.Parameters.AddWithValue("fencing",lease.FencingToken.Value);
            BlockingAlertEvidence result;
            await using (NpgsqlDataReader row=await command.ExecuteReaderAsync(deadline.Token).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
                DateTimeOffset readAt=row.GetFieldValue<DateTimeOffset>(0);
                string payload=row.GetString(1),digest=row.GetString(2);
                if (Encoding.UTF8.GetByteCount(payload)>2048 || digest!=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))) throw Invalid();
                result=Decode(payload,digest,readAt);
                if (result.TargetId.Value!=targetId.Value || result.TargetRevision!=targetRevision || result.ReadAtUtc!=readAt ||
                    await row.ReadAsync(deadline.Token).ConfigureAwait(false)) throw Invalid();
            }
            await transaction.CommitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested(); return result;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {throw PostgreSqlRuntimeSupport.CreateTimeoutException("blocking alert evidence read",exception);}
    }
    private static BlockingAlertEvidence Decode(string payload,string digest,DateTimeOffset readAt)
    {
        try
        {
            using JsonDocument document=JsonDocument.Parse(payload,new(){MaxDepth=3}); JsonElement root=document.RootElement;
            string[] expected=["schemaVersion","targetId","targetRevision","validUntilUtc","scheduleRevision","runId",
                "completedAtUtc","sourceObservedAtUtc","retainedEdges","maximumWaitMilliseconds","reason","completionDigest"];
            if (root.ValueKind!=JsonValueKind.Object || !root.EnumerateObject().Select(property=>property.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal)) || root.GetProperty("schemaVersion").GetInt32()!=1) throw Invalid();
            BlockingAlertEvidenceReason reason=root.GetProperty("reason").GetString() switch
            {
                "current"=>BlockingAlertEvidenceReason.Current,"collector_disabled"=>BlockingAlertEvidenceReason.CollectorDisabled,
                "source_unavailable"=>BlockingAlertEvidenceReason.SourceUnavailable,"source_stale"=>BlockingAlertEvidenceReason.SourceStale,
                "source_incomplete"=>BlockingAlertEvidenceReason.SourceIncomplete,"source_inconsistent"=>BlockingAlertEvidenceReason.SourceInconsistent,
                _=>throw Invalid(),
            };
            var result=new BlockingAlertEvidence(new(root.GetProperty("targetId").GetGuid()),root.GetProperty("targetRevision").GetInt64(),
                readAt,Utc(root,"validUntilUtc"),Number(root,"scheduleRevision"),
                root.GetProperty("runId").ValueKind==JsonValueKind.Null?null:root.GetProperty("runId").GetGuid(),
                Utc(root,"completedAtUtc"),Utc(root,"sourceObservedAtUtc"),Number(root,"retainedEdges") is { } edges?checked((int)edges):null,
                Number(root,"maximumWaitMilliseconds"),reason,root.GetProperty("completionDigest").GetString(),digest);
            result.Validate(); return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {throw new InvalidDataException("Blocking alert evidence returned an invalid envelope.",exception);}
    }
    private static DateTimeOffset? Utc(JsonElement value,string name)=>value.GetProperty(name).ValueKind==JsonValueKind.Null?null:value.GetProperty(name).GetDateTimeOffset();
    private static long? Number(JsonElement value,string name)=>value.GetProperty(name).ValueKind==JsonValueKind.Null?null:value.GetProperty(name).GetInt64();
    private static InvalidDataException Invalid()=>new("Blocking alert evidence returned an invalid envelope.");
}
