using System.Text.Json;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Pure write-ahead/provenance checks. No signature, database or filesystem mutation.</summary>
public sealed class TrialSignatureJournalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignatureRemovalHasItsOwnImmutableWriteAheadImage(bool missingMapping)
    {
        var (baseline, installed) = TrialFixture.Create(missingMapping);
        var unsigned = UnsignedSnapshot(installed);
        var current = Receipt(2, "prepared", baseline, null);
        foreach (string phase in new[] { "commit_pending", "installed", "signature_remove_pending", "unsigned", "unsigned_remove_pending", "removed" })
        {
            var next = current with { Phase = phase, Installed = installed,
                UnsignedSnapshot = phase is "commit_pending" or "installed" ? null : unsigned };
            TrialJournal.ValidateTransition(current, next);
            Assert.Same(baseline, next.Baseline); Assert.Same(installed, next.Installed);
            current = next;
        }
        Assert.Same(unsigned, current.UnsignedSnapshot);
        Assert.Single(current.Installed!.Facts, fact => fact.Kind == "signature");
        Assert.DoesNotContain(current.UnsignedSnapshot!.Facts, fact => fact.Kind == "signature");
        Assert.Equal(missingMapping ? 0 : installed.Monitor.MasterUserId, current.Baseline.Snapshot.Monitor.MasterUserId);
    }

    [Theory]
    [InlineData("remove_pending")]
    [InlineData("unsigned_remove_pending")]
    public void UncertainSignatureCommitCanSelectEitherExactSavedImageForRemoval(string selected)
    {
        var pending = SignedRemovalPending();
        var selectedReceipt = pending with { Phase = selected };
        TrialJournal.ValidateTransition(pending, selectedReceipt);
        TrialJournal.ValidateTransition(selectedReceipt, selectedReceipt with { Phase = "removed" });
        Assert.NotNull(selectedReceipt.Installed); Assert.NotNull(selectedReceipt.UnsignedSnapshot);
    }

    [Theory]
    [InlineData("signature_remove_pending")]
    [InlineData("unsigned")]
    [InlineData("unsigned_remove_pending")]
    public void ExternallyVerifiedAbsentBaselineMayFinishRecoveryWithoutDiscardingPostimages(string phase)
    {
        var before = SignedRemovalPending() with { Phase = phase };
        var after = before with { Phase = "removed" };
        TrialJournal.ValidateTransition(before, after);
        Assert.Same(before.UnsignedSnapshot, after.UnsignedSnapshot); Assert.Same(before.Installed, after.Installed);
    }

    [Theory]
    [InlineData("signature_remove_pending")]
    [InlineData("unsigned")]
    [InlineData("unsigned_remove_pending")]
    [InlineData("remove_pending")]
    [InlineData("removed")]
    public void RecordedUnsignedImageRoundtripsAndAllowsOnlyDiagnosticUpdatesWithinItsPhase(string phase)
    {
        var original = SignedRemovalPending() with { Phase = phase };
        var copy = JsonSerializer.Deserialize<TrialReceipt>(JsonSerializer.Serialize(original))!;
        TrialJournal.ValidateTransition(original, copy with { FailureCode = "signature_probe:SqlException:229" });
        Assert.NotNull(copy.UnsignedSnapshot);
    }

    [Fact]
    public void SchemaOneWireShapeRemainsExactlySevenFieldsAndReadsWithoutUnsigned()
    {
        var (baseline, installed) = TrialFixture.Create();
        var receipt = Receipt(1, "installed", baseline, installed);
        string originalShape = JsonSerializer.Serialize(new
        {
            receipt.SchemaVersion, receipt.DeploymentId, receipt.MonitorPrincipal, receipt.Phase,
            receipt.Baseline, receipt.Installed, receipt.FailureCode,
        });
        Assert.Equal(originalShape, JsonSerializer.Serialize(receipt));
        var parsed = JsonSerializer.Deserialize<TrialReceipt>(originalShape)!;
        Assert.Null(parsed.UnsignedSnapshot); TrialJournal.ValidateTransition(receipt, parsed);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public void EvenPreparedReceiptsCannotSilentlyChangeSchemaVersion(int from, int to)
    {
        var (baseline, _) = TrialFixture.Create();
        var before = Receipt(from, "prepared", baseline, null);
        var after = before with { SchemaVersion = to };
        TrialJournal.ValidateReceipt(after, baseline.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData("signature_remove_pending")]
    [InlineData("unsigned")]
    [InlineData("unsigned_remove_pending")]
    [InlineData("remove_pending")]
    [InlineData("removed")]
    public void SchemaOneNeverAcceptsAnUnsignedImage(string phase)
    {
        var receipt = SignedRemovalPending() with { SchemaVersion = 1, Phase = phase };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt, receipt.DeploymentId));
    }

    [Theory]
    [InlineData("prepared", "signature_remove_pending")]
    [InlineData("commit_pending", "signature_remove_pending")]
    [InlineData("installed", "unsigned")]
    [InlineData("installed", "unsigned_remove_pending")]
    [InlineData("installed", "remove_pending")]
    [InlineData("installed", "removed")]
    [InlineData("remove_pending", "remove_pending")]
    [InlineData("removed", "removed")]
    public void UnsignedImageCannotBeInventedOutsideItsSingleWriteAheadTransition(string from, string to)
    {
        var pending = SignedRemovalPending();
        var before = pending with { Phase = from, UnsignedSnapshot = null, Installed = from == "prepared" ? null : pending.Installed };
        TrialJournal.ValidateReceipt(before, before.DeploymentId);
        var after = pending with { Phase = to };
        TrialJournal.ValidateReceipt(after, after.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("commit_pending")]
    [InlineData("installed")]
    public void EarlierPhasesCannotContainUnsignedProvenance(string phase)
    {
        var receipt = SignedRemovalPending() with { Phase = phase };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt, receipt.DeploymentId));
    }

    [Theory]
    [InlineData("signature_remove_pending", false)]
    [InlineData("signature_remove_pending", true)]
    [InlineData("unsigned", false)]
    [InlineData("unsigned", true)]
    [InlineData("unsigned_remove_pending", false)]
    [InlineData("unsigned_remove_pending", true)]
    public void SignaturePhasesRequireBothExactImages(string phase, bool removeSigned)
    {
        var receipt = SignedRemovalPending() with { Phase = phase };
        receipt = removeSigned ? receipt with { Installed = null } : receipt with { UnsignedSnapshot = null };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt, receipt.DeploymentId));
    }

    [Theory]
    [InlineData("signature_remove_pending", "installed")]
    [InlineData("unsigned", "signature_remove_pending")]
    [InlineData("unsigned", "remove_pending")]
    [InlineData("unsigned_remove_pending", "unsigned")]
    [InlineData("remove_pending", "unsigned_remove_pending")]
    [InlineData("removed", "signature_remove_pending")]
    [InlineData("removed", "unsigned")]
    [InlineData("removed", "unsigned_remove_pending")]
    public void RecordedUnsignedPathCannotRewindOrSwitchRemovalBranches(string from, string to)
    {
        var before = SignedRemovalPending() with { Phase = from };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, before with { Phase = to }));
    }

    [Theory]
    [InlineData("signed")]
    [InlineData("validity")]
    [InlineData("grant")]
    [InlineData("object")]
    [InlineData("identity")]
    [InlineData("monitor")]
    [InlineData("deployment")]
    public void CorruptUnsignedImageCannotEnterRecovery(string mutation)
    {
        var receipt = SignedRemovalPending(); var unsigned = receipt.UnsignedSnapshot!;
        unsigned = mutation switch
        {
            "signed" => receipt.Installed!,
            "validity" => unsigned with { Facts = TrialFixture.Order(unsigned.Facts.Select(f => f.Kind == "signature_validity" ? f with { Value = "0:1" } : f)) },
            "grant" => unsigned with { PackagePermissions = unsigned.PackagePermissions.Skip(1).ToArray() },
            "object" => unsigned with { Objects = unsigned.Objects.Skip(1).ToArray() },
            "identity" => unsigned with { Identity = unsigned.Identity with { MasterDatabaseGuid = Guid.NewGuid() } },
            "monitor" => unsigned with { Monitor = unsigned.Monitor with { LoginId = unsigned.Monitor.LoginId + 1 } },
            _ => unsigned with { DeploymentId = Guid.NewGuid() },
        };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt with { UnsignedSnapshot = unsigned }, receipt.DeploymentId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NeitherPostimageMayDisappearAfterSignatureRemovalEvenAtTerminalPhase(bool removeSigned)
    {
        var before = SignedRemovalPending() with { Phase = "unsigned" };
        var after = before with { Phase = "removed" };
        after = removeSigned ? after with { Installed = null } : after with { UnsignedSnapshot = null };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Fact]
    public void CoherentCertificateReplacementStillCannotRewriteEitherSavedImage()
    {
        var before = SignedRemovalPending();
        TrialSnapshot signed = before.Installed! with { Facts = TrialFixture.Order(before.Installed!.Facts.Select(f =>
            f.Kind == "certificate" && f.Key == "public_sha256" ? f with { Value = new string('c', 64) } : f)) };
        var after = before with { Phase = "unsigned", Installed = signed, UnsignedSnapshot = UnsignedSnapshot(signed) };
        TrialJournal.ValidateReceipt(after, after.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Fact]
    public void CoherentTargetReplacementCannotRewriteBaselineAndBothPostimages()
    {
        var before = SignedRemovalPending(); var identity = before.Baseline.Snapshot.Identity with { MasterDatabaseGuid = Guid.NewGuid() };
        var after = before with { Phase = "unsigned", Baseline = new(before.Baseline.Snapshot with { Identity = identity }),
            Installed = before.Installed! with { Identity = identity }, UnsignedSnapshot = before.UnsignedSnapshot! with { Identity = identity } };
        TrialJournal.ValidateReceipt(after, after.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    private static TrialReceipt SignedRemovalPending()
    {
        var (baseline, installed) = TrialFixture.Create();
        return Receipt(2, "signature_remove_pending", baseline, installed) with { UnsignedSnapshot = UnsignedSnapshot(installed) };
    }
    private static TrialReceipt Receipt(int schema, string phase, TrialBaseline baseline, TrialSnapshot? installed) =>
        new(schema, baseline.DeploymentId, baseline.MonitorPrincipal, phase, baseline, installed, null);
    private static TrialSnapshot UnsignedSnapshot(TrialSnapshot signed) => signed with
    {
        Facts = TrialFixture.Order(signed.Facts.Where(f => f.Kind != "signature")
            .Select(f => f.Kind == "signature_validity" ? f with { Value = "0:0" } : f)),
    };
}
