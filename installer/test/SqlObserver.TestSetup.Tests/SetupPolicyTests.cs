using System.Security.Cryptography;
using System.Text.Json;

namespace SqlObserver.TestSetup.Tests;

public sealed class SetupPolicyTests
{
    [Theory]
    [InlineData("sqlobserver_test")]
    [InlineData("sqlobserver_test_20261015")]
    public void FreshTestRepositoryNamesAreAccepted(string name) => Assert.Equal(name, SetupPolicy.Database(name));

    [Theory]
    [InlineData("sqlobserver")]
    [InlineData("postgres")]
    [InlineData("sqlobserver_test;DROP DATABASE postgres")]
    [InlineData("sqlobserver_test/../postgres")]
    [InlineData("SQLOBSERVER_TEST")]
    [InlineData("sqlobserver_test_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void ExistingOrAmbiguousRepositoryNamesAreRejected(string name) => Assert.Throws<SetupException>(() => SetupPolicy.Database(name));

    [Theory]
    [InlineData("localhost")]
    [InlineData("sqlobserver.lab.example")]
    public void HostsAreDnsNames(string name) => Assert.Equal(name, SetupPolicy.Host(name));

    [Theory]
    [InlineData("https://host:5444")]
    [InlineData("host\";whoami")]
    [InlineData("host/path")]
    [InlineData("*.example.com")]
    [InlineData("a..b")]
    public void HostInputCannotBecomeAUrlPathOrCommand(string name) => Assert.Throws<SetupException>(() => SetupPolicy.Host(name));

    [Fact]
    public void ServicePathsAreQuotedAndRejectControlCharacters()
    {
        if (!OperatingSystem.IsWindows()) return;
        string command = SetupPolicy.ServiceCommand(@"C:\Program Files\SqlObserver Test\Application", @"C:\ProgramData\SqlObserverTest", "Server");
        Assert.Equal("\"C:\\Program Files\\SqlObserver Test\\Application\\Server\\SqlObserver.Server.exe\" --contentRoot \"C:\\ProgramData\\SqlObserverTest\\Server\"", command);
        Assert.Throws<SetupException>(() => SetupPolicy.ServiceCommand("C:\\x\" --bad", @"C:\data", "Server"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SetupPolicy.ServiceCommand(@"C:\app", @"C:\data", "Other"));
    }

    [Fact]
    public void PasswordsAreIndependentAndSqlQuotingDoesNotInterpolate()
    {
        Assert.NotEqual(SetupPolicy.NewSecret(), SetupPolicy.NewSecret());
        Assert.True(SetupPolicy.NewSecret().Length >= 64);
        Assert.Equal("'a''b'", SetupPolicy.QuoteLiteral("a'b"));
        Assert.Equal("\"a\"\"b\"", SetupPolicy.QuoteIdentifier("a\"b"));
        Assert.Throws<SetupException>(() => SetupPolicy.Port(80));
        Assert.Throws<SetupException>(() => SetupPolicy.Port(65536));
    }

    [Fact]
    public void PayloadRejectsTamperingMissingExtraAndDuplicateFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "SqlObserver-installer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "app.exe"), "verified-content");
            File.WriteAllText(Path.Combine(root, "checksums.sha256"), "metadata");
            string hash = WriteManifest(root, "app.exe");
            Assert.Equal(new string('a', 40), SetupPolicy.VerifyPayload(root, hash));
            File.WriteAllText(Path.Combine(root, "app.exe"), "tampered-content");
            Assert.Throws<SetupException>(() => SetupPolicy.VerifyPayload(root, hash));
            File.WriteAllText(Path.Combine(root, "app.exe"), "verified-content");
            File.WriteAllText(Path.Combine(root, "extra.exe"), "unlisted");
            Assert.Throws<SetupException>(() => SetupPolicy.VerifyPayload(root, hash));
            File.Delete(Path.Combine(root, "extra.exe"));
            File.Delete(Path.Combine(root, "app.exe"));
            Assert.ThrowsAny<Exception>(() => SetupPolicy.VerifyPayload(root, hash));
            File.WriteAllText(Path.Combine(root, "app.exe"), "verified-content");
            hash = WriteManifest(root, "app.exe", "app.exe");
            Assert.Throws<SetupException>(() => SetupPolicy.VerifyPayload(root, hash));
            hash = WriteManifest(root, "../escape.exe");
            Assert.Throws<SetupException>(() => SetupPolicy.VerifyPayload(root, hash));
            Assert.Throws<SetupException>(() => SetupPolicy.VerifyPayload(root, new string('0', 64)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string WriteManifest(string root, params string[] names)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, "app.exe"));
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = "lab-test", productionSupported = false, runtimeIdentifier = "win-x64", selfContained = false, sourceCommit = new string('a', 40),
            files = names.Select(name => new { path = name, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)) })
        });
        File.WriteAllBytes(Path.Combine(root, "package-manifest.json"), manifest);
        return Convert.ToHexString(SHA256.HashData(manifest));
    }
}
