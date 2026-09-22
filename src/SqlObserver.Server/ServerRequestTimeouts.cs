using Microsoft.AspNetCore.Http.Timeouts;

namespace SqlObserver.Server;

/// <summary>HTTP budgets include bounded inventory, evidence composition, and response serialization.</summary>
public static class ServerRequestTimeouts
{
    public const string OverviewPolicyName = "overview";

    public static void Configure(RequestTimeoutOptions options)
    {
        options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(15),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        };
        options.AddPolicy(OverviewPolicyName, new RequestTimeoutPolicy
        {
            Timeout = TimeSpan.FromSeconds(30),
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        });
    }
}
