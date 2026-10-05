namespace SqlObserver.IntegrationTests.SqlServer;

/// <summary>Diagnostic string shapes only: no executable resolution or service identity attestation.</summary>
internal sealed record SqlAgentServiceFilenameShape(bool Valid, bool Candidate, bool StrictMatch,
    bool Quoted, bool TrailingArguments, bool Ambiguous, bool AgentExecutableToken)
{
    internal const int MaximumLength = 256;
    internal static readonly SqlAgentServiceFilenameShape Invalid = new(false, false, false, false, false, true, false);

    internal static SqlAgentServiceFilenameShape Parse(string? filename)
    {
        if (filename is null || filename.Length > MaximumLength || filename.Any(char.IsControl)) return Invalid;
        string value = filename.Trim(' ').Replace('\\', '/');
        if (value.Length == 0) return Invalid;
        bool candidate = value.Contains("sqlagent.exe", StringComparison.OrdinalIgnoreCase);
        // Mirrors the existing SQL filter; intentionally does not interpret command arguments.
        string strictPath = value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        bool strict = !strictPath.Contains('"') && AgentBasename(strictPath);
        if (value[0] == '"')
        {
            int close = value.IndexOf('"', 1);
            if (close < 0) return new(true, candidate, strict, true, false, true, false);
            string executable = value[1..close];
            string suffix = value[(close + 1)..];
            bool hasArguments = suffix.Trim(' ').Length > 0;
            bool ambiguous = executable.Length == 0 || suffix.Length > 0 && suffix[0] != ' ';
            return new(true, candidate, strict, true, hasArguments, ambiguous, !ambiguous && AgentBasename(executable));
        }
        if (value.Contains('"')) return new(true, candidate, strict, false, false, true, false);
        // An unquoted path can contain spaces. A .exe followed by a space is only
        // a diagnostic token boundary, not proof of Windows executable resolution.
        int boundary = -1;
        for (int index = 0; index <= value.Length - 4; index++)
        {
            if (!value.AsSpan(index, 4).Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            int end = index + 4;
            if (end == value.Length || value[end] == ' ') { boundary = end; break; }
        }
        if (boundary < 0) return new(true, candidate, strict, false, false, true, false);
        string token = value[..boundary];
        bool arguments = value[boundary..].Trim(' ').Length > 0;
        return new(true, candidate, strict, false, arguments, token.Contains(' '), AgentBasename(token));
    }

    private static bool AgentBasename(string value) => value[(value.LastIndexOf('/') + 1)..]
        .Equals("sqlagent.exe", StringComparison.OrdinalIgnoreCase);
}

public sealed class SqlAgentServiceFilenameShapeTests
{
    [Theory]
    [InlineData(@"C:\SQL\SQLAGENT.EXE", true, true, false, false, false, true)]
    [InlineData("\"C:\\Program Files\\SQLAGENT.EXE\"", true, true, true, false, false, true)]
    [InlineData("\"C:\\Program Files\\SQLAGENT.EXE\" -i INSTANCE", true, false, true, true, false, true)]
    [InlineData(@"C:\Program Files\SQLAGENT.EXE", true, true, false, false, true, true)]
    [InlineData(@"C:\Program Files\SQLAGENT.EXE -i INSTANCE", true, false, false, true, true, true)]
    [InlineData(@"C:\SQL\SQLAGENT.EXE -i INSTANCE", true, false, false, true, false, true)]
    [InlineData(@"C:\SQL\SQLAGENT.EXE.bak", true, false, false, false, true, false)]
    [InlineData(@"C:\sqlagent.exe\different.exe", true, false, false, false, false, false)]
    [InlineData(@"C:\SQL\different.exe -x sqlagent.exe", true, false, false, true, false, false)]
    [InlineData("\"C:\\SQL\\SQLAGENT.EXE\"suffix", true, false, true, true, true, false)]
    [InlineData("\"C:\\SQL\\SQLAGENT.EXE", true, false, true, false, true, false)]
    [InlineData("  c:/SQL/sQlAgEnT.ExE  ", true, true, false, false, false, true)]
    [InlineData(@"C:\SQL\different.exe", false, false, false, false, false, false)]
    public void ShapesPreserveStrictFilterAndDoNotPromoteSubstringMatches(string value, bool candidate, bool strict,
        bool quoted, bool arguments, bool ambiguous, bool agentToken)
    {
        var shape = SqlAgentServiceFilenameShape.Parse(value);
        Assert.True(shape.Valid); Assert.Equal(candidate, shape.Candidate); Assert.Equal(strict, shape.StrictMatch);
        Assert.Equal(quoted, shape.Quoted); Assert.Equal(arguments, shape.TrailingArguments);
        Assert.Equal(ambiguous, shape.Ambiguous); Assert.Equal(agentToken, shape.AgentExecutableToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:/SQL/SQLAGENT.EXE\n")]
    public void InvalidValuesRemainUnclassified(string? value) => Assert.Equal(SqlAgentServiceFilenameShape.Invalid, SqlAgentServiceFilenameShape.Parse(value));

    [Fact]
    public void LengthBoundIsEnforcedBeforeInterpretation()
    {
        Assert.Equal(SqlAgentServiceFilenameShape.Invalid, SqlAgentServiceFilenameShape.Parse(new string('a', 257)));
        Assert.True(SqlAgentServiceFilenameShape.Parse(new string('a', 256)).Valid);
    }

    [Fact]
    public void DiagnosticShapeCarriesNoSourceStrings()
    {
        Assert.All(typeof(SqlAgentServiceFilenameShape).GetProperties(), property => Assert.Equal(typeof(bool), property.PropertyType));
    }
}
