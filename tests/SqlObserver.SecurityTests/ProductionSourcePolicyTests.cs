using System.Text.RegularExpressions;

namespace SqlObserver.SecurityTests;

public sealed partial class ProductionSourcePolicyTests
{
    [Fact]
    public void RepositoryAndMcpSourceContainNoArbitrarySqlApiOrEmbeddedConnectionSecret()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] productionRoots =
        [
            Path.Combine(repositoryRoot, "src", "SqlObserver.Application"),
            Path.Combine(repositoryRoot, "src", "SqlObserver.Infrastructure.PostgreSql"),
            Path.Combine(repositoryRoot, "src", "SqlObserver.Mcp"),
            Path.Combine(repositoryRoot, "src", "SqlObserver.McpStdio"),
            Path.Combine(repositoryRoot, "database", "migrations"),
        ];
        SourceFile[] files = productionRoots
            .SelectMany(EnumerateProductionSource)
            .ToArray();
        var violations = new List<string>();

        Assert.NotEmpty(files);

        foreach (SourceFile file in files)
        {
            if (file.Content.Contains("execute_sql", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"{file.Path}: contains the forbidden execute_sql API token");
            }

            if (ArbitrarySqlMethodPattern().IsMatch(file.Content))
            {
                violations.Add($"{file.Path}: declares or calls a forbidden ExecuteSql API");
            }

            string secretCandidate = file.Content;
            if (file.Path == Path.Combine(repositoryRoot, "src", "SqlObserver.Infrastructure.PostgreSql", "PostgreSqlDevelopmentBootstrap.cs"))
            {
                // ADR-0020 allows this guarded loopback Npgsql builder. These
                // exact assignments forward validated settings, not embedded
                // connection strings or literal passwords. Keep scanning every
                // other expression in the file; guard behavior has runtime tests.
                secretCandidate = secretCandidate
                    .Replace("Host = supplied.Host, Port = supplied.Port, Database = DatabaseName, Username = \"postgres\",", "", StringComparison.Ordinal)
                    .Replace("Password = supplied.Password,", "", StringComparison.Ordinal);
            }

            if (EmbeddedConnectionSecretPattern().IsMatch(secretCandidate))
            {
                violations.Add($"{file.Path}: appears to embed a connection string or secret");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ProtectedPayloadAdapterHasNoLoggingSinkForCapturedContent()
    {
        string repositoryRoot = FindRepositoryRoot();
        string adapterPath = Path.Combine(
            repositoryRoot,
            "src",
            "SqlObserver.Infrastructure.PostgreSql",
            "PostgreSqlSensitivePayloadPort.cs");
        string source = File.ReadAllText(adapterPath);

        Assert.DoesNotContain("ILogger", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Write", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(LoggingCallPattern(), source);
    }

    private static IEnumerable<SourceFile> EnumerateProductionSource(string root)
    {
        Assert.True(Directory.Exists(root), $"Expected production source root was not found: {root}");

        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] segments = Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains("bin", StringComparer.OrdinalIgnoreCase) ||
                segments.Contains("obj", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new SourceFile(path, File.ReadAllText(path));
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SqlObserver.slnx")) &&
                Directory.Exists(Path.Combine(current.FullName, "src")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }

    [GeneratedRegex(@"\bExecuteSql(?:Async)?\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArbitrarySqlMethodPattern();

    [GeneratedRegex(
        @"\b(?:Host|Server|Database|Username|User\s+ID|Password|Pwd)\s*=\s*[^;\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedConnectionSecretPattern();

    [GeneratedRegex(
        @"\bLog(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(",
        RegexOptions.CultureInvariant)]
    private static partial Regex LoggingCallPattern();

    private sealed record SourceFile(string Path, string Content);
}
