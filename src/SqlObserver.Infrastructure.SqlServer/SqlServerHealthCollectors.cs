using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Repository;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Base for fixed, checksum-verified, bounded and passive SQL Server collectors.</summary>
public abstract class SqlServerHealthCollector : ISqlServerCollector
{
    private readonly ISqlServerConnectionFactory _connectionFactory;
    private readonly SqlServerCollectorAsset _asset;

    private protected SqlServerHealthCollector(
        string collectorId,
        SqlServerCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _asset = catalog.Get(new Domain.Capabilities.CollectorId(collectorId));
    }

    public CollectorManifest Manifest => _asset.Manifest;

    public async ValueTask<CollectorExecutionResult> CollectAsync(
        CollectorExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CapabilityProfile.ServerIdentity is null ||
            request.CapabilityProfile.TargetId != request.TargetId ||
            request.CapabilityProfile.TargetRevision != request.TargetRevision)
        {
            return Failure(
                request,
                CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.CapabilityProfileStale,
                outputWasRejected: true);
        }

        int majorVersion = request.CapabilityProfile.ServerIdentity.Version.Major;
        if (!Manifest.SupportedVersions.Contains(majorVersion))
        {
            return Failure(
                request,
                CollectorRunOutcome.Unsupported,
                CollectorRunReason.TargetVersionUnsupported);
        }

        TimeSpan operationBudget = request.Timeout.Value < Manifest.Limits.CommandTimeout
            ? request.Timeout.Value
            : Manifest.Limits.CommandTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(operationBudget);

