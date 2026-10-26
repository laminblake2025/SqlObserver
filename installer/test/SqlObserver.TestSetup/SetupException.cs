using SqlObserver.Domain.Repository;

namespace SqlObserver.TestSetup;

// Only installer-authored messages are displayed. Provider/Windows exceptions are reduced to type names.
public sealed class SetupException(string message) : Exception(message)
{
    public int? MigrationNumber { get; private init; }
    public string? FailureCode { get; private init; }

    internal static SetupException MigrationFailure(MigrationBatchResult? batch)
    {
        MigrationExecutionResult? failed = batch?.Results.FirstOrDefault(result => result.Outcome == MigrationOutcome.Failed);
        string code = failed?.FailureCode ?? "migration_batch_unavailable";
        string detail = failed is null ? $"Migration batch could not be started ({code})." : $"Migration {failed.Migration.Number} failed ({code}).";
        return new SetupException($"{detail} Its verified prefix was retained; setup did not start services.")
        {
            MigrationNumber = failed?.Migration.Number.Value,
            FailureCode = code
        };
    }
}
