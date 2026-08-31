using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.Domain.Collection;

/// <summary>Allowlisted deadlock resource categories. Provider text is never represented.</summary>
public enum DeadlockResourceCategory { Key = 1, Page = 2, ObjectLock = 3, Metadata = 4, Exchange = 5, Other = 6 }
public enum DeadlockRelationKind { BlockerWaiter = 1 }
public static class DeadlockLockModes
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "NL", "S", "U", "X", "IS", "IU", "IX", "SIU", "SIX", "UIX", "SCH_S", "SCH_M", "BU", "RANGES_S", "RANGES_U", "RANGEI_N", "RANGEI_S", "RANGEX_X", "OTHER" };
    public static string Normalize(string? value)
    {
        string token = (value ?? string.Empty).Trim().ToUpperInvariant().Replace('-', '_');
        return token.Length <= DeadlockRelation.MaximumTokenLength && Allowed.Contains(token) ? token : "OTHER";
    }
}

public sealed record DeadlockFingerprint
{
    public const int HexLength = 64;
    public DeadlockFingerprint(string value)
    {
        if (value is null || value.Length != HexLength || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("A deadlock fingerprint must be a SHA-256 hex digest.", nameof(value));
        Value = value.ToLowerInvariant();
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record DeadlockParticipant
{
    public const int MaximumSessionId = 32767;
    public DeadlockParticipant(int sessionId, bool victim)
    {
        if (sessionId is <= 0 or > MaximumSessionId) throw new ArgumentOutOfRangeException(nameof(sessionId));
        SessionId = sessionId; IsVictim = victim;
    }
    public int SessionId { get; }
    public bool IsVictim { get; }
}

public sealed record DeadlockRelation
{
    public const int MaximumTokenLength = 32;
    public DeadlockRelation(int blockerSessionId, int waiterSessionId, DeadlockResourceCategory resourceCategory, string lockMode)
    {
        if (blockerSessionId is <= 0 or > DeadlockParticipant.MaximumSessionId) throw new ArgumentOutOfRangeException(nameof(blockerSessionId));
        if (waiterSessionId is <= 0 or > DeadlockParticipant.MaximumSessionId) throw new ArgumentOutOfRangeException(nameof(waiterSessionId));
        if (!Enum.IsDefined(resourceCategory)) throw new ArgumentOutOfRangeException(nameof(resourceCategory));
        BlockerSessionId = blockerSessionId; WaiterSessionId = waiterSessionId; ResourceCategory = resourceCategory; LockMode = DeadlockLockModes.Normalize(lockMode);
    }
    public int BlockerSessionId { get; }
    public int WaiterSessionId { get; }
    public DeadlockResourceCategory ResourceCategory { get; }
    public string LockMode { get; }
}

/// <summary>Safe, typed deadlock evidence. Raw event XML is intentionally discarded.</summary>
public sealed class DeadlockObservation : IIngestionRecord
{
    public const int MaximumParticipants = 128;
    public const int MaximumRelations = 256;
    public const int FixedEstimatedBytes = 192;
    public DeadlockObservation(MonitoredInstanceId targetId, ObservationTargetRevision targetRevision, DeadlockFingerprint fingerprint, DateTimeOffset occurredAtUtc, IReadOnlyList<DeadlockParticipant> participants, IReadOnlyList<DeadlockRelation> relations, bool parseTruncated = false)
    {
        TargetId = targetId ?? throw new ArgumentNullException(nameof(targetId)); TargetRevision = targetRevision ?? throw new ArgumentNullException(nameof(targetRevision)); Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        if (participants is null || participants.Count > MaximumParticipants) throw new ArgumentOutOfRangeException(nameof(participants));
        if (relations is null || relations.Count > MaximumRelations) throw new ArgumentOutOfRangeException(nameof(relations));
        var p = participants.Select(x => x ?? throw new ArgumentException("Null participant", nameof(participants))).Distinct().OrderBy(x => x.SessionId).ToArray();
        var r = relations.Select(x => x ?? throw new ArgumentException("Null relation", nameof(relations))).Distinct().OrderBy(x => x.BlockerSessionId).ThenBy(x => x.WaiterSessionId).ThenBy(x => x.ResourceCategory).ThenBy(x => x.LockMode, StringComparer.Ordinal).ToArray();
        Participants = Array.AsReadOnly(p); Relations = Array.AsReadOnly(r); OccurredAtUtc = RequireUtc(occurredAtUtc); ParseTruncated = parseTruncated;
        string participantJson = JsonSerializer.Serialize(p.Select(static item => new { sessionId = item.SessionId, victim = item.IsVictim }));
        string relationJson = JsonSerializer.Serialize(r.Select(static item => new { blockerSessionId = item.BlockerSessionId, waiterSessionId = item.WaiterSessionId, resourceCategory = ResourceToken(item.ResourceCategory), lockMode = item.LockMode }));
        EstimatedSizeBytes = checked(FixedEstimatedBytes + Encoding.UTF8.GetByteCount(participantJson) + Encoding.UTF8.GetByteCount(relationJson));
    }
    public MonitoredInstanceId TargetId { get; }
    public ObservationTargetRevision TargetRevision { get; }
    public DeadlockFingerprint Fingerprint { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public IReadOnlyList<DeadlockParticipant> Participants { get; }
    public IReadOnlyList<DeadlockRelation> Relations { get; }
    public bool ParseTruncated { get; }
    public int EstimatedSizeBytes { get; }
    /// <summary>Stable opaque event identity derived from the content fingerprint.</summary>
    public Guid EventId
    {
        get => ComputeEventId(TargetId, Fingerprint.Value);
    }
    public int ParticipantCount => Participants.Count;
    public int RelationCount => Relations.Count;
    private static DateTimeOffset RequireUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("Deadlock timestamps must be UTC.", nameof(value));
    public static DeadlockFingerprint FingerprintOf(ReadOnlySpan<byte> eventXml) => new(Convert.ToHexString(SHA256.HashData(eventXml)).ToLowerInvariant());
    public static Guid ComputeEventId(MonitoredInstanceId targetId, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(targetId);
        var digest = new DeadlockFingerprint(fingerprint);
        byte[] identity = new byte[48];
        targetId.Value.TryWriteBytes(identity.AsSpan(0, 16), bigEndian: true, out _);
        Convert.FromHexString(digest.Value).CopyTo(identity, 16);
        return new Guid(SHA256.HashData(identity).AsSpan(0, 16), bigEndian: true);
    }
    private static string ResourceToken(DeadlockResourceCategory category) => category switch { DeadlockResourceCategory.Key => "key", DeadlockResourceCategory.Page => "page", DeadlockResourceCategory.ObjectLock => "object_lock", DeadlockResourceCategory.Metadata => "metadata", DeadlockResourceCategory.Exchange => "exchange", _ => "other" };
}

public sealed class DeadlockObservationBatch : ObservationBatch<DeadlockObservation>
{
    public const int MaximumItems = 256;
    public DeadlockObservationBatch(IReadOnlyList<DeadlockObservation> items) : base(items, MaximumItems, x => x.Fingerprint.Value, "deadlock") { }
}

/// <summary>Safe parser for hostile system_health event XML with explicit quotas.</summary>
public static class DeadlockXmlParser
{
    public const int MaximumBytes = 512 * 1024;
    public const int MaximumDepth = 32;
    public const int MaximumNodes = 4096;
    private sealed class ResourceCapture
    {
        public ResourceCapture(int depth, DeadlockResourceCategory category, string mode) { Depth = depth; Category = category; Mode = mode; }
        public int Depth { get; }
        public DeadlockResourceCategory Category { get; }
        public string Mode { get; }
        public List<string> Owners { get; } = [];
        public List<string> Waiters { get; } = [];
    }
    public static DeadlockObservation Parse(MonitoredInstanceId targetId, ObservationTargetRevision revision, ReadOnlySpan<byte> xml, DateTimeOffset fallbackUtc, CancellationToken cancellationToken = default)
    {
        if (xml.Length == 0 || xml.Length > MaximumBytes) throw new InvalidDataException("Deadlock XML exceeds the bounded input limit.");
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(xml.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes, MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreWhitespace = true });
        var participants = new List<DeadlockParticipant>(); var relations = new List<DeadlockRelation>(); var processToSession = new Dictionary<string, int>(StringComparer.Ordinal); var victims = new HashSet<string>(StringComparer.Ordinal); var resources = new Stack<ResourceCapture>();
        int depth = 0, nodes = 0; bool parseTruncated = false; DateTimeOffset occurred = fallbackUtc;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > MaximumNodes) throw new InvalidDataException("Deadlock XML node limit exceeded.");
            depth = reader.Depth; if (depth > MaximumDepth) throw new InvalidDataException("Deadlock XML depth limit exceeded.");
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName.Equals("victimProcess", StringComparison.Ordinal)) { string? id = reader.GetAttribute("id"); if (id is not null && id.Length <= 64) victims.Add(id); }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName.Equals("process", StringComparison.Ordinal))
            {
                string? id = reader.GetAttribute("id"); string? spid = reader.GetAttribute("spid");
                if (id is not null && id.Length <= 64 && spid is not null && int.TryParse(spid, out int sid) && sid is > 0 and <= 32767) { processToSession[id] = sid; if (participants.Count < DeadlockObservation.MaximumParticipants) participants.Add(new DeadlockParticipant(sid, victims.Contains(id))); else parseTruncated = true; }
            }
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "keylock" or "pagelock" or "objectlock" or "metadatalock" or "exchangeEvent")
            {
                DeadlockResourceCategory category = reader.LocalName switch { "keylock" => DeadlockResourceCategory.Key, "pagelock" => DeadlockResourceCategory.Page, "objectlock" => DeadlockResourceCategory.ObjectLock, "metadatalock" => DeadlockResourceCategory.Metadata, "exchangeEvent" => DeadlockResourceCategory.Exchange, _ => DeadlockResourceCategory.Other };
                string mode = reader.GetAttribute("mode") ?? "OTHER";
                var capture = new ResourceCapture(reader.Depth, category, mode); string? owner = reader.GetAttribute("ownerId"); string? waiter = reader.GetAttribute("waiterId"); if (owner is not null && waiter is not null) { capture.Owners.Add(owner); capture.Waiters.Add(waiter); }
                if (!reader.IsEmptyElement) resources.Push(capture); else AddRelations(capture, processToSession, relations, ref parseTruncated);
            }
            else if (reader.NodeType == XmlNodeType.Element && (reader.LocalName is "owner" or "waiter") && resources.Count > 0)
            {
                string? id = reader.GetAttribute("id"); if (id is not null && id.Length <= 64) { ResourceCapture capture = resources.Peek(); List<string> ids = reader.LocalName == "owner" ? capture.Owners : capture.Waiters; if (ids.Count < DeadlockObservation.MaximumRelations) ids.Add(id); else parseTruncated = true; }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && resources.Count > 0 && reader.Depth == resources.Peek().Depth)
            {
                ResourceCapture capture = resources.Pop(); AddRelations(capture, processToSession, relations, ref parseTruncated);
            }
            if ((nodes & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
        }
        if (occurred.Offset != TimeSpan.Zero) occurred = occurred.ToUniversalTime();
        while (resources.Count > 0) AddRelations(resources.Pop(), processToSession, relations, ref parseTruncated);
        participants = participants.GroupBy(static participant => participant.SessionId).Select(static group => new DeadlockParticipant(group.Key, group.Any(static participant => participant.IsVictim))).ToList();
        return new DeadlockObservation(targetId, revision, DeadlockObservation.FingerprintOf(xml), occurred, participants, relations, parseTruncated);
    }

    private static void AddRelations(ResourceCapture capture, Dictionary<string, int> processToSession, List<DeadlockRelation> relations, ref bool parseTruncated)
    {
        foreach (string owner in capture.Owners)
            foreach (string waiter in capture.Waiters)
            {
                if (relations.Count >= DeadlockObservation.MaximumRelations) { parseTruncated = true; return; }
                if (processToSession.TryGetValue(owner, out int blocker) && processToSession.TryGetValue(waiter, out int blocked)) relations.Add(new DeadlockRelation(blocker, blocked, capture.Category, capture.Mode));
            }
    }
}
