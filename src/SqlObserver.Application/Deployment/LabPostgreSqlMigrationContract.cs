using System.Text;
using System.Text.Json;
using SqlObserver.Domain.Repository;

namespace SqlObserver.Application.Deployment;

/// <summary>
/// Closed invocation accepted by the source-based, single-host lab migration command.
/// It deliberately has no arbitrary host, connection-string, SQL, or migration-path input.
/// </summary>
public sealed class LabPostgreSqlMigrationInvocation
{
    public const string LoopbackAddress = "127.0.0.1";
    public const int PostgreSqlPort = 5432;
    public const string RepositoryName = "sqlobserver";
    public const string BootstrapLogin = "sqlobserver_bootstrap";
    public const string CommandName = "postgres_migrate_lab_loopback";
    public const string Usage =
        "SqlObserver.Cli postgres migrate --lab-loopback --allow-loopback-cleartext --database sqlobserver --username sqlobserver_bootstrap";

    private LabPostgreSqlMigrationInvocation()
    {
    }

    public static bool IsHelp(IReadOnlyList<string> arguments) =>
        arguments.Count == 1 && arguments[0] is "--help" or "-h";

    public static bool TryParse(IReadOnlyList<string> arguments, out LabPostgreSqlMigrationInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        invocation = null;
        if (arguments.Count != 8 ||
            !string.Equals(arguments[0], "postgres", StringComparison.Ordinal) ||
            !string.Equals(arguments[1], "migrate", StringComparison.Ordinal) ||
            !string.Equals(arguments[2], "--lab-loopback", StringComparison.Ordinal) ||
            !string.Equals(arguments[3], "--allow-loopback-cleartext", StringComparison.Ordinal) ||
            !string.Equals(arguments[4], "--database", StringComparison.Ordinal) ||
            !string.Equals(arguments[5], RepositoryName, StringComparison.Ordinal) ||
            !string.Equals(arguments[6], "--username", StringComparison.Ordinal) ||
            !string.Equals(arguments[7], BootstrapLogin, StringComparison.Ordinal))
        {
            return false;
        }

        invocation = new LabPostgreSqlMigrationInvocation();
        return true;
    }

    public static string CreateResultJson(MigrationBatchResult batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("command", CommandName);
            writer.WriteString("status", batch.HasFailures ? "failed" : "succeeded");
            writer.WriteNumber(
                "appliedCount",
                batch.Results.Count(static result => result.Outcome == MigrationOutcome.Applied));
            writer.WriteNumber(
                "alreadyAppliedCount",
                batch.Results.Count(static result => result.Outcome == MigrationOutcome.AlreadyApplied));
            writer.WriteNumber(
                "failedCount",
                batch.Results.Count(static result => result.Outcome == MigrationOutcome.Failed));
            writer.WriteString("completedAtUtc", batch.CompletedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteStartArray("migrations");
            foreach (MigrationExecutionResult result in batch.Results)
            {
                writer.WriteStartObject();
                writer.WriteNumber("number", result.Migration.Number.Value);
                writer.WriteString("name", result.Migration.Name);
                writer.WriteString("sha256", result.Migration.Checksum.ToHexString().ToLowerInvariant());
                writer.WriteString("outcome", ToWireOutcome(result.Outcome));
                if (result.FailureCode is not null)
                {
                    writer.WriteString("failureCode", result.FailureCode);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static string ToWireOutcome(MigrationOutcome outcome) => outcome switch
    {
        MigrationOutcome.Applied => "applied",
        MigrationOutcome.AlreadyApplied => "already_applied",
        MigrationOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
