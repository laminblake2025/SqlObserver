using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

internal sealed record PlanCacheDatabaseIdentitySnapshot(MonitoredInstanceId TargetId,
    ObservationTargetRevision TargetRevision, int DatabaseId, DateTimeOffset ReadStartedAtUtc,
    DateTimeOffset ReadCompletedAtUtc, string SourceEpoch, string? DatabaseIncarnation,
    bool CatalogEligible);

/// <summary>Live identity evidence from an already-open database, with no context switching.</summary>
internal sealed class PlanCacheDatabaseIdentitySource
{
    internal const string ExpectedSqlChecksum = "59f718e9c8725b4b42a2729c1ce532cbc3e26c21138c39c172933637a02bc349";
    private const string Resource = "SqlObserver.Infrastructure.SqlServer.PlanCacheIdentitySourceAssets.read.v1.sql";
    private readonly string sql;

    private PlanCacheDatabaseIdentitySource(string sql) => this.sql = sql;

    internal static PlanCacheDatabaseIdentitySource LoadEmbedded()
    {
        using Stream stream = typeof(PlanCacheDatabaseIdentitySource).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidDataException("Cache database identity source is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ExpectedSqlChecksum, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cache database identity source checksum is not approved.");
        return new(new UTF8Encoding(false, true).GetString(bytes));
    }

    internal async Task<PlanCacheDatabaseIdentitySnapshot> ReadAsync(SqlConnection connection,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (connection.State != ConnectionState.Open) throw new ArgumentException("An open database connection is required.");
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 5 };
        await using SqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess,
            cancellationToken).ConfigureAwait(false);
        return await DecodeAsync(reader, targetId, targetRevision, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<PlanCacheDatabaseIdentitySnapshot> DecodeAsync(DbDataReader reader,
        MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(targetId);
        ArgumentNullException.ThrowIfNull(targetRevision);
        if (reader.FieldCount != 7 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt32(0) != 1)
            throw new InvalidDataException("Cache database identity returned an unapproved frame.");
        DateTimeOffset started = reader.GetFieldValue<DateTimeOffset>(1), completed = reader.GetFieldValue<DateTimeOffset>(2);
        int databaseId = reader.GetInt32(3);
        string epoch = QueryOpaqueIdentity.RequireDigest(reader.GetString(4), "sourceEpoch");
        string? incarnation = reader.IsDBNull(5) ? null : QueryOpaqueIdentity.RequireDigest(reader.GetString(5), "databaseIncarnation");
        bool eligible = reader.GetBoolean(6);
        if (databaseId is <= 4 or >= 32767 || started.Offset != TimeSpan.Zero || completed.Offset != TimeSpan.Zero ||
            completed < started || completed - started > TimeSpan.FromSeconds(5) || (!eligible && incarnation is not null) ||
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Cache database identity has an invalid scope, window or result count.");
        return new(targetId, targetRevision, databaseId, started, completed, epoch, incarnation, eligible);
    }
}
