using System.Text.Json.Serialization;

namespace SqlObserver.TestSetup;

internal sealed record SetupFailureDiagnostic(string Phase, string ExceptionType, string HResult, DateTimeOffset FailedAtUtc)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MigrationNumber { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NativeErrorCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeOperation { get; init; }

    internal static SetupFailureDiagnostic Create(string phase, Exception exception) => new(
        phase, exception.GetType().Name,
        "0x" + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
        DateTimeOffset.UtcNow)
    {
        MigrationNumber = (exception as SetupException)?.MigrationNumber,
        FailureCode = (exception as SetupException)?.FailureCode,
        NativeErrorCode = (exception as System.ComponentModel.Win32Exception)?.NativeErrorCode,
        NativeOperation = (exception as SetupWin32Exception)?.Operation.ToString()
    };
}
