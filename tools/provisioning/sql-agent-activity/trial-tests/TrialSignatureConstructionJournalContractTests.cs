using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Synthetic metadata and pure journal validation only. No filesystem, SQL,
/// signature creation, service, connection, transaction or recovery mutation occurs.</summary>
public sealed class TrialSignatureConstructionJournalContractTests
{
    private static readonly Guid FixtureId = Guid.Parse("66666666-6666-4666-8666-666666666666");
    private static readonly Guid ConnectionId = Guid.Parse("77777777-7777-4777-8777-777777777777");
    private const string DbaSid = "010500000000000515000000010000000200000003000000e8030000";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AttachedInvalidPathPreservesEveryCapturedImageUntilIndependentlyVerifiedRollback(bool temporaryMonitor)
    {
        var fixture = Fixture(temporaryMonitor); var current = Prepared(fixture.Binding);
        foreach (var next in AttachedPath(fixture))
        {
            TrialSignatureConstructionJournal.ValidateTransition(current, next);
            Assert.Same(fixture.Binding, next.Binding);
            if (next.Installation is not null) Assert.Same(fixture.Installation, next.Installation);
            if (next.AttachedInvalid is not null) Assert.Same(fixture.Invalid, next.AttachedInvalid);
            current = next;
        }
        Assert.Equal("rollback_verified", current.Phase);
        Assert.Equal(TrialSignatureConstructionRecoveryAction.AlreadyRollbackVerified, TrialSignatureConstructionJournal.SelectRecoveryAction(current));
        Assert.Equal(1, current.SchemaVersion);
    }

    [Theory]
    [InlineData("sql_import_refused")] [InlineData("attached_invalid_image_unproven")] [InlineData("probe_failed")]
    public void AttachmentRefusalPreservesSignedDonorProvenanceWithoutInventingInvalidImage(string reason)
    {
        var fixture = Fixture(); var pending = Prepared(fixture.Binding) with { Phase = "attachment_attempt_pending", Installation = fixture.Installation };
        var refusal = reason == "sql_import_refused" ? new TrialSignatureConstructionRefusal(reason, 15151, 1) : new(reason);
        var observed = pending with { Phase = "attachment_refused", Refusal = refusal };
        TrialSignatureConstructionJournal.ValidateTransition(pending, observed);
        var rollback = observed with { Phase = "rollback_pending" };
        TrialSignatureConstructionJournal.ValidateTransition(observed, rollback);
        TrialSignatureConstructionJournal.ValidateTransition(rollback, rollback with { Phase = "rollback_verified", Rollback = Proof(fixture.Binding) });
        Assert.Null(rollback.AttachedInvalid); Assert.Same(fixture.Installation, rollback.Installation); Assert.Same(refusal, rollback.Refusal);
    }

    [Fact]
    public void FailedInstallationCanRollBackFromTransactionPendingWithoutAnyPostimage()
    {
        var fixture = Fixture(); var pending = Prepared(fixture.Binding) with { Phase = "transaction_pending" };
        var rollback = pending with { Phase = "rollback_pending", Refusal = new("probe_failed", 229, 1) };
        TrialSignatureConstructionJournal.ValidateTransition(pending, rollback);
        var verified = rollback with { Phase = "rollback_verified", Rollback = Proof(fixture.Binding) };
        TrialSignatureConstructionJournal.ValidateTransition(rollback, verified);
        Assert.Null(verified.Installation); Assert.Null(verified.AttachedInvalid);
    }

