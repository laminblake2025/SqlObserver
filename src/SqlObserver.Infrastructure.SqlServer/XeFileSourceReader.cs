using System.Data.Common;
using System.Xml;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.SqlServer;

internal enum XeFileSourceState { Ready = 1, Missing = 2, ConfigurationInvalid = 3, ResetRequired = 4 }
internal enum XeFileReadStop { None = 0, MetadataLimit = 1, DeadlockLimit = 2, ResponseLimit = 3 }

internal sealed class XeFileReadLimits
{
    internal XeFileReadLimits(int metadataRows = 8_192, int deadlocks = 256, int responseBytes = 1_048_576)
    {
        if (metadataRows is < 1 or > 8_192 || deadlocks is < 1 or > 256 || responseBytes is < 4_096 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(metadataRows), "XE read limits exceed the bounded source contract.");
        // Reserve the header and metadata probe before allocating XML bytes.
        MetadataRows = Math.Min(metadataRows, (responseBytes - XeFileSourceReader.HeaderBytes) / XeFileSourceReader.MetadataBytes - 1);
        Deadlocks = deadlocks;
        ResponseBytes = responseBytes;
    }
    internal int MetadataRows { get; }
    internal int Deadlocks { get; }
    internal int ResponseBytes { get; }
}

internal sealed record XeFileSourcePage(XeFileSourceState State, XeReadIdentity? Source,
    DateTimeOffset ReadAtUtc, DateTimeOffset? RetainedFromUtc, XeFileReadProgress? Progress,
    IReadOnlyList<XeFilePosition> Positions, DeadlockObservationBatch Deadlocks, int ResponseBytes, int RejectedDeadlocks, bool TimingGap, XeFileReadStop Stop)
{
    internal bool ReachedEnd => State == XeFileSourceState.Ready && Progress?.Kind == XeFileReadProgressKind.CaughtUp;
    internal bool SourceLoss => RejectedDeadlocks > 0 || TimingGap;
    public override string ToString() => "XE file source page";
}

/// <summary>Consumes only bounded numeric metadata and safe deadlock projections.</summary>
internal static class XeFileSourceReader
{
    internal const int HeaderBytes = 128;
    internal const int MetadataBytes = 64;
    internal const long MaximumOffset = 101L * 1_048_576;

