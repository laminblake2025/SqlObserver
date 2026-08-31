using SqlObserver.Domain.Collection;

namespace SqlObserver.PerformanceTests;

public sealed class M7QueryPerformanceBoundaryPerformanceTests
{
    [Fact] public void HardCapsAreBounded() { Assert.InRange(QueryPerformanceBounds.MaximumCandidatesPerDatabase,1,2000); Assert.InRange(QueryPerformanceBounds.MaximumObservationsPerDatabase,1,20000); Assert.InRange(QueryPerformanceBounds.ResponseBytes,1,8*1024*1024); }
}
