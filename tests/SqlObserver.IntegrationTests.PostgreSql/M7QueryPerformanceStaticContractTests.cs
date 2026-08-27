namespace SqlObserver.IntegrationTests.PostgreSql;

/// <summary>Environment-independent M7 contract coverage kept in the Local profile.</summary>
public sealed class M7QueryPerformanceStaticContractTests
{
    [Fact]
    public void CanonicalPersistenceDisposesResultReaderBeforeCommit()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCollectorRuntimeRepositoryPort.cs"));
        string source = File.ReadAllText(path);
        int readerScope = source.IndexOf("await using (NpgsqlDataReader reader = await canonical.ExecuteReaderAsync", StringComparison.Ordinal);
        int commit = source.IndexOf("await transaction.CommitAsync(cancellationToken)", readerScope, StringComparison.Ordinal);
        Assert.True(readerScope >= 0 && commit > readerScope);
        string between = source[readerScope..commit];
        Assert.Contains("await reader.ReadAsync", between, StringComparison.Ordinal);
        Assert.Contains("while (await reader.ReadAsync", between, StringComparison.Ordinal);
        Assert.DoesNotContain("await transaction.CommitAsync", between, StringComparison.Ordinal);
    }
}