        try
        {
            await using SqlConnection connection = await _connectionFactory
                .OpenConnectionAsync(request.ConnectionPolicy, deadline.Token)
                .ConfigureAwait(false);
            await using var command = new SqlCommand(_asset.GetQuery(majorVersion), connection)
            {
                CommandTimeout = Math.Max(1, checked((int)Math.Ceiling(operationBudget.TotalSeconds))),
            };
            command.Parameters.Add("maximum_rows", SqlDbType.Int).Value = Manifest.Limits.MaxRows;
            await using SqlDataReader reader = await command.ExecuteReaderAsync(
                    CommandBehavior.SequentialAccess | CommandBehavior.SingleResult,
                    deadline.Token)
                .ConfigureAwait(false);
            CollectorReadResult collected = await ReadPayloadAsync(request, reader, deadline.Token)
                .ConfigureAwait(false);
            CollectorPayload payload = collected.Payload;
            int responseBytes = Math.Max(collected.ResponseBytes, payload.EstimatedSizeBytes);
            var accounting = new CollectorRunAccounting(
                collected.SourceRowsRead,
                payload.ItemCount,
                responseBytes,
                payload.EstimatedSizeBytes);
            return new CollectorExecutionResult(
                request.TargetId,
                request.TargetRevision,
                Manifest.Id,
                Manifest.ManifestVersion.Value,
                Manifest.OutputSchemaVersion.Value,
                collected.Loss.HasLoss ? CollectorRunOutcome.Partial : CollectorRunOutcome.Succeeded,
                collected.Loss.Kind switch
                {
                    CollectorLossKind.None => CollectorRunReason.Completed,
                    CollectorLossKind.SourceRowLimit => CollectorRunReason.SourceRowLimit,
                    CollectorLossKind.ResponseByteLimit => CollectorRunReason.ResponseByteLimit,
                    _ => CollectorRunReason.OutputValidationFailed,
                },
                payload,
                accounting,
                collected.Loss);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded);
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return Failure(request, CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded);
        }
        catch (SqlException exception) when (IsPermissionDenied(exception.Number))
        {
            return Failure(
                request,
                CollectorRunOutcome.PermissionDenied,
                CollectorRunReason.RequiredPermissionMissing);
        }
        catch (SqlException exception) when (IsTransient(exception.Number))
        {
            return Failure(
                request,
                CollectorRunOutcome.TransientFailure,
                CollectorRunReason.TransientTargetFailure);
        }
        catch (SqlException)
        {
            return Failure(
                request,
                CollectorRunOutcome.PermanentFailure,
                CollectorRunReason.PermanentTargetFailure);
        }
        catch (CollectorReadValidationException exception)
        {
            return Failure(
                request,
                CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.OutputValidationFailed,
                outputWasRejected: true,
                exception.Accounting);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or InvalidCastException)
        {
            return Failure(
                request,
                CollectorRunOutcome.OutputInvalid,
                CollectorRunReason.OutputValidationFailed,
                outputWasRejected: true);
        }
    }

    private protected abstract ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken);

    private protected static CollectorReadResult CompleteRead(
        CollectorPayload payload,
        int sourceRowsRead,
        int responseBytes,
        bool byteLimitReached,
        bool rowLimitReached)
    {
        if (payload.ItemCount == 0)
        {
            throw new InvalidDataException("A core-health collector returned no validated rows.");
        }

        CollectorLossEvidence loss = CreateLossEvidence(byteLimitReached, rowLimitReached);
        return new CollectorReadResult(payload, sourceRowsRead, responseBytes, loss);
    }

    internal static CollectorLossEvidence CreateLossEvidence(
        bool byteLimitReached,
        bool rowLimitReached) => byteLimitReached
            ? new CollectorLossEvidence(
                CollectorLossKind.ResponseByteLimit,
                minimumLostItems: 1,
                countIsExact: false,
                minimumLostBytes: 1)
            : rowLimitReached
                ? new CollectorLossEvidence(
                    CollectorLossKind.SourceRowLimit,
                    minimumLostItems: 1,
                    countIsExact: false)
                : CollectorLossEvidence.None;

    private CollectorExecutionResult Failure(
        CollectorExecutionRequest request,
        CollectorRunOutcome outcome,
        CollectorRunReason reason,
        bool outputWasRejected = false,
        CollectorRunAccounting? rejectedAccounting = null)
    {
        CollectorRunAccounting accounting = rejectedAccounting ?? new CollectorRunAccounting(0, 0, 0, 0);
        return new CollectorExecutionResult(
            request.TargetId,
            request.TargetRevision,
            Manifest.Id,
            Manifest.ManifestVersion.Value,
            Manifest.OutputSchemaVersion.Value,
            outcome,
            reason,
            CollectorPayload.Empty,
            accounting,
            outputWasRejected
                ? new CollectorLossEvidence(
                    CollectorLossKind.OutputValidationFailure,
                    minimumLostItems: Math.Max(1, accounting.OutputItemsProduced),
                    countIsExact: accounting.OutputItemsProduced > 0,
                    minimumLostBytes: accounting.OutputBytes)
                : CollectorLossEvidence.None);
    }

    private static bool IsPermissionDenied(int number) => number is 229 or 297 or 300;

    private static bool IsTransient(int number) => number is
        20 or 53 or 64 or 233 or 10053 or 10054 or 10060 or 10928 or 10929 or 40197 or 40501 or 40613;

    private protected static DateTimeOffset ReadUtcMicrosecond(SqlDataReader reader, int ordinal)
    {
        DateTime value = reader.GetDateTime(ordinal);
        long ticks = value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond);
        return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
    }

    private protected static int AddBoundedBytes(int current, int additional, int maximum)
    {
        int total = checked(current + additional);
        return total <= maximum ? total : -1;
    }

    private protected static int Utf8Bytes(string value) => Encoding.UTF8.GetByteCount(value);

    private protected static MetricSampleId CreateMetricSampleId(
        CollectorRunId runId,
        string metricId)
    {
        byte[] identity = Encoding.UTF8.GetBytes(string.Concat(runId.Value.ToString("D"), "\n", metricId));
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        return new MetricSampleId(new Guid(digest[..16]));
    }

    private protected sealed record CollectorReadResult(
        CollectorPayload Payload,
        int SourceRowsRead,
        int ResponseBytes,
        CollectorLossEvidence Loss);
}

/// <summary>
/// Reserves the final manifest-bounded source row as a truncation probe and accounts bytes only
/// for rows accepted into the safe payload.
/// </summary>
internal sealed class BoundedCollectorReadBudget
{
    private readonly int _maximumRows;
    private readonly int _maximumResponseBytes;
    private bool _rowAwaitingBytes;

    internal BoundedCollectorReadBudget(int maximumRows, int maximumResponseBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        _maximumRows = maximumRows;
        _maximumResponseBytes = maximumResponseBytes;
    }

