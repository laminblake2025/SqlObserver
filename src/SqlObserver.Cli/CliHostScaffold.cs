namespace SqlObserver.Cli;

/// <summary>Defines stable process exit codes for the closed command-line boundary.</summary>
public static class CliHostScaffold
{
    /// <summary>The requested operation completed successfully.</summary>
    public const int SuccessExitCode = 0;

    /// <summary>The command line was not one of the explicitly supported forms.</summary>
    public const int UsageExitCode = 2;

    /// <summary>The migration completed with a closed migration failure result.</summary>
    public const int MigrationFailureExitCode = 3;

    /// <summary>The bounded migration host could not safely complete the operation.</summary>
    public const int RuntimeFailureExitCode = 4;

    /// <summary>The operator cancelled the command.</summary>
    public const int CancelledExitCode = 130;
}
