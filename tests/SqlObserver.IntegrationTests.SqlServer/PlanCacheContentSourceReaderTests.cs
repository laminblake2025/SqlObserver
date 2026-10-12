using System.Data;
using System.Security.Cryptography;
using System.Text;
using SqlObserver.Domain.SensitiveData;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

public sealed class PlanCacheContentSourceReaderTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly ObservationTargetRevision Revision = new(2);
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    [Fact]
    public async Task ProtectsExactStatementContentAndClearsTemporaryBuffersWithoutChangingCounters()
    {
        var protector = new Protector();
        using var reader = Results([Content('a', "select '😀';", "<ShowPlanXML><StmtSimple /></ShowPlanXML>")]);
        var result = await Read(reader, protector);
        var item = Assert.Single(result.Content);
        Assert.Equal("protected", item.TextState); Assert.Equal("protected", item.PlanState);
        Assert.Equal("select '😀';", Decode(item.Text!));
        Assert.Equal("<ShowPlanXML><StmtSimple /></ShowPlanXML>", Decode(item.Plan!));
        Assert.Equal(Target, result.Counters.TargetId); Assert.Equal(3L, item.Statement.Counters.Executions);
        Assert.Equal(item.Statement, Assert.Single(result.Counters.Rows));
        Assert.All(protector.Inputs, input => Assert.All(input.ToArray(), value => Assert.Equal((byte)0, value)));
        Assert.All(protector.Targets, target => Assert.Equal(Target.Value, target));
        Assert.Equal(item.Text!.CiphertextLengthBytes + item.Plan!.CiphertextLengthBytes, result.ProtectedContentBytes);
    }

    [Fact]
    public async Task Utf8OversizeIsUnavailableWithoutTruncationOrLossOfTheOtherContentKind()
    {
        using var reader = Results([Content('a', new string('界', 6000), "<ShowPlanXML />")]);
        var result = await Read(reader, new Protector());
        var item = Assert.Single(result.Content);
        Assert.Equal("oversized", item.TextState); Assert.Null(item.Text);
        Assert.Equal("protected", item.PlanState); Assert.Equal("<ShowPlanXML />", Decode(item.Plan!));
        Assert.Equal(3L, item.Statement.Counters.Executions);
    }

    [Fact]
    public async Task InvalidUnicodeAndAKeyLostDuringCaptureRemainUnavailable()
    {
        using (var reader = Results([Content('a', "\ud800", "<plan />")]))
        {
            var result = await Read(reader, new Protector());
            Assert.Equal("unavailable", result.Content[0].TextState); Assert.Null(result.Content[0].Text);
            Assert.Equal("protected", result.Content[0].PlanState);
        }
        using var lost = Results([Content('a', "secret", "<plan />")]);
        var noKey = await PlanCacheContentSourceReader.ReadAsync(lost, Target, Revision, 2000, 8,
            new UnavailableQuerySensitiveContentProtector(), CancellationToken.None);
        Assert.Equal("protection_unavailable", noKey.Content[0].TextState);
        Assert.Equal("protection_unavailable", noKey.Content[0].PlanState);
        Assert.Equal(0, noKey.ProtectedContentBytes);
    }

    [Fact]
    public async Task AProtectorCannotSubstituteAContentKind()
    {
        var protector = new Protector { WrongKind = true };
        using var reader = Results([Content('a', "secret", "<plan />")]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(reader, protector));
        Assert.All(protector.Inputs, input => Assert.All(input.ToArray(), value => Assert.Equal((byte)0, value)));
    }

    [Fact]
    public async Task EvictedAndOversizedSourceStatesRemainExplicitWithoutProtectionCalls()
    {
        var protector = new Protector();
        object[] row = Content('a', null, null); row[4] = "unavailable"; row[6] = "oversized";
        using var reader = Results([row]);
        var result = await Read(reader, protector);
        Assert.Equal("unavailable", result.Content[0].TextState); Assert.Equal("oversized", result.Content[0].PlanState);
        Assert.Empty(protector.Inputs); Assert.Null(result.Content[0].Text); Assert.Null(result.Content[0].Plan);
    }

    [Fact]
    public async Task ProtectionFailureDoesNotReturnPlaintextAndCancellationCannotBeReclassified()
    {
        using (var reader = Results([Content('a', "secret", "<plan />")]))
        {
            var result = await Read(reader, new Protector { Failure = true });
            Assert.Equal("protection_failed", result.Content[0].TextState);
            Assert.Equal("protection_failed", result.Content[0].PlanState);
            Assert.Null(result.Content[0].Text); Assert.Null(result.Content[0].Plan);
        }
        using var deadline = new CancellationTokenSource();
        using var cancelled = Results([Content('a', "secret", "<plan />")]);
        var protector = new Protector { Cancel = deadline };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Read(cancelled, protector, deadline.Token));
        Assert.All(protector.Inputs, input => Assert.All(input.ToArray(), value => Assert.Equal((byte)0, value)));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("query")]
    [InlineData("plan")]
    [InlineData("entry")]
    [InlineData("state")]
    [InlineData("missing")]
    [InlineData("unavailable_with_content")]
    [InlineData("oversize")]
    [InlineData("duplicate")]
    public async Task ForgedContentIdentityShapeAndBoundsFailClosed(string fault)
    {
        object[] row = Content('a', "select 1", "<plan />");
        switch (fault)
        {
            case "database": row[0] = 6; break;
            case "query": row[1] = new string('f', 64); break;
            case "plan": row[2] = new string('f', 64); break;
            case "entry": row[3] = new string('f', 64); break;
            case "state": row[4] = "partial"; break;
            case "missing": row[5] = DBNull.Value; break;
            case "unavailable_with_content": row[4] = "unavailable"; break;
            case "oversize": row[5] = new string('x', 16385).ToCharArray(); break;
        }
        using var reader = Results(fault == "duplicate" ? [row, row] : [row]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(reader, new Protector()));
    }

    [Fact]
    public async Task AggregateSourceBoundAndExtraResultsCannotEscapeTheClosedFrame()
    {
        using (var reader = Results([Content('a', null, new string('x', 600000)), Content('b', null, new string('x', 600000))], ['a', 'b']))
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(reader, new Protector()));
        using (var reader = Results([Content('a', "select 1", "<plan />")], extra: true))
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(reader, new Protector()));
        using var empty = Results([], []);
        var noContent = await PlanCacheContentSourceReader.ReadAsync(empty, Target, Revision, 2000, 0,
            new UnavailableQuerySensitiveContentProtector(), CancellationToken.None);
        Assert.Empty(noContent.Content); Assert.Empty(noContent.Counters.Rows);
    }

    [Fact]
    public void LoadsEachApprovedContentVariantSeparatelyFromCounterOnlyAssets()
    {
        var bundle = PlanCacheContentSourceBundle.LoadEmbedded();
        foreach (int major in new[] { 15, 16, 17 }) Assert.NotEmpty(bundle.GetSql(major));
        Assert.Throws<ArgumentOutOfRangeException>(() => bundle.GetSql(14));
        Assert.DoesNotContain("sys.dm_exec_sql_text", PlanCacheRuntimeSourceBundle.LoadEmbedded().GetSql(16), StringComparison.Ordinal);
    }

    private static Task<PlanCacheContentSourceRead> Read(DataTableReader reader, Protector protector, CancellationToken token = default) =>
        PlanCacheContentSourceReader.ReadAsync(reader, Target, Revision, 2000, 8, protector, token);

    private static string Decode(ProtectedSensitivePayload payload) => Encoding.UTF8.GetString(payload.GetCiphertext().Select(value => (byte)(value ^ 0x5a)).ToArray());

    private static object[] Content(char entry, string? text, string? plan) => [5, new string('b', 64), new string('c', 64), new string(entry, 64),
        text is null ? "unavailable" : "available", (object?)text?.ToCharArray() ?? DBNull.Value,
        plan is null ? "unavailable" : "available", (object?)plan?.ToCharArray() ?? DBNull.Value];

    private static DataTableReader Results(object[][] content, char[]? entries = null, bool extra = false)
    {
        entries ??= ['a'];
        var header = Table([typeof(int), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)]);
        header.Rows.Add(1, At, At.AddMilliseconds(20), new string('d', 64), entries.Length, 0, 0, 0, entries.Length);
        var counters = Table([typeof(int), typeof(string), typeof(string), typeof(string), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long), typeof(long)]);
        foreach (char entry in entries) counters.Rows.Add(5, new string('b', 64), new string('c', 64), new string(entry, 64), 1L, 2L, 3L, 4L, 5L, 6L);
        var payloads = Table([typeof(int), typeof(string), typeof(string), typeof(string), typeof(string), typeof(char[]), typeof(string), typeof(char[])]);
        foreach (object[] row in content) payloads.Rows.Add(row);
        return new DataTableReader(extra ? [header, counters, payloads, Table([])] : [header, counters, payloads]);
    }

    private static DataTable Table(Type[] types)
    {
        var table = new DataTable();
        for (int index = 0; index < types.Length; index++) table.Columns.Add($"c{index}", types[index]);
        return table;
    }

    private sealed class Protector : IQuerySensitiveContentProtector
    {
        public bool IsAvailable => true;
        internal bool Failure { get; init; }
        internal bool WrongKind { get; init; }
        internal CancellationTokenSource? Cancel { get; init; }
        internal List<ReadOnlyMemory<byte>> Inputs { get; } = [];
        internal List<Guid> Targets { get; } = [];
        public ValueTask<ProtectedSensitivePayload?> ProtectAsync(Guid targetId, SensitivePayloadKind kind, ReadOnlyMemory<byte> content, CancellationToken token)
        {
            Inputs.Add(content); Targets.Add(targetId);
            if (Failure) throw new CryptographicException();
            Cancel?.Cancel(); token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ProtectedSensitivePayload?>(new(WrongKind ? SensitivePayloadKind.ExecutionPlan : kind, new(SHA256.HashData(content.Span)),
                "AES-256-GCM", "fixture-only", new byte[12], new byte[16], content.ToArray().Select(value => (byte)(value ^ 0x5a)).ToArray()));
        }
        public ValueTask<byte[]> UnprotectAsync(Guid targetId, ProtectedSensitivePayload payload, CancellationToken token) => throw new NotSupportedException();
    }
}
