using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

internal enum MigrationHistoryValidationCode
{
    ValidPrefix,
    Missing,
    Duplicate,
    Gap,
    Unknown,
    NameDrift,
    ChecksumDrift,
}

internal readonly record struct MigrationHistoryValidation(MigrationHistoryValidationCode Code, int? Number)
{
    // An absent ledger is the supported fresh-repository state for the mutator.
    public bool IsValid => Code is MigrationHistoryValidationCode.ValidPrefix or MigrationHistoryValidationCode.Missing;
}

/// <summary>One validator shared by mutation and read-only assessment paths.</summary>
internal static class PostgreSqlMigrationHistoryValidator
{
    public static int VerifiedPrefixLength(
        IReadOnlyList<MigrationHistoryEntry> history,
        PostgreSqlMigrationCatalog catalog)
    {
        int prefix = 0;
        var seen = new HashSet<int>();
        while (prefix < history.Count && prefix < catalog.Migrations.Count)
        {
            MigrationHistoryEntry applied = history[prefix];
            PostgreSqlMigrationResource expected = catalog.Migrations[prefix];
            if (!seen.Add(applied.Number) || applied.Number != prefix + 1 ||
                !string.Equals(applied.Name, expected.FileName, StringComparison.Ordinal) ||
                !string.Equals(applied.Checksum, expected.ChecksumHex, StringComparison.Ordinal))
                break;
            prefix++;
        }
        return prefix;
    }

    public static MigrationHistoryValidation Validate(
        IReadOnlyList<MigrationHistoryEntry> history,
        PostgreSqlMigrationCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(catalog);
        if (history.Count == 0) return new(MigrationHistoryValidationCode.Missing, 1);
        if (history.Count > catalog.Migrations.Count) return new(MigrationHistoryValidationCode.Unknown, null);

        var seen = new HashSet<int>();
        for (int index = 0; index < history.Count; index++)
        {
            MigrationHistoryEntry applied = history[index];
            if (!seen.Add(applied.Number)) return new(MigrationHistoryValidationCode.Duplicate, applied.Number);
            int expectedNumber = index + 1;
            if (applied.Number != expectedNumber) return new(MigrationHistoryValidationCode.Gap, expectedNumber);
            PostgreSqlMigrationResource expected = catalog.Migrations[index];
            if (!string.Equals(applied.Name, expected.FileName, StringComparison.Ordinal))
                return new(MigrationHistoryValidationCode.NameDrift, expectedNumber);
            if (!string.Equals(applied.Checksum, expected.ChecksumHex, StringComparison.OrdinalIgnoreCase))
                return new(MigrationHistoryValidationCode.ChecksumDrift, expectedNumber);
        }
        return new(MigrationHistoryValidationCode.ValidPrefix, null);
    }

    public static void ThrowIfInvalid(
        IReadOnlyList<MigrationHistoryEntry> history,
        PostgreSqlMigrationCatalog catalog)
    {
        MigrationHistoryValidation validation = Validate(history, catalog);
        if (validation.IsValid) return;
        string number = validation.Number is int n ? $" at {n:D4}" : string.Empty;
        throw new InvalidDataException($"Repository migration history is invalid ({validation.Code}{number}).");
    }
}
