using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Pure versioned-policy, write-ahead and recovery provenance checks; no SQL or file writes.</summary>
public sealed class TrialMetadataJournalTests
{
    [Fact]
    public void ActualMissingMetadataGrantReportsOnlyItsFixedInvariant()
    {
        var (baseline, installed) = MetadataFixture();
        var missing = installed with
        {
            PackagePermissions = installed.PackagePermissions.Where(grant => grant.Name != "VIEW DEFINITION").ToArray(),
        };
        var failure = Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(
            baseline, missing, TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Equal("invariant:Metadata grants differ from the exact policy recipient, grantor or object.", DiagnosticCode(failure));
    }

    [Fact]
    public void ActualInvalidReceiptPolicyReportsOnlyItsFixedInvariant()
    {
        var (baseline, _) = TrialFixture.Create();
        var invalid = Receipt("prepared", baseline, null) with { InstallationPolicyVersion = 99 };
        var failure = Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(invalid, baseline.DeploymentId));
        Assert.Equal("invariant:The trial receipt installation policy is invalid.", DiagnosticCode(failure));
    }

    [Theory]
    [InlineData("Metadata grants differ from the exact policy recipient, grantor or object.")]
    [InlineData("Monitor EXECUTE inventories disagree under the metadata policy.")]
    [InlineData("Package grants differ from the exact installation policy.")]
    [InlineData("The trial receipt installation policy is invalid.")]
    public void NewDiagnosticMessagesRequireExactTextAndApprovedExceptionType(string invariant)
    {
        const string privateCanary = "PRIVATE_CANARY: Server=private-host; Password=private-value";
        Assert.Equal("invariant:" + invariant, DiagnosticCode(new InvalidDataException(invariant, new InvalidOperationException(privateCanary))));
        Assert.Equal(nameof(InvalidDataException), DiagnosticCode(new InvalidDataException(invariant + privateCanary)));
        Assert.Equal(nameof(InvalidDataException), DiagnosticCode(new InvalidDataException(privateCanary + invariant)));
        Assert.Equal(nameof(InvalidDataException), DiagnosticCode(new InvalidDataException(invariant + "\n")));
        Assert.Equal(nameof(InvalidOperationException), DiagnosticCode(new InvalidOperationException(invariant)));
    }

