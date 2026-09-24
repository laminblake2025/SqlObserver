using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SqlObserver.Mcp;

internal sealed record McpCursorSigningConfiguration(McpCursorSigner? Signer, string Reason)
{
    public static McpCursorSigningConfiguration Read(IConfiguration? configuration)
    {
        string? value = configuration?["SqlObserver:Mcp:CursorSigningKey"]
            ?? configuration?["SQLOBSERVER_MCP_CURSOR_KEY"]
            ?? Environment.GetEnvironmentVariable("SQLOBSERVER_MCP_CURSOR_KEY");
        if (value is null) return new(null, "missing key");
        try
        {
            byte[] key = Convert.FromBase64String(value);
            if (key.Length is >= 32 and <= 4096) return new(new McpCursorSigner(key), string.Empty);
        }
        catch (FormatException) { }
        return new(null, "invalid key");
    }
}

internal sealed class McpCursorStartupDiagnostics(IServiceProvider services, ILogger<McpCursorStartupDiagnostics> logger) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> Warning = LoggerMessage.Define<string>(LogLevel.Warning,
        new EventId(1101, "McpCursorSigningUnavailable"),
        "MCP cursor signing is unavailable ({Reason}). First pages remain available; continuation is disabled. Configure SqlObserver:Mcp:CursorSigningKey or SQLOBSERVER_MCP_CURSOR_KEY.");
    private int started;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) == 0 && services.GetService<McpCursorSigner>() is null)
            Warning(logger, services.GetRequiredService<McpCursorSigningConfiguration>().Reason, null);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
