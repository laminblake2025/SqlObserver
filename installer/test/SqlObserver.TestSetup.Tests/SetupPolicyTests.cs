using System.Security.Cryptography;
using System.Text.Json;

namespace SqlObserver.TestSetup.Tests;

public sealed class SetupPolicyTests
{
    [Fact]
    public void MissingServerTlsFilesAreReportedBeforeAnotherConnectionAttempt()
    {
        string root = Path.Combine(Path.GetTempPath(), "SqlObserver-tls-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var error = Assert.Throws<SetupException>(() => SetupPolicy.AssertPostgresTlsFilesExist(root, "repository.crt", "repository.key"));
            Assert.Contains("missing or inaccessible", error.Message, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(root, "repository.crt"), "retained-certificate");
            Assert.Throws<SetupException>(() => SetupPolicy.AssertPostgresTlsFilesExist(root, "repository.crt", "repository.key"));
            File.WriteAllText(Path.Combine(root, "repository.key"), "retained-key");
            SetupPolicy.AssertPostgresTlsFilesExist(root, "repository.crt", "repository.key");
            SetupPolicy.AssertPostgresTlsFilesExist(root, Path.Combine(root, "repository.crt"), Path.Combine(root, "repository.key"));
            Assert.Equal("retained-key", File.ReadAllText(Path.Combine(root, "repository.key")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PostgreSQL\\18\\bin\\pg_ctl.exe\" runservice -N CustomRepository -D \"D:\\PgData\"", true)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice -D D:\\PgData", true)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice --pgdata=\"D:\\PgData\"", true)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice -D D:\\PgDataOther", false)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\other-pg_ctl.exe runservice -D D:\\PgData", false)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice -D D:\\PgData -D D:\\Other", false)]
    [InlineData("C:\\Program Files\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice -D D:\\PgData", false)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice -N \"-D D:\\PgData\"", false)]
    [InlineData("C:\\PostgreSQL\\18\\bin\\pg_ctl.exe runservice -D D:/PgData", true)]
    public void PostgresServiceUsesExecutableAndExactDataArgument(string command, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, SetupPolicy.IsPostgresServiceForData(command, @"D:\PgData"));
    }

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
    [InlineData("host.-bad")]
    [InlineData("host.bad-")]
    [InlineData("127.0.0.1")]
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
            File.AppendAllText(Path.Combine(root, "checksums.sha256"), "tampered");
            Assert.Throws<SetupException>(() => SetupPolicy.VerifyPayload(root, hash));
            hash = WriteManifest(root, "app.exe");
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
        string hash = Convert.ToHexString(SHA256.HashData(manifest));
        IEnumerable<string> lines = names.Select(name => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + "  " + name)
            .Append(hash.ToLowerInvariant() + "  package-manifest.json").Order(StringComparer.Ordinal);
        File.WriteAllText(Path.Combine(root, "checksums.sha256"), string.Join('\n', lines) + "\n");
        return hash;
    }
}