    private static string DiagnosticCode(Exception failure)
    {
        var method = typeof(TrialCoordinator).GetMethod("FailureCode", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The reviewed diagnostic boundary was removed.");
        return Assert.IsType<string>(method.Invoke(null, [failure]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataPolicyRemainsExplicitAcrossSignedUnsignedAndRemovedStates(bool missingMapping)
    {
        var (baseline, signed) = MetadataFixture(missingMapping);
        var current = Receipt("prepared", baseline, null);
        foreach (string phase in new[] { "commit_pending", "installed", "signature_remove_pending", "unsigned", "unsigned_remove_pending", "removed" })
        {
            var next = current with { Phase = phase, Installed = signed,
                UnsignedSnapshot = phase is "commit_pending" or "installed" ? null : UnsignedSnapshot(signed) };
            TrialJournal.ValidateTransition(current, next);
            Assert.Equal(3, next.SchemaVersion); Assert.Equal(2, next.InstallationPolicyVersion);
            Assert.Same(baseline, next.Baseline); Assert.Same(signed, next.Installed);
            current = next;
        }
        Assert.Equal(missingMapping ? 0 : signed.Monitor.MasterUserId, current.Baseline.Snapshot.Monitor.MasterUserId);
        Assert.Equal(2, current.UnsignedSnapshot!.PackagePermissions.Count(p => p.Name == "VIEW DEFINITION"));
    }

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(2, null, true)]
    [InlineData(3, 2, true)]
    [InlineData(0, null, false)]
    [InlineData(4, 2, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, null, false)]
    [InlineData(3, 0, false)]
    [InlineData(3, 1, false)]
    [InlineData(3, 3, false)]
    public void ReceiptSchemaHasOneExactPolicyMapping(int schema, int? policy, bool valid)
    {
        var (baseline, _) = TrialFixture.Create();
        var receipt = Receipt("prepared", baseline, null) with { SchemaVersion = schema, InstallationPolicyVersion = policy };
        if (valid) TrialJournal.ValidateReceipt(receipt, baseline.DeploymentId);
        else Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt, baseline.DeploymentId));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    public void IndividuallyValidReceiptsCannotUpgradeOrDowngradePolicy(int from, int to)
    {
        var (baseline, _) = TrialFixture.Create();
        var before = Receipt("prepared", baseline, null) with { SchemaVersion = from, InstallationPolicyVersion = from == 3 ? 2 : null };
        var after = before with { SchemaVersion = to, InstallationPolicyVersion = to == 3 ? 2 : null };
        TrialJournal.ValidateReceipt(before, baseline.DeploymentId); TrialJournal.ValidateReceipt(after, baseline.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacySerializationDoesNotAcquireANewPolicyField(int schema)
    {
        var (baseline, signed) = TrialFixture.Create();
        var receipt = new TrialReceipt(schema, baseline.DeploymentId, baseline.MonitorPrincipal, "installed", baseline, signed, null);
        string oldShape = JsonSerializer.Serialize(new
        {
            receipt.SchemaVersion, receipt.DeploymentId, receipt.MonitorPrincipal, receipt.Phase,
            receipt.Baseline, receipt.Installed, receipt.FailureCode,
        });
        Assert.Equal(oldShape, JsonSerializer.Serialize(receipt));
        var copy = JsonSerializer.Deserialize<TrialReceipt>(oldShape)!;
        Assert.Null(copy.InstallationPolicyVersion); TrialJournal.ValidateTransition(receipt, copy);
    }

    [Fact]
    public void LegacyUnsignedReceiptRetainsItsExactEightFieldShape()
    {
        var (baseline, signed) = TrialFixture.Create();
        var receipt = new TrialReceipt(2, baseline.DeploymentId, baseline.MonitorPrincipal, "unsigned", baseline, signed, null, UnsignedSnapshot(signed));
        string oldShape = JsonSerializer.Serialize(new
        {
            receipt.SchemaVersion, receipt.DeploymentId, receipt.MonitorPrincipal, receipt.Phase,
            receipt.Baseline, receipt.Installed, receipt.FailureCode, receipt.UnsignedSnapshot,
        });
        Assert.Equal(oldShape, JsonSerializer.Serialize(receipt));
        TrialJournal.ValidateTransition(receipt, JsonSerializer.Deserialize<TrialReceipt>(oldShape)!);
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("commit_pending")]
    [InlineData("installed")]
    [InlineData("signature_remove_pending")]
    [InlineData("unsigned")]
    [InlineData("unsigned_remove_pending")]
    [InlineData("remove_pending")]
    [InlineData("removed")]
    public void MetadataReceiptRoundtripPreservesVersionedProvenance(string phase)
    {
        var (baseline, signed) = MetadataFixture();
        var receipt = Receipt(phase, baseline, phase == "prepared" ? null : signed);
        if (phase is "signature_remove_pending" or "unsigned" or "unsigned_remove_pending" or "remove_pending" or "removed")
            receipt = receipt with { UnsignedSnapshot = UnsignedSnapshot(signed) };
        var copy = JsonSerializer.Deserialize<TrialReceipt>(JsonSerializer.Serialize(receipt))!;
        TrialJournal.ValidateTransition(receipt, copy with { FailureCode = "explicit_recovery" });
        Assert.Equal(2, copy.InstallationPolicyVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyReceiptsCannotAcceptTheMetadataGrantPostimage(int schema)
    {
        var (baseline, signed) = MetadataFixture();
        var receipt = new TrialReceipt(schema, baseline.DeploymentId, baseline.MonitorPrincipal, "installed", baseline, signed, null);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(receipt, baseline.DeploymentId));
    }

    [Fact]
    public void MetadataReceiptCannotAcceptTheOldFiveGrantImage()
    {
        var (baseline, signed) = TrialFixture.Create();
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateReceipt(Receipt("installed", baseline, signed), baseline.DeploymentId));
    }

    [Fact]
    public void VerifiedAbsentBaselineMayClosePreparedMetadataTrialWithoutInventingPostimages()
    {
        var (baseline, _) = TrialFixture.Create(); var prepared = Receipt("prepared", baseline, null);
        var removed = prepared with { Phase = "removed" };
        TrialJournal.ValidateTransition(prepared, removed);
        Assert.Null(removed.Installed); Assert.Null(removed.UnsignedSnapshot); Assert.Equal(2, removed.InstallationPolicyVersion);
    }

    [Theory]
    [InlineData("remove_pending")]
    [InlineData("unsigned_remove_pending")]
    [InlineData("removed")]
    public void UncertainMetadataCommitRetainsBothImagesForEitherRecoveryOutcome(string phase)
    {
        var before = Pending(); var after = before with { Phase = phase };
        TrialJournal.ValidateTransition(before, after);
        Assert.Same(before.UnsignedSnapshot, after.UnsignedSnapshot); Assert.Same(before.Installed, after.Installed);
        if (phase != "removed") TrialJournal.ValidateTransition(after, after with { Phase = "removed" });
    }

    [Theory]
    [InlineData("installed", "unsigned")]
    [InlineData("installed", "removed")]
    [InlineData("remove_pending", "remove_pending")]
    [InlineData("removed", "removed")]
    public void MetadataPolicyStillRequiresTheSignatureRemovalWriteAheadTransition(string from, string to)
    {
        var pending = Pending(); var before = pending with { Phase = from, UnsignedSnapshot = null };
        TrialJournal.ValidateReceipt(before, before.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, pending with { Phase = to }));
    }

    [Theory]
    [InlineData("drop-signed")]
    [InlineData("drop-unsigned")]
    [InlineData("drop-policy")]
    [InlineData("change-policy")]
    [InlineData("drop-certificate-grant")]
    public void RemovalCannotDiscardPolicyOrPostimageEvidence(string mutation)
    {
        var before = Pending() with { Phase = "unsigned" }; var after = before with { Phase = "removed" };
        after = mutation switch
        {
            "drop-signed" => after with { Installed = null },
            "drop-unsigned" => after with { UnsignedSnapshot = null },
            "drop-policy" => after with { InstallationPolicyVersion = null },
            "change-policy" => after with { InstallationPolicyVersion = 1 },
            _ => after with { UnsignedSnapshot = after.UnsignedSnapshot! with
                { PackagePermissions = after.UnsignedSnapshot!.PackagePermissions.Where(p => p.Class != 25).ToArray() } },
        };
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Fact]
    public void CoherentIdentityReplacementCannotRewriteMetadataEnrollment()
    {
        var before = Pending(); var identity = before.Baseline.Snapshot.Identity with { MasterDatabaseGuid = Guid.NewGuid() };
        var after = before with { Phase = "unsigned", Baseline = new(before.Baseline.Snapshot with { Identity = identity }),
            Installed = before.Installed! with { Identity = identity }, UnsignedSnapshot = before.UnsignedSnapshot! with { Identity = identity } };
        TrialJournal.ValidateReceipt(after, after.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MetadataEntryRequiresBothCallbacksBeforeAnyConnectionOrModuleWork(bool omitSigned)
    {
        using var connection = new SqlConnection();
        Func<CancellationToken, Task<bool>> callback = _ => throw new InvalidOperationException("Callback must not run.");
        var error = Assert.Throws<ArgumentNullException>(() =>
        {
            _ = TrialCoordinator.RunMetadataVerificationAsync(connection, "TEST\\Monitor", Guid.NewGuid(), [],
                omitSigned ? null! : callback, omitSigned ? callback : null!, CancellationToken.None);
        });
        Assert.Equal(omitSigned ? "qualifySigned" : "qualifyUnsigned", error.ParamName);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static TrialReceipt Receipt(string phase, TrialBaseline baseline, TrialSnapshot? signed) =>
        new(3, baseline.DeploymentId, baseline.MonitorPrincipal, phase, baseline, signed, null, InstallationPolicyVersion: 2);
    private static TrialReceipt Pending()
    {
        var (baseline, signed) = MetadataFixture();
        return Receipt("signature_remove_pending", baseline, signed) with { UnsignedSnapshot = UnsignedSnapshot(signed) };
    }
    private static (TrialBaseline Baseline, TrialSnapshot Installed) MetadataFixture(bool missingMapping = false)
    {
        var (baseline, signed) = TrialFixture.Create(missingMapping);
        int module = signed.Objects.Single(o => o.Kind == "module").Id;
        int owner = signed.Objects.Single(o => o.Kind == "owner_user").Id;
        int certificate = signed.Objects.Single(o => o.Kind == "master_certificate").Id;
        TrialGrant[] additions =
        [
            new("master", signed.Monitor.MasterUserId, owner, 1, module, 0, "VIEW DEFINITION", "G"),
            new("master", signed.Monitor.MasterUserId, 1, 25, certificate, 0, "VIEW DEFINITION", "G"),
        ];
        TrialGrant ObjectGrantor(TrialGrant grant) => grant.Scope == "master" && grant.Class == 1 &&
            grant.MajorId == module && grant.Name == "EXECUTE" && grant.GranteeId == signed.Monitor.MasterUserId
                ? grant with { GrantorId = owner } : grant;
        return (baseline, signed with { PackagePermissions = TrialFixture.Order(signed.PackagePermissions.Select(ObjectGrantor).Concat(additions)),
            MonitorPermissions = TrialFixture.Order(signed.MonitorPermissions.Select(ObjectGrantor).Concat(additions)) });
    }
    private static TrialSnapshot UnsignedSnapshot(TrialSnapshot signed) => signed with
    {
        Facts = TrialFixture.Order(signed.Facts.Where(f => f.Kind != "signature")
            .Select(f => f.Scope == "master" && f.Kind == "signature_validity" && f.Key == "module" ? f with { Value = "0:0" } : f)),
    };
}
