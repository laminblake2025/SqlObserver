using System.Reflection;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class M5ActivityApiContractTests
{
    [Fact]
    public void ActivityDtosExposeOnlyBoundedSafeFields()
    {
        Type[] dtoTypes =
        [
            typeof(ActivitySessionResponse), typeof(ActivityRequestResponse),
            typeof(ServerWaitSummaryResponse), typeof(BlockingEdgeResponse),
            typeof(BlockingHistoryResponse),
        ];
        string[] propertyNames = dtoTypes
            .SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(static property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, static name =>
            name.Contains("Query", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Plan", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Login", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Resource", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Physical", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BlockingHistoryContractKeepsEvidenceSeparateFromEdge()
    {
        PropertyInfo[] properties = typeof(BlockingHistoryResponse).GetProperties();
        Assert.Equal(["Evidence", "Edge"], properties.Select(static property => property.Name));
        Assert.Equal(typeof(ActivitySnapshotEvidenceResponse), properties[0].PropertyType);
        Assert.Equal(typeof(BlockingEdgeResponse), properties[1].PropertyType);
    }
}
