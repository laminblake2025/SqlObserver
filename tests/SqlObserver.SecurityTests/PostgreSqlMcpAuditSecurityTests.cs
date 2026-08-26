using System.Reflection;
using Npgsql;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.SecurityTests;

public sealed class PostgreSqlMcpAuditSecurityTests
{
    [Fact]
    public void MigrationUsesDedicatedTerminalVocabularyAndNeverDeclaresRawPayloadColumns()
    {
        string root = FindRepositoryRoot();
        string sql = File.ReadAllText(Path.Combine(root, "database", "migrations", "0015_mcp_invocation_audit.sql"));

        Assert.Contains("CREATE TABLE audit.mcp_invocation", sql, StringComparison.Ordinal);
        Assert.Contains("outcome IN ('succeeded', 'denied', 'invalid', 'unknown_tool', 'timeout'", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT EXECUTE ON FUNCTION audit.append_mcp_invocation", sql, StringComparison.Ordinal);
        Assert.Contains("TO sqlobserver_server", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT SELECT ON TABLE audit.mcp_invocation TO sqlobserver_auditor", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("parameters json", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("result json", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("query_text", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan_xml", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("existing.authorization_result IS DISTINCT FROM", sql, StringComparison.Ordinal);
        Assert.Contains("existing.correlation_id IS DISTINCT FROM", sql, StringComparison.Ordinal);
        Assert.Contains("existing.safe_detail IS DISTINCT FROM", sql, StringComparison.Ordinal);
        Assert.Contains("clock_timestamp(), p_invocation_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterUsesTheServerOnlyAppendFunctionAndNoCallerTimestamp()
    {
        FieldInfo? field = typeof(PostgreSqlMcpInvocationAuditPort).GetField("AppendSql", BindingFlags.NonPublic | BindingFlags.Static);
        string sql = Assert.IsType<string>(field?.GetRawConstantValue());

        Assert.Contains("audit.append_mcp_invocation", sql, StringComparison.Ordinal);
        Assert.Contains("@parameter_digest", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@occurred_at", sql, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlObserver.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
