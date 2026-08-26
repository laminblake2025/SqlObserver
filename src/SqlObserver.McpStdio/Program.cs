using SqlObserver.Mcp;

string endpointText = Environment.GetEnvironmentVariable("SQLOBSERVER_MCP_ENDPOINT") ?? string.Empty;
return await McpStdioBridge.RunAsync(endpointText, args);
