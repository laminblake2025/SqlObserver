using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class DatabaseLogReuseObservationTests
{
    [Fact]
    public void LegacyObservationHasUnknownLogReuseState()
    {
        DatabaseObservation observation = Create(null);
        Assert.Null(observation.LogReuseWaitDescription);
        Assert.Equal(DatabaseObservation.FixedEstimatedBytes + observation.Name.Utf8Bytes,
            observation.EstimatedSizeBytes);
    }

    [Fact]
    public void SourceStateIsPreservedAndAccountedForExactly()
    {
        DatabaseObservation observation = Create("ACTIVE_TRANSACTION");
        Assert.Equal("ACTIVE_TRANSACTION", observation.LogReuseWaitDescription);
        Assert.Equal(DatabaseObservation.FixedEstimatedBytes + observation.Name.Utf8Bytes +
            "ACTIVE_TRANSACTION".Length, observation.EstimatedSizeBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("active_transaction")]
    [InlineData("ACTIVE TRANSACTION")]
    [InlineData("ACTIVE-TRANSACTION")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidStateCannotEnterThePayload(string value) =>
        Assert.Throws<ArgumentException>(() => Create(value));

    private static DatabaseObservation Create(string? description) => new(
        new MonitoredInstanceId(Guid.NewGuid()), new ObservationTargetRevision(1),
        5, new SqlServerObjectName("Ledger"), DatabaseOperationalState.Online,
        DatabaseRecoveryModel.Full, DatabaseUserAccess.MultiUser, false, 160,
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), description);
}
