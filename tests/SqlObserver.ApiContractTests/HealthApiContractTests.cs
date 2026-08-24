using System.Reflection;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class HealthApiContractTests
{
    private static readonly string[] ExpectedTargetProperties =
    [
        "Collectors",
        "CoreMetrics",
        "InstanceId",
        "RepositoryTimeUtc",
        "State",
    ];

    private static readonly string[] ExpectedCollectorProperties =
    [
        "CircuitState",
        "CollectorId",
        "DuplicateRows",
        "DurationMilliseconds",
        "HasVisibilityGap",
        "InsertedRows",
        "LastAttemptAtUtc",
        "LastExecutionOutcome",
        "LastSuccessAtUtc",
        "LossCountIsExact",
        "ManifestVersion",
        "MinimumLostBytes",
        "MinimumLostItems",
        "NextDueAtUtc",
        "OutputRows",
        "OutputSchemaVersion",
        "PersistedBytes",
        "Reason",
        "RejectedRows",
        "ResponseBytes",
        "RetryCount",
        "SampleLossKind",
        "ScheduledAtUtc",
        "SourceRows",
        "State",
    ];

    private static readonly string[] ExpectedMetricProperties =
    [
        "Dimensions",
        "MetricId",
        "ObservedAtUtc",
        "SampleId",
        "Value",
    ];

    private static readonly string[] ExpectedDimensionProperties =
    [
        "Key",
        "Value",
    ];

    private static readonly string[] ExpectedDatabasePageProperties =
    [
        "Collector",
        "InstanceId",
        "Items",
        "NextCursor",
        "RepositoryTimeUtc",
    ];

    private static readonly string[] ExpectedDatabaseProperties =
    [
        "Collector",
        "CompatibilityLevel",
        "DatabaseId",
        "IsReadOnly",
        "Name",
        "ObservedAtUtc",
        "RecoveryModel",
        "State",
        "UserAccess",
    ];

    private static readonly string[] ExpectedFilePageProperties =
    [
        "Collector",
        "InstanceId",
        "Items",
        "NextCursor",
        "RepositoryTimeUtc",
    ];

    private static readonly string[] ExpectedFileProperties =
    [
        "BytesRead",
        "BytesWritten",
        "Collector",
        "DatabaseId",
        "FileId",
        "FileType",
        "GrowthBytes",
        "GrowthPercent",
        "IoStallMilliseconds",
        "LogicalName",
        "MaximumSizeBytes",
        "ObservedAtUtc",
        "ReadCount",
        "SizeBytes",
        "State",
        "WriteCount",
    ];

    [Fact]
    public void TargetHealthResponseIsAClosedSafeProjection()
    {
        Assert.Equal(ExpectedTargetProperties, PublicPropertyNames<TargetHealthResponse>());
        Assert.Equal(ExpectedCollectorProperties, PublicPropertyNames<CollectorHealthResponse>());
        Assert.Equal(ExpectedMetricProperties, PublicPropertyNames<CoreMetricResponse>());
        Assert.Equal(ExpectedDimensionProperties, PublicPropertyNames<MetricDimensionResponse>());
        Assert.Equal(ExpectedDatabasePageProperties, PublicPropertyNames<DatabaseHealthPageResponse>());
        Assert.Equal(ExpectedDatabaseProperties, PublicPropertyNames<DatabaseHealthResponse>());
        Assert.Equal(ExpectedFilePageProperties, PublicPropertyNames<DatabaseFileHealthPageResponse>());
        Assert.Equal(ExpectedFileProperties, PublicPropertyNames<DatabaseFileHealthResponse>());

        string[] propertyNames = ExpectedTargetProperties
            .Concat(ExpectedCollectorProperties)
            .Concat(ExpectedMetricProperties)
            .Concat(ExpectedDimensionProperties)
            .Concat(ExpectedDatabasePageProperties)
            .Concat(ExpectedDatabaseProperties)
            .Concat(ExpectedFilePageProperties)
            .Concat(ExpectedFileProperties)
            .ToArray();
        Assert.DoesNotContain(
            propertyNames,
            static name => name.Contains("Connection", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Sql", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Secret", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(typeof(string), typeof(DatabaseFileHealthResponse).GetProperty("BytesRead")?.PropertyType);
        Assert.Equal(typeof(string), typeof(DatabaseFileHealthResponse).GetProperty("BytesWritten")?.PropertyType);
        Assert.Equal(typeof(string), typeof(DatabaseFileHealthResponse).GetProperty("SizeBytes")?.PropertyType);
    }

    private static string[] PublicPropertyNames<T>() => typeof(T)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(static property => property.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
