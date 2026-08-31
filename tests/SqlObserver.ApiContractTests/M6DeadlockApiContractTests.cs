using System.Reflection;
using SqlObserver.Application.Ports;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class M6DeadlockApiContractTests
{
    [Fact]
    public void DeadlockDtosExposeOnlyTypedBoundedEvidence()
    {
        Type[] types = [typeof(DeadlockPageResponse), typeof(DeadlockSummaryResponse), typeof(DeadlockDetailResponse), typeof(DeadlockParticipantResponse), typeof(DeadlockRelationResponse)];
        string[] names = types.SelectMany(static type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Select(static property => property.Name).ToArray();
        Assert.DoesNotContain(names, static name => name.Contains("Xml", StringComparison.OrdinalIgnoreCase) || name.Contains("Query", StringComparison.OrdinalIgnoreCase) || name.Contains("Host", StringComparison.OrdinalIgnoreCase) || name.Contains("Login", StringComparison.OrdinalIgnoreCase) || name.Contains("Path", StringComparison.OrdinalIgnoreCase) || name.Contains("Provider", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(typeof(Guid), typeof(DeadlockPageResponse).GetProperty(nameof(DeadlockPageResponse.TargetId))!.PropertyType);
        Assert.Equal(typeof(string), typeof(DeadlockSummaryResponse).GetProperty(nameof(DeadlockSummaryResponse.Fingerprint))!.PropertyType);
    }

    [Fact]
    public void DetailContractSeparatesSummaryParticipantsAndRelations()
    {
        Assert.Equal(["Summary", "Participants", "Relations"], typeof(DeadlockDetailResponse).GetProperties().Select(static property => property.Name));
    }

    [Fact]
    public async Task ProjectionServiceRejectsRepositoryDetailForRequestedEvent()
    {
        MonitoredInstanceId target = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        Guid requested = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var repository = new MismatchedDeadlockRepository(target);
        var service = new DeadlockProjectionQueryService(repository);
        var authorization = new AuthorizationContext(new ActorSecurityIdentifier("S-1-5-21"), AuthorizationPrincipalState.Active, [ApplicationRole.Viewer], TargetAuthorizationScope.ForTargets([target]));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetDeadlockAsync(authorization, target, requested, new RepositoryCallTimeout(TimeSpan.FromSeconds(1)), CancellationToken.None).AsTask());
    }

    private sealed class MismatchedDeadlockRepository(MonitoredInstanceId target) : IDeadlockProjectionRepositoryPort
    {
        public ValueTask<DeadlockPage?> ListDeadlocksAsync(ListDeadlocksRepositoryRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<DeadlockPage?>(null);
        public ValueTask<DeadlockDetailDto?> GetDeadlockAsync(MonitoredInstanceId targetId, Guid eventId, RepositoryCallTimeout timeout, CancellationToken cancellationToken)
        {
            DeadlockSummaryDto summary = new(target, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), DateTimeOffset.UtcNow, new string('a', 64), 0, 0, false, DateTimeOffset.UtcNow);
            return ValueTask.FromResult<DeadlockDetailDto?>(new DeadlockDetailDto(summary, [], []));
        }
    }
}
