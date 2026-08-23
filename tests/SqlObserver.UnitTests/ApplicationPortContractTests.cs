using System.Reflection;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Coordination;
using SqlObserver.Domain.Repository;

namespace SqlObserver.UnitTests;

public sealed class ApplicationPortContractTests
{
    private static readonly Type[] PortTypes =
    [
        typeof(IMigrationPort),
        typeof(IPostgreSqlCompatibilityPort),
        typeof(IPartitionMaintenancePort),
        typeof(ITelemetryIngestionPort),
        typeof(IDiagnosticEventIngestionPort),
        typeof(ISensitivePayloadPort),
        typeof(IWorkerLeasePort),
        typeof(IObservationTargetRepositoryPort),
        typeof(ICapabilityProfileRepositoryPort),
        typeof(IAdministrativeAuditPort),
        typeof(ISqlServerCapabilityDiscoveryPort),
    ];

    [Fact]
    public void EveryPortOperationIsAsyncAndCancellationAware()
    {
        MethodInfo[] methods = PortTypes.SelectMany(static type => type.GetMethods()).ToArray();

        Assert.NotEmpty(methods);

        foreach (MethodInfo method in methods)
        {
            ParameterInfo[] parameters = method.GetParameters();

            Assert.NotEmpty(parameters);
            Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
            Assert.True(
                method.ReturnType == typeof(ValueTask) ||
                method.ReturnType.IsGenericType &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
        }
    }

    [Fact]
    public void PortsExposeNoRawStringOrUnboundedCollectionInput()
    {
        ParameterInfo[] parameters = PortTypes
            .SelectMany(static type => type.GetMethods())
            .SelectMany(static method => method.GetParameters())
            .Where(static parameter => parameter.ParameterType != typeof(CancellationToken))
            .ToArray();

        Assert.DoesNotContain(parameters, static parameter => parameter.ParameterType == typeof(string));
        Assert.DoesNotContain(parameters, static parameter =>
            parameter.ParameterType.IsGenericType &&
            parameter.ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>));
    }

    [Fact]
    public void EveryPortRequestCarriesAnExplicitBoundedTimeout()
    {
        Type[] requestTypes = PortTypes
            .SelectMany(static type => type.GetMethods())
            .Select(static method => method.GetParameters()[0].ParameterType)
            .Distinct()
            .ToArray();

        foreach (Type requestType in requestTypes)
        {
            PropertyInfo? timeout = requestType.GetProperty("Timeout");

            Assert.NotNull(timeout);
            Assert.True(
                timeout.PropertyType == typeof(RepositoryCallTimeout) ||
                timeout.PropertyType == typeof(CapabilityDiscoveryTimeout));
            Assert.DoesNotContain(
                requestType.GetProperties(),
                static property => property.PropertyType == typeof(string));
        }
    }

    [Fact]
    public void RequestContractsEnforceTimeoutAndResultBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryCallTimeout(
            RepositoryCallTimeout.Minimum - TimeSpan.FromTicks(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryCallTimeout(
            RepositoryCallTimeout.Maximum + TimeSpan.FromTicks(1)));

        var timeout = new RepositoryCallTimeout(TimeSpan.FromSeconds(30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MigrationApplyRequest(0, timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionPreviewRequest(
            new PartitionSetName("metric_samples"),
            DateTimeOffset.UtcNow,
            RetentionPreviewRequest.MaximumEntries + 1,
            timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PartitionCareRequest(
            new PartitionSetName("metric_samples"),
            PartitionGranularity.Daily,
            DateTimeOffset.UtcNow,
            PartitionCareRequest.MaximumPartitionsAhead + 1,
            CreateLeaseIdentity(),
            timeout));
    }

    [Fact]
    public void MigrationRequestDoesNotDependOnLeaseTableBootstrap()
    {
        var request = new MigrationApplyRequest(
            maxMigrations: 10,
            new RepositoryCallTimeout(TimeSpan.FromMinutes(1)));

        Assert.Equal(10, request.MaxMigrations);
        Assert.DoesNotContain(
            typeof(MigrationApplyRequest).GetProperties(),
            static property => property.PropertyType == typeof(WorkerLeaseIdentity));
    }

    [Fact]
    public void PostgreSqlVersionUsesPostVersionTenMajorAndUpdateSemantics()
    {
        var version = new PostgreSqlVersion(18, 4);

        Assert.Equal(18, version.Major);
        Assert.Equal(4, version.Update);
        Assert.Equal("18.4", version.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgreSqlVersion(18, -1));
    }

    private static WorkerLeaseIdentity CreateLeaseIdentity() =>
        new(
            new WorkerLeaseKey("partition/telemetry"),
            new WorkerExecutionId(Guid.Parse("f7e57402-065f-468d-9e57-e01883ee922e")),
            new FencingToken(1));
}
