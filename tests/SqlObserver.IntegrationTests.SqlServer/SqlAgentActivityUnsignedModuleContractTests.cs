namespace SqlObserver.IntegrationTests.SqlServer;

internal static class SqlAgentUnsignedModuleEnrollment
{
    internal const string Gate = "SQLOBSERVER_AGENT_MODULE_UNSIGNED_QUALIFICATION";

    internal static SqlAgentModuleEnrollment Load(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read(Gate) != "1")
            throw new InvalidOperationException("Unsigned Agent module qualification requires explicit opt-in; selected cases never skip.");
        return SqlAgentModuleEnrollment.Load(read);
    }
}

public sealed class SqlAgentActivityUnsignedModuleContractTests
{
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("0")] [InlineData("true")] [InlineData(" 1")]
    public void DisabledUnsignedSelectionFailsBeforeOtherInputs(string? value)
    {
        int reads = 0;
        Assert.Throws<InvalidOperationException>(() => SqlAgentUnsignedModuleEnrollment.Load(name =>
        {
            reads++;
            Assert.Equal(SqlAgentUnsignedModuleEnrollment.Gate, name);
            return value;
        }));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("QUALIFICATION", null)] [InlineData("QUALIFICATION", "0")]
    [InlineData("SOURCE_VERSION", null)] [InlineData("SOURCE_VERSION", "1")]
    [InlineData("DEFINITION_SHA256", null)] [InlineData("DEFINITION_SHA256", "private-unreviewed-value")]
    public void UnsignedOptInStillRequiresReviewedSignedModuleEnrollment(string field, string? value)
    {
        var values = Valid();
        values[SqlAgentModuleEnrollment.Prefix + field] = value;
        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlAgentUnsignedModuleEnrollment.Load(name => values.GetValueOrDefault(name)));
        Assert.DoesNotContain("private-unreviewed-value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnrollmentRetainsDefinitionWithoutAttestingDatabaseSignatureState()
    {
        var values = Valid();
        var enrollment = SqlAgentUnsignedModuleEnrollment.Load(name => values.GetValueOrDefault(name));
        Assert.Equal(2, enrollment.SourceVersion);
        Assert.Equal(SqlAgentModuleEnrollment.ReviewedDefinitionHash, enrollment.DefinitionHash);
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        [SqlAgentUnsignedModuleEnrollment.Gate] = "1",
        [SqlAgentModuleEnrollment.Prefix + "QUALIFICATION"] = "1",
        [SqlAgentModuleEnrollment.Prefix + "SOURCE_VERSION"] = "2",
        [SqlAgentModuleEnrollment.Prefix + "DEFINITION_SHA256"] = SqlAgentModuleEnrollment.ReviewedDefinitionHash,
    };
}
