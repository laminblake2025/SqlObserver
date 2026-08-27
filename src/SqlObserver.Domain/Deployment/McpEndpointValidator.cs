namespace SqlObserver.Domain.Deployment;

/// <summary>Shared credential-free MCP endpoint validator used by stdio and assessments.</summary>
public static class McpEndpointValidator
{
    public static bool TryValidate(string endpointText, out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out Uri? candidate) || candidate is null ||
            candidate.Scheme != Uri.UriSchemeHttps || !string.Equals(candidate.AbsolutePath, "/mcp", StringComparison.Ordinal) ||
            candidate.UserInfo.Length != 0 || !string.IsNullOrEmpty(candidate.Query) || !string.IsNullOrEmpty(candidate.Fragment))
            return false;
        endpoint = candidate;
        return true;
    }
}
