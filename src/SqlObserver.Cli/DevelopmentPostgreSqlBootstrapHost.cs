using System.Data.Common;
using System.Text.Json;
using SqlObserver.Infrastructure.PostgreSql;

namespace SqlObserver.Cli;

public static class DevelopmentPostgreSqlBootstrapHost
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<int> RunAsync()
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            DevelopmentBootstrapResult result = await PostgreSqlDevelopmentBootstrap.ApplyAsync(
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
                Environment.GetEnvironmentVariable("SQLOBSERVER_DEV_POSTGRES"),
                Environment.GetEnvironmentVariable("SQLOBSERVER_DEV_APP_PASSWORD"), deadline.Token).ConfigureAwait(false);
            Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return CliHostScaffold.SuccessExitCode;
        }
        catch (ArgumentException) { return Failure("development_configuration_rejected"); }
        catch (InvalidOperationException) { return Failure("development_precondition_failed"); }
        catch (InvalidDataException) { return Failure("development_migration_history_invalid"); }
        catch (DbException) { return Failure("development_repository_unavailable"); }
        catch (TimeoutException) { return Failure("development_repository_timeout"); }
        catch (OperationCanceledException) { return Failure("development_bootstrap_cancelled"); }
    }

    private static int Failure(string code)
    {
        Console.Error.WriteLine(code);
        return CliHostScaffold.RuntimeFailureExitCode;
    }
}