    [Theory]
    [InlineData("transaction_pending")] [InlineData("signed_observed")] [InlineData("attachment_attempt_pending")]
    public void FailureBreadcrumbCanBeAddedOnceAtKnownPhaseAndCannotResumeConstruction(string phase)
    {
        var fixture = Fixture(); var before = At(phase, fixture);
        var failed = before with { Refusal = new("probe_failed", 229, 1) };
        TrialSignatureConstructionJournal.ValidateTransition(before, failed);
        Assert.Same(before.Installation, failed.Installation); Assert.Same(before.AttachedInvalid, failed.AttachedInvalid);
        TrialSignatureConstructionJournal.ValidateTransition(failed, failed);
        TrialSignatureConstructionJournal.ValidateTransition(failed, failed with { Phase = "rollback_pending" });
        TrialSignatureConstructionJournal.ValidateTransition(failed, failed with { Phase = "blocked", BlockedFromPhase = phase });
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(failed,
            failed with { Refusal = new("probe_failed", 15151, 1) }));
        var next = phase switch
        {
            "transaction_pending" => failed with { Phase = "signed_observed", Installation = fixture.Installation },
            "signed_observed" => failed with { Phase = "attachment_attempt_pending" },
            _ => failed with { Phase = "attached_invalid_observed", AttachedInvalid = fixture.Invalid },
        };
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(failed, next));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(before,
            before with { Refusal = new("sql_import_refused", 15151, 1) }));
    }

    [Theory]
    [InlineData("prepared")] [InlineData("transaction_pending")] [InlineData("signed_observed")]
    [InlineData("attachment_attempt_pending")] [InlineData("attached_invalid_observed")]
    [InlineData("attachment_refused")] [InlineData("rollback_pending")]
    public void AnyUnconfirmedStageMayBecomeBlockedAndRetainsItsOriginalPhase(string phase)
    {
        var fixture = Fixture(); var before = At(phase, fixture);
        var blocked = before with { Phase = "blocked", BlockedFromPhase = phase };
        TrialSignatureConstructionJournal.ValidateTransition(before, blocked);
        Assert.Equal(phase, blocked.BlockedFromPhase);
        Assert.Same(before.Installation, blocked.Installation); Assert.Same(before.AttachedInvalid, blocked.AttachedInvalid);
        Assert.Equal(TrialSignatureConstructionRecoveryAction.PreserveForInspection, TrialSignatureConstructionJournal.SelectRecoveryAction(blocked));
        TrialSignatureConstructionJournal.ValidateTransition(blocked, blocked);
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(blocked,
            blocked with { Phase = "rollback_pending", BlockedFromPhase = null }));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(blocked,
            blocked with { BlockedFromPhase = phase == "prepared" ? "transaction_pending" : "prepared" }));
    }

    [Fact]
    public void ValidCapturedImagesMayBePreservedWhenTheirObservationWriteWasUnconfirmed()
    {
        var fixture = Fixture(); var pending = Prepared(fixture.Binding) with { Phase = "transaction_pending" };
        TrialSignatureConstructionJournal.ValidateTransition(pending, pending with { Phase = "blocked", BlockedFromPhase = pending.Phase, Installation = fixture.Installation });
        pending = pending with { Phase = "attachment_attempt_pending", Installation = fixture.Installation };
        var blocked = pending with { Phase = "blocked", BlockedFromPhase = pending.Phase, AttachedInvalid = fixture.Invalid };
        TrialSignatureConstructionJournal.ValidateTransition(pending, blocked);
        Assert.Equal(TrialSignatureConstructionRecoveryAction.PreserveForInspection, TrialSignatureConstructionJournal.SelectRecoveryAction(blocked));
    }

    [Theory]
    [InlineData("prepared", "signed_observed")] [InlineData("transaction_pending", "attachment_attempt_pending")]
    [InlineData("signed_observed", "attached_invalid_observed")] [InlineData("attachment_attempt_pending", "rollback_verified")]
    [InlineData("attached_invalid_observed", "attachment_refused")] [InlineData("attachment_refused", "attached_invalid_observed")]
    [InlineData("rollback_pending", "attachment_attempt_pending")] [InlineData("rollback_verified", "rollback_pending")]
    public void PhasesCannotSkipObservationRewindBranchesOrReuseVerifiedRollback(string from, string to)
    {
        var fixture = Fixture(); var before = At(from, fixture); var after = At(to, fixture);
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData("connection")] [InlineData("spid")] [InlineData("sid")] [InlineData("os-digest")]
    [InlineData("foreign-digest")] [InlineData("physical-master")]
    public void EvenCoherentValidBindingReplacementCannotRewriteAnExistingJournal(string change)
    {
        var fixture = Fixture(); var before = Prepared(fixture.Binding);
        var binding = change switch
        {
            "connection" => fixture.Binding with { ConnectionId = FixtureId },
            "spid" => fixture.Binding with { SessionId = 43 },
            "sid" => fixture.Binding with { DbaSidHex = DbaSid[..^8] + "e9030000" },
            "os-digest" => fixture.Binding with { AgentConfigurationSha256 = new string('c', 64) },
            "foreign-digest" => fixture.Binding with { ForeignInventorySha256 = new string('c', 64) },
            _ => fixture.Binding with { Baseline = new(fixture.Binding.Baseline.Snapshot with { Identity = fixture.Binding.Baseline.Snapshot.Identity with { MasterDatabaseGuid = FixtureId } }) },
        };
        var after = before with { Phase = "transaction_pending", Binding = binding };
        TrialSignatureConstructionJournal.ValidateReceipt(after, after.DeploymentId, after.FixtureId);
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(before, after));
    }

    [Theory]
    [InlineData("donor-object")] [InlineData("donor-bytes")] [InlineData("original-bytes")]
    public void ValidNewSignatureCapsulesCannotReplaceCapturedProvenance(string change)
    {
        var fixture = Fixture(); var before = At("signed_observed", fixture);
        var installation = change switch
        {
            "donor-object" => fixture.Installation with { Donor = fixture.Installation.Donor with { SourceObjectId = 101 } },
            "donor-bytes" => fixture.Installation with { Donor = Blob(99, TrialAttachedInvalidSignatureSql.DonorDefinitionUtf16Sha256, 3) },
            _ => fixture.Installation with { Original = Blob(62, AgentActivityTrialSql.DefinitionUtf16Sha256, 3) },
        };
        TrialAttachedInvalidSignatureSql.ValidateInstallation(fixture.Binding.Baseline, installation);
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(before,
            before with { Phase = "attachment_attempt_pending", Installation = installation }));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateTransition(before,
            before with { Phase = "rollback_pending", Installation = null }));
    }

    [Theory]
    [InlineData("bytes")] [InlineData("definition")] [InlineData("thumbprint")] [InlineData("validity")]
    [InlineData("permissions")] [InlineData("physical-guid")]
    public void AttachedInvalidImageRequiresExactActualTargetReadbackAndDonorBytes(string change)
    {
        var fixture = Fixture(); var image = change switch
        {
            "bytes" => fixture.Invalid with { Signature = fixture.Installation.Original },
            "definition" => fixture.Invalid with { Signature = fixture.Invalid.Signature with { SourceDefinitionUtf16Sha256 = TrialAttachedInvalidSignatureSql.DonorDefinitionUtf16Sha256 } },
            "thumbprint" => fixture.Invalid with { Signature = fixture.Invalid.Signature with { CertificateThumbprintHex = new string('c', 40) } },
            "validity" => fixture.Invalid with { Snapshot = fixture.Installation.Installed },
            "permissions" => fixture.Invalid with { Snapshot = fixture.Invalid.Snapshot with { MonitorPermissions = [] } },
            _ => fixture.Invalid with { Snapshot = fixture.Invalid.Snapshot with { Identity = fixture.Invalid.Snapshot.Identity with { MsdbDatabaseGuid = FixtureId } } },
        };
        var receipt = At("attached_invalid_observed", fixture) with { AttachedInvalid = image };
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId));
    }

    [Theory]
    [InlineData("ack")] [InlineData("closed")] [InlineData("readback-ack")] [InlineData("connection")]
    [InlineData("spid")] [InlineData("sid")] [InlineData("os")]
    [InlineData("foreign")] [InlineData("objects-present")] [InlineData("baseline-changed")]
    public void RollbackVerificationRequiresBothAcknowledgementsAbsentReadbackAndIndependentOriginalBindings(string change)
    {
        var fixture = Fixture(); var proof = Proof(fixture.Binding);
        proof = change switch
        {
            "ack" => proof with { RollbackAcknowledged = false },
            "closed" => proof with { TransactionClosed = false },
            "readback-ack" => proof with { ReadbackTransactionRollbackAcknowledged = false },
            "connection" => proof with { ConnectionId = FixtureId },
            "spid" => proof with { SessionId = 43 },
            "sid" => proof with { DbaSidHex = DbaSid[..^8] + "e9030000" },
            "os" => proof with { AgentConfigurationSha256 = new string('c', 64) },
            "foreign" => proof with { ForeignInventorySha256 = new string('c', 64) },
            "objects-present" => proof with { ExactAbsentReadback = fixture.Installation.Installed },
            _ => proof with { ExactAbsentReadback = proof.ExactAbsentReadback with { Identity = proof.ExactAbsentReadback.Identity with { MsdbDatabaseGuid = FixtureId } } },
        };
        var receipt = At("rollback_verified", fixture) with { Rollback = proof };
        if (change == "objects-present")
            Assert.Throws<TrialObjectsPresentException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId));
        else
            Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId));
    }

    [Theory]
    [InlineData("prepared")] [InlineData("transaction_pending")] [InlineData("signed_observed")]
    [InlineData("attachment_attempt_pending")] [InlineData("attached_invalid_observed")]
    [InlineData("attachment_refused")] [InlineData("rollback_pending")]
    public void RecoveryNeverAuthorizesAnyMutationOrConnectionForPendingOrObservedImages(string phase)
    {
        var fixture = Fixture(); var receipt = At(phase, fixture);
        Assert.Equal(TrialSignatureConstructionRecoveryAction.PreserveForInspection, TrialSignatureConstructionJournal.SelectRecoveryAction(receipt));
        Assert.Equal(new[] { TrialSignatureConstructionRecoveryAction.PreserveForInspection, TrialSignatureConstructionRecoveryAction.AlreadyRollbackVerified }, Enum.GetValues<TrialSignatureConstructionRecoveryAction>());
    }

    [Theory]
    [InlineData("sql_import_refused", 15151, 1, true)] [InlineData("sql_import_refused", null, null, false)]
    [InlineData("attached_invalid_image_unproven", null, null, true)] [InlineData("attached_invalid_image_unproven", 229, 1, false)]
    [InlineData("probe_failed", null, null, true)] [InlineData("probe_failed", -2, 0, true)]
    [InlineData("probe_failed", 229, null, false)] [InlineData("probe_failed", null, 1, false)]
    [InlineData("probe_failed", 0, 1, false)] [InlineData("probe_failed", -1, 1, false)] [InlineData("probe_failed", 1000000, 1, false)]
    [InlineData("Password=private-canary", null, null, false)]
    public void RefusalFieldsAreClosedAndBoundedWithoutPrivateDetails(string reason, int? number, int? state, bool valid)
    {
        byte? sqlState = state is { } value ? checked((byte)value) : null;
        var fixture = Fixture(); var receipt = At("attachment_refused", fixture) with { Refusal = new(reason, number, sqlState) };
        if (valid) TrialSignatureConstructionJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId);
        else
        {
            var failure = Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId));
            Assert.Null(failure.InnerException); Assert.DoesNotContain("private-canary", failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ClosedSerializationRoundtripsEveryPrivateImageWithoutAddingAuthority()
    {
        var fixture = Fixture(); var receipt = At("rollback_verified", fixture);
        byte[] bytes = TrialSignatureConstructionJournal.Serialize(receipt);
        var parsed = TrialSignatureConstructionJournal.Deserialize(bytes, receipt.DeploymentId, receipt.FixtureId);
        TrialSignatureConstructionJournal.ValidateTransition(receipt, parsed);
        Assert.Equal(bytes, TrialSignatureConstructionJournal.Serialize(parsed));
        Assert.Equal("Private rollback-only signature-construction receipt", parsed.ToString());
        Assert.DoesNotContain(DbaSid, parsed.ToString(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(bytes); Assert.Equal(10, document.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("")] [InlineData("Binding")] [InlineData("Binding.Baseline")] [InlineData("Binding.Baseline.Snapshot.Identity")]
    [InlineData("Installation")] [InlineData("Installation.Original")] [InlineData("Installation.Donor")]
    [InlineData("AttachedInvalid")] [InlineData("AttachedInvalid.Signature")] [InlineData("Rollback")]
    public void RecursiveSerializationRefusesMissingUnknownCaseAndDuplicateFields(string path)
    {
        var fixture = Fixture(); var receipt = At("rollback_verified", fixture);
        foreach (string mutation in new[] { "missing", "unknown", "case", "duplicate" })
        {
            var root = JsonNode.Parse(TrialSignatureConstructionJournal.Serialize(receipt))!.AsObject(); JsonObject node = ObjectAt(root, path);
            string key = node.First().Key; string json;
            if (mutation == "duplicate")
            {
                string literal = JsonSerializer.Serialize(key) + ":" + (node[key]?.ToJsonString() ?? "null");
                json = root.ToJsonString().Replace(literal, literal + "," + literal, StringComparison.Ordinal);
            }
            else
            {
                if (mutation == "missing") node.Remove(key);
                else if (mutation == "unknown") node["Password"] = "private-canary";
                else { node[key.ToLowerInvariant()] = node[key]?.DeepClone(); node.Remove(key); }
                json = root.ToJsonString();
            }
            var failure = Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.Deserialize(Encoding.UTF8.GetBytes(json), receipt.DeploymentId, receipt.FixtureId));
            Assert.Null(failure.InnerException); Assert.DoesNotContain("private-canary", failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SerializationRefusesIgnoredComputedClaimsUnknownPhasesAndBounds()
    {
        var fixture = Fixture(); var receipt = Prepared(fixture.Binding); var root = JsonNode.Parse(TrialSignatureConstructionJournal.Serialize(receipt))!.AsObject();
        root["Binding"]!["Baseline"]!["DeploymentId"] = FixtureId.ToString("D");
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.Deserialize(Encoding.UTF8.GetBytes(root.ToJsonString()), receipt.DeploymentId, receipt.FixtureId));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.Deserialize(Array.Empty<byte>(), receipt.DeploymentId, receipt.FixtureId));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.Deserialize(new byte[TrialSignatureConstructionJournal.MaximumBytes + 1], receipt.DeploymentId, receipt.FixtureId));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt with { Phase = "committed_invalid" }, receipt.DeploymentId, receipt.FixtureId));
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt with { SchemaVersion = 3 }, receipt.DeploymentId, receipt.FixtureId));
    }

    [Theory]
    [InlineData("Binding.Baseline.Snapshot", "Identity")]
    [InlineData("Installation.Installed", "Monitor")]
    [InlineData("AttachedInvalid.Snapshot", "Facts")]
    [InlineData("Rollback.ExactAbsentReadback", "Objects")]
    public void SerializationRejectsNullSnapshotMembersAndNullCollectionElements(string path, string member)
    {
        var fixture = Fixture(); var receipt = At("rollback_verified", fixture);
        foreach (bool collectionElement in new[] { false, true })
        {
            var root = JsonNode.Parse(TrialSignatureConstructionJournal.Serialize(receipt))!.AsObject();
            JsonObject snapshot = ObjectAt(root, path);
            if (collectionElement) snapshot["Facts"]!.AsArray().Add((JsonNode?)null);
            else snapshot[member] = null;
            var failure = Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.Deserialize(
                Encoding.UTF8.GetBytes(root.ToJsonString()), receipt.DeploymentId, receipt.FixtureId));
            Assert.Null(failure.InnerException);
        }
    }

    [Theory]
    [InlineData("empty-deployment")] [InlineData("empty-fixture")] [InlineData("same-id")]
    [InlineData("empty-connection")] [InlineData("zero-spid")] [InlineData("oversize-spid")]
    [InlineData("uppercase-sid")] [InlineData("malformed-sid")] [InlineData("zero-digest")]
    public void FreshIdentityAndPhysicalBindingFieldsMustBeCompleteCanonicalAndBounded(string change)
    {
        var fixture = Fixture(); var receipt = Prepared(fixture.Binding);
        receipt = change switch
        {
            "empty-deployment" => receipt with { DeploymentId = Guid.Empty },
            "empty-fixture" => receipt with { FixtureId = Guid.Empty },
            "same-id" => receipt with { FixtureId = receipt.DeploymentId },
            "empty-connection" => receipt with { Binding = receipt.Binding with { ConnectionId = Guid.Empty } },
            "zero-spid" => receipt with { Binding = receipt.Binding with { SessionId = 0 } },
            "oversize-spid" => receipt with { Binding = receipt.Binding with { SessionId = 32768 } },
            "uppercase-sid" => receipt with { Binding = receipt.Binding with { DbaSidHex = DbaSid.ToUpperInvariant() } },
            "malformed-sid" => receipt with { Binding = receipt.Binding with { DbaSidHex = "0104" + DbaSid[4..] } },
            _ => receipt with { Binding = receipt.Binding with { ForeignInventorySha256 = new string('0', 64) } },
        };
        Assert.Throws<InvalidDataException>(() => TrialSignatureConstructionJournal.ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId));
    }

    private static TrialSignatureConstructionReceipt Prepared(TrialSignatureConstructionBinding binding) => new(1, binding.Baseline.DeploymentId, FixtureId, "prepared", binding);
    private static TrialSignatureConstructionReceipt At(string phase, SignatureFixture fixture)
    {
        var receipt = Prepared(fixture.Binding) with { Phase = phase };
        if (phase is "signed_observed" or "attachment_attempt_pending" or "attached_invalid_observed" or "attachment_refused" or "rollback_pending" or "rollback_verified") receipt = receipt with { Installation = fixture.Installation };
        if (phase is "attached_invalid_observed" or "rollback_pending" or "rollback_verified") receipt = receipt with { AttachedInvalid = fixture.Invalid };
        if (phase == "attachment_refused") receipt = receipt with { Refusal = new("sql_import_refused", 15151, 1) };
        if (phase == "rollback_verified") receipt = receipt with { Rollback = Proof(fixture.Binding) };
        return receipt;
    }
    private static TrialSignatureConstructionReceipt[] AttachedPath(SignatureFixture fixture) => [At("transaction_pending", fixture), At("signed_observed", fixture),
        At("attachment_attempt_pending", fixture), At("attached_invalid_observed", fixture), At("rollback_pending", fixture), At("rollback_verified", fixture)];
    private static TrialSignatureRollbackVerification Proof(TrialSignatureConstructionBinding binding) => new(binding.Baseline.Snapshot, binding.ConnectionId, binding.SessionId,
        binding.DbaSidHex, binding.AgentConfigurationSha256, binding.ForeignInventorySha256, true, true, true);
    private static SignatureFixture Fixture(bool missingMapping = false)
    {
        var (baseline, signed) = TrialFixture.Create(missingMapping);
        int module = signed.Objects.Single(item => item.Kind == "module").Id, owner = signed.Objects.Single(item => item.Kind == "owner_user").Id;
        int certificate = signed.Objects.Single(item => item.Kind == "master_certificate").Id;
        TrialGrant[] additions = [new("master", signed.Monitor.MasterUserId, owner, 1, module, 0, "VIEW DEFINITION", "G"),
            new("master", signed.Monitor.MasterUserId, 1, 25, certificate, 0, "VIEW DEFINITION", "G")];
        TrialGrant ObjectGrantor(TrialGrant grant) => grant.Scope == "master" && grant.Class == 1 && grant.MajorId == module &&
            grant.Name == "EXECUTE" && grant.GranteeId == signed.Monitor.MasterUserId ? grant with { GrantorId = owner } : grant;
        signed = signed with { PackagePermissions = TrialFixture.Order(signed.PackagePermissions.Select(ObjectGrantor).Concat(additions)),
            MonitorPermissions = TrialFixture.Order(signed.MonitorPermissions.Select(ObjectGrantor).Concat(additions)) };
        var installation = new TrialSignatureDonorInstallation(signed, Blob(module, AgentActivityTrialSql.DefinitionUtf16Sha256, 1),
            Blob(99, TrialAttachedInvalidSignatureSql.DonorDefinitionUtf16Sha256, 2));
        var invalid = new TrialAttachedInvalidSignatureImage(signed with
        { Facts = TrialFixture.Order(signed.Facts.Select(fact => fact.Kind == "signature_validity" ? fact with { Value = "1:0" } : fact)) },
            installation.Donor with { SourceObjectId = module, SourceDefinitionUtf16Sha256 = AgentActivityTrialSql.DefinitionUtf16Sha256 });
        var binding = new TrialSignatureConstructionBinding(baseline, ConnectionId, 42, DbaSid, new string('a', 64), new string('b', 64));
        return new(binding, installation, invalid);
    }
    private static TrialSignatureBlob Blob(int objectId, string definition, byte value)
    { byte[] bytes = [value]; return new(objectId, definition, new string('b', 40), Convert.ToHexString(bytes).ToLowerInvariant(), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()); }
    private static JsonObject ObjectAt(JsonObject root, string path)
    { JsonNode node = root; foreach (string key in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node[key]!; return node.AsObject(); }
    private sealed record SignatureFixture(TrialSignatureConstructionBinding Binding, TrialSignatureDonorInstallation Installation, TrialAttachedInvalidSignatureImage Invalid);
}
