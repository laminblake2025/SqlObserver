using SqlObserver.Domain.Deployment;
using SqlObserver.Domain.Targets;

namespace SqlObserver.Application.Services;

/// <summary>Projects existing endpoint/target policy types into safe assessment facts.</summary>
public static class DeploymentSecurityObservationFactory
{
    public static DeploymentSecurityObservation ObserveMcpEndpoint(string? endpointText)
    {
        if (string.IsNullOrWhiteSpace(endpointText))
            return new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Missing, "endpoint_missing");
        return McpEndpointValidator.TryValidate(endpointText, out _)
            ? new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Accepted, "https_mcp_endpoint")
            : new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Unsafe, "endpoint_invalid");
    }

    public static DeploymentSecurityObservation ObserveTargetConnectionPolicy(SqlServerConnectionPolicy? policy)
    {
        if (policy is null)
            return new DeploymentSecurityObservation("target_connection_policy", DeploymentSecurityObservationDisposition.Missing, "policy_missing");
        return policy.AuthenticationMode == SqlServerAuthenticationMode.WindowsIntegrated &&
            policy.TransportSecurity == SqlServerTransportSecurity.EncryptAndValidateCertificate
            ? new DeploymentSecurityObservation("target_connection_policy", DeploymentSecurityObservationDisposition.Accepted, "validated_integrated_policy")
            : new DeploymentSecurityObservation("target_connection_policy", DeploymentSecurityObservationDisposition.Unsafe, "policy_not_validated");
    }

    public static DeploymentSecurityObservation ObserveSensitiveContent(bool? enabled)
    {
        if (enabled is null)
            return new DeploymentSecurityObservation("sensitive_content", DeploymentSecurityObservationDisposition.Missing, "sensitive_content_unspecified");
        return enabled.Value
            ? new DeploymentSecurityObservation("sensitive_content", DeploymentSecurityObservationDisposition.Unsafe, "sensitive_content_enabled")
            : new DeploymentSecurityObservation("sensitive_content", DeploymentSecurityObservationDisposition.Accepted, "sensitive_content_disabled");
    }
}
