using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Closed, bounded private checkpoint frames; raw provider text never enters errors.</summary>
internal static class DeadlockReadCheckpointCodec
{
    internal static DeadlockReadCheckpointResult Decode(BeginCollectorRunRequest request, DateTimeOffset readAtUtc,
        string json, string sourceDigest)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > 131_072) throw new InvalidDataException();
            CheckDigest(json, sourceDigest);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            Shape(root, "schemaVersion", "targetId", "targetRevision", "scheduleRevision", "runId", "checkpoint", "checkpointDigest", "resumeAllowed");
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || GuidValue(root, "targetId") != request.Work.TargetId.Value
                || root.GetProperty("targetRevision").GetInt64() != request.Work.TargetRevision.Value
                || root.GetProperty("scheduleRevision").GetInt64() != request.Work.ScheduleRevision.Value
                || GuidValue(root, "runId") != request.RunId.Value) throw new InvalidDataException();
            JsonElement saved = root.GetProperty("checkpoint");
            DeadlockReadCheckpoint? checkpoint = null;
            if (saved.ValueKind == JsonValueKind.Null)
            {
                if (root.GetProperty("checkpointDigest").ValueKind != JsonValueKind.Null) throw new InvalidDataException();
            }
            else
            {
                string digest = String(root, "checkpointDigest");
                CheckDigest(saved.GetRawText(), digest);
                Shape(saved, "schemaVersion", "targetId", "targetRevision", "scheduleRevision", "runId", "completedAtUtc",
                    "completionDigest", "sourceState", "sourceEpoch", "bookmark", "window");
                if (saved.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException();
                var target = new MonitoredInstanceId(GuidValue(saved, "targetId"));
                var revision = new ObservationTargetRevision(saved.GetProperty("targetRevision").GetInt64());
                XeReadIdentity? source = saved.GetProperty("sourceEpoch").ValueKind == JsonValueKind.Null ? null
                    : new(target, revision, String(saved, "sourceEpoch"));
                JsonElement position = saved.GetProperty("bookmark"), window = saved.GetProperty("window");
                checkpoint = new(target, revision, saved.GetProperty("scheduleRevision").GetInt64(),
                    new CollectorRunId(GuidValue(saved, "runId")), Time(saved, "completedAtUtc"),
                    new(String(saved, "completionDigest")), State(String(saved, "sourceState")), source,
                    position.ValueKind == JsonValueKind.Null ? null : Position(position),
                    window.ValueKind == JsonValueKind.Null ? null : Window(window, source), new(digest));
            }
            return new(request, readAtUtc, new(sourceDigest), checkpoint, root.GetProperty("resumeAllowed").GetBoolean());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException
            or FormatException or OverflowException or InvalidDataException)
        {
            throw new InvalidDataException("PostgreSQL returned an invalid deadlock checkpoint frame.");
        }
    }

    internal static string Encode(DeadlockReadSourceProof proof) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        expectedCheckpointDigest = proof.ExpectedCheckpointDigest?.Value,
        sourceState = State(proof.State),
        sourceEpoch = proof.Source?.SourceEpoch,
        readAtUtc = Format(proof.ReadAtUtc),
        retainedFromUtc = Format(proof.RetainedFromUtc),
        positions = proof.Positions.Select(p => new { generation = p.FileGeneration, rolloverIndex = p.RolloverIndex, bufferOffset = p.BufferOffset }),
        reachedEnd = proof.ReachedEnd,
        sourceLoss = proof.SourceLoss,
    });

    private static DeadlockReadWindow Window(JsonElement window, XeReadIdentity? source)
    {
        Shape(window, "schemaVersion", "targetId", "targetRevision", "sourceEpoch", "readAtUtc", "coveredFromUtc", "gapThroughUtc", "reason", "events");
        if (source is null || window.GetProperty("schemaVersion").GetInt32() != 1
            || GuidValue(window, "targetId") != source.TargetId.Value
            || window.GetProperty("targetRevision").GetInt64() != source.TargetRevision.Value
            || String(window, "sourceEpoch") != source.SourceEpoch) throw new InvalidDataException();
        JsonElement items = window.GetProperty("events");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > DeadlockReadWindowCalculator.MaximumEvents)
            throw new InvalidDataException();
        var events = new List<DeadlockWindowEvent>();
        foreach (JsonElement item in items.EnumerateArray())
        {
            Shape(item, "eventId", "occurredAtUtc", "parseTruncated");
            events.Add(new(GuidValue(item, "eventId"), Time(item, "occurredAtUtc"), item.GetProperty("parseTruncated").GetBoolean()));
        }
        DeadlockReadWindowReason reason = String(window, "reason") switch
        {
            "current" => DeadlockReadWindowReason.Current,
            "catching_up" => DeadlockReadWindowReason.CatchingUp,
            "source_incomplete" => DeadlockReadWindowReason.SourceIncomplete,
            "coverage_gap" => DeadlockReadWindowReason.CoverageGap,
            "window_overflow" => DeadlockReadWindowReason.WindowOverflow,
            _ => throw new InvalidDataException(),
        };
        return DeadlockReadWindow.Restore(source, Time(window, "readAtUtc"), Time(window, "coveredFromUtc"),
            window.GetProperty("gapThroughUtc").ValueKind == JsonValueKind.Null ? null : Time(window, "gapThroughUtc"), events, reason);
    }

    private static XeFilePosition Position(JsonElement position)
    {
        Shape(position, "generation", "rolloverIndex", "bufferOffset");
        return new(position.GetProperty("generation").GetInt64(), position.GetProperty("rolloverIndex").GetInt32(), position.GetProperty("bufferOffset").GetInt64());
    }

    private static void Shape(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException();
        if (seen.Count != names.Length) throw new InvalidDataException();
    }

    private static void CheckDigest(string json, string digest)
    {
        _ = new DeadlockReadDigest(digest);
        if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) != digest) throw new InvalidDataException();
    }

    private static string String(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException();
    private static Guid GuidValue(JsonElement value, string name)
    {
        string text = String(value, name);
        if (!Guid.TryParseExact(text, "D", out Guid guid) || guid == Guid.Empty || guid.ToString("D") != text) throw new InvalidDataException();
        return guid;
    }
    private static DateTimeOffset Time(JsonElement value, string name)
    {
        string text = String(value, name);
        if (!DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset time))
            throw new InvalidDataException();
        return time;
    }
    private static string? Format(DateTimeOffset? time) => time?.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    private static DeadlockReadSourceState State(string state) => state switch
    {
        "ready" => DeadlockReadSourceState.Ready,
        "missing" => DeadlockReadSourceState.Missing,
        "config_invalid" => DeadlockReadSourceState.ConfigurationInvalid,
        "reset_required" => DeadlockReadSourceState.ResetRequired,
        "failed" => DeadlockReadSourceState.Failed,
        _ => throw new InvalidDataException(),
    };
    private static string State(DeadlockReadSourceState state) => state switch
    {
        DeadlockReadSourceState.Ready => "ready",
        DeadlockReadSourceState.Missing => "missing",
        DeadlockReadSourceState.ConfigurationInvalid => "config_invalid",
        DeadlockReadSourceState.ResetRequired => "reset_required",
        DeadlockReadSourceState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
