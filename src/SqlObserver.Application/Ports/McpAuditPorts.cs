using SqlObserver.Domain.Auditing;

namespace SqlObserver.Application.Ports;

public sealed class AppendMcpInvocationAuditRequest
{
    public AppendMcpInvocationAuditRequest(McpInvocationAuditRecord record, RepositoryCallTimeout timeout)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(timeout);
        Record = record;
        Timeout = timeout;
    }

    public McpInvocationAuditRecord Record { get; }
    public RepositoryCallTimeout Timeout { get; }
}

/// <summary>Append-only terminal audit for every MCP invocation.</summary>
public interface IMcpInvocationAuditPort
{
    ValueTask<McpInvocationAuditReceipt> AppendAsync(
        AppendMcpInvocationAuditRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Short compatibility name for the MCP invocation audit port.</summary>
public interface IMcpAuditPort : IMcpInvocationAuditPort
{
}

/// <summary>Application boundary for the required, caller-independent terminal append.</summary>
public interface IMcpInvocationAuditService
{
    ValueTask<McpInvocationAuditReceipt> AppendTerminalAsync(McpInvocationAuditRecord record);
}
