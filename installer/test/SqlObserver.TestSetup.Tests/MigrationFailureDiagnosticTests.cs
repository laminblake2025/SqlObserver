using System.Text.Json;
using SqlObserver.Domain.Repository;

namespace SqlObserver.TestSetup.Tests;

public sealed class MigrationFailureDiagnosticTests
{
    [Fact]
    public void FailedMigrationPreservesNumberAndSafeCodeInConsoleAndJson()
    {
        var descriptor = new MigrationDescriptor(new MigrationNumber(1), "repository_bootstrap", new MigrationChecksum(new byte[32]), true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var failure = new MigrationExecutionResult(descriptor, MigrationOutcome.Failed, now, now, "postgres_42501");
        SetupException error = SetupException.MigrationFailure(new MigrationBatchResult([failure], now));
        Assert.Contains("0001", error.Message, StringComparison.Ordinal);
        Assert.Contains("postgres_42501", error.Message, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(SetupFailureDiagnostic.Create("repository-migrate", error)));
        Assert.Equal(1, json.RootElement.GetProperty("MigrationNumber").GetInt32());
        Assert.Equal("postgres_42501", json.RootElement.GetProperty("FailureCode").GetString());
    }

    [Fact]
    public void FailureBeforeBatchHasExplicitCodeAndNoInventedMigrationNumber()
    {
        SetupException error = SetupException.MigrationFailure(null);
        string json = JsonSerializer.Serialize(SetupFailureDiagnostic.Create("repository-migrate", error));
        Assert.Contains("migration_batch_unavailable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MigrationNumber", json, StringComparison.Ordinal);
    }
}
