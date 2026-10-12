using System.Reflection;

namespace SqlObserver.AgentActivityTrial.Tests;

public sealed class TrialDiagnosticPrivacyTests
{
    private const string SafeIdentityInvariant = "Trial identity or monitor mapping drifted.";
    private const string PrivateCanary = "PRIVATE_CANARY: Server=private-host; User=private-user; Password=private-value";

    [Fact]
    public void ActualValidatorInvariantRemainsIdentifiableWithoutPrivateIdentity()
    {
        var (baseline, installed) = TrialFixture.Create();
        var changed = installed with { Identity = installed.Identity with { ServerName = PrivateCanary } };
        var failure = Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, changed));
        Assert.Equal(SafeIdentityInvariant, failure.Message);
        Assert.Equal("invariant:" + SafeIdentityInvariant, Code(failure));
        Assert.DoesNotContain(PrivateCanary, Code(failure), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("suffix")]
    [InlineData("prefix")]
    [InlineData("space")]
    [InlineData("newline")]
    [InlineData("case")]
    [InlineData("embedded")]
    [InlineData("unicode")]
    public void NearMatchesNeverPassTheExactMessageFence(string mutation)
    {
        string message = mutation switch
        {
            "suffix" => SafeIdentityInvariant + PrivateCanary,
            "prefix" => PrivateCanary + SafeIdentityInvariant,
            "space" => SafeIdentityInvariant + " ",
            "newline" => SafeIdentityInvariant + "\n" + PrivateCanary,
            "case" => SafeIdentityInvariant.ToUpperInvariant(),
            "embedded" => SafeIdentityInvariant.Replace("identity", PrivateCanary, StringComparison.Ordinal),
            _ => SafeIdentityInvariant + "\u200b",
        };
        Assert.Equal(nameof(InvalidDataException), Code(new InvalidDataException(message)));
    }

    [Fact]
    public void AllowlistedTextOnAnUnapprovedExceptionTypeRemainsTypeOnly() =>
        Assert.Equal(nameof(InvalidOperationException), Code(new InvalidOperationException(SafeIdentityInvariant)));

    [Fact]
    public void ApprovedOuterInvariantNeverDisclosesInnerExceptionOrData()
    {
        var failure = new InvalidDataException(SafeIdentityInvariant, new InvalidOperationException(PrivateCanary));
        failure.Data["private"] = PrivateCanary;
        Assert.Equal("invariant:" + SafeIdentityInvariant, Code(failure));
    }

    [Fact]
    public void MalformedSqlMetadataReportsOnlyTypeAndLength()
    {
        string privateJson = "{\"ServerName\":\"" + PrivateCanary + "\",";
        var failure = Assert.Throws<TrialMetadataException>(() => AgentActivityTrialSql.ParseMetadata<TrialIdentity>(privateJson));
        Assert.Equal("metadata_TrialIdentity_length_" + privateJson.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), Code(failure));
        Assert.DoesNotContain(PrivateCanary, Code(failure), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    private static string Code(Exception failure)
    {
        var method = typeof(TrialCoordinator).GetMethod("FailureCode", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The reviewed diagnostic boundary was removed.");
        return Assert.IsType<string>(method.Invoke(null, [failure]));
    }
}
