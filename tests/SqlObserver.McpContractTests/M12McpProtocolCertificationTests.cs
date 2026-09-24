using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SqlObserver.Mcp;

namespace SqlObserver.McpContractTests;

/// <summary>
/// Release-only, live transport certification. Local validation excludes this
/// class by its explicit trait; absence of a release prerequisite is a test
/// failure, never a runtime skip.
/// </summary>
public sealed class M12McpProtocolCertificationTests
{
    private const string EndpointVariable = "SQLOBSERVER_RELEASE_MCP_ENDPOINT";
    private const string AuthorizedTargetVariable = "SQLOBSERVER_RELEASE_MCP_AUTHORIZED_INSTANCE_ID";
    private const string CancellationTargetVariable = "SQLOBSERVER_RELEASE_MCP_CANCELLATION_INSTANCE_ID";
    private const string DeniedAttestationVariable = "SQLOBSERVER_RELEASE_MCP_DENIED_TARGET_ATTESTATION";
    private const string DeniedAttestationShaVariable = "SQLOBSERVER_RELEASE_MCP_DENIED_TARGET_ATTESTATION_SHA256";
    private const int MaximumChildOutputBytes = 64 * 1024;
    // The reviewed 27-tool catalog includes input descriptions and output
    // schemas. Bound its two protocol frames separately from diagnostics.
    private const int MaximumStdioProtocolOutputBytes = 128 * 1024;
    private const string ApprovedCurrentProtocol = "2026-07-28";
    private const string ApprovedDownlevelProtocol = "2025-11-25";
    private const int ApprovedToolCount = 27;
    private const string ApprovedCatalogDigest = "984319BD896C532E6E4B942334318FF5AB00E768677824023CCCEA180BF1DC2C";
    private const string ApprovedServerVersion = "m11-2.2.0+catalog-984319BD896C532E6E4B942334318FF5AB00E768677824023CCCEA180BF1DC2C";
    private const string ApprovedContractSha256 = "95c304644bd26dc497022856c2153bbbc1b6eb0313121256b0c035442e9f7605";
    private const string ApprovedContractSchemaSha256 = "70ad81e0069eaf57b06bf542e9ce3c7e62ad80fd6c2439b157582a3c72ab28b4";
    private static readonly string[] ApprovedToolNames =
    [
        "list_instances", "get_instance_capabilities", "get_instance_health", "get_active_alerts", "get_metric_series",
        "compare_metric_windows", "get_wait_summary", "get_active_sessions", "get_active_requests", "get_blocking_chain",
        "get_blocking_history", "get_deadlock", "search_deadlocks", "get_top_queries", "get_query_history",
        "get_query_plan_metadata", "get_database_health", "get_tempdb_health", "get_file_io", "get_storage_forecast",
        "get_backup_status", "get_job_failures", "get_availability_health", "get_incident_evidence", "search_diagnostic_events",
        "list_metric_catalog", "list_incidents"
    ];
    private static readonly string[] JsonRpcResponseProperties = ["jsonrpc", "id", "result", "error"];
    private static readonly string[] SafeResponseMediaTypes = ["application/json", "text/event-stream"];

    [Fact]
    public void CatalogDriftCannotRetainTheApprovedIdentity()
    {
        Assert.Equal(ApprovedCatalogDigest, McpCatalog.Digest, StringComparer.Ordinal);
        Assert.Equal(ApprovedServerVersion, McpCatalog.ServerVersion, StringComparer.Ordinal);
        Assert.Equal(ApprovedToolCount, ApprovedToolNames.Length);
        Assert.Equal(ApprovedToolNames.OrderBy(static x => x), McpCatalog.Definitions.Select(static x => x.Name).OrderBy(static x => x));
    }

