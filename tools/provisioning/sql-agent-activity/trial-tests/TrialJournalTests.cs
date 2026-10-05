using System.Text.Json;

namespace SqlObserver.AgentActivityTrial.Tests;

public sealed class TrialJournalTests
{
    private static readonly string[] Phases = ["prepared", "commit_pending", "installed", "remove_pending", "removed"];

    public static IEnumerable<object[]> Transitions()
    {
        foreach (string before in Phases)
        foreach (string after in Phases)
        {
            bool allowed = before == after || (before, after) is
                ("prepared", "commit_pending") or ("prepared", "removed") or
                ("commit_pending", "installed") or ("commit_pending", "remove_pending") or ("commit_pending", "removed") or
                ("installed", "remove_pending") or ("installed", "removed") or ("remove_pending", "removed");
            yield return [before, after, allowed];
        }
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void RecoveryStateMachineRefusesBackwardOrSkippedInstallation(string from, string to, bool allowed)
    {
        var (baseline, installed) = TrialFixture.Create();
        var before = Receipt(from, baseline, from == "prepared" ? null : installed);
        var after = Receipt(to, baseline, to == "prepared" || from == "prepared" && to == "removed" ? null : installed);
        if (allowed) TrialJournal.ValidateTransition(before, after);
        else Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("commit_pending")]
    [InlineData("installed")]
    [InlineData("remove_pending")]
    [InlineData("removed")]
    public void SerializationRoundtripRetainsProvenanceAndAllowsDiagnosticUpdate(string phase)
    {
        var (baseline, installed) = TrialFixture.Create();
        TrialReceipt original = Receipt(phase, baseline, phase == "prepared" ? null : installed);
        var roundtrip = JsonSerializer.Deserialize<TrialReceipt>(JsonSerializer.Serialize(original))!;
        TrialJournal.ValidateTransition(original, roundtrip with { FailureCode = "explicit_recovery" });
    }

    [Theory]
    [InlineData("commit_pending")] [InlineData("installed")] [InlineData("remove_pending")]
    public void PhasesThatMayOwnDatabaseObjectsRequireExactPostimage(string phase)
    {
        var (baseline, _) = TrialFixture.Create();
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt(phase, baseline, null), baseline.DeploymentId));
    }

    [Fact]
    public void PreparedRollbackMayFinishWithoutAnInstalledPostimage()
    {
        var (baseline, _) = TrialFixture.Create();
        var prepared = Receipt("prepared", baseline, null);
        TrialJournal.ValidateTransition(prepared, prepared with { Phase = "removed" });
    }

    [Theory]
    [InlineData("prepared")] [InlineData("removed")]
    public void PostimageCannotBeInventedOutsideTheWriteAheadTransition(string phase)
    {
        var (baseline, installed) = TrialFixture.Create();
        var before = Receipt(phase, baseline, null);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, before with { Installed = installed }));
    }

    [Theory]
    [InlineData("schema")] [InlineData("outer-deployment")] [InlineData("baseline-deployment")]
    [InlineData("principal")] [InlineData("phase")] [InlineData("baseline-null")]
    [InlineData("snapshot-null")] [InlineData("failure-bound")]
    public void MalformedReceiptEnvelopeCannotEnterRecovery(string mutation)
    {
        var (baseline, _) = TrialFixture.Create(); var receipt = Receipt("prepared", baseline, null);
        receipt = mutation switch
        {
            "schema" => receipt with { SchemaVersion = 3 },
            "outer-deployment" => receipt with { DeploymentId = Guid.NewGuid() },
            "baseline-deployment" => receipt with { Baseline = new(baseline.Snapshot with { DeploymentId = Guid.NewGuid() }) },
            "principal" => receipt with { MonitorPrincipal = "TEST\\Other" },
            "phase" => receipt with { Phase = "committed" },
            "baseline-null" => receipt with { Baseline = null! },
            "snapshot-null" => receipt with { Baseline = new(null!) },
            _ => receipt with { FailureCode = new string('x', 257) },
        };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt, baseline.DeploymentId));
    }

    [Theory]
    [InlineData("major")] [InlineData("edition")] [InlineData("platform")]
    [InlineData("master")] [InlineData("msdb")] [InlineData("login-id")]
    [InlineData("user-id")] [InlineData("sid")]
    public void CorruptBaselineIdentityCannotBeAcceptedAsAValidSelfComparison(string mutation)
    {
        var (baseline, _) = TrialFixture.Create(); TrialSnapshot snapshot = baseline.Snapshot;
        snapshot = mutation switch
        {
            "major" => snapshot with { Identity = snapshot.Identity with { MajorVersion = 16 } },
            "edition" => snapshot with { Identity = snapshot.Identity with { EngineEdition = 4 } },
            "platform" => snapshot with { Identity = snapshot.Identity with { PathSeparator = "/" } },
            "master" => snapshot with { Identity = snapshot.Identity with { MasterDatabaseGuid = Guid.Empty } },
            "msdb" => snapshot with { Identity = snapshot.Identity with { MsdbDatabaseGuid = Guid.Empty } },
            "login-id" => snapshot with { Monitor = snapshot.Monitor with { LoginId = 0 } },
            "user-id" => snapshot with { Monitor = snapshot.Monitor with { MasterUserId = -1 } },
            _ => snapshot with { Monitor = snapshot.Monitor with { SidHex = "nothex" } },
        };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("prepared", new(snapshot), null), baseline.DeploymentId));
    }

    [Theory]
    [InlineData("deployment")] [InlineData("identity")] [InlineData("monitor")]
    public void InstalledPostimageMustBelongToTheOriginalTargetAndMonitor(string mutation)
    {
        var (baseline, installed) = TrialFixture.Create();
        installed = mutation switch
        {
            "deployment" => installed with { DeploymentId = Guid.NewGuid() },
            "identity" => installed with { Identity = installed.Identity with { MasterDatabaseGuid = Guid.NewGuid() } },
            _ => installed with { Monitor = installed.Monitor with { LoginId = installed.Monitor.LoginId + 1 } },
        };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("installed", baseline, installed), baseline.DeploymentId));
    }

    [Fact]
    public void CoherentlyRewrittenBaselineAndPostimageStillCannotReplaceOriginalProvenance()
    {
        var (baseline, installed) = TrialFixture.Create();
        var before = Receipt("prepared", baseline, null);
        var changedIdentity = baseline.Snapshot.Identity with { MasterDatabaseGuid = Guid.NewGuid() };
        var after = Receipt("commit_pending", new(baseline.Snapshot with { Identity = changedIdentity }), installed with { Identity = changedIdentity });
        TrialJournal.ValidateReceipt(after, baseline.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Fact]
    public void ExistingInstalledPostimageCannotBeDroppedEvenWhenRemovalIsReported()
    {
        var (baseline, installed) = TrialFixture.Create(); var before = Receipt("installed", baseline, installed);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, before with { Phase = "removed", Installed = null }));
    }

    [Fact]
    public void ReplacingBothCertificatePublicImagesCannotRewriteSavedPostimage()
    {
        var (baseline, installed) = TrialFixture.Create(); var before = Receipt("installed", baseline, installed);
        var changed = installed with { Facts = TrialFixture.Order(installed.Facts.Select(fact =>
            fact.Kind == "certificate" && fact.Key == "public_sha256" ? fact with { Value = new string('c', 64) } : fact)) };
        var after = before with { Phase = "remove_pending", Installed = changed };
        TrialJournal.ValidateReceipt(after, baseline.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Fact]
    public void EmptyExpectedDeploymentIsRejected()
    {
        var (baseline, _) = TrialFixture.Create();
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("prepared", baseline, null), Guid.Empty));
    }

    private static TrialReceipt Receipt(string phase, TrialBaseline baseline, TrialSnapshot? installed) =>
        new(1, baseline.DeploymentId, baseline.MonitorPrincipal, phase, baseline, installed, null);
}
