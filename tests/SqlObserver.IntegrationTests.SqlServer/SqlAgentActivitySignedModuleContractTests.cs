namespace SqlObserver.IntegrationTests.SqlServer;

internal sealed record SqlAgentModuleEnrollment(string DefinitionHash, int SourceVersion)
{
    internal const string Prefix = "SQLOBSERVER_AGENT_MODULE_";
    internal const string ReviewedDefinitionHash = "447846fb7a7eaa0442b2292b17b82ee17aaf998c3b91c161cba403f7c0bf8512";
    internal const string ExpandedSourceHash = "a4069e5dafe00c85ade2a9d10e27cc7e5fc5e0565a261f48eed8bd1b9e336002";
    internal static SqlAgentModuleEnrollment Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Prefix + "QUALIFICATION") != "1") throw new InvalidOperationException("Signed Agent module qualification requires explicit opt-in; selected cases never skip.");
        if (read(Prefix + "SOURCE_VERSION") != "2") throw new InvalidOperationException("Signed Agent module source version is not reviewed.");
        if (read(Prefix + "DEFINITION_SHA256") != ReviewedDefinitionHash) throw new InvalidOperationException("Signed Agent module enrollment definition hash is not reviewed; value withheld.");
        return new(ReviewedDefinitionHash, 2);
    }
}

public sealed class SqlAgentActivitySignedModuleContractTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")]
    public void DisabledSelectionFailsBeforeOtherInputs(string? value)
    {
        int reads = 0;
        Assert.Throws<InvalidOperationException>(() => SqlAgentModuleEnrollment.Load(_ => { reads++; return value; }));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("SOURCE_VERSION", null)] [InlineData("SOURCE_VERSION", "1")] [InlineData("SOURCE_VERSION", "02")]
    [InlineData("DEFINITION_SHA256", null)] [InlineData("DEFINITION_SHA256", "private-unreviewed-value")]
    public void MissingOrUnreviewedEnrollmentFailsWithoutEcho(string field, string? value)
    {
        var values = Valid(); values[SqlAgentModuleEnrollment.Prefix + field] = value;
        var error = Assert.Throws<InvalidOperationException>(() => SqlAgentModuleEnrollment.Load(name => values.GetValueOrDefault(name)));
        Assert.DoesNotContain("private-unreviewed-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewedEnrollmentDoesNotClaimDatabaseVerification()
    {
        var values = Valid(); var enrollment = SqlAgentModuleEnrollment.Load(name => values.GetValueOrDefault(name));
        Assert.Equal(2, enrollment.SourceVersion); Assert.Equal(SqlAgentModuleEnrollment.ReviewedDefinitionHash, enrollment.DefinitionHash);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        [SqlAgentModuleEnrollment.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentModuleEnrollment.Prefix + "SOURCE_VERSION"] = "2",
        [SqlAgentModuleEnrollment.Prefix + "DEFINITION_SHA256"] = SqlAgentModuleEnrollment.ReviewedDefinitionHash,
    };
}
