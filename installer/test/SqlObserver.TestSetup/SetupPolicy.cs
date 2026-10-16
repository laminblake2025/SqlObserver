using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SqlObserver.TestSetup;

public static partial class SetupPolicy
{
    public const string ServerService = "SqlObserverTestServer";
    public const string CollectorService = "SqlObserverTestCollector";
    public const string OperatorGroup = "SqlObserverTestOperators";
    public static readonly string[] ProtectedOwners = ["sqlobserver_report_expirer", "sqlobserver_payload_expirer", "sqlobserver_run_dependency_reader"];

    [GeneratedRegex("^[a-z][a-z0-9_]{0,47}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabasePattern();
    [GeneratedRegex("^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();

    public static string Database(string value) => DatabasePattern().IsMatch(value) &&
        value.StartsWith("sqlobserver_test", StringComparison.Ordinal) ? value :
        throw new SetupException("Use a fresh lowercase database name starting with sqlobserver_test (maximum 48 characters).");

    public static string Host(string value) => HostPattern().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal) ? value :
        throw new SetupException("Enter a DNS hostname, without a URL, port, wildcard, or path.");

    public static int Port(int value) => value is >= 1024 and <= 65535 ? value :
        throw new SetupException("Use an unoccupied TCP port from 1024 to 65535.");

    public static string NewSecret() => "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    public static string QuoteLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    public static string ServiceCommand(string applicationRoot, string dataRoot, string component)
    {
        if (component is not ("Server" or "Collector")) throw new ArgumentOutOfRangeException(nameof(component));
        foreach (string value in new[] { applicationRoot, dataRoot })
            if (value.Contains('"') || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
                throw new SetupException("Service paths must be absolute ordinary paths.");
        return $"\"{Path.Combine(applicationRoot, component, $"SqlObserver.{component}.exe")}\" --contentRoot \"{Path.Combine(dataRoot, component)}\"";
    }

    public static void AssertOrdinaryPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('"'))
            throw new SetupException("A local absolute path is required.");
        for (DirectoryInfo? current = new(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new SetupException("Reparse-point paths are not allowed.");
    }

    // Verify against a digest embedded by the installer builder, not a digest supplied by the payload itself.
    public static string VerifyPayload(string root, string expectedManifestHash)
    {
        AssertOrdinaryPath(root);
        string manifestPath = Path.Combine(root, "package-manifest.json");
        byte[] manifest = File.ReadAllBytes(manifestPath);
        if (manifest.Length > 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(manifest)).Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase))
            throw new SetupException("Application manifest checksum mismatch.");
        using JsonDocument json = JsonDocument.Parse(manifest);
        JsonElement m = json.RootElement;
        if (m.GetProperty("kind").GetString() != "lab-test" || m.GetProperty("productionSupported").GetBoolean() ||
            m.GetProperty("runtimeIdentifier").GetString() != "win-x64" || m.GetProperty("selfContained").GetBoolean())
            throw new SetupException("Only the verified Windows x64 lab payload is permitted.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "package-manifest.json", "checksums.sha256" };
        long total = 0;
        foreach (JsonElement file in m.GetProperty("files").EnumerateArray())
        {
            string name = file.GetProperty("path").GetString()!;
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*$", RegexOptions.CultureInvariant) ||
                name.Split('/').Any(part => part is "." or ".." || part.EndsWith('.')) || !expected.Add(name) || expected.Count > 2048)
                throw new SetupException("Unsafe or duplicate payload path.");
            string path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            AssertOrdinaryPath(Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            long length = file.GetProperty("bytes").GetInt64();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || length is <= 0 or > 67108864 || info.Length != length || (total += length) > 536870912)
                throw new SetupException("Payload length or file type mismatch.");
            using FileStream stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new SetupException("Application payload checksum mismatch.");
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new SetupException("Payload links are not permitted.");
            if (File.Exists(path) && !expected.Remove(Path.GetRelativePath(root, path).Replace('\\', '/')))
                throw new SetupException("Unlisted payload file.");
        }
        if (expected.Count != 0) throw new SetupException("Incomplete application payload.");
        return m.GetProperty("sourceCommit").GetString()!;
    }
}
