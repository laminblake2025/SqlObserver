using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Security;

namespace SqlObserver.Server;

/// <summary>Configuration-only representation of one Windows group authorization binding.</summary>
public sealed class WindowsGroupAuthorizationOptions
{
    public string GroupSid { get; init; } = string.Empty;

    public string[] Roles { get; init; } = [];

    public bool AllTargets { get; init; }

    public Guid[] TargetIds { get; init; } = [];
}

/// <summary>Bounded SID-based authorization configuration for the web process.</summary>
public sealed class WindowsAuthorizationOptions
{
    public WindowsGroupAuthorizationOptions[] Bindings { get; init; } = [];

    public string[] DisabledActorSids { get; init; } = [];
}

public static class WindowsAuthorizationConfiguration
{
    public const string SectionName = "SqlObserver:Authorization";

    public static WindowsGroupRoleResolver CreateResolver(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        WindowsAuthorizationOptions options = configuration
            .GetSection(SectionName)
            .Get<WindowsAuthorizationOptions>() ?? new WindowsAuthorizationOptions();

        WindowsGroupRoleBinding[] bindings = options.Bindings
            .Select(CreateBinding)
            .ToArray();
        ActorSecurityIdentifier[] disabled = options.DisabledActorSids
            .Select(static sid => new ActorSecurityIdentifier(sid))
            .ToArray();

        return new WindowsGroupRoleResolver(bindings, disabled);
    }

    private static WindowsGroupRoleBinding CreateBinding(WindowsGroupAuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ApplicationRole[] roles = options.Roles.Select(ParseRole).ToArray();
        MonitoredInstanceId[] targetIds = options.TargetIds
            .Select(static id => new MonitoredInstanceId(id))
            .ToArray();

        return new WindowsGroupRoleBinding(
            new ActorSecurityIdentifier(options.GroupSid),
            roles,
            options.AllTargets,
            targetIds);
    }

    private static ApplicationRole ParseRole(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: false, out ApplicationRole role) || !Enum.IsDefined(role))
        {
            throw new InvalidOperationException($"Unknown SqlObserver application role '{value}'.");
        }

        return role;
    }
}
