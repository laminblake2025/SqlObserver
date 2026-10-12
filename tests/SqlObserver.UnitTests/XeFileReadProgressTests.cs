using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class XeFileReadProgressTests
{
    private static readonly XeReadIdentity Source = new(new(Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")), new(1), new string('a', 64));

    [Fact]
    public void PartialSingleBufferCannotSkipUnreadEvents()
    {
        XeFilePosition buffer = new(100, 0, 65_536);
        XeFileReadProgress progress = XeFileReadProgressCalculator.Compare(null, Source, [buffer, buffer], false);
        Assert.Equal(XeFileReadProgressKind.CatchingUp, progress.Kind);
        Assert.Null(progress.SafeBookmark);
        Assert.Equal(0, progress.CompleteBuffers);
        Assert.Equal(2, progress.RowsRead);
    }

    [Fact]
    public void RowOrByteCutoffAdvancesOnlyBeforeTheFinalBuffer()
    {
        XeFilePosition first = new(100, 0, 65_536), partial = new(100, 0, 131_072);
        XeFileReadProgress progress = XeFileReadProgressCalculator.Compare(null, Source, [first, first, partial, partial], false);
        Assert.Equal(XeFileReadProgressKind.CatchingUp, progress.Kind);
        Assert.Equal(first, progress.SafeBookmark?.Position);
        Assert.Equal(1, progress.CompleteBuffers);
        // Retry starts after the committed first buffer, and includes the entire
        // formerly partial buffer rather than only its unread tail.
        XeFileReadProgress retry = XeFileReadProgressCalculator.Compare(progress.SafeBookmark, Source, [partial, partial, partial], true);
        Assert.Equal(XeFileReadProgressKind.CaughtUp, retry.Kind);
        Assert.Equal(partial, retry.SafeBookmark?.Position);
        Assert.Equal(1, retry.CompleteBuffers);
    }

    [Fact]
    public void EndOfStreamCompletesTheLastBufferIncludingMultipleEvents()
    {
        XeFilePosition buffer = new(100, 0, 65_536);
        XeFileReadProgress progress = XeFileReadProgressCalculator.Compare(null, Source, [buffer, buffer, buffer], true);
        Assert.Equal(buffer, progress.SafeBookmark?.Position);
        Assert.Equal(1, progress.CompleteBuffers);
        Assert.Equal(XeFileReadProgressKind.CaughtUp, progress.Kind);
    }

    [Fact]
    public void EmptyQuietReadKeepsItsBookmarkWithoutInventingANewBuffer()
    {
        XeReadBookmark prior = new(Source, new(100, 0, 65_536));
        XeFileReadProgress quiet = XeFileReadProgressCalculator.Compare(prior, Source, [], true);
        Assert.Same(prior, quiet.SafeBookmark);
        Assert.Equal(XeFileReadProgressKind.CaughtUp, quiet.Kind);
        Assert.Equal(0, quiet.CompleteBuffers);
        Assert.Null(XeFileReadProgressCalculator.Compare(null, Source, [], true).SafeBookmark);
    }

    [Fact]
    public void RolloverUsesGenerationRatherThanTheFilenameIndex()
    {
        XeReadBookmark prior = new(Source, new(100, 9, 131_072));
        XeFilePosition rolled = new(101, 0, 65_536), next = new(101, 0, 131_072);
        XeFileReadProgress progress = XeFileReadProgressCalculator.Compare(prior, Source, [rolled, rolled, next], false);
        Assert.Equal(rolled, progress.SafeBookmark?.Position);
        Assert.Equal(1, progress.CompleteBuffers);
    }

    [Theory]
    [InlineData(100, 0, 65_536)]
    [InlineData(100, 0, 0)]
    [InlineData(99, 0, 131_072)]
    [InlineData(100, 1, 131_072)]
    public void ReplayedRegressedOrAmbiguousFileCannotAdvance(long generation, int index, long offset)
    {
        XeReadBookmark prior = new(Source, new(100, 0, 65_536));
        XeFileReadProgress result = XeFileReadProgressCalculator.Compare(prior, Source, [new(generation, index, offset)], true);
        Assert.Equal(XeFileReadProgressKind.ResetRequired, result.Kind);
        Assert.Null(result.SafeBookmark);
    }

    [Fact]
    public void InterleavedBuffersInvalidateEvenAnApparentlyCompletePrefix()
    {
        XeFilePosition first = new(100, 0, 65_536), later = new(100, 0, 131_072);
        XeFileReadProgress result = XeFileReadProgressCalculator.Compare(null, Source, [first, later, first], true);
        Assert.Equal(XeFileReadProgressKind.ResetRequired, result.Kind);
        Assert.Null(result.SafeBookmark);
        Assert.Equal(0, result.CompleteBuffers);
    }

    [Fact]
    public void RestartOrTargetRevisionChangeRequiresAColdRead()
    {
        XeReadBookmark prior = new(Source, new(100, 0, 65_536));
        XeReadIdentity[] changes = [new(Source.TargetId, Source.TargetRevision, new string('b', 64)),
            new(Source.TargetId, new(2), Source.SourceEpoch)];
        foreach (XeReadIdentity changed in changes)
        {
            XeFileReadProgress result = XeFileReadProgressCalculator.Compare(prior, changed, [new(101, 0, 0)], true);
            Assert.Equal(XeFileReadProgressKind.ResetRequired, result.Kind);
            Assert.Null(result.SafeBookmark);
        }
        Assert.Throws<ArgumentException>(() => XeFileReadProgressCalculator.Compare(prior,
            new(new(Guid.NewGuid()), new(1), Source.SourceEpoch), [], true));
    }

    [Fact]
    public void FullBudgetWithoutEndProbeDoesNotCompleteItsFinalBuffer()
    {
        XeFilePosition buffer = new(100, 0, 65_536);
        XeFilePosition[] rows = Enumerable.Repeat(buffer, XeFileReadProgressCalculator.MaximumRows).ToArray();
        Assert.Null(XeFileReadProgressCalculator.Compare(null, Source, rows, false).SafeBookmark);
        Assert.Throws<ArgumentOutOfRangeException>(() => XeFileReadProgressCalculator.Compare(null, Source,
            Enumerable.Repeat(buffer, rows.Length + 1).ToArray(), false));
    }

    [Fact]
    public void PositionAndIdentityRejectUnboundedOrNoncanonicalMetadata()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFilePosition(0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFilePosition(1, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeFilePosition(1, 0, -1));
        Assert.Throws<ArgumentException>(() => new XeReadIdentity(Source.TargetId, Source.TargetRevision, new string('A', 64)));
        Assert.Throws<ArgumentException>(() => new XeReadIdentity(Source.TargetId, Source.TargetRevision, "provider path"));
        Assert.Throws<ArgumentException>(() => XeFileReadProgressCalculator.Compare(null, Source, [null!], true));
        Assert.Equal("XE read bookmark", new XeReadBookmark(Source, new(100, 0, 65_536)).ToString());
    }
}
