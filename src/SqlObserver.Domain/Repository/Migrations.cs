using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace SqlObserver.Domain.Repository;

public sealed record MigrationNumber
{
    public const int MaximumValue = 9_999;

    public MigrationNumber(int value)
    {
        if (value is <= 0 or > MaximumValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A migration number must be between 1 and {MaximumValue}.");
        }

        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The SHA-256 checksum of one immutable migration resource.</summary>
public readonly record struct MigrationChecksum
{
    public const int RequiredLength = 32;

    private readonly ulong _first;
    private readonly ulong _second;
    private readonly ulong _third;
    private readonly ulong _fourth;

    public MigrationChecksum(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RequiredLength)
        {
            throw new ArgumentException(
                $"A migration checksum must contain exactly {RequiredLength} bytes.",
                nameof(bytes));
        }

        _first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _third = BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]);
        _fourth = BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < RequiredLength)
        {
            throw new ArgumentException(
                $"The destination must contain at least {RequiredLength} bytes.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _first);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _second);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], _third);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], _fourth);
    }

    public string ToHexString()
    {
        Span<byte> bytes = stackalloc byte[RequiredLength];
        CopyTo(bytes);
        return Convert.ToHexString(bytes);
    }

    public override string ToString() => ToHexString();
}

public sealed class MigrationDescriptor
{
    public const int MaximumNameLength = 96;

    public MigrationDescriptor(
        MigrationNumber number,
        string name,
        MigrationChecksum checksum,
        bool isTransactional)
    {
        ArgumentNullException.ThrowIfNull(number);

        Number = number;
        Name = DomainValidation.RequireAsciiToken(
            name,
            nameof(name),
            MaximumNameLength,
            static character =>
                (character is >= 'a' and <= 'z') ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '_' or '-',
            requireLeadingLetter: true);
        Checksum = checksum;
        IsTransactional = isTransactional;
    }

    public MigrationNumber Number { get; }

    public string Name { get; }

    public MigrationChecksum Checksum { get; }

    public bool IsTransactional { get; }
}

public enum MigrationOutcome
{
    Applied = 1,
    AlreadyApplied = 2,
    Failed = 3,
}

public sealed class MigrationExecutionResult
{
    public const int MaximumFailureCodeLength = 64;

    public MigrationExecutionResult(
        MigrationDescriptor migration,
        MigrationOutcome outcome,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode = null)
    {
        ArgumentNullException.ThrowIfNull(migration);

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        startedAtUtc = DomainValidation.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        completedAtUtc = DomainValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));

        if (completedAtUtc < startedAtUtc)
        {
            throw new ArgumentException("Completion cannot precede start.", nameof(completedAtUtc));
        }

        if (outcome == MigrationOutcome.Failed)
        {
            failureCode = DomainValidation.RequireAsciiToken(
                failureCode ?? string.Empty,
                nameof(failureCode),
                MaximumFailureCodeLength,
                static character => DomainValidation.IsAsciiLetter(character) ||
                    DomainValidation.IsAsciiDigit(character) ||
                    character is '_' or '-' or '.');
        }
        else if (failureCode is not null)
        {
            throw new ArgumentException("Only a failed migration can include a failure code.", nameof(failureCode));
        }

        Migration = migration;
        Outcome = outcome;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FailureCode = failureCode;
    }

    public MigrationDescriptor Migration { get; }

    public MigrationOutcome Outcome { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public string? FailureCode { get; }
}

public sealed class MigrationBatchResult
{
    public const int MaximumResults = 256;

    private readonly ReadOnlyCollection<MigrationExecutionResult> _results;

    public MigrationBatchResult(
        IReadOnlyList<MigrationExecutionResult> results,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(results);
        completedAtUtc = DomainValidation.RequireUtc(completedAtUtc, nameof(completedAtUtc));

        if (results.Count > MaximumResults)
        {
            throw new ArgumentException(
                $"A migration batch cannot return more than {MaximumResults} results.",
                nameof(results));
        }

        var copy = new MigrationExecutionResult[results.Count];
        int previousMigrationNumber = 0;

        for (int index = 0; index < results.Count; index++)
        {
            MigrationExecutionResult result = results[index] ?? throw new ArgumentException(
                "A migration result cannot be null.",
                nameof(results));

            if (result.Migration.Number.Value <= previousMigrationNumber)
            {
                throw new ArgumentException(
                    "Migration results must be unique and ordered by increasing migration number.",
                    nameof(results));
            }

            if (result.CompletedAtUtc > completedAtUtc)
            {
                throw new ArgumentException(
                    "A migration cannot complete after its enclosing batch.",
                    nameof(results));
            }

            copy[index] = result;
            previousMigrationNumber = result.Migration.Number.Value;
        }

        _results = Array.AsReadOnly(copy);
        CompletedAtUtc = completedAtUtc;
    }

    public IReadOnlyList<MigrationExecutionResult> Results => _results;

    public DateTimeOffset CompletedAtUtc { get; }

    public bool HasFailures => _results.Any(static result => result.Outcome == MigrationOutcome.Failed);
}

/// <summary>Read-only, safe projection of one row from the repository migration ledger.</summary>
public sealed class MigrationHistoryEntry
{
    public const int MaximumNameLength = 96;
    public const int MaximumChecksumLength = 64;

    public MigrationHistoryEntry(int number, string name, string checksum)
    {
        if (number is <= 0 or > MigrationNumber.MaximumValue) throw new ArgumentOutOfRangeException(nameof(number));
        Number = number;
        Name = RequireToken(name, nameof(name), MaximumNameLength);
        Checksum = RequireHex(checksum, nameof(checksum), MaximumChecksumLength);
    }

    public int Number { get; }
    public string Name { get; }
    public string Checksum { get; }

    private static string RequireToken(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Length > maximumLength || value.Any(static c => !(c is >= 'a' and <= 'z') && !(c is >= '0' and <= '9') && c != '_' && c != '-' && c != '.'))
            throw new ArgumentException("The value contains unsupported characters.", parameterName);
        return value;
    }

    private static string RequireHex(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 64 || value.Length > maximumLength || value.Any(static c => !(c is >= '0' and <= '9') && !(c is >= 'a' and <= 'f')))
            throw new ArgumentException("The value must be hexadecimal.", parameterName);
        return value;
    }
}
