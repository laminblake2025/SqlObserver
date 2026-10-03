using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using SqlObserver.Domain.Auditing;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Server;

namespace SqlObserver.ApiContractTests;

public sealed class AlertAssignmentConfigurationTests
{
    private static readonly Guid Target = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Other = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static AlertAssignmentAssigneeOptions Entry(string sid = "S-1-5-21-2201", string label = "Approved responder", bool enabled = true, bool all = false, Guid[]? targets = null) =>
        new() { Sid = sid, DisplayName = label, Enabled = enabled, AllTargets = all, TargetIds = targets ?? [Target] };
    [Fact]
    public void ScopeAndDisableOverridesApplyWithoutChangingLoginGrants()
    {
        var roster = new ConfiguredAlertAssignmentEligibility([Entry(), Entry("S-1-5-21-2202", enabled: false),
            Entry("S-1-5-21-2203", all: true, targets: []), Entry("S-1-5-21-2204")], ["S-1-5-21-2204"]);
        Assert.Equal(2, roster.ListEligible(new(Target)).Count); Assert.Single(roster.ListEligible(new(Other)));
        Assert.Null(roster.ResolveApproved(new(Other), new ActorSecurityIdentifier("S-1-5-21-2201")));
        Assert.Null(roster.ResolveApproved(new(Target), new ActorSecurityIdentifier("S-1-5-21-2204")));
        Assert.NotNull(roster.ResolveApproved(new(Target), new ActorSecurityIdentifier("S-1-5-21-2201")));
    }
    [Theory]
    [InlineData("sid")]
    [InlineData("label")]
    [InlineData("unicode")]
    [InlineData("duplicate")]
    [InlineData("count")]
    [InlineData("all-and-targets")]
    [InlineData("empty-target")]
    [InlineData("duplicate-target")]
    [InlineData("target-count")]
    [InlineData("disabled-sid")]
    public void MalformedOrUnboundedConfigurationFailsClosed(string failure)
    {
        IReadOnlyList<AlertAssignmentAssigneeOptions> rows = failure switch
        {
            "sid" => [Entry(sid: "invented")], "label" => [Entry(label: "bad\nlabel")], "unicode" => [Entry(label: new string('é', 65))],
            "duplicate" => [Entry(), Entry()], "count" => Enumerable.Range(0, 257).Select(i => Entry(sid: $"S-1-5-21-{i + 1}")).ToArray(),
            "all-and-targets" => [Entry(all: true)], "empty-target" => [Entry(targets: [Guid.Empty])],
            "duplicate-target" => [Entry(targets: [Target, Target])], "target-count" => [Entry(targets: Enumerable.Range(0, 1025).Select(_ => Guid.NewGuid()).ToArray())], _ => [Entry()],
        };
        Assert.ThrowsAny<ArgumentException>(() => new ConfiguredAlertAssignmentEligibility(rows, failure == "disabled-sid" ? ["invalid"] : []));
    }
    [Fact]
    public void Utf8LabelBoundAndEmptyRosterAreExplicit()
    {
        Assert.Empty(new ConfiguredAlertAssignmentEligibility([]).ListEligible(new(Target)));
        Assert.Equal(new string('é', 64), Assert.Single(new ConfiguredAlertAssignmentEligibility([Entry(label: new string('é', 64))]).ListEligible(new(Target))).DisplayName);
    }
    [Fact]
    public void InvalidRosterIsValidatedDuringHostStartupBeforeAnyEndpointRuns()
    {
        using var factory = new InvalidRosterFactory();
        Assert.ThrowsAny<ArgumentException>(() => factory.CreateClient());
    }
    private sealed class InvalidRosterFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("ContractTesting"); builder.ConfigureLogging(static logging => logging.ClearProviders());
            builder.UseSetting("AlertAssignment:Assignees:0:Sid", "invalid");
            builder.UseSetting("AlertAssignment:Assignees:0:DisplayName", "Approved responder");
            builder.UseSetting("AlertAssignment:Assignees:0:Enabled", "true");
        }
    }
}