    [Fact]
    public void RepositoryHeadResolverCoversNormalPackedAndLinkedMetadataFailClosed()
    {
        string parent = Path.Combine(Path.GetTempPath(), "m12-head-" + Guid.NewGuid().ToString("N"));
        string externalParent = Path.Combine(Path.GetTempPath(), "m12-main-" + Guid.NewGuid().ToString("N"));
        string normal = Path.Combine(parent, "normal");
        string linked = Path.Combine(parent, "linked");
        string sha = "0123456789abcdef0123456789abcdef01234567";
        try
        {
            Directory.CreateDirectory(Path.Combine(normal, ".git", "refs", "heads"));
            File.WriteAllText(Path.Combine(normal, "SqlObserver.slnx"), "root\n");
            File.WriteAllText(Path.Combine(normal, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(normal, ".git", "refs", "heads", "main"), sha + "\n");
            Assert.Equal(sha, ReadCurrentHead(normal));
            File.Delete(Path.Combine(normal, ".git", "refs", "heads", "main"));
            File.WriteAllText(Path.Combine(normal, ".git", "packed-refs"), "# pack-refs with: peeled fully-peeled\n" + sha + " refs/heads/main\n");
            Assert.Equal(sha, ReadCurrentHead(normal));
            string looseSha = "fedcba9876543210fedcba9876543210fedcba98";
            File.WriteAllText(Path.Combine(normal, ".git", "refs", "heads", "main"), looseSha + "\n");
            Assert.Equal(looseSha, ReadCurrentHead(normal));
            File.WriteAllText(Path.Combine(normal, ".git", "refs", "heads", "main"), "FEDCBA9876543210FEDCBA9876543210FEDCBA98\n");
            Assert.ThrowsAny<Exception>(() => ReadCurrentHead(normal));
            File.Delete(Path.Combine(normal, ".git", "refs", "heads", "main"));
            File.WriteAllText(Path.Combine(normal, ".git", "HEAD"), sha + "\n");
            Assert.Equal(sha, ReadCurrentHead(normal));
            File.WriteAllText(Path.Combine(normal, ".git", "HEAD"), sha.ToUpperInvariant() + "\n");
            Assert.ThrowsAny<Exception>(() => ReadCurrentHead(normal));

            string common = Path.Combine(externalParent, "common", ".git");
            string worktreeMetadata = Path.Combine(common, "worktrees", "m12");
            Directory.CreateDirectory(Path.Combine(worktreeMetadata));
            Directory.CreateDirectory(Path.Combine(common, "refs", "heads"));
            Directory.CreateDirectory(linked);
            File.WriteAllText(Path.Combine(linked, "SqlObserver.slnx"), "root\n");
            File.WriteAllText(Path.Combine(linked, ".git"), "gitdir: " + worktreeMetadata + "\n");
            File.WriteAllText(Path.Combine(worktreeMetadata, "gitdir"), Path.Combine(linked, ".git") + "\n");
            File.WriteAllText(Path.Combine(worktreeMetadata, "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(worktreeMetadata, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(common, "refs", "heads", "main"), sha + "\n");
            Assert.Equal(sha, ReadCurrentHead(linked));
            File.WriteAllText(Path.Combine(worktreeMetadata, "HEAD"), sha + "\n");
            Assert.Equal(sha, ReadCurrentHead(linked));
            File.WriteAllText(Path.Combine(worktreeMetadata, "commondir"), "..\\..\n");
            Assert.ThrowsAny<Exception>(() => ReadCurrentHead(linked));
        }
        finally
        {
            try { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true); } catch { }
            try { if (Directory.Exists(externalParent)) Directory.Delete(externalParent, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BoundedReaderDetectsEmptyAndOversizeStreams()
    {
        BoundedRead empty = await ReadBoundedAsync(new MemoryStream(), 4);
        Assert.Empty(empty.Bytes);
        Assert.False(empty.Truncated);
        BoundedRead oversize = await ReadBoundedAsync(new MemoryStream(Encoding.UTF8.GetBytes("12345")), 4);
        Assert.True(oversize.Truncated);
        Assert.InRange(oversize.Bytes.Length, 0, 4);
    }

    [Fact]
    public void JsonRpcParserRejectsClosedShapeViolations()
    {
        foreach (string malformed in new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"error\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{},\"noise\":1}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}{}",
            "{\"jsonrpc\":\"1.0\",\"id\":1,\"result\":{}}"
        }) Assert.ThrowsAny<Exception>(() => ParseJsonRpcResponse(Encoding.UTF8.GetBytes(malformed)));
    }

    [Theory]
    [InlineData("empty", "exit 0", "EMPTY")]
    [InlineData("free-text", "[Console]::WriteLine('free text'); exit 0", "NOISE")]
    [InlineData("stderr", "[Console]::Error.Write('stderr'); exit 0", "STDERR")]
    [InlineData("oversize-stdout", "[Console]::Write('x' * 70000); exit 0", "CAP_STDOUT")]
    [InlineData("oversize-stderr", "[Console]::Error.Write('x' * 70000); exit 0", "CAP_STDERR")]
    [InlineData("timeout", "Start-Sleep -Seconds 20", "TIMEOUT")]
    [InlineData("nonprotocol", "[Console]::WriteLine('{\"hello\":1}'); exit 0", "PROTOCOL")]
    [InlineData("descendant", "Start-Process pwsh -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 20') | Out-Null; Start-Sleep -Seconds 20", "DESCENDANT")]
    public async Task AdversarialChildFixturesFailClosedWithinBound(string fixture, string command, string expectedReason)
    {
        (BoundedRead output, BoundedRead error, bool timedOut, int activeProcesses, bool descendantObserved) = await RunAdversarialChildAsync(command, fixture == "descendant");
        string reason = fixture == "descendant" ? descendantObserved ? "DESCENDANT" : "DESCENDANT_MISSING"
            : timedOut ? "TIMEOUT"
            : output.Truncated ? "CAP_STDOUT"
            : error.Truncated ? "CAP_STDERR"
            : error.Bytes.Length != 0 ? "STDERR"
            : output.Bytes.Length == 0 ? "EMPTY"
            : IsProtocolOnly(output.Bytes) ? "PROTOCOL"
            : output.Bytes.Length > 0 && output.Bytes[0] == (byte)'{' ? "PROTOCOL" : "NOISE";
        Assert.Equal(expectedReason, reason);
        Assert.Equal(0, activeProcesses);
    }

    [Fact]
    [Trait("Category", "RequiresM12McpRelease")]
    public async Task LiveReleaseMcpProtocolAndProcessBoundaryAreCertified()
    {
        string environmentId = RequireEnvironment("SQLOBSERVER_RELEASE_MCP_ENVIRONMENT");
        AssertContractIntegrity();
        RequireReleaseHost(environmentId);
        string endpointText = RequireEnvironment(EndpointVariable);
        Assert.True(McpStdioBridge.TryValidateEndpoint(endpointText, out Uri? endpoint));
        Assert.NotNull(endpoint);
        Assert.Equal("https", endpoint!.Scheme, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("/mcp", endpoint.AbsolutePath, StringComparer.Ordinal);
        Assert.Empty(endpoint.UserInfo);
        Assert.Empty(endpoint.Query);
        Assert.Empty(endpoint.Fragment);

        using HttpClientHandler handler = new()
        {
            UseDefaultCredentials = true,
            AllowAutoRedirect = false,
            UseCookies = false,
            CheckCertificateRevocationList = true
        };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(15) };
        await AssertUnauthenticatedDeniedAsync(endpoint, http);
        await AssertProtocolRevisionsAsync(endpoint, http);

        await using HttpClientTransport transport = new(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0,
            ConnectionTimeout = TimeSpan.FromSeconds(10)
        }, http, ownsHttpClient: false);
        await using McpClient client = await McpClient.CreateAsync(transport);
        Assert.Equal(ApprovedCurrentProtocol, client.NegotiatedProtocolVersion);
        Assert.Equal(ApprovedCatalogDigest, McpCatalog.Digest, StringComparer.Ordinal);
        Assert.Equal(ApprovedServerVersion, client.ServerInfo.Version);
        Guid authorizedTarget = RequireGuidEnvironment(AuthorizedTargetVariable);
        Guid cancellationTarget = RequireGuidEnvironment(CancellationTargetVariable);
        Guid deniedTarget = ReadDeniedAttestation(environmentId);
        Assert.NotEqual(authorizedTarget, deniedTarget);
        Assert.NotEqual(authorizedTarget, cancellationTarget);
        Assert.NotEqual(deniedTarget, cancellationTarget);
        CallToolResult cancellationHealth = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "get_instance_health",
            Arguments = new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(cancellationTarget) }
        });
        Assert.False(cancellationHealth.IsError);
        Assert.True(cancellationHealth.StructuredContent.HasValue);
        Assert.Equal(cancellationTarget.ToString(), cancellationHealth.StructuredContent!.Value.GetProperty("data").GetProperty("targetId").GetString(), StringComparer.OrdinalIgnoreCase);
        CallToolResult authorizedHealth = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "get_instance_health",
            Arguments = new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(authorizedTarget) }
        });
        Assert.False(authorizedHealth.IsError);
        Assert.True(authorizedHealth.StructuredContent.HasValue);
        Assert.Equal(authorizedTarget.ToString(), authorizedHealth.StructuredContent!.Value.GetProperty("data").GetProperty("targetId").GetString(), StringComparer.OrdinalIgnoreCase);
        CallToolResult deniedHealth = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "get_instance_health",
            Arguments = new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(deniedTarget) }
        });
        Assert.True(deniedHealth.IsError);
        Assert.Equal("forbidden", ReadMcpErrorCode(deniedHealth), StringComparer.Ordinal);
        string[] expected = ApprovedToolNames.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        string[] actual = (await client.ListToolsAsync()).Select(static x => x.Name).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
        Assert.Equal(ApprovedToolCount, actual.Length);

        CallToolResult read = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "list_instances",
            Arguments = new Dictionary<string, JsonElement>()
        });
        Assert.False(read.IsError);
        Assert.True(read.StructuredContent.HasValue);

        CallToolResult crossTarget = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = "get_instance_health",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["instanceId"] = JsonSerializer.SerializeToElement(Guid.NewGuid())
            }
        });
        Assert.True(crossTarget.IsError);

        using (CancellationTokenSource cancelled = new(TimeSpan.FromMilliseconds(100)))
        {
            CallToolResult cancellation;
            try
            {
                cancellation = await client.CallToolAsync(new CallToolRequestParams
                {
                    Name = "get_instance_health",
                    Arguments = new Dictionary<string, JsonElement> { ["instanceId"] = JsonSerializer.SerializeToElement(cancellationTarget) }
                }, cancelled.Token);
            }
            catch (OperationCanceledException) { Assert.Fail("Release lab did not return a protocol cancellation result."); return; }
            Assert.True(cancellation.IsError);
            Assert.Equal("request_cancelled", ReadMcpErrorCode(cancellation), StringComparer.Ordinal);
        }

        await AssertActualStdioChildAsync(endpointText);
    }

    private static void RequireReleaseHost(string environmentId)
    {
        Assert.True(string.Equals(environmentId, "release-windows-server-2022", StringComparison.Ordinal)
            || string.Equals(environmentId, "release-windows-server-2025", StringComparison.Ordinal));
        Assert.True(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows));
        Assert.True(Environment.Is64BitOperatingSystem);
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(System.Runtime.InteropServices.Architecture.X64, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture);
        Assert.Equal(System.Runtime.InteropServices.Architecture.X64, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
        Assert.Equal("Release", Environment.GetEnvironmentVariable("SQLOBSERVER_VALIDATION_PROFILE"));
        try
        {
#pragma warning disable CA1416
            object? product = Microsoft.Win32.Registry.GetValue(
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion", "ProductName", null);
#pragma warning restore CA1416
            string caption = product?.ToString() ?? string.Empty;
            string expectedProduct = string.Equals(environmentId, "release-windows-server-2022", StringComparison.Ordinal)
                ? "Windows Server 2022" : "Windows Server 2025";
            Assert.StartsWith(expectedProduct, caption, StringComparison.Ordinal);
        }
        catch
        {
            Assert.Fail("Unable to establish an approved Windows Server release host.");
        }
    }

    private static string RequireEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value));
        return value!;
    }

    private static Guid RequireGuidEnvironment(string name)
    {
        string value = RequireEnvironment(name);
        Assert.True(Guid.TryParseExact(value, "D", out Guid parsed));
        return parsed;
    }

    private static Guid ReadDeniedAttestation(string environmentId)
    {
        string path = RequireEnvironment(DeniedAttestationVariable);
        string expectedHash = RequireEnvironment(DeniedAttestationShaVariable);
        Assert.Matches("^[0-9a-f]{64}$", expectedHash);
        string full = Path.GetFullPath(path);
        string root = FindRoot();
        AssertNoReparseAncestors(full, root);
        using FileStream stream = TrustedFile.Open(full, full);
        Assert.InRange(stream.Length, 1, 16 * 1024);
        byte[] bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); Assert.True(count > 0); offset += count; }
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.DoesNotContain((byte)0x0D, bytes);
        Assert.Equal((byte)0x0A, bytes[^1]);
        Assert.Equal(1, bytes.Count(static value => value == 0x0A));
        using JsonDocument document = JsonDocument.Parse(bytes);
        Assert.Equal(["schemaVersion", "attestationId", "runId", "environmentId", "commitSha", "deniedTargetId", "exists", "targetRevision", "generatedAtUtc"], document.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(Guid.TryParseExact(document.RootElement.GetProperty("attestationId").GetString(), "D", out _));
        Assert.True(Guid.TryParseExact(document.RootElement.GetProperty("runId").GetString(), "D", out Guid runId));
        string expectedPath = Path.GetFullPath(Path.Combine(root, "TestResults", "m12-lab-inputs", runId.ToString("D"), "denied-target.json"));
        Assert.Equal(expectedPath, full, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(environmentId, document.RootElement.GetProperty("environmentId").GetString(), StringComparer.Ordinal);
        Assert.True(Guid.TryParseExact(document.RootElement.GetProperty("deniedTargetId").GetString(), "D", out Guid denied));
        Assert.True(document.RootElement.GetProperty("exists").GetBoolean());
        Assert.True(document.RootElement.GetProperty("targetRevision").TryGetInt64(out long revision) && revision > 0);
        string generated = document.RootElement.GetProperty("generatedAtUtc").GetString()!;
        DateTimeOffset timestamp = DateTimeOffset.ParseExact(generated, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        Assert.Equal(generated, timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture), StringComparer.Ordinal);
        Assert.InRange(Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalMinutes), 0, 5);
        Assert.Equal(ReadCurrentHead(root), document.RootElement.GetProperty("commitSha").GetString(), StringComparer.Ordinal);
        return denied;
    }

    private static void AssertNoReparseAncestors(string path, string root)
    {
        string full = Path.GetFullPath(path);
        AssertNoReparsePath(full);
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(prefix, full, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoReparsePath(string path)
    {
        string full = Path.GetFullPath(path);
        string cursor = Path.GetPathRoot(full)!;
        string relative = full[(cursor.Length)..];
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, component);
            if (File.Exists(cursor) || Directory.Exists(cursor)) Assert.False((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0);
        }
    }

    private static string ReadCurrentHead(string root)
    {
        string repositoryRoot = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(repositoryRoot, "SqlObserver.slnx"))) throw new InvalidOperationException("repository root unavailable");
        string gitPath = Path.Combine(repositoryRoot, ".git");
        AssertNoReparseAncestors(gitPath, repositoryRoot);
        if (!File.Exists(gitPath) && !Directory.Exists(gitPath)) throw new InvalidOperationException("git metadata unavailable");
        string metadataDirectory;
        string commonDirectory;
        FileAttributes gitAttributes = File.GetAttributes(gitPath);
        if ((gitAttributes & FileAttributes.Directory) != 0)
        {
            metadataDirectory = gitPath;
            commonDirectory = gitPath;
        }
        else
        {
            string gitFile = ReadExactMetadataLine(gitPath, repositoryRoot, 4096);
            if (!gitFile.StartsWith("gitdir: ", StringComparison.Ordinal) || gitFile.Length <= 8) throw new InvalidOperationException("invalid gitdir");
            metadataDirectory = ResolveMetadataPath(repositoryRoot, gitFile[8..], null);
            if (!Directory.Exists(metadataDirectory)) throw new InvalidOperationException("git worktree metadata unavailable");
            string worktreesDirectory = Path.GetDirectoryName(metadataDirectory) ?? throw new InvalidOperationException("invalid worktree metadata");
            string commonCandidate = Path.GetDirectoryName(worktreesDirectory) ?? throw new InvalidOperationException("invalid common git directory");
            if (!string.Equals(Path.GetFileName(worktreesDirectory), "worktrees", StringComparison.Ordinal) || string.IsNullOrEmpty(Path.GetFileName(metadataDirectory)) || !Regex.IsMatch(Path.GetFileName(metadataDirectory), "^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant) || !string.Equals(metadataDirectory, Path.Combine(commonCandidate, "worktrees", Path.GetFileName(metadataDirectory)), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("invalid worktree metadata structure");
            string reciprocalPath = Path.Combine(metadataDirectory, "gitdir");
            string reciprocal = ReadExactMetadataLine(reciprocalPath, repositoryRoot, 4096, requireContainment: false);
            string reciprocalPathResolved = ResolveMetadataPath(metadataDirectory, reciprocal, null);
            if (!string.Equals(reciprocalPathResolved, gitPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("non-reciprocal gitdir");
            string commondirPath = Path.Combine(metadataDirectory, "commondir");
            byte[] commondir = ReadTrustedMetadataBytes(commondirPath, repositoryRoot, 64, requireContainment: false);
            byte[] expectedCommondir = [0x2e, 0x2e, 0x2f, 0x2e, 0x2e, 0x0a];
            if (!commondir.AsSpan().SequenceEqual(expectedCommondir)) throw new InvalidOperationException("invalid commondir");
            commonDirectory = Path.GetFullPath(Path.Combine(metadataDirectory, "../.."));
            if (!string.Equals(commonDirectory, commonCandidate, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("invalid common git directory");
            AssertNoReparsePath(commonDirectory);
        }
        if (string.Equals(metadataDirectory, gitPath, StringComparison.OrdinalIgnoreCase)) AssertNoReparseAncestors(metadataDirectory, repositoryRoot); else AssertNoReparsePath(metadataDirectory);
        bool linked = !string.Equals(metadataDirectory, gitPath, StringComparison.OrdinalIgnoreCase);
        string head = ReadExactMetadataLine(Path.Combine(metadataDirectory, "HEAD"), repositoryRoot, 4096, requireContainment: !linked);
        if (Regex.IsMatch(head, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)) return head;
        Match reference = Regex.Match(head, "^ref: (refs/[A-Za-z0-9._/-]+)$", RegexOptions.CultureInvariant);
        if (!reference.Success || reference.Groups[1].Value.Contains("..", StringComparison.Ordinal) || reference.Groups[1].Value.Contains("//", StringComparison.Ordinal) || reference.Groups[1].Value.EndsWith('/')) throw new InvalidOperationException("invalid HEAD");
        string referenceName = reference.Groups[1].Value;
        string loosePath = ResolveMetadataPath(commonDirectory, referenceName, linked ? commonDirectory : repositoryRoot);
        if (File.Exists(loosePath))
        {
            string looseValue = ReadExactMetadataLine(loosePath, repositoryRoot, 4096, requireContainment: !linked);
            if (!Regex.IsMatch(looseValue, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("invalid ref");
            return looseValue;
        }
        Dictionary<string, string> packed = ReadPackedRefs(commonDirectory, repositoryRoot, requireContainment: !linked);
        string value = packed.TryGetValue(referenceName, out string? packedValue) ? packedValue : throw new InvalidOperationException("missing ref");
        if (!Regex.IsMatch(value, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("invalid ref");
        return value;
    }

    private static string ReadExactMetadataLine(string path, string root, int maximumBytes, bool requireContainment = true)
    {
        byte[] bytes = ReadTrustedMetadataBytes(path, root, maximumBytes, requireContainment);
        if (bytes.Length < 2 || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) || bytes.Contains((byte)0x0D) || bytes.Count(static value => value == 0x0A) != 1 || bytes[^1] != 0x0A) throw new InvalidOperationException("non-canonical git metadata");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        return text[..^1];
    }

    private static byte[] ReadTrustedMetadataBytes(string path, string root, int maximumBytes, bool requireContainment = true)
    {
        if (requireContainment) AssertNoReparseAncestors(path, root); else AssertNoReparsePath(path);
        using FileStream stream = TrustedFile.Open(path, path);
        Assert.InRange(stream.Length, 1, maximumBytes);
        byte[] bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read <= 0) throw new InvalidOperationException("short git metadata");
            offset += read;
        }
        return bytes;
    }

    private static string ResolveMetadataPath(string baseDirectory, string value, string? containmentRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.Contains('\r') || value.Contains('\n')) throw new InvalidOperationException("invalid metadata path");
        string resolved = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
        if (containmentRoot is not null)
        {
            string baseRoot = Path.GetFullPath(containmentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(baseRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("metadata escapes repository");
        }
        return resolved;
    }

    private static Dictionary<string, string> ReadPackedRefs(string commonDirectory, string root, bool requireContainment)
    {
        string packedPath = Path.Combine(commonDirectory, "packed-refs");
        if (!File.Exists(packedPath)) return new(StringComparer.Ordinal);
        byte[] bytes = ReadTrustedMetadataBytes(packedPath, root, 1024 * 1024, requireContainment);
        if (bytes.Contains((byte)0x0D) || !bytes.Contains((byte)0x0A)) throw new InvalidOperationException("invalid packed refs");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.EndsWith('\n')) throw new InvalidOperationException("invalid packed refs");
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        string? previousRef = null;
        foreach (string line in text[..^1].Split('\n'))
        {
            if (line.StartsWith('#')) { if (previousRef is not null) throw new InvalidOperationException("invalid packed refs"); continue; }
            if (line.StartsWith('^')) { if (previousRef is null || line.Length != 41 || !Regex.IsMatch(line[1..], "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException("invalid packed refs"); continue; }
            Match match = Regex.Match(line, "^([0-9a-f]{40}) (refs/[A-Za-z0-9._/-]+)$", RegexOptions.CultureInvariant);
            if (!match.Success || match.Groups[2].Value.Contains("..", StringComparison.Ordinal) || match.Groups[2].Value.Contains("//", StringComparison.Ordinal) || match.Groups[2].Value.EndsWith('/')) throw new InvalidOperationException("invalid packed refs");
            if (!result.TryAdd(match.Groups[2].Value, match.Groups[1].Value)) throw new InvalidOperationException("duplicate packed ref");
            previousRef = match.Groups[2].Value;
        }
        return result;
    }

    private static string ReadMcpErrorCode(CallToolResult result)
    {
        TextContentBlock text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        using JsonDocument document = JsonDocument.Parse(text.Text);
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static class TrustedFile
    {
        public static FileStream Open(string path, string expected)
        {
            FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out FileInfo info) || info.NumberOfLinks != 1)
            { stream.Dispose(); throw new InvalidOperationException("untrusted file identity"); }
            char[] final = new char[32768];
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, final, (uint)final.Length, 0);
            if (length == 0 || length >= final.Length || !string.Equals(NormalizeNativePath(new string(final, 0, (int)length)), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            { stream.Dispose(); throw new InvalidOperationException("untrusted file path"); }
            return stream;
        }
        private static string NormalizeNativePath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation; public System.Runtime.InteropServices.ComTypes.FILETIME Access; public System.Runtime.InteropServices.ComTypes.FILETIME Write; public uint Volume; public uint SizeHigh; public uint SizeLow; public uint NumberOfLinks; public uint IndexHigh; public uint IndexLow; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
    }

    private static async Task AssertUnauthenticatedDeniedAsync(Uri endpoint, HttpClient authenticatedClient)
    {
        using HttpClientHandler unauthenticatedHandler = new()
        {
            UseDefaultCredentials = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            CheckCertificateRevocationList = true
        };
        using HttpClient unauthenticated = new(unauthenticatedHandler) { Timeout = TimeSpan.FromSeconds(10) };
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"m12-unauthenticated\",\"version\":\"1\"}}}", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using HttpResponseMessage response = await unauthenticated.SendAsync(request);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        byte[] body = await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.InRange(body.Length, 0, MaximumChildOutputBytes);
        if (body.Length > 0)
        {
            Assert.Contains(response.Content.Headers.ContentType?.MediaType, SafeResponseMediaTypes);
            using JsonDocument deniedBody = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(body).Trim());
            Assert.Equal(JsonValueKind.Object, deniedBody.RootElement.ValueKind);
        }
    }

    private static async Task AssertProtocolRevisionsAsync(Uri endpoint, HttpClient http)
    {
        foreach (string protocol in new[] { ApprovedCurrentProtocol, ApprovedDownlevelProtocol })
            await AssertProtocolRevisionAsync(endpoint, http, protocol);
    }

    internal static async Task AssertProtocolRevisionAsync(Uri endpoint, HttpClient http, string protocol)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(CreateHandshakeRequest(protocol), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("MCP-Protocol-Version", protocol);
        if (protocol == ApprovedCurrentProtocol) request.Headers.Add("Mcp-Method", "server/discover");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using HttpResponseMessage response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains(response.Content.Headers.ContentType?.MediaType, SafeResponseMediaTypes);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(bytes.Length, 1, MaximumChildOutputBytes);
        JsonElement result = ParseJsonRpcResponse(bytes);
        Assert.Equal("2.0", result.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, result.GetProperty("id").GetInt32());
        AssertHandshakeResult(result.GetProperty("result"), protocol);
    }

    internal static string CreateHandshakeRequest(string protocol) => protocol switch
    {
        ApprovedCurrentProtocol => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "server/discover", @params = new { _meta = CurrentMetadata() } }),
        ApprovedDownlevelProtocol => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = protocol, capabilities = new { }, clientInfo = new { name = "m12-certification", version = "1" } } }),
        _ => throw new ArgumentException("Unapproved certification protocol.", nameof(protocol))
    };

    private static Dictionary<string, object> CurrentMetadata() => new(StringComparer.Ordinal)
    {
        ["io.modelcontextprotocol/protocolVersion"] = ApprovedCurrentProtocol,
        ["io.modelcontextprotocol/clientInfo"] = new { name = "m12-certification", version = "1" },
        ["io.modelcontextprotocol/clientCapabilities"] = new { }
    };

    internal static string CreateStdioProtocolTranscript(string protocol)
    {
        string initialized = protocol == ApprovedDownlevelProtocol ? "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" : string.Empty;
        object parameters = protocol == ApprovedCurrentProtocol ? new { _meta = CurrentMetadata() } : new { };
        string tools = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = parameters });
        return CreateHandshakeRequest(protocol) + "\n" + initialized + tools + "\n";
    }

    private static void AssertHandshakeResult(JsonElement result, string protocol)
    {
        if (protocol == ApprovedCurrentProtocol)
            Assert.Contains(ApprovedCurrentProtocol, result.GetProperty("supportedVersions").EnumerateArray().Select(static value => value.GetString()));
        else
            Assert.Equal(ApprovedDownlevelProtocol, result.GetProperty("protocolVersion").GetString());
        Assert.Equal(ApprovedServerVersion, HandshakeServerInfo(result, protocol).GetProperty("version").GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("instructions").GetString()));
    }

    private static JsonElement HandshakeServerInfo(JsonElement result, string protocol) => protocol == ApprovedCurrentProtocol
        ? result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo")
        : result.GetProperty("serverInfo");

    internal static JsonElement ParseJsonRpcResponse(byte[] bytes)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        string[] lines = text.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        string[] dataLines = lines.Where(static value => value.StartsWith("data:", StringComparison.Ordinal)).Select(static value => value[5..].Trim()).ToArray();
        if (dataLines.Length > 0)
        {
            Assert.All(lines, value => Assert.True(string.IsNullOrWhiteSpace(value) || value.StartsWith("data:", StringComparison.Ordinal) || value == "event: message"));
            Assert.InRange(lines.Count(static value => value == "event: message"), 0, 1);
            lines = dataLines;
        }
        else lines = lines.Select(static value => value.Trim()).ToArray();
        Assert.Single(lines);
        string line = lines[0];
        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement.Clone();
        AssertClosedJsonRpcSuccess(root);
        return root;
    }

    private static void AssertClosedJsonRpcSuccess(JsonElement root)
    {
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            Assert.True(names.Add(property.Name), "Duplicate JSON-RPC response property.");
            Assert.Contains(property.Name, JsonRpcResponseProperties);
        }
        Assert.True(root.TryGetProperty("result", out _));
        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
    }

    private static void AssertContractIntegrity()
    {
        string root = FindRoot();
        string contractPath = Path.Combine(root, "release", "certification", "m12-mcp-protocol-contract.v1.json");
        string schemaPath = Path.Combine(root, "release", "certification", "m12-mcp-protocol-contract.v1.schema.json");
        string pinPath = Path.Combine(root, "release", "certification", "m12-mcp-protocol-contract.v1.sha256");
        byte[] bytes = File.ReadAllBytes(contractPath);
        Assert.Equal(ApprovedContractSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal($"{ApprovedContractSchemaSha256}", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath))).ToLowerInvariant());
        byte[] pinBytes = File.ReadAllBytes(pinPath);
        Assert.True(pinBytes.Length > 2 && pinBytes[0] != 0xEF && pinBytes[1] != 0xBB && pinBytes[2] != 0xBF);
        Assert.DoesNotContain((byte)0x0D, pinBytes);
        Assert.Equal(2, pinBytes.Count(static value => value == 0x0A));
        string pinText = new UTF8Encoding(false, true).GetString(pinBytes);
        Assert.Equal($"{ApprovedContractSha256}  m12-mcp-protocol-contract.v1.json\n{ApprovedContractSchemaSha256}  m12-mcp-protocol-contract.v1.schema.json\n", pinText);
        using JsonDocument contract = JsonDocument.Parse(bytes);
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllBytes(schemaPath));
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        string[] required = schema.RootElement.GetProperty("required").EnumerateArray().Select(static value => value.GetString()!).ToArray();
        Assert.Equal(required, contract.RootElement.EnumerateObject().Select(static property => property.Name));
        JsonElement properties = schema.RootElement.GetProperty("properties");
        foreach (JsonProperty property in contract.RootElement.EnumerateObject())
        {
            Assert.True(properties.TryGetProperty(property.Name, out JsonElement definition));
            Assert.True(definition.TryGetProperty("const", out JsonElement expected));
            Assert.True(JsonElement.DeepEquals(expected, property.Value));
        }
    }

    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static async Task AssertActualStdioChildAsync(string endpointText)
    {
        foreach (string protocol in new[] { ApprovedCurrentProtocol, ApprovedDownlevelProtocol })
            await AssertActualStdioChildAsync(endpointText, protocol);
    }

    private static async Task AssertActualStdioChildAsync(string endpointText, string protocol)
    {
        string root = FindRoot();
        string full = Path.GetFullPath(Path.Combine(root, "src", "SqlObserver.McpStdio", "bin", "Release", "net10.0", "SqlObserver.McpStdio.exe"));
        Assert.True(File.Exists(full));
        AssertNoReparseAncestors(full, root);
        using FileStream executableHandle = TrustedFile.Open(full, full);
        // The deployed child is created suspended and assigned to a kill-on-close
        // Job Object before its first instruction can run; late attachment is
        // deliberately not sufficient for release certification.
        using SuspendedChild child = SuspendedChild.Start(full, endpointText, Path.GetDirectoryName(full)!);
        Task<BoundedRead>? outputTask = null;
        Task<BoundedRead>? errorTask = null;
        BoundedRead output = new([], false);
        BoundedRead error = new([], false);
        int[] observedProcessIds = Array.Empty<int>();
        try
        {
            observedProcessIds = child.ProcessIds;
            outputTask = ReadBoundedAsync(child.Output, MaximumStdioProtocolOutputBytes);
            errorTask = ReadBoundedAsync(child.Error, MaximumChildOutputBytes);
            byte[] request = Encoding.UTF8.GetBytes(CreateStdioProtocolTranscript(protocol));
            await child.Input.WriteAsync(request);
            await child.Input.FlushAsync();
            await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            try { child.Terminate(); } catch { }
            try { await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (outputTask is not null) { try { output = await outputTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            if (errorTask is not null) { try { error = await errorTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
            Assert.Equal(0, child.ActiveProcesses);
            AssertObservedPidsExited(observedProcessIds);
        }
        Assert.True(child.Process.HasExited);
        Assert.False(output.Truncated);
        Assert.False(error.Truncated);
        Assert.NotEmpty(output.Bytes);
        Assert.Empty(error.Bytes);
        AssertStdioProtocolOutput(output.Bytes, protocol);
    }

    internal static void AssertStdioProtocolOutput(byte[] output, string protocol)
    {
        Assert.InRange(output.Length, 1, MaximumStdioProtocolOutputBytes);
        string stdout = new UTF8Encoding(false, true).GetString(output);
        Assert.DoesNotContain('\r', stdout);
        Assert.EndsWith("\n", stdout, StringComparison.Ordinal);
        string protocolOutput = stdout[..^1];
        string[] protocolLines = protocolOutput.Split('\n');
        Assert.All(protocolLines, line => Assert.False(string.IsNullOrWhiteSpace(line)));
        JsonDocument[] responses = protocolLines
            .Select(static line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(2, responses.Length);
            foreach (JsonDocument response in responses)
            {
                JsonElement responseRoot = response.RootElement;
                AssertClosedJsonRpcSuccess(responseRoot);
                Assert.True(responseRoot.TryGetProperty("id", out _));
            }
            JsonElement handshake = responses.Select(static x => x.RootElement).Single(x => x.GetProperty("id").GetInt32() == 1);
            AssertHandshakeResult(handshake.GetProperty("result"), protocol);
            Assert.Equal("SqlObserver.McpStdio", HandshakeServerInfo(handshake.GetProperty("result"), protocol).GetProperty("name").GetString());
            JsonElement tools = responses.Select(static x => x.RootElement).Single(x => x.GetProperty("id").GetInt32() == 2).GetProperty("result").GetProperty("tools");
            Assert.Equal(ApprovedToolCount, tools.GetArrayLength());
            Assert.Equal(ApprovedToolNames.OrderBy(static x => x), tools.EnumerateArray().Select(x => x.GetProperty("name").GetString()).OrderBy(static x => x));
        }
        finally { foreach (JsonDocument response in responses) response.Dispose(); }
    }

    private sealed record BoundedRead(byte[] Bytes, bool Truncated);

    private static bool IsProtocolOnly(byte[] bytes)
    {
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.EndsWith('\n') || text.Contains('\r')) return false;
            foreach (string line in text[..^1].Split('\n'))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                AssertClosedJsonRpcSuccess(document.RootElement);
            }
            return true;
        }
        catch { return false; }
    }

    private static async Task<(BoundedRead Output, BoundedRead Error, bool TimedOut, int ActiveProcesses, bool DescendantObserved)> RunAdversarialChildAsync(string command, bool requireDescendant)
    {
        Assert.True(OperatingSystem.IsWindows());
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = "pwsh.exe";
        using SuspendedChild child = SuspendedChild.Start(pwsh, ["-NoProfile", "-NonInteractive", "-Command", command], string.Empty, Environment.CurrentDirectory);
        Process process = child.Process;
        int[] observedPids = Array.Empty<int>();
        bool descendantObserved = false;
        if (!requireDescendant) observedPids = child.ProcessIds;
        else
        {
            DateTime descendantDeadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < descendantDeadline)
            {
                int[] current = child.ProcessIds;
                if (child.ActiveProcesses > 1 && current.Length > 1)
                {
                    observedPids = current;
                    descendantObserved = true;
                    break;
                }
                await Task.Delay(25);
            }
        }
        TaskCompletionSource<string> capSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BoundedRead> outputTask = ReadBoundedAsync(child.Output, MaximumChildOutputBytes, () => capSignal.TrySetResult("stdout"));
        Task<BoundedRead> errorTask = ReadBoundedAsync(child.Error, MaximumChildOutputBytes, () => capSignal.TrySetResult("stderr"));
        bool timedOut = false;
        try
        {
            Task all = Task.WhenAll(outputTask, errorTask);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(2));
            Task winner = await Task.WhenAny(all, capSignal.Task, timeout);
            timedOut = winner == timeout;
            if (timedOut || outputTask.IsCompletedSuccessfully && outputTask.Result.Truncated || errorTask.IsCompletedSuccessfully && errorTask.Result.Truncated)
            {
                try { child.Terminate(); } catch { }
                Assert.True(child.WaitForZero(2000));
            }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await outputTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await errorTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (child.ActiveProcesses != 0) { try { child.Terminate(); } catch { }; Assert.True(child.WaitForZero(2000)); }
            AssertObservedPidsExited(observedPids);
            int active = child.ActiveProcesses;
            Assert.Equal(0, active);
            return (outputTask.IsCompletedSuccessfully ? outputTask.Result : new([], true), errorTask.IsCompletedSuccessfully ? errorTask.Result : new([], true), timedOut, active, descendantObserved);
        }
        finally
        {
            try { child.Terminate(); } catch { }
            try { child.WaitForZero(2000); } catch { }
            try { process.WaitForExit(1000); } catch { }
        }
    }

    private static void AssertObservedPidsExited(IEnumerable<int> processIds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow <= deadline)
        {
            bool anyLive = false;
            foreach (int id in processIds)
            {
                try { using Process process = Process.GetProcessById(id); if (!process.HasExited) anyLive = true; }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            if (!anyLive) return;
            Thread.Sleep(25);
        }
        Assert.Fail("Observed child process remained alive after termination.");
    }

    private static async Task<BoundedRead> ReadBoundedAsync(Stream stream, int maximumBytes, Action? onTruncated = null)
    {
        byte[] buffer = new byte[4096];
        using MemoryStream captured = new();
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory());
            if (count == 0) return new(captured.ToArray(), false);
            if (captured.Length + count > maximumBytes) { onTruncated?.Invoke(); return new(captured.ToArray(), true); }
            await captured.WriteAsync(buffer.AsMemory(0, count));
        }
    }

    /// <summary>Native suspended launch used for the deployed stdio boundary.</summary>
    [SuppressMessage("Interoperability", "CA2101", Justification = "CreateProcessW requires an explicitly marshaled mutable command line.")]
    [SuppressMessage("Performance", "CA1838", Justification = "CreateProcess requires a mutable command-line buffer.")]
    private sealed class SuspendedChild : IDisposable
    {
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint HandleFlagInherit = 1;
        private const uint KillOnClose = 0x2000;
        private const int ExtendedLimits = 9;
        private const int ProcessIdList = 3;
        private readonly IntPtr processHandle;
        private readonly IntPtr jobHandle;
        private readonly SafeFileHandle inputWrite;
        private readonly SafeFileHandle outputRead;
        private readonly SafeFileHandle errorRead;
        private readonly Process process;
        private SuspendedChild(IntPtr processValue, IntPtr jobValue, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error, int pid)
        {
            processHandle = processValue;
            jobHandle = jobValue;
            inputWrite = input;
            outputRead = output;
            errorRead = error;
            process = Process.GetProcessById(pid);
            // Anonymous Win32 pipes are synchronous handles. FileStream still
            // provides bounded Task-based reads/writes without claiming
            // overlapped I/O that the handle does not support.
            Input = new FileStream(inputWrite, FileAccess.Write, 4096, isAsync: false);
            Output = new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false);
            Error = new FileStream(errorRead, FileAccess.Read, 4096, isAsync: false);
        }
        public Process Process => process;
        public Stream Input { get; }
        public Stream Output { get; }
        public Stream Error { get; }
        public int ActiveProcesses
        {
            get
            {
                IntPtr buffer = Marshal.AllocHGlobal(65536);
                try
                {
                    if (!QueryInformationJobObject(jobHandle, ProcessIdList, buffer, 65536, IntPtr.Zero)) throw new InvalidOperationException("job accounting unavailable");
                    return Marshal.ReadInt32(buffer);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        public int[] ProcessIds
        {
            get
            {
                IntPtr buffer = Marshal.AllocHGlobal(65536);
                try
                {
                    if (!QueryInformationJobObject(jobHandle, ProcessIdList, buffer, 65536, IntPtr.Zero)) throw new InvalidOperationException("job process list unavailable");
                    int count = Marshal.ReadInt32(buffer, 4);
                    int[] ids = new int[count];
                    for (int i = 0; i < count; i++) ids[i] = IntPtr.Size == 8 ? unchecked((int)Marshal.ReadInt64(buffer, 8 + i * IntPtr.Size)) : Marshal.ReadInt32(buffer, 8 + i * IntPtr.Size);
                    return ids;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        public bool WaitForZero(int milliseconds)
        {
            long deadline = Environment.TickCount64 + milliseconds;
            while (Environment.TickCount64 <= deadline)
            {
                if (ActiveProcesses == 0) return true;
                Thread.Sleep(25);
            }
            return ActiveProcesses == 0;
        }
        public static SuspendedChild Start(string executable, string endpoint, string workingDirectory)
            => Start(executable, Array.Empty<string>(), endpoint, workingDirectory);

        public static SuspendedChild Start(string executable, IReadOnlyList<string> args, string endpoint, string workingDirectory)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(workingDirectory)) throw new InvalidOperationException("invalid child path");
            SecurityAttributes pipeAttributes = new() { nLength = (uint)Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
            if (!CreatePipe(out IntPtr inputRead, out IntPtr inputWriteRaw, ref pipeAttributes, 0)) throw new InvalidOperationException("stdin pipe");
            if (!CreatePipe(out IntPtr outputReadRaw, out IntPtr outputWrite, ref pipeAttributes, 0)) { CloseHandle(inputRead); CloseHandle(inputWriteRaw); throw new InvalidOperationException("stdout pipe"); }
            if (!CreatePipe(out IntPtr errorReadRaw, out IntPtr errorWrite, ref pipeAttributes, 0)) { CloseHandle(inputRead); CloseHandle(inputWriteRaw); CloseHandle(outputReadRaw); CloseHandle(outputWrite); throw new InvalidOperationException("stderr pipe"); }
            IntPtr processValue = IntPtr.Zero, threadValue = IntPtr.Zero, jobValue = IntPtr.Zero, environment = IntPtr.Zero;
            try
            {
                if (!SetHandleInformation(inputWriteRaw, HandleFlagInherit, 0) || !SetHandleInformation(outputReadRaw, HandleFlagInherit, 0) || !SetHandleInformation(errorReadRaw, HandleFlagInherit, 0)) throw new InvalidOperationException("pipe inheritance");
                var variables = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().ToDictionary(static x => (string)x.Key, static x => (string?)x.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);
                variables["SQLOBSERVER_MCP_ENDPOINT"] = endpoint;
                StringBuilder block = new();
                foreach (var pair in variables.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase)) block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
                block.Append('\0');
                environment = Marshal.StringToHGlobalUni(block.ToString());
                StartupInfo startup = new()
                {
                    cb = (uint)Marshal.SizeOf<StartupInfo>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = inputRead,
                    hStdOutput = outputWrite,
                    hStdError = errorWrite
                };
                ProcessInformation information;
                StringBuilder commandLine = new(Quote(executable));
                foreach (string arg in args) commandLine.Append(' ').Append(Quote(arg));
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, true, CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out information)) throw new InvalidOperationException("suspended child launch");
                processValue = information.hProcess;
                threadValue = information.hThread;
                CloseHandle(inputRead); inputRead = IntPtr.Zero;
                CloseHandle(outputWrite); outputWrite = IntPtr.Zero;
                CloseHandle(errorWrite); errorWrite = IntPtr.Zero;
                jobValue = CreateJobObject(IntPtr.Zero, null);
                if (jobValue == IntPtr.Zero) throw new InvalidOperationException("job creation");
                var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnClose } };
                if (!SetInformationJobObject(jobValue, ExtendedLimits, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>())) throw new InvalidOperationException("job configuration");
                if (!AssignProcessToJobObject(jobValue, processValue)) throw new InvalidOperationException("job assignment");
                if (ResumeThread(threadValue) == uint.MaxValue) throw new InvalidOperationException("child resume");
                CloseHandle(threadValue); threadValue = IntPtr.Zero;
                return new SuspendedChild(processValue, jobValue,
                    new SafeFileHandle(inputWriteRaw, ownsHandle: true),
                    new SafeFileHandle(outputReadRaw, ownsHandle: true),
                    new SafeFileHandle(errorReadRaw, ownsHandle: true), (int)information.dwProcessId);
            }
            catch
            {
                if (processValue != IntPtr.Zero) { try { TerminateProcess(processValue, 1); } catch { } try { _ = WaitForSingleObject(processValue, 2000); } catch { } }
                if (jobValue != IntPtr.Zero) { try { TerminateJobObject(jobValue, 1); } catch { } CloseHandle(jobValue); }
                if (processValue != IntPtr.Zero) CloseHandle(processValue);
                throw;
            }
            finally
            {
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (threadValue != IntPtr.Zero) CloseHandle(threadValue);
                if (inputRead != IntPtr.Zero) CloseHandle(inputRead);
                if (inputWriteRaw != IntPtr.Zero && processValue == IntPtr.Zero) CloseHandle(inputWriteRaw);
                if (outputReadRaw != IntPtr.Zero && processValue == IntPtr.Zero) CloseHandle(outputReadRaw);
                if (outputWrite != IntPtr.Zero) CloseHandle(outputWrite);
                if (errorReadRaw != IntPtr.Zero && processValue == IntPtr.Zero) CloseHandle(errorReadRaw);
                if (errorWrite != IntPtr.Zero) CloseHandle(errorWrite);
            }
        }
        public void Terminate() { try { if (jobHandle != IntPtr.Zero) TerminateJobObject(jobHandle, 1); } catch { } }
        public void Dispose()
        {
            try { Terminate(); } catch { }
            try { Input.Dispose(); } catch { }
            try { Output.Dispose(); } catch { }
            try { Error.Dispose(); } catch { }
            try { process.Dispose(); } catch { }
            try { if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle); } catch { }
            try { if (processHandle != IntPtr.Zero) CloseHandle(processHandle); } catch { }
        }
        private static string Quote(string value)
        {
            StringBuilder builder = new("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { builder.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                builder.Append('\\', slashes).Append(c); slashes = 0;
            }
            builder.Append('\\', slashes * 2).Append('"');
            return builder.ToString();
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfo { public uint cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public uint dwX; public uint dwY; public uint dwXSize; public uint dwYSize; public uint dwXCountChars; public uint dwYCountChars; public uint dwFillAttribute; public uint dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public uint nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations; public ulong WriteOperations; public ulong OtherOperations; public ulong ReadBytes; public ulong WriteBytes; public ulong OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string currentDirectory, ref StartupInfo startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length, IntPtr returnLength);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }

}