    internal int SourceRowsRead { get; private set; }

    internal int ResponseBytes { get; private set; }

    internal bool RowLimitReached { get; private set; }

    internal bool ByteLimitReached { get; private set; }

    internal bool TryBeginRow()
    {
        if (_rowAwaitingBytes || RowLimitReached || ByteLimitReached)
        {
            throw new InvalidOperationException("The collector read budget is not ready for another source row.");
        }

        SourceRowsRead = checked(SourceRowsRead + 1);
        if (SourceRowsRead >= _maximumRows)
        {
            RowLimitReached = true;
            return false;
        }

        _rowAwaitingBytes = true;
        return true;
    }

    internal bool TryAcceptResponseBytes(int rowBytes)
    {
        if (!_rowAwaitingBytes)
        {
            throw new InvalidOperationException("A source row must begin before its response bytes are accepted.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowBytes);
        _rowAwaitingBytes = false;
        int nextBytes = checked(ResponseBytes + rowBytes);
        if (nextBytes > _maximumResponseBytes)
        {
            ByteLimitReached = true;
            return false;
        }

        ResponseBytes = nextBytes;
        return true;
    }
}

internal sealed class CollectorReadValidationException : Exception
{
    internal CollectorReadValidationException(
        BoundedCollectorReadBudget budget,
        int acceptedOutputItems,
        int acceptedOutputBytes,
        Exception innerException)
        : base("SQL Server returned collector output that failed validation.", innerException)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentOutOfRangeException.ThrowIfNegative(acceptedOutputItems);
        ArgumentOutOfRangeException.ThrowIfNegative(acceptedOutputBytes);
        int rejectedOutputItems = checked(acceptedOutputItems + 1);
        Accounting = new CollectorRunAccounting(
            budget.SourceRowsRead,
            rejectedOutputItems,
            Math.Max(budget.ResponseBytes, acceptedOutputBytes),
            acceptedOutputBytes);
    }

    internal CollectorRunAccounting Accounting { get; }
}

public sealed class SqlServerCoreEngineCollector : SqlServerHealthCollector
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        [
            new CollectorMetricOutputContract(new MetricId("engine.batch_requests_total"), []),
            new CollectorMetricOutputContract(new MetricId("engine.sql_compilations_total"), []),
            new CollectorMetricOutputContract(new MetricId("engine.sql_recompilations_total"), []),
            new CollectorMetricOutputContract(new MetricId("engine.page_life_expectancy_seconds"), []),
            new CollectorMetricOutputContract(new MetricId("engine.user_connections"), []),
            new CollectorMetricOutputContract(new MetricId("engine.process_physical_memory_bytes"), []),
            new CollectorMetricOutputContract(new MetricId("engine.committed_memory_bytes"), []),
            new CollectorMetricOutputContract(new MetricId("engine.target_memory_bytes"), []),
        ],
        maxMetricSamples: 8,
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 0);

    private static readonly HashSet<string> AllowedMetricIds = new(StringComparer.Ordinal)
    {
        "engine.batch_requests_total",
        "engine.sql_compilations_total",
        "engine.sql_recompilations_total",
        "engine.page_life_expectancy_seconds",
        "engine.user_connections",
        "engine.process_physical_memory_bytes",
        "engine.committed_memory_bytes",
        "engine.target_memory_bytes",
    };

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerCoreEngineCollector()
        : this(
            SqlServerCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerCoreEngineCollector(SqlServerCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerCoreEngineCollector(
        SqlServerCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("engine.core", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var metrics = new List<MetricSample>();
        var budget = new BoundedCollectorReadBudget(
            Manifest.Limits.MaxRows,
            Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                string metricId = reader.GetString(1);
                double value = reader.GetDouble(2);
                if (!AllowedMetricIds.Contains(metricId) || !double.IsFinite(value) || value < 0)
                {
                    throw new InvalidDataException("The engine collector returned an invalid output row.");
                }

                int rowBytes = checked(32 + Utf8Bytes(metricId));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                metrics.Add(new MetricSample(
                    CreateMetricSampleId(request.RunId, metricId),
                    request.TargetId,
                    new MetricId(metricId),
                    observedAt,
                    value));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                metrics.Count,
                metrics.Sum(static item => item.EstimatedSizeBytes),
                exception);
        }

        return CompleteRead(
            new CollectorPayload(metrics),
            budget.SourceRowsRead,
            budget.ResponseBytes,
            budget.ByteLimitReached,
            budget.RowLimitReached);
    }
}

public sealed class SqlServerDatabaseInventoryCollector : SqlServerHealthCollector
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        metrics: [],
        maxMetricSamples: 0,
        maxDatabaseObservations: 1_000,
        maxDatabaseFileObservations: 0);

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerDatabaseInventoryCollector()
        : this(
            SqlServerCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerDatabaseInventoryCollector(SqlServerCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerDatabaseInventoryCollector(
        SqlServerCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("database.inventory", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var databases = new List<DatabaseObservation>();
        var budget = new BoundedCollectorReadBudget(
            Manifest.Limits.MaxRows,
            Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                int databaseId = reader.GetInt32(1);
                string name = reader.GetString(2);
                string state = reader.GetString(3);
                string access = reader.GetString(4);
                string recovery = reader.GetString(5);
                int rowBytes = checked(
                    48 + Utf8Bytes(name) + Utf8Bytes(state) + Utf8Bytes(access) + Utf8Bytes(recovery));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                databases.Add(new DatabaseObservation(
                    request.TargetId,
                    request.TargetRevision,
                    databaseId,
                    new SqlServerObjectName(name),
                    ParseDatabaseState(state),
                    ParseRecoveryModel(recovery),
                    ParseUserAccess(access),
                    reader.GetBoolean(6),
                    reader.GetByte(7),
                    observedAt));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                databases.Count,
                databases.Sum(static item => item.EstimatedSizeBytes),
                exception);
        }

        return CompleteRead(
            new CollectorPayload(databases: new DatabaseObservationBatch(databases)),
            budget.SourceRowsRead,
            budget.ResponseBytes,
            budget.ByteLimitReached,
            budget.RowLimitReached);
    }

    private static DatabaseOperationalState ParseDatabaseState(string value) => value switch
    {
        "ONLINE" => DatabaseOperationalState.Online,
        "RESTORING" => DatabaseOperationalState.Restoring,
        "RECOVERING" => DatabaseOperationalState.Recovering,
        "RECOVERY_PENDING" => DatabaseOperationalState.RecoveryPending,
        "SUSPECT" => DatabaseOperationalState.Suspect,
        "EMERGENCY" => DatabaseOperationalState.Emergency,
        "OFFLINE" => DatabaseOperationalState.Offline,
        "COPYING" => DatabaseOperationalState.Copying,
        "OFFLINE_SECONDARY" => DatabaseOperationalState.OfflineSecondary,
        _ => DatabaseOperationalState.Other,
    };

    private static DatabaseRecoveryModel ParseRecoveryModel(string value) => value switch
    {
        "FULL" => DatabaseRecoveryModel.Full,
        "BULK_LOGGED" => DatabaseRecoveryModel.BulkLogged,
        "SIMPLE" => DatabaseRecoveryModel.Simple,
        _ => DatabaseRecoveryModel.Other,
    };

    private static DatabaseUserAccess ParseUserAccess(string value) => value switch
    {
        "MULTI_USER" => DatabaseUserAccess.MultiUser,
        "RESTRICTED_USER" => DatabaseUserAccess.RestrictedUser,
        "SINGLE_USER" => DatabaseUserAccess.SingleUser,
        _ => DatabaseUserAccess.Other,
    };
}

public sealed class SqlServerDatabaseFilesCollector : SqlServerHealthCollector
{
    private static readonly CollectorOutputContract RegisteredOutputContract = new(
        new CollectorOutputSchemaVersion(1),
        metrics: [],
        maxMetricSamples: 0,
        maxDatabaseObservations: 0,
        maxDatabaseFileObservations: 1_000);

    public static CollectorOutputContract OutputContract => RegisteredOutputContract;

    public SqlServerDatabaseFilesCollector()
        : this(
            SqlServerCollectorAssetCatalog.LoadEmbedded(),
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    public SqlServerDatabaseFilesCollector(SqlServerCollectorAssetCatalog catalog)
        : this(
            catalog,
            new SqlServerIntegratedConnectionFactory(SqlServerIntegratedConnectionFactory.CollectionApplicationName))
    {
    }

    internal SqlServerDatabaseFilesCollector(
        SqlServerCollectorAssetCatalog catalog,
        ISqlServerConnectionFactory connectionFactory)
        : base("database.files", catalog, connectionFactory)
    {
    }

    private protected override async ValueTask<CollectorReadResult> ReadPayloadAsync(
        CollectorExecutionRequest request,
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var files = new List<DatabaseFileObservation>();
        var budget = new BoundedCollectorReadBudget(
            Manifest.Limits.MaxRows,
            Manifest.Limits.MaxResponseBytes);
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!budget.TryBeginRow())
                {
                    break;
                }

                DateTimeOffset observedAt = ReadUtcMicrosecond(reader, 0);
                int databaseId = reader.GetInt32(1);
                int fileId = reader.GetInt32(2);
                string logicalName = reader.GetString(3);
                string type = reader.GetString(4);
                string state = reader.GetString(5);
                long sizeBytes = DecimalToInt64(reader.GetDecimal(6));
                long? maximumSizeBytes = reader.IsDBNull(7)
                    ? null
                    : DecimalToInt64(reader.GetDecimal(7));
                long rawGrowth = reader.GetInt32(8);
                bool percentGrowth = reader.GetBoolean(9);
                long readsTotal = reader.GetInt64(10);
                long writesTotal = reader.GetInt64(11);
                long bytesReadTotal = reader.GetInt64(12);
                long bytesWrittenTotal = reader.GetInt64(13);
                long readStall = reader.GetInt64(14);
                long writeStall = reader.GetInt64(15);
                int rowBytes = checked(160 + Utf8Bytes(logicalName) + Utf8Bytes(type) + Utf8Bytes(state));
                if (!budget.TryAcceptResponseBytes(rowBytes))
                {
                    break;
                }

                files.Add(new DatabaseFileObservation(
                    request.TargetId,
                    request.TargetRevision,
                    databaseId,
                    fileId,
                    new SqlServerObjectName(logicalName),
                    ParseFileType(type),
                    ParseFileState(state),
                    sizeBytes,
                    maximumSizeBytes,
                    percentGrowth ? 0 : checked(rawGrowth * 8192),
                    percentGrowth ? checked((int)rawGrowth) : 0,
                    readsTotal,
                    writesTotal,
                    bytesReadTotal,
                    bytesWrittenTotal,
                    checked(readStall + writeStall),
                    observedAt));
            }
        }
        catch (Exception exception) when (exception is
            InvalidDataException or OverflowException or InvalidCastException or ArgumentException)
        {
            throw new CollectorReadValidationException(
                budget,
                files.Count,
                files.Sum(static item => item.EstimatedSizeBytes),
                exception);
        }

        return CompleteRead(
            new CollectorPayload(databaseFiles: new DatabaseFileObservationBatch(files)),
            budget.SourceRowsRead,
            budget.ResponseBytes,
            budget.ByteLimitReached,
            budget.RowLimitReached);
    }

    private static long DecimalToInt64(decimal value)
    {
        if (value < 0 || value > long.MaxValue || decimal.Truncate(value) != value)
        {
            throw new InvalidDataException("A database-file byte value is invalid.");
        }

        return decimal.ToInt64(value);
    }

    private static DatabaseFileType ParseFileType(string value) => value switch
    {
        "ROWS" => DatabaseFileType.Rows,
        "LOG" => DatabaseFileType.Log,
        "FILESTREAM" => DatabaseFileType.Filestream,
        "FULLTEXT" => DatabaseFileType.FullText,
        _ => DatabaseFileType.Other,
    };

    private static DatabaseFileState ParseFileState(string value) => value switch
    {
        "ONLINE" => DatabaseFileState.Online,
        "RESTORING" => DatabaseFileState.Restoring,
        "RECOVERING" => DatabaseFileState.Recovering,
        "RECOVERY_PENDING" => DatabaseFileState.RecoveryPending,
        "SUSPECT" => DatabaseFileState.Suspect,
        "EMERGENCY" => DatabaseFileState.Emergency,
        "OFFLINE" => DatabaseFileState.Offline,
        "DEFUNCT" => DatabaseFileState.Defunct,
        _ => DatabaseFileState.Other,
    };
}
