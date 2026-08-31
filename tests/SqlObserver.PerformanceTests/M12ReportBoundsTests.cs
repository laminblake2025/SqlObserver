using SqlObserver.Reporting;

namespace SqlObserver.PerformanceTests;

public sealed class M12ReportBoundsTests
{
    [Fact] public void ReportBoundsRemainFixedAndBounded() { Assert.Equal(200, ReportContract.PageRows); Assert.Equal(2_000, ReportContract.HtmlRows); Assert.Equal(10_000, ReportContract.TotalRows); Assert.Equal(2, ReportContract.MaximumActorConcurrency); Assert.Equal(16, ReportContract.MaximumGlobalConcurrency); Assert.Equal(TimeSpan.FromHours(24), ReportContract.MaterializationRetention); }
}
