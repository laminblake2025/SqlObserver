using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

public sealed class PostgreSqlProjectionNormalizationTests
{
    [Fact]
    public void SnapshotIdentityOmitsScheduleRevisionWhenNoUsableRunExists()
    {
        (Guid? runId, long? targetRevision) =
            PostgreSqlHealthProjectionPort.NormalizeSnapshotIdentity(runId: null, targetRevision: 7);

        Assert.Null(runId);
        Assert.Null(targetRevision);
    }

    [Fact]
    public void SnapshotIdentityRejectsRunWithoutTargetRevision()
    {
        Assert.Throws<InvalidDataException>(() =>
            PostgreSqlHealthProjectionPort.NormalizeSnapshotIdentity(Guid.NewGuid(), targetRevision: null));
    }

    [Fact]
    public void SnapshotIdentityPreservesCompleteBinding()
    {
        Guid expectedRunId = Guid.NewGuid();

        (Guid? runId, long? targetRevision) =
            PostgreSqlHealthProjectionPort.NormalizeSnapshotIdentity(expectedRunId, targetRevision: 3);

        Assert.Equal(expectedRunId, runId);
        Assert.Equal(3, targetRevision);
    }

    [Fact]
    public void ScalarTimestampNormalizationAcceptsNpgsqlDateTimeShape()
    {
        var timestamp = new DateTime(2026, 9, 2, 12, 34, 56, DateTimeKind.Unspecified);

        DateTimeOffset normalized = PostgreSqlRuntimeSupport.ConvertUtcTimestamp(timestamp);

        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(timestamp.Ticks, normalized.Ticks);
    }

    [Fact]
    public void ScalarTimestampNormalizationConvertsDateTimeOffsetToUtc()
    {
        var timestamp = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.FromHours(-4));

        DateTimeOffset normalized = PostgreSqlRuntimeSupport.ConvertUtcTimestamp(timestamp);

        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(timestamp.UtcDateTime, normalized.UtcDateTime);
    }

    [Fact]
    public void ScalarTimestampNormalizationRejectsUnexpectedTypes()
    {
        Assert.Throws<InvalidDataException>(() => PostgreSqlRuntimeSupport.ConvertUtcTimestamp("2026-09-02"));
    }
}
