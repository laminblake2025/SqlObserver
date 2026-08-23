using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>An immutable migration and its verified embedded SQL.</summary>
public sealed class PostgreSqlMigrationResource
{
    internal PostgreSqlMigrationResource(
        string fileName,
        MigrationDescriptor descriptor,
        string sql,
        string checksumHex)
    {
        FileName = fileName;
        Descriptor = descriptor;
        Sql = sql;
        ChecksumHex = checksumHex;
    }

    public string FileName { get; }

    public MigrationDescriptor Descriptor { get; }

    public string ChecksumHex { get; }

    internal string Sql { get; }
}

/// <summary>
/// Loads the checked-in SQL migration resources and verifies every exact committed byte sequence
/// against the embedded SHA-256 manifest before any database connection is opened.
/// </summary>
public sealed partial class PostgreSqlMigrationCatalog
{
    private const int MaximumMigrationBytes = 4 * 1024 * 1024;
    private const int MaximumManifestBytes = 64 * 1024;
    private const string ManifestFileName = "checksums.sha256";

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ReadOnlyCollection<PostgreSqlMigrationResource> _migrations;

    private PostgreSqlMigrationCatalog(IReadOnlyList<PostgreSqlMigrationResource> migrations)
    {
        _migrations = Array.AsReadOnly(migrations.ToArray());
    }

    public IReadOnlyList<PostgreSqlMigrationResource> Migrations => _migrations;

    public static PostgreSqlMigrationCatalog LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(PostgreSqlMigrationCatalog).Assembly;
        string[] resourceNames = assembly.GetManifestResourceNames();
        string manifestResourceName = FindSingleResource(resourceNames, ManifestFileName);
        byte[] manifestBytes = ReadBoundedResource(assembly, manifestResourceName, MaximumManifestBytes);
        List<MigrationManifestEntry> expectedChecksums = ParseManifest(
            DecodeExactLfUtf8(manifestBytes, ManifestFileName));

        var migrations = new List<PostgreSqlMigrationResource>();

