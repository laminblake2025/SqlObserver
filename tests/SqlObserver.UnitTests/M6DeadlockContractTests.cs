using System.Text;
using System.Text.Json;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class M6DeadlockContractTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("61616161-6161-6161-6161-616161616161"));
    private static readonly ObservationTargetRevision Revision = new(1);
    private static readonly DateTimeOffset Occurred = new(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParserProducesOpaqueFingerprintAndAllowlistedParticipants()
    {
        const string xml = "<event name=\"xml_deadlock_report\"><data><value><deadlock><victim-list><victimProcess id=\"p1\" /></victim-list><process-list><process id=\"p1\" spid=\"51\" /><process id=\"p2\" spid=\"52\" /></process-list></deadlock></value></data></event>";
        DeadlockObservation value = DeadlockXmlParser.Parse(Target, Revision, Encoding.UTF8.GetBytes(xml), Occurred);
        Assert.Equal(64, value.Fingerprint.Value.Length);
        Assert.Equal([51, 52], value.Participants.Select(x => x.SessionId));
        Assert.True(value.Participants.Single(x => x.SessionId == 51).IsVictim);
        Assert.DoesNotContain("xml_deadlock_report", value.Fingerprint.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRejectsDtdAndDepthExhaustion()
    {
        const string dtd = "<!DOCTYPE event [<!ENTITY x SYSTEM 'file:///secret'>]><event>&x;</event>";
        Assert.ThrowsAny<Exception>(() => DeadlockXmlParser.Parse(Target, Revision, Encoding.UTF8.GetBytes(dtd), Occurred));
        string nested = string.Concat(Enumerable.Repeat("<a>", DeadlockXmlParser.MaximumDepth + 2)) + string.Concat(Enumerable.Repeat("</a>", DeadlockXmlParser.MaximumDepth + 2));
        Assert.Throws<InvalidDataException>(() => DeadlockXmlParser.Parse(Target, Revision, Encoding.UTF8.GetBytes(nested), Occurred));
    }

    [Fact]
    public void ParserMapsNestedLockOwnersToTypedRelations()
    {
        const string xml = "<event><data><value><deadlock><process-list><process id=\"p1\" spid=\"51\" /><process id=\"p2\" spid=\"52\" /></process-list><resource-list><keylock mode=\"X\"><owner-list><owner id=\"p1\" /></owner-list><waiter-list><waiter id=\"p2\" /></waiter-list></keylock></resource-list></deadlock></value></data></event>";
        DeadlockObservation value = DeadlockXmlParser.Parse(Target, Revision, Encoding.UTF8.GetBytes(xml), Occurred);
        DeadlockRelation relation = Assert.Single(value.Relations);
        Assert.Equal(51, relation.BlockerSessionId);
        Assert.Equal(52, relation.WaiterSessionId);
        Assert.Equal(DeadlockResourceCategory.Key, relation.ResourceCategory);
        Assert.Equal("X", relation.LockMode);
    }

    [Fact]
    public void ParserRetainsBoundedObservationAndMarksParticipantTruncation()
    {
        var builder = new StringBuilder("<event><data><value><deadlock><process-list>");
        for (int session = 1; session <= DeadlockObservation.MaximumParticipants + 1; session++)
            builder.Append("<process id=\"p").Append(session).Append("\" spid=\"").Append(session).Append("\" />");
        builder.Append("</process-list></deadlock></value></data></event>");
        DeadlockObservation value = DeadlockXmlParser.Parse(Target, Revision, Encoding.UTF8.GetBytes(builder.ToString()), Occurred);
        Assert.Equal(DeadlockObservation.MaximumParticipants, value.Participants.Count);
        Assert.True(value.ParseTruncated);
    }

    [Fact]
    public void ObservationBatchDeduplicatesFingerprintIdentity()
    {
        DeadlockObservation first = new(Target, Revision, new DeadlockFingerprint(new string('a', 64)), Occurred, [], []);
        DeadlockObservation second = new(Target, Revision, new DeadlockFingerprint(new string('a', 64)), Occurred.AddSeconds(1), [], []);
        Assert.Throws<ArgumentException>(() => new DeadlockObservationBatch([first, second]));
    }

    [Fact]
    public void LockModesUseClosedCanonicalAllowlist()
    {
        Assert.Equal("SCH_S", DeadlockLockModes.Normalize("Sch-S"));
        Assert.Equal("RANGES_U", DeadlockLockModes.Normalize("RangeS-U"));
        Assert.Equal("OTHER", DeadlockLockModes.Normalize("provider-secret"));
    }

    [Fact]
    public void ParserRejectsOversizedAndMalformedPayloads()
    {
        Assert.Throws<InvalidDataException>(() => DeadlockXmlParser.Parse(Target, Revision, new byte[DeadlockXmlParser.MaximumBytes + 1], Occurred));
        Assert.ThrowsAny<Exception>(() => DeadlockXmlParser.Parse(Target, Revision, Encoding.UTF8.GetBytes("<event><broken>"), Occurred));
    }

    [Fact]
    public void PaginationCursorBindsTargetAndRepositorySnapshot()
    {
        var cursor = new DeadlockPageCursor(Target, Occurred, Guid.Parse("11111111-1111-1111-1111-111111111111"), Occurred.AddMinutes(1), Occurred.AddHours(-1), Occurred.AddHours(1));
        Assert.Equal(Target, cursor.TargetId);
        Assert.Equal(Occurred.AddMinutes(1), cursor.SnapshotCollectedAtUtc);
        Assert.Equal(Occurred.AddHours(-1), cursor.FromUtc);
        Assert.Throws<ArgumentException>(() => new DeadlockPageCursor(Target, Occurred, Guid.Empty, Occurred, Occurred.AddHours(-1), Occurred.AddHours(1)));
    }

    [Fact]
    public void IdenticalFingerprintsAcrossTargetsHaveDistinctEventIdentities()
    {
        var other = new MonitoredInstanceId(Guid.Parse("62626262-6262-6262-6262-626262626262"));
        var first = new DeadlockObservation(Target, Revision, new DeadlockFingerprint(new string('f', 64)), Occurred, [], []);
        var second = new DeadlockObservation(other, Revision, first.Fingerprint, Occurred, [], []);
        Assert.NotEqual(first.EventId, second.EventId);
    }

    [Fact]
    public void EventIdentityMatchesNetworkOrderSqlFormulaVector()
    {
        var target = new MonitoredInstanceId(Guid.Parse("11111111-1111-4111-8111-111111111111"));
        var value = new DeadlockObservation(target, Revision, new DeadlockFingerprint(new string('f', 64)), Occurred, [], []);
        Assert.Equal(Guid.Parse("f321555e-329f-421b-485c-cf796aa0f475"), value.EventId);
    }

    [Fact]
    public void MaximumGraphSizeAccountingMatchesNormalizedUtf8Json()
    {
        DeadlockParticipant[] participants = Enumerable.Range(1, 128).Select(static session => new DeadlockParticipant(session, session == 1)).ToArray();
        DeadlockRelation[] relations = Enumerable.Range(0, 256).Select(static index => new DeadlockRelation(index / 128 + 1, index % 128 + 1, DeadlockResourceCategory.Key, "SCH_M")).ToArray();
        DeadlockObservation value = new(Target, Revision, new DeadlockFingerprint(new string('e', 64)), Occurred, participants, relations);
        string participantJson = JsonSerializer.Serialize(participants.Select(static item => new { sessionId = item.SessionId, victim = item.IsVictim }));
        string relationJson = JsonSerializer.Serialize(relations.Select(static item => new { blockerSessionId = item.BlockerSessionId, waiterSessionId = item.WaiterSessionId, resourceCategory = "key", lockMode = item.LockMode }));
        Assert.Equal(DeadlockObservation.FixedEstimatedBytes + Encoding.UTF8.GetByteCount(participantJson) + Encoding.UTF8.GetByteCount(relationJson), value.EstimatedSizeBytes);
    }
}
