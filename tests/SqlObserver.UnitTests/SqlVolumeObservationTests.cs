using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;

namespace SqlObserver.UnitTests;

public sealed class SqlVolumeObservationTests
{
    private const string KeyA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string KeyB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void OneVolumeCanRepresentSeveralFilesWithoutMultiplyingCapacity()
    {
        var volume = Observation(KeyA, 3, 1_000, 250);
        var batch = new SqlVolumeObservationBatch([volume]);
        var payload = new CollectorPayload(sqlVolumes: batch);

        Assert.Single(batch.Items);
        Assert.Equal(3, volume.MappedFileCount);
        Assert.Equal(250, volume.AvailableBytes);
        Assert.Equal(1, payload.ItemCount);
        Assert.Equal(SqlVolumeObservation.FixedEstimatedBytes, payload.EstimatedSizeBytes);
        Assert.Throws<ArgumentException>(() => new SqlVolumeObservationBatch([volume, volume]));
    }

    [Fact]
    public void UnknownCapacityIsDistinctFromZeroFreeBytes()
    {
        var unknown = Observation(KeyA, 1, null, null);
        var full = Observation(KeyB, 1, 1_000, 0);
        Assert.Null(unknown.AvailableBytes);
        Assert.Equal(0, full.AvailableBytes);
        Assert.Equal(2, new SqlVolumeObservationBatch([unknown, full]).Items.Count);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(100L, 101L)]
    [InlineData(100L, -1L)]
    [InlineData(null, 10L)]
    [InlineData(100L, null)]
    public void InvalidCapacityCannotEnterThePayload(long? total, long? free)
    {
        Assert.Throws<ArgumentException>(() => Observation(KeyA, 1, total, free));
    }

    [Fact]
    public void RawOrNonCanonicalVolumeIdentityIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Observation("C:\\", 1, 100, 20));
        Assert.Throws<ArgumentException>(() => Observation(KeyA.ToUpperInvariant(), 1, 100, 20));
    }

    [Fact]
    public void ValidatorRequiresAnExplicitVolumeContractBeforeRepositoryIo()
    {
        CollectorManifest manifest = M4TestData.CreateManifest("storage.volume", outputKind: CollectorOutputKind.SqlVolumes);
        CollectorExecutionRequest request = M4TestData.CreateExecutionRequest();
        var payload = new CollectorPayload(sqlVolumes: new SqlVolumeObservationBatch([Observation(KeyA, 2, 1_000, 200)]));
        var result = new CollectorExecutionResult(
            request.TargetId, request.TargetRevision, manifest.Id, manifest.ManifestVersion.Value,
            manifest.OutputSchemaVersion.Value, CollectorRunOutcome.Succeeded, CollectorRunReason.Completed,
            payload, new CollectorRunAccounting(2, 1, payload.EstimatedSizeBytes, payload.EstimatedSizeBytes),
            CollectorLossEvidence.None);
        var allowed = new CollectorOutputValidator(M4TestData.CreateOutputContract(CollectorOutputKind.SqlVolumes));
        var denied = new CollectorOutputValidator(new CollectorOutputContract(
            new CollectorOutputSchemaVersion(1), [], 0, 0, 0));

        allowed.Validate(manifest, request, result);
        Assert.Throws<InvalidDataException>(() => denied.Validate(manifest, request, result));
    }

    private static SqlVolumeObservation Observation(string key, int files, long? total, long? free) => new(
        M4TestData.TargetId, M4TestData.TargetRevision, key, SqlVolumeIdentityKind.VolumeId,
        files, total, free, M4TestData.RepositoryTime);
}
