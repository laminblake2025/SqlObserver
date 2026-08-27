using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace SqlObserver.ReleaseTests;

public sealed class M12ReleaseIdentityAssessmentTests
{
    [Fact]
    public void EmitsClosedDecisionNeutralAssessmentWithOrdered29Checks()
    {
        JsonDocument document = RunAssessment(out string raw);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("m12-release-identity", root.GetProperty("assessmentId").GetString());
        Assert.Equal("not_ready", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("releaseEvidence").GetBoolean());
        Assert.False(root.GetProperty("readyToRelease").GetBoolean());
        Assert.Equal("sqlobserver-m12-policy-v1", root.GetProperty("policyId").GetString());
        Assert.Equal("sqlobserver-m12", root.GetProperty("matrixId").GetString());
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{7}Z$", root.GetProperty("assessedAtUtc").GetString());
        JsonElement counts = root.GetProperty("counts");
        Assert.Equal(20, counts.GetProperty("matrixLanes").GetInt32());
        Assert.Equal(35, counts.GetProperty("matrixCases").GetInt32());
        Assert.Equal(8, counts.GetProperty("implementedLanes").GetInt32());
        Assert.Equal(8, counts.GetProperty("implementedCases").GetInt32());
        Assert.Equal(12, counts.GetProperty("pendingLanes").GetInt32());
        Assert.Equal(27, counts.GetProperty("pendingCases").GetInt32());
        Assert.Equal(29, counts.GetProperty("checks").GetInt32());
        JsonElement.ArrayEnumerator checks = root.GetProperty("checks").EnumerateArray();
        int expected = 1;
        while (checks.MoveNext()) Assert.Equal(expected++, checks.Current.GetProperty("order").GetInt32());
        Assert.Equal(30, expected);
        Assert.DoesNotContain("\\release\\", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publisherName", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("licenseName", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("signingIdentity", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcesBlockedOwnerAndExternalRanges()
    {
        using JsonDocument document = RunAssessment(out _);
        JsonElement.ArrayEnumerator checks = document.RootElement.GetProperty("checks").EnumerateArray();
        while (checks.MoveNext())
        {
            int order = checks.Current.GetProperty("order").GetInt32();
            string status = checks.Current.GetProperty("status").GetString()!;
            if (order is >= 11 and <= 22) Assert.Equal("blocked", status);
            if (order is >= 23 and <= 29) Assert.Equal("blocked", status);
            if (order is 9 or 10) Assert.Equal("blocked", status);
        }
    }

    [Fact]
    public void SchemaIsClosedAndChecksumMatches()
    {
        string root = FindRoot();
        string schemaPath = Path.Combine(root, "release", "contracts", "release-identity-assessment.v1.schema.json");
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("releaseEvidence").GetProperty("const").GetBoolean());
        Assert.False(schema.RootElement.GetProperty("properties").GetProperty("readyToRelease").GetProperty("const").GetBoolean());
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(schemaPath))).ToLowerInvariant();
        string pin = File.ReadAllLines(Path.Combine(root, "release", "contracts", "checksums.sha256"))
            .Single(line => line.EndsWith("  release-identity-assessment.v1.schema.json", StringComparison.Ordinal))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(actual, pin);
        JsonElement check = schema.RootElement.GetProperty("$defs").GetProperty("check");
        Assert.False(check.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("blocked", check.GetProperty("properties").GetProperty("status").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void RepeatedAssessmentCannotBecomeReadyOrReleaseEvidence()
    {
        using JsonDocument first = RunAssessment(out _);
        using JsonDocument second = RunAssessment(out _);
        foreach (JsonDocument result in new[] { first, second })
        {
            Assert.Equal("not_ready", result.RootElement.GetProperty("status").GetString());
            Assert.False(result.RootElement.GetProperty("releaseEvidence").GetBoolean());
            Assert.False(result.RootElement.GetProperty("readyToRelease").GetBoolean());
        }
    }

    [Fact]
    public void RejectsCallerSelectedRootWithoutLeakingIt()
    {
        string fakeRoot = Path.Combine(Path.GetTempPath(), "m12-assessment-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeRoot);
        try
        {
            using JsonDocument document = RunAssessment(out string raw, fakeRoot);
            Assert.Equal("not_ready", document.RootElement.GetProperty("status").GetString());
            Assert.False(document.RootElement.GetProperty("releaseEvidence").GetBoolean());
            Assert.False(document.RootElement.GetProperty("readyToRelease").GetBoolean());
            Assert.DoesNotContain(fakeRoot, raw, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(fakeRoot, recursive: true); }
    }

    [Fact]
    public void FakeGitOnPathCannotInfluenceAssessment()
    {
        string fakeBin = Path.Combine(Path.GetTempPath(), "m12-fake-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fakeBin);
        try
        {
            File.WriteAllText(Path.Combine(fakeBin, "git.cmd"), "@echo fake-git");
            using JsonDocument baseline = RunAssessment(out _);
            using JsonDocument document = RunAssessment(out _, pathPrefix: fakeBin);
            Assert.Equal(baseline.RootElement.GetProperty("commitSha").GetString(), document.RootElement.GetProperty("commitSha").GetString());
            Assert.Equal("not_ready", document.RootElement.GetProperty("status").GetString());
        }
        finally { Directory.Delete(fakeBin, recursive: true); }
    }

    private static JsonDocument RunAssessment(out string raw, string? requestedRoot = null, string? pathPrefix = null)
    {
        string root = FindRoot();
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (pathPrefix is not null) start.Environment["PATH"] = pathPrefix + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(Path.Combine(root, "tools", "assess-release-identity.ps1"));
        start.ArgumentList.Add("-RepositoryRoot");
        start.ArgumentList.Add(requestedRoot ?? root);
        using Process process = Process.Start(start)!;
        raw = process.StandardOutput.ReadToEnd().Trim();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.DoesNotContain("\n", raw, StringComparison.Ordinal);
        return JsonDocument.Parse(raw);
    }

    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new InvalidOperationException("Repository root not found.");
    }
}
