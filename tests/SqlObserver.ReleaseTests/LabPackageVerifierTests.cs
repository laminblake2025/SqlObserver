using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlObserver.ReleaseTests;

public sealed class LabPackageVerifierTests
{
    [Fact]
    public async Task ValidDirectoryAndArchiveVerifyWithoutExecutingPayloads()
    {
        using var fixture = new PackageFixture();
        var directoryResult = await RunAsync("-PackageDirectory", fixture.Root);
        Assert.True(directoryResult.ExitCode == 0, directoryResult.Output);
        string archive = fixture.CreateArchive();
        var result = await RunAsync("-ArchivePath", archive, "-ExpectedArchiveSha256", Digest(File.ReadAllBytes(archive)));
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("verified", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "executed.txt")));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("missing")]
    [InlineData("unlisted")]
    [InlineData("secret")]
    [InlineData("false_certification")]
    [InlineData("wrong_architecture")]
    [InlineData("wrong_framework")]
    public async Task InvalidPayloadOrDeploymentClaimsFailClosed(string change)
    {
        using var fixture = new PackageFixture();
        switch (change)
        {
            case "tampered": File.AppendAllText(Path.Combine(fixture.Root,"README.md"), "changed"); break;
            case "missing": File.Delete(Path.Combine(fixture.Root,"Collector/SqlObserver.Collector.dll")); break;
            case "unlisted": File.WriteAllText(Path.Combine(fixture.Root,"extra.txt"), "unexpected"); break;
            case "secret": File.WriteAllText(Path.Combine(fixture.Root,"key.dpapi"), "not-a-real-key"); break;
            case "false_certification": fixture.Manifest["productionSupported"] = true; fixture.WriteManifest(); break;
            case "wrong_architecture": fixture.Manifest["runtimeIdentifier"] = "win-arm64"; fixture.WriteManifest(); break;
            case "wrong_framework":
                File.WriteAllText(Path.Combine(fixture.Root,"Collector/SqlObserver.Collector.runtimeconfig.json"), "{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}");
                fixture.RefreshPayload(); break;
        }
        Assert.NotEqual(0, (await RunAsync("-PackageDirectory", fixture.Root)).ExitCode);
    }

    [Theory]
    [InlineData("traversal")]
    [InlineData("case_collision")]
    [InlineData("symlink")]
    [InlineData("bad_digest")]
    public async Task UnsafeArchivesAreRejectedBeforeExtraction(string change)
    {
        using var fixture = new PackageFixture();
        string archive = fixture.CreateArchive();
        if (change != "bad_digest")
        {
            using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
            string name = change switch { "traversal" => "../escaped.txt", "case_collision" => "readme.md", _ => "linked.txt" };
            ZipArchiveEntry entry = zip.CreateEntry(name);
            if (change == "symlink") entry.ExternalAttributes = unchecked((int)0xA0000000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("not executable");
        }
        string expected = change == "bad_digest" ? new string('0',64) : Digest(File.ReadAllBytes(archive));
        Assert.NotEqual(0, (await RunAsync("-ArchivePath", archive, "-ExpectedArchiveSha256", expected)).ExitCode);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Root)!, "escaped.txt")));
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
    {
        string repository = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repository,"SqlObserver.slnx"))) repository = Directory.GetParent(repository)?.FullName ?? throw new DirectoryNotFoundException();
        string runtime = Environment.GetEnvironmentVariable("SQLOBSERVER_M12_TRUSTED_PWSH_PATH") ?? "pwsh";
        var start = new ProcessStartInfo(runtime) { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", Path.Combine(repository,"tools/test-lab-package.ps1") }.Concat(arguments)) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree:true); throw; }
        return (process.ExitCode, (await output) + (await error));
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class PackageFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),"sqlobserver-package-test-" + Guid.NewGuid().ToString("N"));
        public JsonObject Manifest { get; }
        private readonly string archive;
        public PackageFixture()
        {
            Directory.CreateDirectory(Root);
            archive = Root + ".zip";
            Write("README.md", "Lab package fixture. Payloads must never execute.\n");
            foreach (var (folder,name) in new[] { ("Server","SqlObserver.Server"), ("Collector","SqlObserver.Collector"), ("MigrationHost","SqlObserver.Cli") })
            {
                byte[] apphost = new byte[128]; apphost[0]=0x4D; apphost[1]=0x5A;
                BitConverter.GetBytes(64).CopyTo(apphost,60); BitConverter.GetBytes(0x4550).CopyTo(apphost,64); BitConverter.GetBytes((ushort)0x8664).CopyTo(apphost,68);
                Write(folder + "/" + name + ".exe", apphost);
                Write(folder + "/" + name + ".dll", "fixture");
                var frameworks = new JsonArray(new JsonObject { ["name"]="Microsoft.NETCore.App", ["version"]="10.0.0" });
                frameworks.Add(new JsonObject { ["name"]="Microsoft.AspNetCore.App", ["version"]="10.0.0" });
                Write(folder + "/" + name + ".runtimeconfig.json", new JsonObject { ["runtimeOptions"] = new JsonObject { ["tfm"]="net10.0", ["frameworks"]=frameworks } }.ToJsonString());
            }
            foreach (string name in new[] { "Web/index.html", "Docs/web-asset-manifest.v1.json", "ConfigTemplates/server.example.json", "ConfigTemplates/collector.example.json", "Tools/test-lab-package.ps1", "Tools/generate-permissions.ps1", "Tools/provision-live-activity-key.ps1" }) Write(name,"fixture");
            Manifest = new JsonObject { ["schemaVersion"]=1, ["kind"]="lab-test", ["sourceCommit"]=new string('a',40), ["sourceTree"]=new string('b',40), ["builtAtUtc"]="2026-10-13T00:00:00.0000000Z", ["configuration"]="Release", ["runtimeIdentifier"]="win-x64", ["selfContained"]=false, ["productionSupported"]=false, ["nativeQualification"]="pending" };
            RefreshPayload();
        }
        public void RefreshPayload()
        {
            var members = new JsonArray();
            foreach (string path in Directory.GetFiles(Root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string name = Path.GetRelativePath(Root,path).Replace('\\','/');
                if (name is "package-manifest.json" or "checksums.sha256") continue;
                members.Add(new JsonObject { ["path"]=name, ["bytes"]=new FileInfo(path).Length, ["sha256"]=Digest(File.ReadAllBytes(path)) });
            }
            Manifest["files"] = members; WriteManifest();
        }
        public void WriteManifest()
        {
            Write("package-manifest.json", Manifest.ToJsonString() + "\n");
            string[] lines = Manifest["files"]!.AsArray().Select(row => row!["sha256"]!.GetValue<string>() + "  " + row["path"]!.GetValue<string>())
                .Append(Digest(File.ReadAllBytes(Path.Combine(Root,"package-manifest.json"))) + "  package-manifest.json").Order(StringComparer.Ordinal).ToArray();
            Write("checksums.sha256", string.Join('\n',lines) + "\n");
        }
        public string CreateArchive() { ZipFile.CreateFromDirectory(Root,archive); return archive; }
        private void Write(string name, string value) => Write(name, Encoding.UTF8.GetBytes(value));
        private void Write(string name, byte[] bytes) { string path=Path.Combine(Root,name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path,bytes); }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            string intendedPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "sqlobserver-package-test-";
            if (!resolved.StartsWith(intendedPrefix, StringComparison.Ordinal) || resolved.Length != intendedPrefix.Length + 32) throw new InvalidOperationException("Unexpected package fixture cleanup target.");
            Directory.Delete(resolved,recursive:true);
            if (File.Exists(archive)) File.Delete(archive);
        }
    }
}
