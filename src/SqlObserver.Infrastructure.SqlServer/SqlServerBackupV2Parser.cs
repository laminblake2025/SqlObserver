using System.Globalization;
using System.Text;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Backups;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;

namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Sequential parsing of the pinned inventory/five-slot source, outside the active v1 runtime.</summary>
public sealed record BackupV2ParseResult(BackupV2ReadSnapshot Snapshot, int ResponseBytes, CollectorLossEvidence Loss);

public static class SqlServerBackupV2Parser
{
    public const int MaximumRows = 3072;
    public const int MaximumDatabases = 512;
    public const int MaximumResponseBytes = 4_194_304;

    public static async ValueTask<BackupV2ParseResult> ParseAsync(CollectorExecutionRequest request,
        IOperationalHealthRowReader reader, IdentityFingerprintKey key, DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken, BackupHistoricalZoneQualification? qualification = null,
        int maximumRows = MaximumRows, int maximumDatabases = MaximumDatabases,
        int maximumResponseBytes = MaximumResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(reader); ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumRows is < 1 or > MaximumRows || maximumDatabases is < 1 or > MaximumDatabases
            || maximumResponseBytes is < BackupV2ReadSnapshot.EnvelopeEstimatedBytes or > MaximumResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        if (receivedAtUtc.Offset != TimeSpan.Zero || receivedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
            throw new ArgumentException("Backup receipt time must be UTC and microsecond aligned.", nameof(receivedAtUtc));
        if (qualification is not null && (qualification.TargetId != request.TargetId || qualification.TargetRevision != request.TargetRevision))
            throw new ArgumentException("Historical zone qualification belongs to a different target/revision.", nameof(qualification));
        if (reader.FieldCount != 29) throw new InvalidDataException("Backup v2 source requires exactly 29 fields.");

        var run = new BackupV2RunIdentity(request.TargetId, request.TargetRevision, request.RunId);
        var databases = new List<BackupV2DatabaseRead>();
        var samples = new List<BackupV2SampleRead>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        BackupV2DatabaseContext? current = null;
        (int Database, int Kind, int Backup, bool CopyOnly)? previous = null;
        DateTimeOffset? sourceObserved = null;
        string? sourceZone = null;
        DateTime? historyLowerBound = null;
        int rows = 0, databaseCount = 0, responseBytes = BackupV2ReadSnapshot.EnvelopeEstimatedBytes,
            outputBytes = BackupV2ReadSnapshot.EnvelopeEstimatedBytes;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows++;
            if (rows > maximumRows) return Withheld(CollectorLossKind.SourceRowLimit);
            int rowKind = RequiredInt(0), catalogId = RequiredInt(1);
            if (rowKind is not (0 or 1) || catalogId is < 1 or 2)
                throw new InvalidDataException("Backup source stream or ephemeral association is invalid.");
            string? database = Digest(2, "database"), family = Digest(3, "family"), fork = Digest(4, "fork");
            string? group = Digest(5, "group"), agDatabase = Digest(6, "ag-database"), replica = Digest(7, "replica");
            bool identityKnown = RequiredBoolean(8);
            string? recovery = Text(9, 60), databaseState = Text(10, 60);
            short scopeValue = RequiredShort(11);
            string? role = Text(12, 60);
            bool? preferred = Boolean(13);
            int? backupValue = Integer(14);
            bool? copyOnly = Boolean(15);
            DateTime? start = LocalTime(16), finish = LocalTime(17);
            short? offset = reader.IsDBNull(18) ? null : reader.GetInt16(18);
            long? size = reader.IsDBNull(19) ? null : reader.GetInt64(19);
            bool? checksum = Boolean(20), damaged = Boolean(21), metadata = Boolean(22);
            string? firstFork = Digest(23, "fork"), lastFork = Digest(24, "fork");
            DateTime sourceUtc = RequiredTime(25);
            if (sourceUtc.Kind != DateTimeKind.Unspecified || sourceUtc.Ticks % TimeSpan.TicksPerMicrosecond != 0)
                throw new InvalidDataException("Backup source clock requires the SQL UTC datetime2(6) shape.");
            var sourceTime = new DateTimeOffset(sourceUtc, TimeSpan.Zero);
            string? zoneId = Text(26, 128);
            DateTime lowerBound = LocalTime(27) ?? throw new InvalidDataException("Backup source lookback is missing.");
            bool unclassified = RequiredBoolean(28);

            var model = recovery switch
            {
                null => BackupRecoveryModel.Unknown, "SIMPLE" => BackupRecoveryModel.Simple,
                "FULL" => BackupRecoveryModel.Full, "BULK_LOGGED" => BackupRecoveryModel.BulkLogged,
                _ => throw new InvalidDataException("Backup recovery model is outside its closed contract."),
            };
            if (databaseState is not (null or "ONLINE" or "RESTORING" or "RECOVERING" or "RECOVERY_PENDING"
                or "SUSPECT" or "EMERGENCY" or "OFFLINE") || role is not (null or "PRIMARY" or "SECONDARY" or "RESOLVING")
                || scopeValue is < 0 or > 2 || identityKnown && database is null
                || size < 0 || offset is { } value && (value is < -720 or > 720 || value % 15 != 0))
                throw new InvalidDataException("Backup metadata is outside its closed contract.");
            var scope = (BackupFreshnessScope)scopeValue;
            bool hasAgIdentity = group is not null || agDatabase is not null || replica is not null;
            if (scope != BackupFreshnessScope.AvailabilityGroupLocalOnly && (hasAgIdentity || role is not null || preferred.HasValue)
                || scope == BackupFreshnessScope.AvailabilityGroupLocalOnly && !hasAgIdentity
                || preferred.HasValue && (group is null || agDatabase is null || replica is null))
                throw new InvalidDataException("Backup AG context is inconsistent.");

            var context = new BackupV2DatabaseContext(database, family, fork, group, agDatabase, replica,
                identityKnown, model, databaseState, scope, role, preferred, sourceTime, zoneId, lowerBound, unclassified);
            if (sourceObserved.HasValue && (sourceObserved != sourceTime || sourceZone != zoneId || historyLowerBound != lowerBound))
                throw new InvalidDataException("Backup rows do not belong to one source scan.");
            sourceObserved = sourceTime; sourceZone = zoneId; historyLowerBound = lowerBound;
            var sortKey = (catalogId, rowKind, backupValue ?? 0, copyOnly ?? false);
            if (previous.HasValue && previous.Value.CompareTo(sortKey) >= 0)
                throw new InvalidDataException("Backup streams or slots are duplicated or unordered.");

            if (rowKind == 0)
            {
                if (backupValue.HasValue || copyOnly.HasValue || start.HasValue || finish.HasValue || offset.HasValue || size.HasValue
                    || checksum.HasValue || damaged.HasValue || metadata.HasValue || firstFork is not null || lastFork is not null)
                    throw new InvalidDataException("Backup inventory row carries sample data.");
                CompleteDatabase();
                if (++databaseCount > maximumDatabases) return Withheld(CollectorLossKind.SourceRowLimit);
                if (identityKnown && !identities.Add(database!)) throw new InvalidDataException("Backup database identity is duplicated.");
                current = context; outputBytes += BackupV2ReadSnapshot.DatabaseEstimatedBytes;
            }
            else
            {
                if (current != context || previous?.Database != catalogId || !identityKnown || family is null || fork is null
                    || backupValue is not (1 or 2 or 3) || !copyOnly.HasValue || backupValue == 2 && copyOnly.Value
                    || lastFork != fork || metadata == true && firstFork is null || samples.Count >= BackupFreshnessEvidence.MaximumSamples
                    || finish.HasValue && finish < lowerBound || !finish.HasValue && start.HasValue && start < lowerBound)
                    throw new InvalidDataException("Backup sample association, slot or lineage is invalid.");
                var resolution = BackupHistoricalTimeResolver.Resolve(request.TargetId, request.TargetRevision,
                    start, finish, offset, zoneId, qualification);
                var freshness = new BackupFreshnessSample((BackupKind)backupValue.Value, copyOnly.Value,
                    resolution.FinishUtc, finish, checksum, damaged, metadata);
                samples.Add(new BackupV2SampleRead(freshness, start, offset, size, firstFork, lastFork!, resolution));
                outputBytes += BackupV2ReadSnapshot.SampleEstimatedBytes;
            }
            previous = sortKey;
            responseBytes += 384 + (recovery?.Length ?? 0) * 2 + (databaseState?.Length ?? 0) * 2
                + (role?.Length ?? 0) * 2 + (zoneId?.Length ?? 0) * 2;
            if (responseBytes > maximumResponseBytes || outputBytes > maximumResponseBytes)
                return Withheld(CollectorLossKind.ResponseByteLimit);
        }
        cancellationToken.ThrowIfCancellationRequested();
        CompleteDatabase();
        bool incomplete = databaseCount == 0 || databases.Any(item => item.Coverage != BackupFreshnessCoverage.Complete);
        var snapshot = Snapshot(databases, incomplete ? BackupFreshnessCoverage.Partial : BackupFreshnessCoverage.Complete);
        return new(snapshot, Math.Max(responseBytes, snapshot.EstimatedSizeBytes), incomplete
            ? new CollectorLossEvidence(CollectorLossKind.VisibilityIncomplete, 1, false) : CollectorLossEvidence.None);

        void CompleteDatabase()
        {
            if (current is null) return;
            databases.Add(new BackupV2DatabaseRead(current, samples, run,
                current.Complete ? BackupFreshnessCoverage.Complete : BackupFreshnessCoverage.Partial));
            samples.Clear(); current = null;
        }

        BackupV2ReadSnapshot Snapshot(IReadOnlyList<BackupV2DatabaseRead> output, BackupFreshnessCoverage coverage) =>
            new(run, receivedAtUtc, sourceObserved, sourceZone, historyLowerBound, output, rows, coverage);

        BackupV2ParseResult Withheld(CollectorLossKind kind)
        {
            // Publish no prefix: dropping a later slot cannot make an earlier full/log result look complete.
            var snapshot = Snapshot([], BackupFreshnessCoverage.Truncated);
            return new(snapshot, Math.Max(Math.Min(responseBytes, maximumResponseBytes), snapshot.EstimatedSizeBytes),
                new CollectorLossEvidence(kind, rows, false));
        }

        string? Digest(int ordinal, string identityClass)
        {
            if (reader.IsDBNull(ordinal)) return null;
            byte[] bytes = reader.GetBinary(ordinal);
            if (bytes.Length != 32) throw new InvalidDataException("Backup source identity digest must be SHA-256.");
            return ReplicationIdentityFingerprint.FromTypedIdentity("backup-v2-" + identityClass,
                Encoding.UTF8.GetBytes(string.Concat(request.TargetId.Value.ToString("D"), "|",
                    request.TargetRevision.Value.ToString(CultureInfo.InvariantCulture), "|", Convert.ToHexString(bytes))), key);
        }

        string? Text(int ordinal, int maximumLength)
        {
            if (reader.IsDBNull(ordinal)) return null;
            string text = reader.GetString(ordinal);
            return text.Length is > 0 && text.Length <= maximumLength && !text.Any(char.IsControl)
                ? text : throw new InvalidDataException("Backup source token exceeds its bounded shape.");
        }

        DateTime? LocalTime(int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            DateTime value = reader.GetDateTime(ordinal);
            if (value.Kind != DateTimeKind.Unspecified || value.Year < 1753)
                throw new InvalidDataException("Backup local time requires the offset-free SQL datetime shape.");
            return new DateTime(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, DateTimeKind.Unspecified);
        }

        int RequiredInt(int ordinal) => Integer(ordinal) ?? throw new InvalidDataException("Backup source integer is missing.");
        int? Integer(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
        bool RequiredBoolean(int ordinal) => Boolean(ordinal) ?? throw new InvalidDataException("Backup source marker is missing.");
        bool? Boolean(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
        short RequiredShort(int ordinal) => reader.IsDBNull(ordinal) ? throw new InvalidDataException("Backup scope is missing.") : reader.GetInt16(ordinal);
        DateTime RequiredTime(int ordinal) => reader.IsDBNull(ordinal) ? throw new InvalidDataException("Backup source clock is missing.") : reader.GetDateTime(ordinal);
    }
}
