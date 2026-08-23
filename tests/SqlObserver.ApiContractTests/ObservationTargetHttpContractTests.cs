using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlObserver.Application.Services;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class ObservationTargetHttpContractTests : IClassFixture<ObservationTargetApiFactory>
{
    private readonly ObservationTargetApiFactory _factory;

    public ObservationTargetHttpContractTests(ObservationTargetApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnauthenticatedAdministrativeWriteIsChallenged()
    {
        using HttpClient client = CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ViewerCannotRegisterTarget()
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration());
        SqlObserverProblemResponse? problem = await response.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", problem?.Code);
        Assert.True(Guid.TryParse(problem?.CorrelationId, out _));
    }

    [Fact]
    public async Task AdministratorRegistersCredentialFreePendingTarget()
    {
        using HttpClient client = CreateClient("admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration());
        ObservationTargetResponse? target = await response.Content
            .ReadFromJsonAsync<ObservationTargetResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("pending_discovery", target?.Lifecycle);
        Assert.Equal("pending", target?.CapabilityStatus);
        Assert.Equal("windows_integrated_service_identity", target?.AuthenticationMode);
        Assert.Equal("mandatory_validated", target?.EncryptionMode);
        Assert.DoesNotContain(
            "password",
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegistrationRequiresNonemptyClientOwnedInstanceId()
    {
        using HttpClient client = CreateClient("admin");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(Guid.Empty));
        SqlObserverProblemResponse? problem = await response.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", problem?.Code);
    }

    [Fact]
    public async Task ExactRegistrationReplayReturnsTheClientOwnedTarget()
    {
        using var factory = new ObservationTargetApiFactory();
        using HttpClient client = CreateClient(factory, "admin");
        RegisterObservationTargetBody registration = ValidRegistration(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "lab.replay");

        HttpResponseMessage firstResponse = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            registration);
        HttpResponseMessage replayResponse = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            registration);
        ObservationTargetResponse? firstTarget = await firstResponse.Content
            .ReadFromJsonAsync<ObservationTargetResponse>();
        ObservationTargetResponse? replayedTarget = await replayResponse.Content
            .ReadFromJsonAsync<ObservationTargetResponse>();

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(registration.InstanceId, firstTarget?.InstanceId);
        Assert.Equal(firstTarget?.InstanceId, replayedTarget?.InstanceId);
        Assert.Equal(firstTarget?.ConfigurationRevision, replayedTarget?.ConfigurationRevision);
        Assert.Equal(firstTarget?.Lifecycle, replayedTarget?.Lifecycle);
    }

    [Fact]
    public async Task ReplayOfActiveTargetPreservesAuthoritativeCapabilityProjection()
    {
        using var factory = new ObservationTargetApiFactory();
        using HttpClient client = CreateClient(factory, "admin");
        var registration = new RegisterObservationTargetBody(
            FakeTargetApplicationServices.ExistingInstanceId,
            "lab.primary",
            "Lab primary",
            "sql01.contoso.example",
            NamedInstance: null,
            TcpPort: 1433,
            "sql01.contoso.example");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            registration);
        ObservationTargetResponse? target = await response.Content
            .ReadFromJsonAsync<ObservationTargetResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("active", target?.Lifecycle);
        Assert.Equal("degraded", target?.CapabilityStatus);
        Assert.Equal(["authentication_scheme_fallback"], target?.CapabilityReasons);
        Assert.Equal(FakeTargetApplicationServices.RepositoryTimestamp, target?.LastDiscoveryAtUtc);
    }

    [Fact]
    public async Task PerPrincipalRateLimitBoundsDeniedMutationAuditsAndReturnsSafe429()
    {
        using var factory = new ObservationTargetApiFactory(
            requestsPerWindow: 2,
            concurrentRequests: 2);
        using HttpClient client = CreateClient(factory, "viewer");

        HttpResponseMessage firstResponse = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "denied.one"));
        HttpResponseMessage secondResponse = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "denied.two"));
        HttpResponseMessage rejectedResponse = await client.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "must-not-reach-audit"));
        string rejectedBody = await rejectedResponse.Content.ReadAsStringAsync();
        SqlObserverProblemResponse? problem = await rejectedResponse.Content
            .ReadFromJsonAsync<SqlObserverProblemResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
        Assert.Equal("rate_limited", problem?.Code);
        Assert.True(Guid.TryParse(problem?.CorrelationId, out _));
        Assert.True(rejectedResponse.Headers.Contains("Retry-After"));
        Assert.DoesNotContain("must-not-reach-audit", rejectedBody, StringComparison.Ordinal);
        Assert.Equal(
            2,
            factory.Targets.GetOnboardingCallCount(TestAuthenticationHandler.ViewerActorSid));

        HttpResponseMessage readResponse = await client.GetAsync(
            "/api/v1/observation-targets?limit=50");
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
    }

    [Fact]
    public async Task ConcurrencyLimitIsIndependentForEachStablePrincipal()
    {
        using var factory = new ObservationTargetApiFactory(
            requestsPerWindow: 100,
            concurrentRequests: 1);
        using HttpClient firstPrincipal = CreateClient(factory, "admin");
        using HttpClient secondPrincipal = CreateClient(factory, "admin-secondary");

        Task<HttpResponseMessage> blockingRequest = firstPrincipal.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: FakeTargetApplicationServices.BlockingInstanceKey));
        await factory.Targets.BlockingCallStarted.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            HttpResponseMessage samePrincipalResponse = await firstPrincipal.PostAsJsonAsync(
                "/api/v1/observation-targets",
                ValidRegistration(instanceKey: "lab.same-principal"));
            HttpResponseMessage otherPrincipalResponse = await secondPrincipal.PostAsJsonAsync(
                "/api/v1/observation-targets",
                ValidRegistration(instanceKey: "lab.other-principal"));

            Assert.Equal(HttpStatusCode.TooManyRequests, samePrincipalResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Created, otherPrincipalResponse.StatusCode);
        }
        finally
        {
            factory.Targets.ReleaseBlockingCall();
        }

        HttpResponseMessage completedResponse = await blockingRequest;
        Assert.Equal(HttpStatusCode.Created, completedResponse.StatusCode);
    }

    [Fact]
    public async Task NameIdentifierProvidesAStablePrincipalPartition()
    {
        using var factory = new ObservationTargetApiFactory(
            requestsPerWindow: 1,
            concurrentRequests: 1);
        using HttpClient firstPrincipal = CreateClient(factory, "name-id-admin");
        using HttpClient secondPrincipal = CreateClient(factory, "name-id-admin-secondary");

        HttpResponseMessage firstResponse = await firstPrincipal.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "lab.name-id-one"));
        HttpResponseMessage secondResponse = await secondPrincipal.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "lab.name-id-two"));

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
    }

    [Fact]
    public async Task AccountNamesWithoutStableIdentifiersShareFailClosedPartition()
    {
        using var factory = new ObservationTargetApiFactory(
            requestsPerWindow: 1,
            concurrentRequests: 1);
        using HttpClient firstPrincipal = CreateClient(factory, "fallback-named-one");
        using HttpClient secondPrincipal = CreateClient(factory, "fallback-named-two");

        HttpResponseMessage admittedResponse = await firstPrincipal.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "denied.fallback-one"));
        HttpResponseMessage rejectedResponse = await secondPrincipal.PostAsJsonAsync(
            "/api/v1/observation-targets",
            ValidRegistration(instanceKey: "denied.fallback-two"));

        Assert.Equal(HttpStatusCode.Forbidden, admittedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedResponse.StatusCode);
    }

    [Fact]
    public async Task UnknownCredentialFieldIsRejectedWithoutEchoingItsValue()
    {
        using HttpClient client = CreateClient("admin");
        const string secretMarker = "do-not-echo-this-value";
        using var content = new StringContent(
            $$"""
            {
              "instanceKey": "lab.primary",
              "instanceId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "displayName": "Lab primary",
              "host": "sql01.contoso.example",
              "namedInstance": null,
              "tcpPort": 1433,
              "certificateHostName": "sql01.contoso.example",
              "password": "{{secretMarker}}"
            }
            """,
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/observation-targets",
            content);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(secretMarker, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetListReturnsCurrentSafeCapabilityOutcome()
    {
        using HttpClient client = CreateClient("viewer");

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/observation-targets?limit=50");
        string responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, responseBody);
        ObservationTargetPageResponse? page = await response.Content
            .ReadFromJsonAsync<ObservationTargetPageResponse>();

        ObservationTargetResponse target = Assert.Single(page?.Items ?? []);
        Assert.Equal("degraded", target.CapabilityStatus);
        Assert.Equal("active", target.Lifecycle);
        Assert.Equal(["authentication_scheme_fallback"], target.CapabilityReasons);
    }

    [Fact]
    public async Task DisabledLifecycleIsMappedExplicitly()
    {
        using HttpClient client = CreateClient("disabled");

        ObservationTargetPageResponse? page = await client
            .GetFromJsonAsync<ObservationTargetPageResponse>(
                "/api/v1/observation-targets?limit=50");

        ObservationTargetResponse target = Assert.Single(page?.Items ?? []);
        Assert.Equal("disabled", target.Lifecycle);
        Assert.Equal("disabled", target.CapabilityStatus);
        Assert.Empty(target.CapabilityReasons);
    }

    [Fact]
    public async Task TargetScopeIsAppliedBeforePaging()
    {
        using HttpClient client = CreateClient("scoped");

        ObservationTargetPageResponse? page = await client.GetFromJsonAsync<ObservationTargetPageResponse>(
            "/api/v1/observation-targets?limit=50");

        Assert.Empty(page?.Items ?? []);
    }

    [Fact]
    public async Task DeclaredOversizedBodyIsRejectedBeforeBinding()
    {
        using HttpClient client = CreateClient("admin");
        using var content = new StringContent(
            new string('x', 65_537),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.PostAsync(
            "/api/v1/observation-targets",
            content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task UnhandledFailureNeverSerializesProviderOrSecretText()
    {
        using HttpClient client = CreateClient("error");

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/observation-targets?limit=50");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("supersecret", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection string", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request_failed", body, StringComparison.Ordinal);
    }

    private HttpClient CreateClient(string? identity = null)
    {
        return CreateClient(_factory, identity);
    }

    private static HttpClient CreateClient(
        ObservationTargetApiFactory factory,
        string? identity = null)
    {
        HttpClient client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (identity is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthenticationHandler.IdentityHeader, identity);
        }

        return client;
    }

    private static RegisterObservationTargetBody ValidRegistration(
        Guid? instanceId = null,
        string instanceKey = "lab.new") => new(
        instanceId ?? Guid.NewGuid(),
        instanceKey,
        $"Display for {instanceKey}",
        "sql01.contoso.example",
        NamedInstance: null,
        TcpPort: 1433,
        "sql01.contoso.example");
}

public sealed class ObservationTargetApiFactory : WebApplicationFactory<Program>
{
    private readonly int? _requestsPerWindow;
    private readonly int? _concurrentRequests;

    public ObservationTargetApiFactory()
    {
    }

    internal ObservationTargetApiFactory(int requestsPerWindow, int concurrentRequests)
    {
        _requestsPerWindow = requestsPerWindow;
        _concurrentRequests = concurrentRequests;
    }

    internal FakeTargetApplicationServices Targets { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("ContractTesting");
        if (_requestsPerWindow is not null)
        {
            builder.UseSetting(
                "SqlObserver:AdministrativeMutationLimits:RequestsPerWindow",
                _requestsPerWindow.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_concurrentRequests is not null)
        {
            builder.UseSetting(
                "SqlObserver:AdministrativeMutationLimits:ConcurrentRequests",
                _concurrentRequests.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureLogging(static logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObservationTargetOnboardingService>();
            services.RemoveAll<IObservationTargetManagementService>();
            services.RemoveAll<IObservationTargetStatusQueryService>();
            services.RemoveAll<WindowsGroupRoleResolver>();

            services.AddSingleton<IObservationTargetOnboardingService>(Targets);
            services.AddSingleton<IObservationTargetManagementService>(Targets);
            services.AddSingleton<IObservationTargetStatusQueryService>(Targets);
            services.AddSingleton(CreateAuthorizationResolver());
            services
                .AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    static _ => { });
        });
    }

    private static WindowsGroupRoleResolver CreateAuthorizationResolver()
    {
        return new WindowsGroupRoleResolver(
        [
            new WindowsGroupRoleBinding(
                new ActorSecurityIdentifier(TestAuthenticationHandler.AdminGroupSid),
                [ApplicationRole.TargetAdministrator, ApplicationRole.Viewer],
                allTargets: true),
            new WindowsGroupRoleBinding(
                new ActorSecurityIdentifier(TestAuthenticationHandler.ViewerGroupSid),
                [ApplicationRole.Viewer],
                allTargets: true),
            new WindowsGroupRoleBinding(
                new ActorSecurityIdentifier(TestAuthenticationHandler.ScopedGroupSid),
                [ApplicationRole.Viewer],
                allTargets: false,
                [new MonitoredInstanceId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))]),
            new WindowsGroupRoleBinding(
                new ActorSecurityIdentifier(TestAuthenticationHandler.ErrorGroupSid),
                [ApplicationRole.Viewer],
                allTargets: true),
            new WindowsGroupRoleBinding(
                new ActorSecurityIdentifier(TestAuthenticationHandler.DisabledGroupSid),
                [ApplicationRole.Viewer],
                allTargets: true),
        ]);
    }
}

internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    internal const string SchemeName = "SqlObserver.Tests";
    internal const string IdentityHeader = "X-SqlObserver-Test-Identity";
    internal const string AdminGroupSid = "S-1-5-21-2000";
    internal const string ViewerGroupSid = "S-1-5-21-2001";
    internal const string ScopedGroupSid = "S-1-5-21-2002";
    internal const string ErrorGroupSid = "S-1-5-21-2003";
    internal const string DisabledGroupSid = "S-1-5-21-2004";
    internal const string ViewerActorSid = "S-1-5-21-1001";

    public TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(IdentityHeader, out var values) || values.Count != 1)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        (string? PrimarySid, string? NameIdentifier, string? AccountName, string GroupSid)? identity =
            values[0] switch
            {
                "admin" => ("S-1-5-21-1000", null, null, AdminGroupSid),
                "admin-secondary" => ("S-1-5-21-1004", null, null, AdminGroupSid),
                "name-id-admin" => (null, "S-1-5-21-1006", null, AdminGroupSid),
                "name-id-admin-secondary" => (null, "S-1-5-21-1007", null, AdminGroupSid),
                "fallback-named-one" => (null, null, "CONTOSO\\fallback-one", AdminGroupSid),
                "fallback-named-two" => (null, null, "CONTOSO\\fallback-two", AdminGroupSid),
                "viewer" => (ViewerActorSid, null, null, ViewerGroupSid),
                "scoped" => ("S-1-5-21-1002", null, null, ScopedGroupSid),
                "error" => (FakeTargetApplicationServices.ErrorActorSid, null, null, ErrorGroupSid),
                "disabled" =>
                    (FakeTargetApplicationServices.DisabledActorSid, null, null, DisabledGroupSid),
                _ => null,
            };
        if (identity is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Unknown test identity."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.GroupSid, identity.Value.GroupSid),
        };
        if (identity.Value.PrimarySid is not null)
        {
            claims.Add(new Claim(ClaimTypes.PrimarySid, identity.Value.PrimarySid));
        }

        if (identity.Value.NameIdentifier is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, identity.Value.NameIdentifier));
        }

        if (identity.Value.AccountName is not null)
        {
            claims.Add(new Claim(ClaimTypes.Name, identity.Value.AccountName));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

internal sealed class FakeTargetApplicationServices :
    IObservationTargetOnboardingService,
    IObservationTargetManagementService,
    IObservationTargetStatusQueryService
{
    internal const string ErrorActorSid = "S-1-5-21-1003";
    internal const string DisabledActorSid = "S-1-5-21-1005";
    internal const string BlockingInstanceKey = "lab.blocking";
    private static readonly DateTimeOffset RepositoryTime =
        new(2026, 8, 23, 16, 0, 0, TimeSpan.Zero);
    private static readonly MonitoredInstanceId ExistingId =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly ObservationTarget ExistingTarget = CreateExistingTarget();
    private static readonly ObservationTarget DisabledTarget = CreateDisabledTarget();
    private static readonly CapabilityProfile ExistingProfile = CreateExistingProfile();
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ObservationTarget> _registrationsById = new()
    {
        [ExistingTarget.TargetId.Value] = ExistingTarget,
    };
    private readonly Dictionary<string, Guid> _registrationIdsByKey = new(StringComparer.Ordinal)
    {
        [ExistingTarget.Key.Value] = ExistingTarget.TargetId.Value,
    };
    private readonly Dictionary<string, int> _onboardingCallsByActor = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _blockingCallStarted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _releaseBlockingCall = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task BlockingCallStarted => _blockingCallStarted.Task;

    internal static Guid ExistingInstanceId => ExistingId.Value;

    internal static DateTimeOffset RepositoryTimestamp => RepositoryTime;

    internal void ReleaseBlockingCall() => _releaseBlockingCall.TrySetResult(true);

    internal int GetOnboardingCallCount(string actorSid)
    {
        lock (_sync)
        {
            return _onboardingCallsByActor.GetValueOrDefault(actorSid);
        }
    }

    public async ValueTask<ObservationTargetOnboardingResult> OnboardAsync(
        OnboardObservationTargetCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _onboardingCallsByActor[command.Authorization.ActorSid.Value] =
                _onboardingCallsByActor.GetValueOrDefault(command.Authorization.ActorSid.Value) + 1;
        }

        if (!command.Authorization.HasRole(ApplicationRole.TargetAdministrator) ||
            !command.Authorization.TargetScope.AllTargets)
        {
            return new ObservationTargetOnboardingResult(
                ObservationTargetOnboardingStatus.Denied,
                target: null,
                AdministrativeAuditReason.RequiredRoleMissing);
        }

        ObservationTargetRegistration registration = command.Registration;
        if (registration.Key.Value == BlockingInstanceKey)
        {
            _blockingCallStarted.TrySetResult(true);
            await _releaseBlockingCall.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (_registrationsById.TryGetValue(registration.TargetId.Value, out ObservationTarget? existing))
            {
                bool isExactReplay = RegistrationMatches(existing, registration);
                return new ObservationTargetOnboardingResult(
                    isExactReplay
                        ? ObservationTargetOnboardingStatus.AlreadyExists
                        : ObservationTargetOnboardingStatus.Conflict,
                    isExactReplay ? existing : null,
                    AdministrativeAuditReason.AlreadyExists);
            }

            if (_registrationIdsByKey.ContainsKey(registration.Key.Value))
            {
                return new ObservationTargetOnboardingResult(
                    ObservationTargetOnboardingStatus.Conflict,
                    target: null,
                    AdministrativeAuditReason.AlreadyExists);
            }

            var target = new ObservationTarget(
                registration.TargetId,
                registration.Key,
                registration.DisplayName,
                registration.ConnectionPolicy,
                ObservationTargetLifecycle.PendingDiscovery,
                new ObservationTargetRevision(1),
                RepositoryTime,
                RepositoryTime,
                RepositoryTime);
            _registrationsById.Add(target.TargetId.Value, target);
            _registrationIdsByKey.Add(target.Key.Value, target.TargetId.Value);
            return new ObservationTargetOnboardingResult(
                ObservationTargetOnboardingStatus.RegisteredPendingDiscovery,
                target,
                AdministrativeAuditReason.Completed);
        }
    }

    public ValueTask<ObservationTargetManagementResult> UpdateAsync(
        UpdateObservationTargetCommand command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Manage(command.Authorization, command.TargetId, command.ExpectedRevision));

    public ValueTask<ObservationTargetManagementResult> RetireAsync(
        RetireObservationTargetCommand command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Manage(command.Authorization, command.TargetId, command.ExpectedRevision));

    public ValueTask<ObservationTargetManagementResult> RequestRediscoveryAsync(
        RequestCapabilityRediscoveryCommand command,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Manage(command.Authorization, command.TargetId, command.ExpectedRevision));

    public ValueTask<ObservationTargetStatusSnapshot?> GetAsync(
        GetObservationTargetStatusQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureReadable(query.Authorization);
        if (!query.Authorization.TargetScope.Contains(query.TargetId))
        {
            return ValueTask.FromResult<ObservationTargetStatusSnapshot?>(null);
        }

        ObservationTarget? registeredTarget;
        lock (_sync)
        {
            _registrationsById.TryGetValue(query.TargetId.Value, out registeredTarget);
        }

        if (registeredTarget is null)
        {
            return ValueTask.FromResult<ObservationTargetStatusSnapshot?>(null);
        }

        ObservationTarget visibleTarget = query.TargetId == ExistingId
            ? GetVisibleTarget(query.Authorization)
            : registeredTarget;
        CapabilityProfile? profile = query.TargetId == ExistingId ? ExistingProfile : null;
        var result = new ObservationTargetStatusSnapshot(visibleTarget, profile);
        return ValueTask.FromResult<ObservationTargetStatusSnapshot?>(result);
    }

    public ValueTask<ObservationTargetStatusPage> ListAsync(
        ListObservationTargetsQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureReadable(query.Authorization);
        ObservationTarget visibleTarget = GetVisibleTarget(query.Authorization);
        ObservationTargetStatusSnapshot[] targets = query.Authorization.TargetScope.Contains(ExistingId)
            ? [new ObservationTargetStatusSnapshot(visibleTarget, ExistingProfile)]
            : [];
        return ValueTask.FromResult(new ObservationTargetStatusPage(targets, nextCursor: null));
    }

    private static void EnsureReadable(AuthorizationContext authorization)
    {
        if (authorization.ActorSid.Value == ErrorActorSid)
        {
            throw new InvalidOperationException(
                "provider connection string Password=supersecret must never reach the response");
        }

        if (!authorization.HasRole(ApplicationRole.Viewer))
        {
            throw new UnauthorizedAccessException();
        }
    }

    private static ObservationTarget GetVisibleTarget(AuthorizationContext authorization) =>
        authorization.ActorSid.Value == DisabledActorSid ? DisabledTarget : ExistingTarget;

    private static bool RegistrationMatches(
        ObservationTarget existing,
        ObservationTargetRegistration registration)
    {
        return existing.Key == registration.Key &&
            existing.DisplayName == registration.DisplayName &&
            existing.ConnectionPolicy.Endpoint.HostName ==
                registration.ConnectionPolicy.Endpoint.HostName &&
            existing.ConnectionPolicy.Endpoint.InstanceName ==
                registration.ConnectionPolicy.Endpoint.InstanceName &&
            existing.ConnectionPolicy.Endpoint.TcpPort ==
                registration.ConnectionPolicy.Endpoint.TcpPort &&
            existing.ConnectionPolicy.CertificateHostName ==
                registration.ConnectionPolicy.CertificateHostName;
    }

    private static ObservationTargetManagementResult Manage(
        AuthorizationContext authorization,
        MonitoredInstanceId targetId,
        ObservationTargetRevision expectedRevision)
    {
        if (!authorization.HasRole(ApplicationRole.TargetAdministrator) ||
            !authorization.TargetScope.Contains(targetId))
        {
            return new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.Denied,
                target: null,
                AdministrativeAuditReason.RequiredRoleMissing);
        }

        if (targetId != ExistingId)
        {
            return new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.NotFound,
                target: null,
                AdministrativeAuditReason.TargetNotFound);
        }

        return expectedRevision != ExistingTarget.Revision
            ? new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.RevisionConflict,
                target: null,
                AdministrativeAuditReason.RevisionConflict)
            : new ObservationTargetManagementResult(
                ObservationTargetManagementStatus.Applied,
                ExistingTarget,
                AdministrativeAuditReason.Completed);
    }

    private static ObservationTarget CreateExistingTarget()
    {
        var policy = new SqlServerConnectionPolicy(
            new SqlServerEndpoint(new SqlServerHostName("sql01.contoso.example"), tcpPort: 1433),
            new SqlServerConnectTimeout(TimeSpan.FromSeconds(5)),
            new SqlServerCertificateHostName("sql01.contoso.example"));
        return new ObservationTarget(
            ExistingId,
            new ObservationTargetKey("lab.primary"),
            new ObservationTargetDisplayName("Lab primary"),
            policy,
            ObservationTargetLifecycle.Active,
            new ObservationTargetRevision(1),
            RepositoryTime,
            RepositoryTime,
            RepositoryTime);
    }

    private static ObservationTarget CreateDisabledTarget()
    {
        ObservationTarget active = ExistingTarget;
        return new ObservationTarget(
            active.TargetId,
            active.Key,
            active.DisplayName,
            active.ConnectionPolicy,
            ObservationTargetLifecycle.Disabled,
            active.Revision,
            active.CreatedAtUtc,
            active.DiscoveryRequestedAtUtc,
            active.UpdatedAtUtc);
    }

    private static CapabilityProfile CreateExistingProfile()
    {
        return new CapabilityProfile(
            ExistingId,
            ExistingTarget.Revision,
            new CollectorId("capability.connection"),
            collectorManifestVersion: 1,
            outputSchemaVersion: 1,
            new SqlServerIdentity(
                new SqlServerVersion(16, 0, 1000, 0),
                new SqlServerEditionName("Developer Edition"),
                SqlServerEngineEdition.Enterprise,
                SqlServerPlatform.Windows),
            CapabilityDiscoveryOutcome.Degraded,
            CapabilityDiscoveryReason.AuthenticationSchemeFallback,
            SqlServerAuthenticationScheme.Ntlm,
            transportEncrypted: true,
            isSysAdmin: false,
            capabilities: [],
            permissions: [],
            TimeSpan.FromMilliseconds(10),
            evidenceBytes: 64,
            RepositoryTime,
            RepositoryTime.AddMinutes(5));
    }
}