        foreach (MigrationManifestEntry manifestEntry in expectedChecksums)
        {
            string fileName = manifestEntry.FileName;
            string expectedChecksum = manifestEntry.Checksum;
            Match match = MigrationFileNamePattern().Match(fileName);
            if (!match.Success)
            {
                throw new InvalidDataException($"The migration manifest contains an invalid file name: {fileName}.");
            }

            string resourceName = FindSingleResource(resourceNames, fileName);
            byte[] rawBytes = ReadBoundedResource(assembly, resourceName, MaximumMigrationBytes);
            string sql = DecodeExactLfUtf8(rawBytes, fileName);
            byte[] actualChecksumBytes = SHA256.HashData(rawBytes);
            string actualChecksum = Convert.ToHexString(actualChecksumBytes).ToLowerInvariant();

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualChecksum),
                    Encoding.ASCII.GetBytes(expectedChecksum)))
            {
                throw new InvalidDataException($"Embedded migration checksum verification failed for {fileName}.");
            }

            if (ContainsTopLevelTransactionControl(sql))
            {
                throw new InvalidDataException(
                    $"Migration {fileName} contains transaction control; the runner owns exactly one transaction per migration.");
            }

            int number = int.Parse(
                match.Groups["number"].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            string name = match.Groups["name"].Value;
            var descriptor = new MigrationDescriptor(
                new MigrationNumber(number),
                name,
                new MigrationChecksum(actualChecksumBytes),
                isTransactional: true);

            migrations.Add(new PostgreSqlMigrationResource(fileName, descriptor, sql, actualChecksum));
        }

        ValidateSequence(migrations);

        string[] sqlResourceNames = resourceNames
            .Where(static name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                name.Contains(".Migrations.", StringComparison.Ordinal))
            .ToArray();
        if (sqlResourceNames.Length != migrations.Count)
        {
            throw new InvalidDataException(
                "The embedded migration resources and checksum manifest must contain exactly the same SQL files.");
        }

        return new PostgreSqlMigrationCatalog(migrations);
    }

    private static List<MigrationManifestEntry> ParseManifest(string manifest)
    {
        var result = new List<MigrationManifestEntry>();
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        string[] lines = manifest.Split('\n');
        int lineCount = lines.Length > 0 && lines[^1].Length == 0
            ? lines.Length - 1
            : lines.Length;

        for (int index = 0; index < lineCount; index++)
        {
            string sourceLine = lines[index];

            Match match = ManifestLinePattern().Match(sourceLine);
            if (!match.Success)
            {
                throw new InvalidDataException("The embedded migration checksum manifest has an invalid line.");
            }

            string fileName = match.Groups["file"].Value;
            string checksum = match.Groups["checksum"].Value;
            if (!fileNames.Add(fileName))
            {
                throw new InvalidDataException($"The migration checksum manifest repeats {fileName}.");
            }

            result.Add(new MigrationManifestEntry(fileName, checksum));
        }

        if (result.Count is 0 or > MigrationBatchResult.MaximumResults)
        {
            throw new InvalidDataException(
                $"The migration manifest must contain between 1 and {MigrationBatchResult.MaximumResults} entries.");
        }

        return result;
    }

    private static void ValidateSequence(IReadOnlyList<PostgreSqlMigrationResource> migrations)
    {
        PostgreSqlMigrationResource[] ordered = migrations
            .OrderBy(static migration => migration.Descriptor.Number.Value)
            .ToArray();

        for (int index = 0; index < ordered.Length; index++)
        {
            int expectedNumber = index + 1;
            if (ordered[index].Descriptor.Number.Value != expectedNumber)
            {
                throw new InvalidDataException(
                    $"Migration history must be a contiguous prefix beginning at 0001; expected {expectedNumber:D4}.");
            }
        }

        if (!ordered.SequenceEqual(migrations))
        {
            throw new InvalidDataException("Migration manifest entries must sort in migration-number order.");
        }
    }

    private static string FindSingleResource(IEnumerable<string> resourceNames, string fileName)
    {
        string suffix = $"Migrations.{fileName}";
        string[] matches = resourceNames
            .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"Embedded migration resource {fileName} is missing."),
            _ => throw new InvalidDataException($"Embedded migration resource {fileName} is ambiguous."),
        };
    }

    private static byte[] ReadBoundedResource(Assembly assembly, string resourceName, int maximumBytes)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidDataException($"Embedded resource {resourceName} could not be opened.");

        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"Embedded resource {resourceName} exceeds its byte limit.");
        }

        using var destination = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(destination);
        return destination.ToArray();
    }

    private static string DecodeExactLfUtf8(byte[] bytes, string fileName)
    {
        if (bytes.AsSpan().StartsWith(Utf8ByteOrderMark))
        {
            throw new InvalidDataException($"Embedded resource {fileName} must not contain a UTF-8 byte-order mark.");
        }

        if (bytes.AsSpan().Contains((byte)'\r'))
        {
            throw new InvalidDataException($"Embedded resource {fileName} must use exact LF line endings.");
        }

        return StrictUtf8.GetString(bytes);
    }

    private static bool ContainsTopLevelTransactionControl(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        int index = 0;
        string? firstKeyword = null;
        bool isTransactionControlCandidate = true;

        while (index < sql.Length)
        {
            char current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                SkipLineComment(sql, ref index);
                continue;
            }

            if (current == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                SkipBlockComment(sql, ref index);
                continue;
            }

            if (current == ';')
            {
                index++;
                firstKeyword = null;
                isTransactionControlCandidate = true;
                continue;
            }

            if (current == '\'')
            {
                SkipSingleQuotedString(sql, ref index);
                firstKeyword = null;
                isTransactionControlCandidate = false;
                continue;
            }

            if (current == '"')
            {
                SkipDoubleQuotedIdentifier(sql, ref index);
                firstKeyword = null;
                isTransactionControlCandidate = false;
                continue;
            }

            if (current == '$' && TrySkipDollarQuotedString(sql, ref index))
            {
                firstKeyword = null;
                isTransactionControlCandidate = false;
                continue;
            }

            if (isTransactionControlCandidate && IsSqlIdentifierStart(current))
            {
                int tokenStart = index;
                index++;
                while (index < sql.Length && IsSqlIdentifierPart(sql[index]))
                {
                    index++;
                }

                ReadOnlySpan<char> keyword = sql.AsSpan(tokenStart, index - tokenStart);
                if (firstKeyword is null)
                {
                    if (IsImmediateTransactionControl(keyword))
                    {
                        return true;
                    }

                    if (RequiresSecondTransactionKeyword(keyword))
                    {
                        firstKeyword = keyword.ToString();
                    }
                    else
                    {
                        isTransactionControlCandidate = false;
                    }
                }
                else
                {
                    if (IsTransactionControlPair(firstKeyword, keyword))
                    {
                        return true;
                    }

                    firstKeyword = null;
                    isTransactionControlCandidate = false;
                }

                continue;
            }

            firstKeyword = null;
            isTransactionControlCandidate = false;
            index++;
        }

        return false;
    }

    private static bool IsImmediateTransactionControl(ReadOnlySpan<char> keyword) =>
        keyword.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("COMMIT", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("END", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("ABORT", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("SAVEPOINT", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("RELEASE", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresSecondTransactionKeyword(ReadOnlySpan<char> keyword) =>
        keyword.Equals("START", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("PREPARE", StringComparison.OrdinalIgnoreCase) ||
        keyword.Equals("SET", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransactionControlPair(string firstKeyword, ReadOnlySpan<char> secondKeyword) =>
        (firstKeyword.Equals("START", StringComparison.OrdinalIgnoreCase) &&
            secondKeyword.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)) ||
        (firstKeyword.Equals("PREPARE", StringComparison.OrdinalIgnoreCase) &&
            secondKeyword.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase)) ||
        (firstKeyword.Equals("SET", StringComparison.OrdinalIgnoreCase) &&
            secondKeyword.Equals("TRANSACTION", StringComparison.OrdinalIgnoreCase));

    private static void SkipLineComment(string sql, ref int index)
    {
        index += 2;
        while (index < sql.Length && sql[index] != '\n')
        {
            index++;
        }
    }

    private static void SkipBlockComment(string sql, ref int index)
    {
        int depth = 1;
        index += 2;
        while (index < sql.Length && depth > 0)
        {
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/')
            {
                depth--;
                index += 2;
            }
            else
            {
                index++;
            }
        }
    }

    private static void SkipSingleQuotedString(string sql, ref int index)
    {
        bool usesBackslashEscapes = HasEscapeStringPrefix(sql, index);
        index++;
        while (index < sql.Length)
        {
            if (usesBackslashEscapes && sql[index] == '\\')
            {
                index = Math.Min(index + 2, sql.Length);
            }
            else if (sql[index] != '\'')
            {
                index++;
            }
            else if (index + 1 < sql.Length && sql[index + 1] == '\'')
            {
                index += 2;
            }
            else
            {
                index++;
                return;
            }
        }
    }

    private static bool HasEscapeStringPrefix(string sql, int quoteIndex)
    {
        int prefixIndex = quoteIndex - 1;
        return prefixIndex >= 0 &&
            (sql[prefixIndex] == 'e' || sql[prefixIndex] == 'E') &&
            (prefixIndex == 0 || !IsSqlIdentifierPart(sql[prefixIndex - 1]));
    }

    private static void SkipDoubleQuotedIdentifier(string sql, ref int index)
    {
        index++;
        while (index < sql.Length)
        {
            if (sql[index] != '"')
            {
                index++;
            }
            else if (index + 1 < sql.Length && sql[index + 1] == '"')
            {
                index += 2;
            }
            else
            {
                index++;
                return;
            }
        }
    }

    private static bool TrySkipDollarQuotedString(string sql, ref int index)
    {
        int tagEnd = index + 1;
        if (tagEnd >= sql.Length)
        {
            return false;
        }

        if (sql[tagEnd] != '$')
        {
            if (!IsSqlIdentifierStart(sql[tagEnd]))
            {
                return false;
            }

            tagEnd++;
            while (tagEnd < sql.Length && IsDollarQuoteTagPart(sql[tagEnd]))
            {
                tagEnd++;
            }

            if (tagEnd >= sql.Length || sql[tagEnd] != '$')
            {
                return false;
            }
        }

        string delimiter = sql[index..(tagEnd + 1)];
        int contentStart = tagEnd + 1;
        int closingDelimiter = sql.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
        index = closingDelimiter < 0
            ? sql.Length
            : closingDelimiter + delimiter.Length;
        return true;
    }

    private static bool IsSqlIdentifierStart(char value) => char.IsLetter(value) || value == '_';

    private static bool IsDollarQuoteTagPart(char value) => char.IsLetterOrDigit(value) || value == '_';

    private static bool IsSqlIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '$';

    [GeneratedRegex("^(?<number>[0-9]{4})_(?<name>[a-z][a-z0-9_]*)[.]sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFileNamePattern();

    [GeneratedRegex("^(?<checksum>[0-9a-f]{64})  (?<file>[0-9]{4}_[a-z][a-z0-9_]*[.]sql)$", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestLinePattern();

    private sealed record MigrationManifestEntry(string FileName, string Checksum);
}
