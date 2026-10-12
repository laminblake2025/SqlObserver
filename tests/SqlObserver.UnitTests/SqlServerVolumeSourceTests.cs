using System.Data;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Security;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlServerVolumeSourceTests
{
    private static readonly IdentityFingerprintKey Key = new(new byte[32]);
    private static readonly CollectorRunId RunId = new(Guid.Parse("7600747d-b611-4162-8507-15754ef94404"));

    [Fact]
    public async Task SharedVolumeIsDeduplicatedAndFreeSpaceIsNotSummed()
    {
        using DataTable table = Rows();
        Add(table, 5, 1, "SQLNODE", "volume-1", "C:\\", 1_000, 250);
        Add(table, 5, 2, "SQLNODE", "volume-1", "C:\\", 1_000, 240);
        Add(table, 6, 1, "SQLNODE", "", "D:\\", 2_000, 900);
        using DataTableReader reader = table.CreateDataReader();

        SqlVolumeSourceRead result = await Read(reader);

        Assert.False(result.SourceRowLimitReached);
        Assert.Equal(3, result.SourceRowsRead);
        Assert.Equal(2, result.Volumes.Items.Count);
        SqlVolumeObservation shared = Assert.Single(result.Volumes.Items, item => item.IdentityKind == SqlVolumeIdentityKind.VolumeId);
        Assert.Equal(2, shared.MappedFileCount);
        Assert.Equal(240, shared.AvailableBytes);
        Assert.Equal(1_000, shared.TotalBytes);
        Assert.Equal(64, shared.VolumeKey.Length);
        Assert.DoesNotContain("volume-1", shared.VolumeKey, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Volumes.Items, item => item.IdentityKind == SqlVolumeIdentityKind.MountPoint && item.AvailableBytes == 900);
    }

    [Fact]
    public async Task UnknownNodeRetainsCapacityWithoutClaimingStableVolumeIdentity()
    {
        using DataTable table = Rows();
        Add(table, 5, 1, "", "volume-1", "C:\\", 1_000, 0);
        Add(table, 5, 2, "", "volume-1", "C:\\", 1_000, 0);
        using DataTableReader reader = table.CreateDataReader();

        SqlVolumeSourceRead result = await Read(reader);

        Assert.Equal(2, result.Volumes.Items.Count);
        Assert.All(result.Volumes.Items, item =>
        {
            Assert.Equal(SqlVolumeIdentityKind.FileScopedUnknown, item.IdentityKind);
            Assert.Equal(0, item.AvailableBytes);
            Assert.Equal(1, item.MappedFileCount);
        });
    }

    [Fact]
    public async Task MissingVolumeIdentifiersAndCapacityStayUnknown()
    {
        using DataTable table = Rows();
        table.Rows.Add(M4TestData.RepositoryTime.UtcDateTime, 5, 1, "SQLNODE", "", "",
            DBNull.Value, DBNull.Value);
        using DataTableReader reader = table.CreateDataReader();

        SqlVolumeObservation item = Assert.Single((await Read(reader)).Volumes.Items);

        Assert.Equal(SqlVolumeIdentityKind.FileScopedUnknown, item.IdentityKind);
        Assert.Null(item.TotalBytes);
        Assert.Null(item.AvailableBytes);
    }

    [Fact]
    public async Task LookaheadRejectsAnIncompleteSnapshotRatherThanPublishingFirstVolume()
    {
        using DataTable table = Rows();
        Add(table, 5, 1, "SQLNODE", "volume-1", "C:\\", 1_000, 250);
        Add(table, 5, 2, "SQLNODE", "volume-2", "D:\\", 2_000, 900);
        using DataTableReader reader = table.CreateDataReader();

        SqlVolumeSourceRead result = await Read(reader, maxRows: 1);

        Assert.Equal(2, result.SourceRowsRead);
        Assert.True(result.SourceRowLimitReached);
        Assert.Empty(result.Volumes.Items);
    }

    [Fact]
    public async Task DuplicateSourceFileIdentityIsRejected()
    {
        using DataTable table = Rows();
        Add(table, 5, 1, "SQLNODE", "volume-1", "C:\\", 1_000, 250);
        Add(table, 5, 1, "SQLNODE", "volume-1", "C:\\", 1_000, 240);
        using DataTableReader reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Read(reader));
    }

    [Fact]
    public async Task ConflictingSharedVolumeTotalIsRejected()
    {
        using DataTable table = Rows();
        Add(table, 5, 1, "SQLNODE", "volume-1", "C:\\", 1_000, 250);
        Add(table, 5, 2, "SQLNODE", "volume-1", "C:\\", 2_000, 240);
        using DataTableReader reader = table.CreateDataReader();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await Read(reader));
    }

    [Fact]
    public async Task ByteCapWithholdsTheIncompleteVolumeSet()
    {
        using DataTable table = Rows();
        Add(table, 5, 1, "SQLNODE", "volume-1", "C:\\", 1_000, 250);
        using DataTableReader reader = table.CreateDataReader();

        SqlVolumeSourceRead result = await Read(reader, maxBytes: 1);

        Assert.True(result.ResponseByteLimitReached);
        Assert.Empty(result.Volumes.Items);
        Assert.Equal(0, result.ResponseBytes);
    }

    [Fact]
    public async Task EmptyMetadataViewCannotMasqueradeAsCompleteCapacityEvidence()
    {
        using DataTable table = Rows();
        using DataTableReader reader = table.CreateDataReader();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await Read(reader));
    }

    [Fact]
    public void PinnedQueryHasBoundedOrderedLookaheadAndNoPhysicalFilePath()
    {
        string query = SqlServerVolumeCapacitySource.LoadPinnedQuery();
        Assert.Contains("TOP (@maximum_rows + 1)", query, StringComparison.Ordinal);
        Assert.Contains("OUTER APPLY sys.dm_os_volume_stats", query, StringComparison.Ordinal);
        Assert.Contains("HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DEFINITION')", query, StringComparison.Ordinal);
        Assert.Contains("THROW 51005", query, StringComparison.Ordinal);
        Assert.True(SqlServerCollectorErrorClassifier.IsPermissionDenied(51005));
        Assert.Contains("ORDER BY files.database_id, files.file_id", query, StringComparison.Ordinal);
        Assert.DoesNotContain("physical_name", query, StringComparison.OrdinalIgnoreCase);
    }

    private static ValueTask<SqlVolumeSourceRead> Read(DataTableReader reader, int maxRows = 1_000, int maxBytes = 4_194_304) =>
        SqlServerVolumeCapacitySource.ReadAsync(reader, M4TestData.TargetId, M4TestData.TargetRevision,
            RunId, Key, maxRows, maxBytes, CancellationToken.None);

    private static DataTable Rows()
    {
        var table = new DataTable();
        table.Columns.Add("observed_at_utc", typeof(DateTime));
        table.Columns.Add("database_id", typeof(int));
        table.Columns.Add("file_id", typeof(int));
        table.Columns.Add("physical_node", typeof(string));
        table.Columns.Add("volume_id", typeof(string));
        table.Columns.Add("volume_mount_point", typeof(string));
        table.Columns.Add("total_bytes", typeof(long));
        table.Columns.Add("available_bytes", typeof(long));
        return table;
    }

    private static void Add(DataTable table, int databaseId, int fileId, string node, string volumeId,
        string mount, long total, long free) => table.Rows.Add(
            M4TestData.RepositoryTime.UtcDateTime, databaseId, fileId, node, volumeId, mount, total, free);
}