    internal static async Task<XeFileSourcePage> ReadAsync(DbDataReader reader, MonitoredInstanceId target,
        ObservationTargetRevision revision, XeReadBookmark? previous, XeFileReadLimits limits, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(limits);
        if (previous is not null && (previous.Identity.TargetId != target || previous.Identity.TargetRevision != revision))
            throw new ArgumentException("XE source read requires the exact bookmark target revision.", nameof(previous));
        if (reader.FieldCount != 5 || !await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("XE source omitted its reviewed status header.");
        XeFileSourceState state = reader.GetString(0) switch
        {
            "ready" => XeFileSourceState.Ready,
            "missing" => XeFileSourceState.Missing,
            "config_invalid" => XeFileSourceState.ConfigurationInvalid,
            "reset_required" => XeFileSourceState.ResetRequired,
            _ => throw new InvalidDataException("XE source returned an unsupported status."),
        };
        XeReadIdentity? source = reader.IsDBNull(1) ? null : new(target, revision, reader.GetString(1));
        DateTimeOffset readAt = Utc(reader.GetDateTime(2));
        DateTimeOffset? retainedFrom = reader.IsDBNull(3) ? null : Utc(reader.GetDateTime(3));
        bool hasMore = reader.GetBoolean(4);
        if (await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("XE source returned more than one status header.");
        bool hasRows = await reader.NextResultAsync(token).ConfigureAwait(false);
        if (state != XeFileSourceState.Ready)
        {
            if (hasRows || retainedFrom is not null || hasMore || state == XeFileSourceState.ResetRequired && source is null)
                throw new InvalidDataException("Unavailable XE source returned contradictory evidence.");
            return new(state, source, readAt, null, null, Array.AsReadOnly(Array.Empty<XeFilePosition>()), new([]), HeaderBytes, 0, false, XeFileReadStop.None);
        }
        if (source is null || retainedFrom is null || retainedFrom > readAt || !hasRows || reader.FieldCount != 8)
            throw new InvalidDataException("Ready XE source omitted its bounded metadata result.");
        if (previous is not null && previous.Identity != source)
            throw new InvalidDataException("Ready XE source changed its bookmark incarnation.");

        var positions = new List<XeFilePosition>();
        var events = new Dictionary<string, DeadlockObservation>(StringComparer.Ordinal);
        int responseBytes = HeaderBytes, deadlockRows = 0, rejected = 0;
        bool timingGap = false, ended = false;
        XeFileReadStop stop = XeFileReadStop.None;
        while (true)
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) { ended = true; break; }
            var position = new XeFilePosition(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt64(2));
            if (position.BufferOffset > MaximumOffset)
                throw new InvalidDataException("XE source buffer offset exceeds the file bound.");
            // A byte stop leaves the previously observed final buffer incomplete.
            // Every retained witness, including the metadata probe, is accounted.
            if (responseBytes + MetadataBytes > limits.ResponseBytes) { stop = XeFileReadStop.ResponseLimit; break; }
            positions.Add(position);
            responseBytes += MetadataBytes;
            if (positions.Count > limits.MetadataRows) { stop = XeFileReadStop.MetadataLimit; break; }
            DateTimeOffset occurred = Utc(reader.GetDateTime(3));
            bool isDeadlock = reader.GetBoolean(4), oversized = reader.GetBoolean(5), quota = reader.GetBoolean(6);
            bool missingXml = reader.IsDBNull(7);
            if (occurred > readAt) timingGap = true;
            if (!isDeadlock)
            {
                if (oversized || quota || !missingXml) throw new InvalidDataException("Non-deadlock metadata exposed event contents.");
                continue;
            }
            if (++deadlockRows > limits.Deadlocks) { stop = XeFileReadStop.DeadlockLimit; break; }
            if (quota) { stop = XeFileReadStop.ResponseLimit; break; }
            if (oversized)
            {
                if (!missingXml) throw new InvalidDataException("Oversized XE evidence exposed its event contents.");
                rejected++;
                continue;
            }
            if (missingXml) throw new InvalidDataException("Deadlock source omitted its reviewed XML or loss sentinel.");
            using TextReader text = reader.GetTextReader(7);
            SqlServerDeadlockCollector.BoundedXmlRead xml = await SqlServerDeadlockCollector.ReadBoundedXmlAsync(text, token).ConfigureAwait(false);
            if (responseBytes + xml.Utf8Bytes + MetadataBytes > limits.ResponseBytes) { stop = XeFileReadStop.ResponseLimit; break; }
            responseBytes += xml.Utf8Bytes;
            if (xml.Oversized || occurred > readAt) { rejected++; continue; }
            try
            {
                DeadlockObservation item = DeadlockXmlParser.Parse(target, revision,
                    System.Text.Encoding.UTF8.GetBytes(xml.Text), occurred, token);
                if (item.OccurredAtUtc > readAt) { timingGap = true; rejected++; continue; }
                if (item.ParseTruncated) rejected++;
                if (events.TryGetValue(item.Fingerprint.Value, out DeadlockObservation? existing) &&
                    (existing.OccurredAtUtc != item.OccurredAtUtc || existing.ParseTruncated != item.ParseTruncated))
                    throw new InvalidDataException("Repeated deadlock identity has conflicting evidence.");
                events[item.Fingerprint.Value] = item;
            }
            catch (Exception exception) when (exception is InvalidDataException or XmlException)
            {
                rejected++; // Explicit source loss, never provider text or raw XML.
            }
        }
        if (ended && (positions.Count > limits.MetadataRows || hasMore) || !hasMore && stop == XeFileReadStop.MetadataLimit)
            throw new InvalidDataException("XE source contradicted its metadata cap.");
        if (ended && await reader.NextResultAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("XE source returned an unreviewed result set.");
        XeFileReadProgress progress = XeFileReadProgressCalculator.Compare(previous, source, positions, ended && !hasMore && stop == XeFileReadStop.None);
        if (progress.Kind == XeFileReadProgressKind.ResetRequired)
            return new(XeFileSourceState.ResetRequired, source, readAt, null, null, Array.AsReadOnly(Array.Empty<XeFilePosition>()), new([]), responseBytes, rejected, timingGap, stop);
        return new(state, source, readAt, retainedFrom, progress, Array.AsReadOnly(positions.ToArray()), new(events.Values.ToArray()), responseBytes, rejected, timingGap, stop);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
