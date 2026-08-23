using System.Text;

namespace SqlObserver.Domain;

internal static class DomainValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string RequireSafeText(
        string value,
        string parameterName,
        int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Control characters are not permitted.", parameterName);
        }

        int byteCount;

        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The value must contain valid Unicode text.", parameterName, exception);
        }

        if (byteCount > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"The UTF-8 representation cannot exceed {maximumUtf8Bytes} bytes.",
                parameterName);
        }

        return value;
    }

    public static string RequireAsciiToken(
        string value,
        string parameterName,
        int maximumLength,
        Func<char, bool> isPermitted,
        bool requireLeadingLetter = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        ArgumentNullException.ThrowIfNull(isPermitted);

        if (value.Length is 0 || value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must contain between 1 and {maximumLength} characters.",
                parameterName);
        }

        if ((requireLeadingLetter && !IsAsciiLetter(value[0])) || value.Any(character => !isPermitted(character)))
        {
            throw new ArgumentException("The value contains unsupported characters.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must have a zero UTC offset.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset RequireUtcMicrosecondAligned(
        DateTimeOffset value,
        string parameterName)
    {
        value = RequireUtc(value, parameterName);

        if (value.Ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentException(
                "The UTC timestamp must align exactly to PostgreSQL microsecond precision.",
                parameterName);
        }

        return value;
    }

    public static bool IsAsciiLetter(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    public static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

    public static int GetUtf8ByteCount(string value) => StrictUtf8.GetByteCount(value);
}
