using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Synthetic tuple and provenance contracts only. These do not prove SQL Server
/// accepts a mismatched genuine donor blob or qualify any monitor execution.</summary>
public sealed class TrialAttachedInvalidSignatureContractTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactPolicy2SignedAndAttachedInvalidImagesRoundtripWithDistinctPrivateBlobProvenance(bool temporaryMonitor)
    {
        var (baseline, installation, image) = Fixture(temporaryMonitor);
        TrialAttachedInvalidSignatureSql.ValidateInstallation(baseline, installation);
        TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(baseline, installation, image);
        var copy = JsonSerializer.Deserialize<TrialSignatureDonorInstallation>(JsonSerializer.Serialize(installation))!;
        var imageCopy = JsonSerializer.Deserialize<TrialAttachedInvalidSignatureImage>(JsonSerializer.Serialize(image))!;
        TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(baseline, copy, imageCopy);
        Assert.Single(image.Snapshot.Facts, x => x.Kind == "signature");
        Assert.Equal("1:0", image.Snapshot.Facts.Single(x => x.Kind == "signature_validity").Value);
        Assert.DoesNotContain(installation.Original.Hex, installation.Original.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(installation.Donor.Hex, installation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(image.Signature.Hex, image.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("original-object")] [InlineData("original-definition")] [InlineData("original-thumb")]
    [InlineData("donor-object")] [InlineData("donor-definition")] [InlineData("donor-thumb")]
    [InlineData("same-blob")] [InlineData("null-original")] [InlineData("null-donor")]
    public void InstallationCapsulesCannotNominateTheirOwnTargetDefinitionCertificateOrDonorIdentity(string mutation)
    {
        var (baseline, installation, _) = Fixture();
        TrialSignatureDonorInstallation changed = mutation switch
        {
            "original-object" => installation with { Original = installation.Original with { SourceObjectId = 999 } },
            "original-definition" => installation with { Original = installation.Original with { SourceDefinitionUtf16Sha256 = new string('c', 64) } },
            "original-thumb" => installation with { Original = installation.Original with { CertificateThumbprintHex = new string('c', 40) } },
            "donor-object" => installation with { Donor = installation.Donor with { SourceObjectId = installation.Original.SourceObjectId } },
            "donor-definition" => installation with { Donor = installation.Donor with { SourceDefinitionUtf16Sha256 = installation.Original.SourceDefinitionUtf16Sha256 } },
            "donor-thumb" => installation with { Donor = installation.Donor with { CertificateThumbprintHex = new string('c', 40) } },
            "same-blob" => installation with { Donor = installation.Donor with { Hex = installation.Original.Hex, Length = installation.Original.Length, Sha256 = installation.Original.Sha256 } },
            "null-original" => installation with { Original = null! }, "null-donor" => installation with { Donor = null! }, _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateInstallation(baseline, changed));
    }

    [Theory]
    [InlineData("original")] [InlineData("donor")]
    public void EveryBlobRequiresCanonicalBoundedHexAndActualHash(string which)
    {
        foreach (string mutation in new[] { "zero-object", "length-zero", "oversize", "upper-hex", "prefix", "odd-hex", "invalid-hex", "hash", "upper-thumb", "short-definition" })
        {
            var (baseline, installation, _) = Fixture(); TrialSignatureBlob blob = which == "original" ? installation.Original : installation.Donor;
            TrialSignatureBlob changed = mutation switch
            {
                "zero-object" => blob with { SourceObjectId = 0 }, "length-zero" => blob with { Length = 0 },
                "oversize" => blob with { Length = TrialAttachedInvalidSignatureSql.MaximumSignatureBytes + 1 },
                "upper-hex" => blob with { Hex = blob.Hex.ToUpperInvariant() }, "prefix" => blob with { Hex = "0x" + blob.Hex },
                "odd-hex" => blob with { Hex = blob.Hex[..^1] }, "invalid-hex" => blob with { Hex = new string('g', blob.Hex.Length) },
                "hash" => blob with { Sha256 = new string('a', 64) }, "upper-thumb" => blob with { CertificateThumbprintHex = blob.CertificateThumbprintHex.ToUpperInvariant() },
                "short-definition" => blob with { SourceDefinitionUtf16Sha256 = "aa" }, _ => throw new ArgumentOutOfRangeException(nameof(which)),
            };
            var candidate = which == "original" ? installation with { Original = changed } : installation with { Donor = changed };
            Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateInstallation(baseline, candidate));
        }
    }

    [Theory]
    [InlineData("original-bytes")] [InlineData("donor-source-id")] [InlineData("donor-source-definition")]
    [InlineData("foreign-blob")] [InlineData("missing-row")] [InlineData("unsigned-status")]
    [InlineData("signed-status")] [InlineData("other-signature")] [InlineData("extra-signature")]
    public void AnAttachedInvalidImageMustBeTheSameTargetWithTheExactGenuineDonorBlobAndRealOneZeroTuple(string mutation)
    {
        var (baseline, installation, image) = Fixture();
        TrialAttachedInvalidSignatureImage changed = mutation switch
        {
            "original-bytes" => image with { Signature = installation.Original },
            "donor-source-id" => image with { Signature = image.Signature with { SourceObjectId = installation.Donor.SourceObjectId } },
            "donor-source-definition" => image with { Signature = image.Signature with { SourceDefinitionUtf16Sha256 = installation.Donor.SourceDefinitionUtf16Sha256 } },
            "foreign-blob" => image with { Signature = Blob(image.Signature.SourceObjectId, image.Signature.SourceDefinitionUtf16Sha256, 0xcd) },
            "missing-row" => image with { Snapshot = image.Snapshot with { Facts = image.Snapshot.Facts.Where(x => x.Kind != "signature").ToArray() } },
            "unsigned-status" => image with { Snapshot = Fact(image.Snapshot, "signature_validity", "module", "0:0") },
            "signed-status" => image with { Snapshot = Fact(image.Snapshot, "signature_validity", "module", "1:1") },
            "other-signature" => image with { Snapshot = Fact(image.Snapshot, "signature", "62", "SPVC:" + new string('C', 40)) },
            "extra-signature" => image with { Snapshot = image.Snapshot with { Facts = TrialFixture.Order(image.Snapshot.Facts.Append(new("master", "signature", "999", "SPVC:" + new string('B', 40)))) } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(baseline, installation, changed));
    }

    [Theory]
    [InlineData("definition", "sha256", "changed")] [InlineData("definition", "ansi", "0:1")]
    [InlineData("definition", "execute_as", "OWNER")] [InlineData("certificate", "public_sha256", "changed")]
    [InlineData("certificate", "private_key", "ENCRYPTED_BY_PASSWORD")]
    [InlineData("marker", "SqlObserver.AgentActivity.DeploymentId", "foreign")]
    public void EveryOtherInstalledFactIsImmutable(string kind, string key, string value)
    {
        var (baseline, installation, image) = Fixture();
        Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(baseline, installation, image with { Snapshot = Fact(image.Snapshot, kind, key, value) }));
    }

    [Theory]
    [InlineData("owner")] [InlineData("object")] [InlineData("permission")] [InlineData("monitor")]
    [InlineData("donor-residue")] [InlineData("ordering")] [InlineData("duplicate")]
    public void OtherInventoryDriftOrDonorResidueCannotBeHiddenByInvalidStatus(string mutation)
    {
        var (baseline, installation, image) = Fixture(); TrialSnapshot snapshot = image.Snapshot;
        snapshot = mutation switch
        {
            "owner" => snapshot with { Objects = TrialFixture.Order(snapshot.Objects.Select(x => x.Kind == "module" ? x with { OwnerId = 1 } : x)) },
            "object" => snapshot with { Objects = snapshot.Objects.Where(x => x.Kind != "schema").ToArray() },
            "permission" => snapshot with { PackagePermissions = snapshot.PackagePermissions.Skip(1).ToArray() },
            "monitor" => snapshot with { Monitor = snapshot.Monitor with { LoginId = 999 } },
            "donor-residue" => snapshot with { Objects = TrialFixture.Order(snapshot.Objects.Append(new("donor", "master", "signature_donor_v1", 999, 60, "P", ""))) },
            "ordering" => snapshot with { Facts = snapshot.Facts.Reverse().ToArray() },
            "duplicate" => snapshot with { Facts = TrialFixture.Order(snapshot.Facts.Append(snapshot.Facts[0])) },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(baseline, installation, image with { Snapshot = snapshot }));
    }

    [Fact]
    public void LegacySignedUnsignedAndPolicyOneContractsDoNotAcceptInvalidImage()
    {
        var (baseline, installation, image) = Fixture();
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateInstalled(baseline, image.Snapshot, TrialInstallationPolicy.MetadataVerificationV2));
        Assert.Throws<InvalidDataException>(() => AgentActivityTrialSql.ValidateUnsigned(baseline, installation.Installed, image.Snapshot, TrialInstallationPolicy.MetadataVerificationV2));
        var (legacyBaseline, legacy) = TrialFixture.Create();
        Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateInstallation(legacyBaseline, installation with { Installed = legacy }));
        var invalidProvenance = installation with { Installed = image.Snapshot };
        Assert.Throws<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(baseline, invalidProvenance, image));
    }

    [Fact]
    public async Task EmptyFixtureRefusesBeforeAnySqlAndPinnedDonorIsFixedHarmlessAndDistinct()
    {
        var (baseline, _, _) = Fixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => TrialAttachedInvalidSignatureSql.InstallWithDonorAsync(null!, null!, baseline, [], Guid.Empty, CancellationToken.None));
        Assert.Equal(TrialAttachedInvalidSignatureSql.DonorDefinitionUtf16Sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(TrialAttachedInvalidSignatureSql.DonorDefinition))));
        Assert.NotEqual(AgentActivityTrialSql.DefinitionUtf16Sha256, TrialAttachedInvalidSignatureSql.DonorDefinitionUtf16Sha256);
        Assert.Contains("SELECT CONVERT(int, 1) AS [donor_constant]", TrialAttachedInvalidSignatureSql.DonorDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("msdb", TrialAttachedInvalidSignatureSql.DonorDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EXEC", TrialAttachedInvalidSignatureSql.DonorDefinition, StringComparison.OrdinalIgnoreCase);
    }

    private static TrialFact[] OrderedFacts(IEnumerable<TrialFact> values) => TrialFixture.Order(values);
    private static TrialSnapshot Fact(TrialSnapshot state, string kind, string key, string value) => state with
    { Facts = OrderedFacts(state.Facts.Select(x => x.Scope == "master" && x.Kind == kind && x.Key == key ? x with { Value = value } : x)) };
    private static TrialSignatureBlob Blob(int id, string definition, byte value)
    {
        byte[] bytes = Enumerable.Repeat(value, 256).ToArray();
        return new(id, definition, new string('b', 40), Convert.ToHexStringLower(bytes), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
    private static (TrialBaseline Baseline, TrialSignatureDonorInstallation Installation, TrialAttachedInvalidSignatureImage Image) Fixture(bool temporaryMonitor = false)
    {
        var (baseline, legacy) = TrialFixture.Create(temporaryMonitor);
        int module = legacy.Objects.Single(x => x.Kind == "module").Id, owner = legacy.Objects.Single(x => x.Kind == "owner_user").Id;
        TrialGrant[] extra = [new("master", legacy.Monitor.MasterUserId, owner, 1, module, 0, "VIEW DEFINITION", "G"),
            new("master", legacy.Monitor.MasterUserId, 1, 25, legacy.Objects.Single(x => x.Kind == "master_certificate").Id, 0, "VIEW DEFINITION", "G")];
        TrialGrant FixGrantor(TrialGrant x) => x.Scope == "master" && x.Class == 1 && x.MajorId == module && x.Name == "EXECUTE" ? x with { GrantorId = owner } : x;
        TrialSnapshot installed = legacy with { PackagePermissions = TrialFixture.Order(legacy.PackagePermissions.Select(FixGrantor).Concat(extra)),
            MonitorPermissions = TrialFixture.Order(legacy.MonitorPermissions.Select(FixGrantor).Concat(extra)) };
        var installation = new TrialSignatureDonorInstallation(installed, Blob(module, AgentActivityTrialSql.DefinitionUtf16Sha256, 0xab),
            Blob(999, TrialAttachedInvalidSignatureSql.DonorDefinitionUtf16Sha256, 0xbc));
        return (baseline, installation, new(Fact(installed, "signature_validity", "module", "1:0"), installation.Donor with
        { SourceObjectId = module, SourceDefinitionUtf16Sha256 = AgentActivityTrialSql.DefinitionUtf16Sha256 }));
    }
}
