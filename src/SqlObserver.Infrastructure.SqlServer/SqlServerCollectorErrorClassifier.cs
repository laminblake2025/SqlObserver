namespace SqlObserver.Infrastructure.SqlServer;

/// <summary>Execution failure categories shared by bounded passive collectors.</summary>
internal static class SqlServerCollectorErrorClassifier
{
    // Command timeouts consume the current deadline; they are not retries.
    internal static bool IsCommandTimeout(int number) => number == -2;

    internal static bool IsPermissionDenied(int number) => number is 229 or 297 or 300 or 916 or 51005;

    // Eligibility only: the execution engine owns attempt limits and the original
    // deadline. Authentication failures and unknown SQL errors remain permanent.
    internal static bool IsTransient(int number) => number is
        -1 or 20 or 26 or 53 or 64 or 121 or 233 or 258 or
        1205 or 1222 or 10053 or 10054 or 10060 or 10061 or 11001 or
        10928 or 10929 or 40197 or 40501 or 40613;
}
