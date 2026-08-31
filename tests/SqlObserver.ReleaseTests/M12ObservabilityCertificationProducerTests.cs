using System.Diagnostics;
using System.Text;

namespace SqlObserver.ReleaseTests;

public sealed class M12ObservabilityCertificationProducerTests
{
    [Fact]
    public void ProducerIsPendingOnlyAndUsesBoundedOtelContract()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "tools", "run-m12-observability-certification.ps1"));
        Assert.Contains("m12-observability-harness", source, StringComparison.Ordinal);
        Assert.Contains("observability-evidence", source, StringComparison.Ordinal);
        Assert.Contains("LiveReleaseRepositoryReadinessIsBoundedFailClosedAndOtelVisible", source, StringComparison.Ordinal);
        Assert.Contains("LiveReleaseCollectorTelemetryIsBoundedAndSensitiveDataFree", source, StringComparison.Ordinal);
        Assert.Contains("M12SuspendedProcess", source, StringComparison.Ordinal);
        Assert.Contains("CreateSuspended", source, StringComparison.Ordinal);
        Assert.Contains("KillOnClose", source, StringComparison.Ordinal);
        Assert.Contains("ReadAsync", source, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("FileShare]::None", source, StringComparison.Ordinal);
        Assert.Contains("Flush($true)", source, StringComparison.Ordinal);
        Assert.Contains("if($CaseId-ceq 'm12-readiness-observability'){Assert-M12ObservabilityConnection", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12NoExternalOtlp;if($CaseId-ceq 'm12-readiness-observability')", source, StringComparison.Ordinal);
        Assert.Contains("SqlObserver__Observability__OtlpEndpoint", source, StringComparison.Ordinal);
        Assert.Contains("Assert-M12JsonNoDuplicateProperties", source, StringComparison.Ordinal);
        Assert.Contains("M12ObservabilityTelemetryCertificationTests.LiveReleaseCollectorTelemetryIsBoundedAndSensitiveDataFree", source, StringComparison.Ordinal);
        Assert.Contains("SqlObserver.IntegrationTests.PostgreSql.M12ObservabilityCertificationTests.LiveReleaseRepositoryReadinessIsBoundedFailClosedAndOtelVisible", source, StringComparison.Ordinal);
        Assert.Contains("TestResults/m12", source, StringComparison.Ordinal);
        Assert.Contains(".pending-", source, StringComparison.Ordinal);
        Assert.Contains(".verify-", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_ENDPOINT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-Expression", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContractOnlyPassesAgainstCurrentPinnedSources()
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-observability-certification.ps1");
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(root); start.ArgumentList.Add("-CaseId"); start.ArgumentList.Add("m12-telemetry-observability"); start.ArgumentList.Add("-ContractOnly");
        using Process process = Process.Start(start)!; process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void TelemetryConnectionGateDoesNotRequirePostgresAndRejectsExternalExporter()
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-observability-certification.ps1");
        Assert.Equal(0, Run(script, root, "m12-telemetry-observability", null));
        Assert.NotEqual(0, Run(script, root, "m12-telemetry-observability", "http://collector.example.invalid:4318"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Host=localhost;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=127.0.0.1;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=1;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=2130706433;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=127.0.0.1.;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=1.;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=[::1];Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=0.0.0.0;Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=[::];Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=[::ffff:127.0.0.1];Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=[::ffff:0.0.0.0];Database=observer;SSL Mode=VerifyFull")]
    [InlineData("Host=[::ffff:169.254.1.1];Database=observer;SSL Mode=VerifyFull")]
    public void ReadinessConnectionGateRejectsMissingOrUnsafePostgresConnection(string? connection)
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-observability-certification.ps1");
        Assert.NotEqual(0, Run(script, root, "m12-readiness-observability", null, connection));
    }

    [Fact]
    public void ReadinessConnectionGateAcceptsStrictRemoteVerifyFullConnection()
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-observability-certification.ps1");
        Assert.Equal(0, Run(script, root, "m12-readiness-observability", null, "Host=release.example;Database=sqlobserver;SSL Mode=VerifyFull"));
        Assert.Equal(0, Run(script, root, "m12-readiness-observability", null, "Host=192.0.2.10;Database=sqlobserver;SSL Mode=VerifyFull"));
        Assert.Equal(0, Run(script, root, "m12-readiness-observability", null, "Host=[::ffff:192.0.2.10];Database=sqlobserver;SSL Mode=VerifyFull"));
    }

    [Fact]
    public void TelemetryConnectionGateRejectsExporterCredentialVariables()
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-observability-certification.ps1");
        foreach (string variable in new[]
        {
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_SERVICE_NAME",
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
            "OTEL_EXPORTER_OTLP_HEADERS",
            "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
            "OTEL_EXPORTER_OTLP_METRICS_HEADERS",
            "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
            "OTEL_EXPORTER_OTLP_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_TRACES_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_METRICS_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_LOGS_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_TRACES_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_METRICS_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_LOGS_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_TRACES_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_METRICS_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_LOGS_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_PRIVATE_KEY",
            "OTEL_EXPORTER_OTLP_TRACES_PRIVATE_KEY",
            "OTEL_EXPORTER_OTLP_METRICS_PRIVATE_KEY",
            "OTEL_EXPORTER_OTLP_LOGS_PRIVATE_KEY",
            "SqlObserver__Observability__OtlpEndpoint",
            "SqlObserver__Observability__TracesEndpoint",
            "SqlObserver__Observability__MetricsEndpoint",
            "SqlObserver__Observability__LogsEndpoint",
            "SqlObserver__Observability__Headers",
            "SqlObserver__Observability__TracesHeaders",
            "SqlObserver__Observability__MetricsHeaders",
            "SqlObserver__Observability__LogsHeaders",
            "SqlObserver__Observability__Certificate",
            "SqlObserver__Observability__TracesCertificate",
            "SqlObserver__Observability__MetricsCertificate",
            "SqlObserver__Observability__LogsCertificate",
            "SqlObserver__Observability__ClientCertificate",
            "SqlObserver__Observability__TracesClientCertificate",
            "SqlObserver__Observability__MetricsClientCertificate",
            "SqlObserver__Observability__LogsClientCertificate",
            "SqlObserver__Observability__ClientKey",
            "SqlObserver__Observability__TracesClientKey",
            "SqlObserver__Observability__MetricsClientKey",
            "SqlObserver__Observability__LogsClientKey",
            "SqlObserver__Observability__PrivateKey",
            "SqlObserver__Observability__TracesPrivateKey",
            "SqlObserver__Observability__MetricsPrivateKey",
            "SqlObserver__Observability__LogsPrivateKey",
        })
        {
            Assert.NotEqual(0, Run(script, root, "m12-telemetry-observability", null, null, variable));
        }
    }

    [Fact]
    public void BothConnectionGatesRejectNestedObservabilityAliasesCaseInsensitively()
    {
        string root = FindRoot();
        string script = Path.Combine(root, "tools", "run-m12-observability-certification.ps1");
        foreach (string caseId in new[] { "m12-readiness-observability", "m12-telemetry-observability" })
        foreach (string variable in new[]
        {
            "SqlObserver__Observability__Headers__Authorization",
            "sqlobserver__observability__Telemetry__Nested__Endpoint",
            "SQLOBSERVER__OBSERVABILITY__Endpoint__Credentials__PrivateKey",
        })
        {
            Assert.NotEqual(0, Run(script, root, caseId, null, caseId == "m12-readiness-observability" ? "Host=release.example;Database=sqlobserver;SSL Mode=VerifyFull" : null, variable));
        }
    }

    [Fact]
    public void CaseMapBindsEachCaseToExactlyOneFullyQualifiedTest()
    {
        string root = FindRoot();
        string source = File.ReadAllText(Path.Combine(root, "tools", "run-m12-observability-certification.ps1"));
        string[] fullNames =
        [
            "SqlObserver.IntegrationTests.PostgreSql.M12ObservabilityCertificationTests.LiveReleaseRepositoryReadinessIsBoundedFailClosedAndOtelVisible",
            "SqlObserver.IntegrationTests.PostgreSql.M12ObservabilityTelemetryCertificationTests.LiveReleaseCollectorTelemetryIsBoundedAndSensitiveDataFree",
        ];
        foreach (string fullName in fullNames)
        {
            Assert.Contains(fullName, source, StringComparison.Ordinal);
            Assert.Equal(1, source.Split(fullName, StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public void ProducerRejectsNestedDuplicateJsonProperties()
    {
        string root = FindRoot();
        string producer = File.ReadAllText(Path.Combine(root, "tools", "run-m12-observability-certification.ps1"));
        int boundary = producer.IndexOf("if ($ContractOnly)", StringComparison.Ordinal);
        Assert.True(boundary > 0);
        string payload = "{\"schemaVersion\":1,\"facts\":{\"readiness\":true,\"readiness\":false}}";
        string probe = producer[..boundary] + Environment.NewLine +
            "$bytes=[Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "');" +
            "try{[void](Read-M12LiveJsonBytes $bytes);exit 0}catch{exit 1}";
        string path = Path.Combine(Path.GetTempPath(), $"m12-observability-parser-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(path, probe, new UTF8Encoding(false));
            Assert.NotEqual(0, RunPowerShellFile(root, path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static int RunPowerShellFile(string root, string script)
    {
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script);
        using Process process = Process.Start(start)!; process.WaitForExit(); return process.ExitCode;
    }

    private static int Run(string script, string root, string caseId, string? endpoint, string? postgres = null, string? forbiddenVariable = null)
    {
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(pwsh)) pwsh = "pwsh";
        ProcessStartInfo start = new(pwsh) { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(script); start.ArgumentList.Add("-RepositoryRoot"); start.ArgumentList.Add(root); start.ArgumentList.Add("-CaseId"); start.ArgumentList.Add(caseId); start.ArgumentList.Add("-ConnectionOnly");
        start.Environment.Remove("SQLOBSERVER_RELEASE_POSTGRES");
        start.Environment.Remove("OTEL_EXPORTER_OTLP_ENDPOINT");
        foreach (string variable in new[]
        {
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
            "OTEL_EXPORTER_OTLP_HEADERS", "OTEL_EXPORTER_OTLP_TRACES_HEADERS", "OTEL_EXPORTER_OTLP_METRICS_HEADERS", "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
            "OTEL_EXPORTER_OTLP_CERTIFICATE", "OTEL_EXPORTER_OTLP_TRACES_CERTIFICATE", "OTEL_EXPORTER_OTLP_METRICS_CERTIFICATE", "OTEL_EXPORTER_OTLP_LOGS_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_CERTIFICATE", "OTEL_EXPORTER_OTLP_TRACES_CLIENT_CERTIFICATE", "OTEL_EXPORTER_OTLP_METRICS_CLIENT_CERTIFICATE", "OTEL_EXPORTER_OTLP_LOGS_CLIENT_CERTIFICATE",
            "OTEL_EXPORTER_OTLP_CLIENT_KEY", "OTEL_EXPORTER_OTLP_TRACES_CLIENT_KEY", "OTEL_EXPORTER_OTLP_METRICS_CLIENT_KEY", "OTEL_EXPORTER_OTLP_LOGS_CLIENT_KEY",
            "OTEL_EXPORTER_OTLP_PRIVATE_KEY", "OTEL_EXPORTER_OTLP_TRACES_PRIVATE_KEY", "OTEL_EXPORTER_OTLP_METRICS_PRIVATE_KEY", "OTEL_EXPORTER_OTLP_LOGS_PRIVATE_KEY",
            "SqlObserver__Observability__OtlpEndpoint", "SqlObserver__Observability__TracesEndpoint", "SqlObserver__Observability__MetricsEndpoint", "SqlObserver__Observability__LogsEndpoint",
            "SqlObserver__Observability__Headers", "SqlObserver__Observability__TracesHeaders", "SqlObserver__Observability__MetricsHeaders", "SqlObserver__Observability__LogsHeaders",
            "SqlObserver__Observability__Certificate", "SqlObserver__Observability__TracesCertificate", "SqlObserver__Observability__MetricsCertificate", "SqlObserver__Observability__LogsCertificate",
            "SqlObserver__Observability__ClientCertificate", "SqlObserver__Observability__TracesClientCertificate", "SqlObserver__Observability__MetricsClientCertificate", "SqlObserver__Observability__LogsClientCertificate",
            "SqlObserver__Observability__ClientKey", "SqlObserver__Observability__TracesClientKey", "SqlObserver__Observability__MetricsClientKey", "SqlObserver__Observability__LogsClientKey",
            "SqlObserver__Observability__PrivateKey", "SqlObserver__Observability__TracesPrivateKey", "SqlObserver__Observability__MetricsPrivateKey", "SqlObserver__Observability__LogsPrivateKey",
        }) start.Environment.Remove(variable);
        foreach (string variable in start.Environment.Keys.Where(static name => name.StartsWith("OTEL_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(variable);
        foreach (string variable in start.Environment.Keys.Where(static name => name.StartsWith("SqlObserver__Observability__", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(variable);
        if (forbiddenVariable is not null) start.Environment.Remove(forbiddenVariable);
        if (endpoint is not null) start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint;
        if (postgres is not null) start.Environment["SQLOBSERVER_RELEASE_POSTGRES"] = postgres;
        if (forbiddenVariable is not null) start.Environment[forbiddenVariable] = "sensitive-test-value";
        using Process process = Process.Start(start)!; process.WaitForExit(); return process.ExitCode;
    }

    private static string FindRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "SqlObserver.slnx"))) current = Directory.GetParent(current)?.FullName;
        return current ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
