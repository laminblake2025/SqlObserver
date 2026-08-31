using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Data.Common;
using SqlObserver.Application.Deployment;
using SqlObserver.Application.Ports;
using SqlObserver.Domain.Repository;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.Cli;

/// <summary>
/// Source-based lab-only host for the verified embedded PostgreSQL migration catalog.
/// This is not an installer or a supported production deployment interface.
/// </summary>
public static class LabPostgreSqlMigrationHost
{
    private const int MaximumPasswordCharacters = 256;
    private static readonly RepositoryCallTimeout MigrationTimeout = new(TimeSpan.FromMinutes(2));

    public static async Task<int> RunAsync(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (LabPostgreSqlMigrationInvocation.IsHelp(arguments))
        {
            Console.Out.WriteLine(LabPostgreSqlMigrationInvocation.Usage);
            Console.Out.WriteLine(
                "Lab only: fixed 127.0.0.1:5432 target, interactive password, verified embedded migrations.");
            return CliHostScaffold.SuccessExitCode;
        }

        if (!LabPostgreSqlMigrationInvocation.TryParse(arguments, out LabPostgreSqlMigrationInvocation? invocation))
        {
            WriteFailure("invalid_invocation");
            Console.Error.WriteLine(LabPostgreSqlMigrationInvocation.Usage);
            return CliHostScaffold.UsageExitCode;
        }

        char[]? secretCharacters = null;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            secretCharacters = ReadPassword();
            PostgreSqlMigrationExecution execution = await ApplyMigrationsAsync(invocation!, secretCharacters, cancellation.Token)
                .ConfigureAwait(false);
            if (!execution.Succeeded)
            {
                WriteFailure("repository_unavailable");
                return CliHostScaffold.RuntimeFailureExitCode;
            }

            MigrationBatchResult result = execution.Batch!;
            Console.Out.Write(LabPostgreSqlMigrationInvocation.CreateResultJson(result));
            return result.HasFailures
                ? CliHostScaffold.MigrationFailureExitCode
                : CliHostScaffold.SuccessExitCode;
        }
        catch (OperationCanceledException)
        {
            WriteFailure("cancelled");
            return CliHostScaffold.CancelledExitCode;
        }
        catch (TimeoutException)
        {
            WriteFailure("repository_timeout");
            return CliHostScaffold.RuntimeFailureExitCode;
        }
        catch (InvalidDataException)
        {
            WriteFailure("migration_history_invalid");
            return CliHostScaffold.RuntimeFailureExitCode;
        }
        catch (InvalidOperationException)
        {
            WriteFailure("lab_precondition_failed");
            return CliHostScaffold.RuntimeFailureExitCode;
        }
        catch (ArgumentException)
        {
            WriteFailure("configuration_rejected");
            return CliHostScaffold.RuntimeFailureExitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            if (secretCharacters is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secretCharacters.AsSpan()));
            }
        }
    }

    private static async ValueTask<PostgreSqlMigrationExecution> ApplyMigrationsAsync(
        LabPostgreSqlMigrationInvocation invocation,
        char[] password,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(password);

        string passwordValue = new(password);
        string repositoryConfiguration = string.Empty;
        try
        {
            var settings = new DbConnectionStringBuilder();
            settings["Host"] = LabPostgreSqlMigrationInvocation.LoopbackAddress;
            settings["Port"] = LabPostgreSqlMigrationInvocation.PostgreSqlPort;
            settings["Database"] = LabPostgreSqlMigrationInvocation.RepositoryName;
            settings["Username"] = LabPostgreSqlMigrationInvocation.BootstrapLogin;
            settings["Password"] = passwordValue;
            settings["SSL Mode"] = "Disable";
            settings["Pooling"] = false;
            settings["Timeout"] = 15;
            settings["Command Timeout"] = checked((int)MigrationTimeout.Value.TotalSeconds);
            settings["Include Error Detail"] = false;
            settings["Persist Security Info"] = false;
            settings["Log Parameters"] = false;
            repositoryConfiguration = settings.ConnectionString;
            settings.Clear();
            passwordValue = string.Empty;
            return await PostgreSqlMigrationExecutor.ApplyPendingAsync(
                    repositoryConfiguration,
                    new MigrationApplyRequest(MigrationBatchResult.MaximumResults, MigrationTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            passwordValue = string.Empty;
            repositoryConfiguration = string.Empty;
        }
    }

    private static char[] ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Interactive input is required.");
        }

        Console.Error.Write("PostgreSQL bootstrap password (input hidden): ");
        char[] buffer = new char[MaximumPasswordCharacters];
        int length = 0;
        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    if (length == 0)
                    {
                        throw new InvalidOperationException("A password is required.");
                    }

                    return buffer[..length].ToArray();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        buffer[--length] = '\0';
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    Console.Error.WriteLine();
                    throw new OperationCanceledException();
                }

                if (char.IsControl(key.KeyChar))
                {
                    continue;
                }

                if (length == buffer.Length)
                {
                    Console.Error.WriteLine();
                    throw new InvalidOperationException("The password exceeds the supported bound.");
                }

                buffer[length++] = key.KeyChar;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    private static void WriteFailure(string code)
    {
        Console.Error.WriteLine(
            FormattableString.Invariant(
                $"{{\"schemaVersion\":1,\"command\":\"{LabPostgreSqlMigrationInvocation.CommandName}\",\"status\":\"failed\",\"code\":\"{code}\"}}"));
    }
}
