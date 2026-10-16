namespace SqlObserver.TestSetup;

internal sealed record SetupFailureDiagnostic(string Phase, string ExceptionType, string HResult, DateTimeOffset FailedAtUtc)
{
    internal static SetupFailureDiagnostic Create(string phase, Exception exception) => new(
        phase, exception.GetType().Name,
        "0x" + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.UtcNow);
}
