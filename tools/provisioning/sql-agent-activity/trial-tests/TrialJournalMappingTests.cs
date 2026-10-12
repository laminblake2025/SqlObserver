namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Missing-master-user provenance is tested using synthetic snapshots only.
/// No database user is created, adopted or removed by these tests.</summary>
public sealed class TrialJournalMappingTests
{
    [Fact]
    public void MissingMappingRemainsTheImmutableBaselineAcrossInstallAndRemoval()
    {
        var (baseline, installed) = TrialFixture.Create(missingMasterMapping: true);
        Assert.Equal(0, baseline.Snapshot.Monitor.MasterUserId);
        Assert.True(installed.Monitor.MasterUserId > 0);
        Assert.Equal(8, installed.Objects.Count);
        Assert.Equal(installed.Monitor.MasterUserId, Assert.Single(installed.Objects, item => item.Kind == "monitor_user").Id);
        var receipt = Receipt("prepared", baseline, null);
        foreach (string phase in new[] { "commit_pending", "installed", "remove_pending", "removed" })
        {
            var next = receipt with { Phase = phase, Installed = installed };
            TrialJournal.ValidateTransition(receipt, next);
            Assert.Equal(0, next.Baseline.Snapshot.Monitor.MasterUserId);
            receipt = next;
        }
        AgentActivityTrialSql.ValidateBaselineRestored(baseline, baseline);
    }

    [Fact]
    public void ExistingMasterUserIsNeverIncludedInTheOwnedObjectInventory()
    {
        var (baseline, installed) = TrialFixture.Create();
        Assert.True(baseline.Snapshot.Monitor.MasterUserId > 0);
        Assert.Equal(7, installed.Objects.Count);
        Assert.DoesNotContain(installed.Objects, item => item.Kind == "monitor_user");
        Assert.DoesNotContain(installed.Facts, fact => fact.Kind == "monitor_marker");
        TrialJournal.ValidateReceipt(Receipt("removed", baseline, installed), baseline.DeploymentId);
        AgentActivityTrialSql.ValidateBaselineRestored(baseline, baseline);
    }

    [Theory]
    [InlineData("name")] [InlineData("database")] [InlineData("type")]
    [InlineData("sid")] [InlineData("id")] [InlineData("owner")]
    public void ForeignUserCannotBeSubstitutedIntoTheRemovalPostimage(string mutation)
    {
        var (baseline, installed) = TrialFixture.Create(missingMasterMapping: true);
        var owned = Assert.Single(installed.Objects, item => item.Kind == "monitor_user");
        var foreign = mutation switch
        {
            "name" => owned with { Name = "TEST\\Foreign" },
            "database" => owned with { Database = "msdb" },
            "type" => owned with { Type = "S" },
            "sid" => owned with { Detail = "FOREIGN_SID" },
            "owner" => owned with { OwnerId = 999 },
            _ => owned with { Id = owned.Id + 1 },
        };
        var changed = installed with { Objects = TrialFixture.Order(installed.Objects.Select(item => item == owned ? foreign : item)) };
        var before = Receipt("installed", baseline, installed);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, before with { Phase = "remove_pending", Installed = changed }));
    }

    [Fact]
    public void ExistingUserCannotBeRelabeledAsPackageOwnedByAnEightObjectPostimage()
    {
        var (existing, _) = TrialFixture.Create();
        var (_, temporaryMappingPostimage) = TrialFixture.Create(missingMasterMapping: true);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("installed", existing, temporaryMappingPostimage), existing.DeploymentId));
    }

    [Fact]
    public void MissingMappingBaselineCannotBeRewrittenAfterInstallation()
    {
        var (missing, installed) = TrialFixture.Create(missingMasterMapping: true);
        var (existing, _) = TrialFixture.Create();
        var before = Receipt("prepared", missing, null);
        var after = Receipt("prepared", existing, null);
        TrialJournal.ValidateReceipt(after, existing.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(Receipt("installed", missing, installed),
            Receipt("removed", existing, installed)));
    }

    [Fact]
    public void ZeroMappingBaselineRejectsContradictoryObservedMasterMapping()
    {
        var (baseline, installed) = TrialFixture.Create(missingMasterMapping: true);
        var mapping = Assert.Single(installed.Facts, fact => fact.Scope == "master" && fact.Kind == "monitor_mapping");
        var corrupt = new TrialBaseline(baseline.Snapshot with { Facts = TrialFixture.Order(baseline.Snapshot.Facts.Append(mapping)) });
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("prepared", corrupt, null), baseline.DeploymentId));
    }

    [Fact]
    public void MissingMappingRestorationCannotLeaveAPositiveForeignOrCreatedMapping()
    {
        var (missing, _) = TrialFixture.Create(missingMasterMapping: true);
        var (existing, _) = TrialFixture.Create();
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateBaselineRestored(missing, existing));
    }

    [Fact]
    public void OptionalOwnedUserCannotDisappearFromTheSavedPostimage()
    {
        var (baseline, installed) = TrialFixture.Create(missingMasterMapping: true);
        var before = Receipt("installed", baseline, installed);
        var missing = installed with { Objects = installed.Objects.Where(item => item.Kind != "monitor_user").ToArray() };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, before with { Phase = "removed", Installed = missing }));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TemporaryUserRequiresItsExactDeploymentMarker(bool replace)
    {
        var (baseline, installed) = TrialFixture.Create(missingMasterMapping: true);
        var marker = Assert.Single(installed.Facts, fact => fact.Scope == "master" && fact.Kind == "monitor_marker");
        Assert.Equal(baseline.DeploymentId.ToString("D"), marker.Value);
        var facts = replace ? installed.Facts.Select(fact => fact == marker ? fact with { Value = Guid.NewGuid().ToString("D") } : fact)
            : installed.Facts.Where(fact => fact != marker);
        var changed = installed with { Facts = TrialFixture.Order(facts) };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("remove_pending", baseline, changed), baseline.DeploymentId));
    }

    [Fact]
    public void ExistingUserWithForeignTrialMarkerCannotBecomeAFreshBaseline()
    {
        var (existing, _) = TrialFixture.Create();
        var (_, installed) = TrialFixture.Create(missingMasterMapping: true);
        var marker = Assert.Single(installed.Facts, fact => fact.Kind == "monitor_marker");
        var adopted = new TrialBaseline(existing.Snapshot with { Facts = TrialFixture.Order(existing.Snapshot.Facts.Append(marker)) });
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("prepared", adopted, null), existing.DeploymentId));
    }

    private static TrialReceipt Receipt(string phase, TrialBaseline baseline, TrialSnapshot? installed) =>
        new(1, baseline.DeploymentId, baseline.MonitorPrincipal, phase, baseline, installed, null);
}
