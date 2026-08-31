using System.Reflection;
using SqlObserver.Server;

namespace SqlObserver.SecurityTests;

public sealed class M5ActivitySecurityPolicyTests
{
    [Fact]
    public void ActivityContractsDoNotExposeSensitiveProviderIdentityOrQueryFields()
    {
        Type[] types =
        [
            typeof(ActivitySessionResponse), typeof(ActivityRequestResponse),
            typeof(ServerWaitSummaryResponse), typeof(BlockingEdgeResponse),
            typeof(BlockingHistoryResponse), typeof(ActivitySnapshotEvidenceResponse),
        ];
        string[] names = types.SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(static property => property.Name).ToArray();
        Assert.DoesNotContain(names, static name =>
            name.Contains("Query", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Handle", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Plan", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Login", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Resource", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Physical", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
    }
}
