using Microsoft.Data.SqlClient;
using System.Text;
using SqlObserver.Domain.Collection;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.IntegrationTests.SqlServer;

[Trait("Category", "RequiresSqlServer")]
public sealed class XeFileSourceIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task SmallNativeResponseBoundsMetadataAndItsProbe()
    {
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync();
        var limits = new XeFileReadLimits(responseBytes: 4096);
        XeFileSourcePage page = await XeFileSourceBundle.LoadEmbedded().ReadAsync(connection,
            new(Guid.NewGuid()), new(1), null, null, limits, default);
        Assert.Equal(XeFileSourceState.Ready, page.State);
        Assert.Equal(XeFileReadStop.MetadataLimit, page.Stop);
        Assert.Equal(limits.MetadataRows + 1, page.Progress!.RowsRead);
        Assert.Equal(page.Progress.RowsRead, page.Positions.Count);
        Assert.True(page.ResponseBytes >= XeFileSourceReader.HeaderBytes + page.Positions.Count * XeFileSourceReader.MetadataBytes);
        Assert.InRange(page.ResponseBytes, 128, limits.ResponseBytes);
        Assert.InRange(XeFileSourceReader.HeaderBytes + page.Progress.RowsRead * XeFileSourceReader.MetadataBytes, 128, limits.ResponseBytes);
        Assert.False(page.ReachedEnd);
    }

    [Fact]
    public async Task NativePagesCatchUpResumeQuietlyAndRejectMissingOrChangedSources()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var connection = new SqlConnection(SqlServerLabContract.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        var bundle = XeFileSourceBundle.LoadEmbedded();
        var target = new SqlObserver.Domain.Telemetry.MonitoredInstanceId(Guid.NewGuid());
        var revision = new SqlObserver.Domain.Targets.ObservationTargetRevision(1);
        var limits = new XeFileReadLimits();
        XeReadBookmark? bookmark = null;
        XeReadIdentity? source = null;
        XeFileSourcePage page;
        int pages = 0;
        DateTimeOffset? firstReadAt = null;
        var observed = new Dictionary<Guid, DeadlockObservation>();
        do
        {
            page = await bundle.ReadAsync(connection, target, revision, bookmark, source, limits, deadline.Token);
            Assert.True(page.State == XeFileSourceState.Ready,
                $"Page {pages}: {page.State}; matching incarnation: {source is null || source == page.Source}; prior buffer: {bookmark?.Position.BufferOffset}.");
            Assert.NotNull(page.Progress);
            Assert.InRange(page.ResponseBytes, XeFileSourceReader.HeaderBytes, limits.ResponseBytes);
            Assert.InRange(page.Progress.RowsRead, 0, limits.MetadataRows + 1);
            Assert.InRange(page.Deadlocks.Items.Count, 0, limits.Deadlocks);
            Assert.False(page.SourceLoss, "The existing lab source needs complete, safe event metadata.");
            firstReadAt ??= page.ReadAtUtc;
            foreach (DeadlockObservation item in page.Deadlocks.Items) observed[item.EventId] = item;
            source ??= page.Source;
            Assert.Equal(source, page.Source);
            if (!page.ReachedEnd)
                Assert.True(page.Progress.SafeBookmark is not null && page.Progress.SafeBookmark != bookmark,
                    "A full metadata page must expose a complete buffer before its cutoff.");
            bookmark = page.Progress.SafeBookmark;
            Assert.True(++pages <= 128, "The bounded lab source exceeded its catch-up page limit.");
        } while (!page.ReachedEnd);
        Assert.NotNull(bookmark);
        output.WriteLine($"Caught up after {pages} bounded pages; retained {observed.Count} unique deadlocks.");

        // Compare the incremental evidence with the independently approved v1
        // full scan. Use the first page's time to exclude concurrent new events.
        var reference = new HashSet<Guid>();
        int major = int.Parse(connection.ServerVersion.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
        await using (var legacy = new SqlCommand(SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded().Asset.GetQuery(major), connection)
        { CommandTimeout = 20 })
        {
            legacy.Parameters.Add("@maximum_rows", System.Data.SqlDbType.Int).Value = 257;
            await using SqlDataReader rows = await legacy.ExecuteReaderAsync(deadline.Token);
            int count = 0;
            while (await rows.ReadAsync(deadline.Token))
            {
                Assert.True(++count <= 256, "The reference lab source must fit its reviewed deadlock bound.");
                string state = rows.GetString(2);
                if (state == "empty") continue;
                Assert.Equal("ready", state);
                var occurred = new DateTimeOffset(DateTime.SpecifyKind(rows.GetDateTime(0), DateTimeKind.Utc));
                using TextReader xml = rows.GetTextReader(1);
                SqlServerDeadlockCollector.BoundedXmlRead bounded = await SqlServerDeadlockCollector.ReadBoundedXmlAsync(xml, deadline.Token);
                Assert.False(bounded.Oversized);
                DeadlockObservation item = DeadlockXmlParser.Parse(target, revision, Encoding.UTF8.GetBytes(bounded.Text), occurred);
                if (item.OccurredAtUtc >= firstReadAt!.Value.AddDays(-33) && item.OccurredAtUtc <= firstReadAt)
                    reference.Add(item.EventId);
            }
        }
        Assert.True(reference.SetEquals(observed.Values.Where(item => item.OccurredAtUtc >= firstReadAt!.Value.AddDays(-33)
            && item.OccurredAtUtc <= firstReadAt).Select(item => item.EventId)),
            "Incremental pages must preserve every reference event without inventing an event identity.");

        XeFileSourcePage warm = await bundle.ReadAsync(connection, target, revision, bookmark, source, limits, deadline.Token);
        Assert.Equal(XeFileSourceState.Ready, warm.State);
        Assert.True(warm.ReachedEnd);
        Assert.Equal(source, warm.Source);
        Assert.True(warm.Progress!.SafeBookmark!.Position.FileGeneration >= bookmark.Position.FileGeneration);
        Assert.DoesNotContain(warm.Deadlocks.Items, item => page.Deadlocks.Items.Any(old => old.EventId == item.EventId));

        var changed = new XeReadIdentity(target, revision, new string(source!.SourceEpoch[0] == 'a' ? 'b' : 'a', 64));
        XeFileSourcePage reset = await bundle.ReadAsync(connection, target, revision, null, changed, limits, deadline.Token);
        Assert.Equal(XeFileSourceState.ResetRequired, reset.State);
        Assert.Null(reset.Progress);
        Assert.False(reset.ReachedEnd);
        Assert.Empty(reset.Deadlocks.Items);

        // Numeric generation 1 cannot name a genuine system_health file. The
        // TVF's native missing-initial-file error must become a reset, not zero.
        var missing = new XeReadBookmark(source, new(1, 0, 0));
        XeFileSourcePage rolledAway = await bundle.ReadAsync(connection, target, revision, missing, source, limits, deadline.Token);
        Assert.Equal(XeFileSourceState.ResetRequired, rolledAway.State);
        Assert.Null(rolledAway.Progress);
        Assert.Empty(rolledAway.Deadlocks.Items);

        XeFileSourcePage partial = await bundle.ReadAsync(connection, target, revision, null, null,
            new XeFileReadLimits(metadataRows: 2), deadline.Token);
        Assert.Equal(XeFileSourceState.Ready, partial.State);
        Assert.False(partial.ReachedEnd);
        Assert.Equal(XeFileReadStop.MetadataLimit, partial.Stop);
        Assert.Equal(3, partial.Progress!.RowsRead);
        Assert.InRange(partial.Progress.CompleteBuffers, 0, 2);
    }
}
