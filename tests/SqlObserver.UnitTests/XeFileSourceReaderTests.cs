using System.Data;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class XeFileSourceReaderTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.NewGuid());
    private static readonly ObservationTargetRevision Revision = new(1);
    private static readonly DateTime ReadAt = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly XeReadIdentity Source = new(Target, Revision, new string('a', 64));
    private const string Xml = "<event name=\"xml_deadlock_report\"><data><value><deadlock><victim-list><victimProcess id=\"p1\" /></victim-list><process-list><process id=\"p1\" spid=\"51\" /><process id=\"p2\" spid=\"52\" /></process-list></deadlock></value></data></event>";

    [Fact]
    public void EmbeddedBundleHasThreeApprovedAssets() => Assert.NotNull(XeFileSourceBundle.LoadEmbedded());

    [Fact]
    public async Task CompleteBuffersDedupeEvidenceAndQuietReadPreservesBookmark()
    {
        XeFileSourcePage page = await Read([Row(100, Xml), Row(100, Xml), Row(200)]);
        Assert.True(page.ReachedEnd);
        Assert.Single(page.Deadlocks.Items);
        Assert.Equal(2, page.Progress!.CompleteBuffers);
        Assert.Equal(new XeFilePosition(1000, 0, 200), page.Progress.SafeBookmark!.Position);
        XeFileSourcePage quiet = await Read([], previous: page.Progress.SafeBookmark);
        Assert.True(quiet.ReachedEnd);
        Assert.Empty(quiet.Deadlocks.Items);
        Assert.Same(page.Progress.SafeBookmark, quiet.Progress!.SafeBookmark);
        Assert.False(quiet.SourceLoss);
    }

    [Fact]
    public async Task MetadataProbeCannotAdvanceItsPartialBuffer()
    {
        XeFileSourcePage page = await Read([Row(100), Row(200, Xml), Row(200, Xml)], hasMore: true, limits: new(metadataRows: 2));
        Assert.False(page.ReachedEnd);
        Assert.Equal(XeFileReadStop.MetadataLimit, page.Stop);
        Assert.Equal(100, page.Progress!.SafeBookmark!.Position.BufferOffset);
        Assert.Single(page.Deadlocks.Items);
        XeFileSourcePage retry = await Read([Row(200, Xml), Row(200, Xml)], previous: page.Progress.SafeBookmark);
        Assert.True(retry.ReachedEnd);
        Assert.Single(retry.Deadlocks.Items);
        Assert.Equal(200, retry.Progress!.SafeBookmark!.Position.BufferOffset);
    }

    [Fact]
    public async Task DeadlockQuotaCannotAdvanceItsFinalBuffer()
    {
        XeFileSourcePage page = await Read([Row(100, Xml), Row(200, Xml)], limits: new(deadlocks: 1));
        Assert.Equal(XeFileReadStop.DeadlockLimit, page.Stop);
        Assert.Equal(100, page.Progress!.SafeBookmark!.Position.BufferOffset);
        Assert.False(page.ReachedEnd);
    }

    [Fact]
    public async Task ServerByteSentinelCannotAdvanceItsFinalBuffer()
    {
        object[] quota = Row(200, Xml); quota[6] = true; quota[7] = DBNull.Value;
        XeFileSourcePage page = await Read([Row(100), quota]);
        Assert.Equal(XeFileReadStop.ResponseLimit, page.Stop);
        Assert.Equal(100, page.Progress!.SafeBookmark!.Position.BufferOffset);
        Assert.False(page.ReachedEnd);
    }

    [Fact]
    public async Task ClientUtf8BudgetCannotAdvanceItsFinalBuffer()
    {
        string large = Xml.Replace("<data>", "<ignored>" + new string('\u20ac', 1500) + "</ignored><data>");
        XeFileSourcePage page = await Read([Row(100), Row(200, large)], limits: new(responseBytes: 4096));
        Assert.Equal(XeFileReadStop.ResponseLimit, page.Stop);
        Assert.Empty(page.Deadlocks.Items);
        Assert.Equal(100, page.Progress!.SafeBookmark!.Position.BufferOffset);
        Assert.InRange(page.ResponseBytes, 128, 4096);
    }

    [Fact]
    public async Task MalformedAndOversizedEvidenceAreExplicitLossEvenAtEndOfStream()
    {
        object[] oversized = Row(200, Xml); oversized[5] = true; oversized[7] = DBNull.Value;
        XeFileSourcePage page = await Read([Row(100, "<event><broken>"), oversized, Row(300)]);
        Assert.True(page.ReachedEnd);
        Assert.True(page.SourceLoss);
        Assert.Equal(2, page.RejectedDeadlocks);
        Assert.Empty(page.Deadlocks.Items);
        Assert.Equal(300, page.Progress!.SafeBookmark!.Position.BufferOffset);
    }

    [Fact]
    public async Task FutureTimestampCannotBecomeValidDeadlockEvidence()
    {
        object[] future = Row(100, Xml); future[3] = ReadAt.AddSeconds(1);
        XeFileSourcePage page = await Read([future]);
        Assert.True(page.TimingGap);
        Assert.True(page.SourceLoss);
        Assert.Equal(1, page.RejectedDeadlocks);
        Assert.Empty(page.Deadlocks.Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NonDeadlockMetadataCannotExposeContentsOrLossSentinels(int column)
    {
        object[] unsafeRow = Row(100);
        unsafeRow[5 + column] = column == 2 ? Xml : true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([unsafeRow]));
    }

    [Fact]
    public async Task MissingXmlCannotSilentlyBecomeZero()
    {
        object[] missing = Row(100, Xml); missing[7] = DBNull.Value;
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([missing]));
    }

    [Fact]
    public async Task ContradictoryProbeAndUnreviewedFramesAreRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([Row(100)], hasMore: true));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([Row(100), Row(200)], limits: new(metadataRows: 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([], extra: true));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("config_invalid")]
    [InlineData("reset_required")]
    public async Task UnavailableSourceNeverReturnsProgressOrZeroEvidence(string state)
    {
        using DataTableReader reader = Header(state, retained: null).CreateDataReader();
        XeFileSourcePage page = await XeFileSourceReader.ReadAsync(reader, Target, Revision, null, new(), default);
        Assert.False(page.ReachedEnd);
        Assert.Null(page.Progress);
        Assert.Null(page.RetainedFromUtc);
        Assert.Empty(page.Deadlocks.Items);
    }

    [Fact]
    public async Task WrongRevisionEpochAndRegressedPositionsCannotAdvance()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Read([], previous: new(new(Target, new(2), Source.SourceEpoch), new(1000, 0, 100))));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read([], previous: new(new(Target, Revision, new string('b', 64)), new(1000, 0, 100))));
        XeFileSourcePage page = await Read([Row(100)], previous: new(Source, new(1000, 0, 100)));
        Assert.Equal(XeFileSourceState.ResetRequired, page.State);
        Assert.Null(page.Progress);
    }

    [Fact]
    public void LimitsRejectUnboundedRequests()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFileReadLimits(metadataRows: 8193));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFileReadLimits(deadlocks: 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFileReadLimits(responseBytes: 4095));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFileReadLimits(responseBytes: 1048577));
    }

    [Fact]
    public async Task SmallResponseReservesMetadataSpaceIncludingItsProbe()
    {
        var limits = new XeFileReadLimits(responseBytes: 4096);
        Assert.Equal(61, limits.MetadataRows);
        object[][] rows = Enumerable.Range(0, limits.MetadataRows + 1).Select(index => Row(100 + index)).ToArray();
        XeFileSourcePage page = await Read(rows, hasMore: true, limits: limits);
        Assert.Equal(XeFileReadStop.MetadataLimit, page.Stop);
        Assert.Equal(62, page.Progress!.RowsRead);
        Assert.Equal(160, page.Progress.SafeBookmark!.Position.BufferOffset);
        Assert.InRange(XeFileSourceReader.HeaderBytes + rows.Length * XeFileSourceReader.MetadataBytes, 128, limits.ResponseBytes);
        Assert.False(page.ReachedEnd);
    }

    private static object[] Row(long offset, string? xml = null) =>
        [1000L, 0, offset, ReadAt.AddMinutes(-1), xml is not null, false, false, (object?)xml ?? DBNull.Value];

    private static DataTable Header(string state = "ready", DateTime? retained = default, bool hasMore = false)
    {
        var table = new DataTable();
        table.Columns.Add("state", typeof(string)); table.Columns.Add("epoch", typeof(string));
        table.Columns.Add("readAt", typeof(DateTime)); table.Columns.Add("retained", typeof(DateTime));
        table.Columns.Add("hasMore", typeof(bool));
        table.Rows.Add(state, Source.SourceEpoch, ReadAt, (object?)retained ?? DBNull.Value, hasMore);
        return table;
    }

    private static async Task<XeFileSourcePage> Read(object[][] rows, bool hasMore = false,
        XeFileReadLimits? limits = null, XeReadBookmark? previous = null, bool extra = false)
    {
        var metadata = new DataTable();
        Type[] types = [typeof(long), typeof(int), typeof(long), typeof(DateTime), typeof(bool), typeof(bool), typeof(bool), typeof(string)];
        for (int column = 0; column < types.Length; column++) metadata.Columns.Add($"c{column}", types[column]);
        foreach (object[] row in rows) metadata.Rows.Add(row);
        DataTable[] frames = extra ? [Header(retained: ReadAt.AddHours(-1), hasMore: hasMore), metadata, new DataTable()]
            : [Header(retained: ReadAt.AddHours(-1), hasMore: hasMore), metadata];
        using var reader = new DataTableReader(frames);
        return await XeFileSourceReader.ReadAsync(reader, Target, Revision, previous, limits ?? new(), default);
    }
}
