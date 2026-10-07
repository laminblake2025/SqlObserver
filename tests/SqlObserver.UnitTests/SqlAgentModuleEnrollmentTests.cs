using System.Reflection;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentModuleEnrollmentTests
{
    private static readonly MonitoredInstanceId Target = new(Guid.Parse("11111111-1111-4111-8111-111111111111"));
    private static readonly Guid EnrollmentId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EquivalentIndependentBindingsAuthorizeExactlyTheSameContext()
    {
        var first = Enrollment(); var second = Enrollment();
        Assert.NotSame(first.Binding, second.Binding);
        Assert.True(first.SameAuthorization(second));
        Assert.False(first.SameAuthorization(null));
        Assert.Equal(1, first.Binding.ModuleVersion); Assert.Equal(2, first.Binding.SourceVersion);
        Assert.Equal(1, first.Binding.ProbeVersion); Assert.Equal(2, first.Binding.InstallationPolicyVersion);
        Assert.Equal(SqlAgentModuleBinding.ApprovedDefinitionSha256, first.Binding.DefinitionSha256);
    }

    [Theory]
    [InlineData("endpoint")] [InlineData("port")] [InlineData("certificate-host")]
    [InlineData("server")] [InlineData("machine")] [InlineData("instance")]
    [InlineData("object")] [InlineData("certificate")]
    [InlineData("public-hash")] [InlineData("thumbprint")]
    public void AnyChangedBindingInvalidatesAuthorizationEvenWithoutRevisionChange(string change)
    {
        Assert.False(Enrollment().SameAuthorization(Enrollment(binding: Binding(change))));
    }

    [Theory]
    [InlineData("target")] [InlineData("target-revision")] [InlineData("revision")]
    [InlineData("id")] [InlineData("sid")] [InlineData("not-before")]
    [InlineData("expiry")] [InlineData("evidence")]
    public void EveryAuthorityFieldParticipatesInExactComparison(string change)
    {
        var changed = new SqlAgentModuleEnrollment(
            change == "target" ? new(Guid.NewGuid()) : Target, new(change == "target-revision" ? 2 : 1),
            change == "revision" ? 2 : 1, change == "id" ? Guid.NewGuid() : EnrollmentId,
            change == "sid" ? "S-1-5-21-123-457" : "S-1-5-21-123-456", Binding(),
            change == "not-before" ? Start.AddTicks(1) : Start,
            change == "expiry" ? Start.AddHours(1).AddTicks(1) : Start.AddHours(1),
            new string(change == "evidence" ? 'b' : 'a', 64));
        Assert.False(Enrollment().SameAuthorization(changed));
    }

    [Theory]
    [InlineData("endpoint", "server;Integrated Security=false")]
    [InlineData("certificate-host", "server host")]
    [InlineData("server", "private\nserver")]
    [InlineData("machine", "private\0machine")]
    [InlineData("instance", "private\tinstance")]
    [InlineData("server", "")]
    [InlineData("machine", " ")]
    [InlineData("instance", " ")]
    [InlineData("definition", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("public-hash", "ABCDEF")]
    [InlineData("thumbprint", "abcdef")]
    public void BindingRejectsUnsafeOrUnpinnedInputs(string field, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => Binding(textField: field, textValue: value));
    }

    [Theory]
    [InlineData("server")] [InlineData("machine")] [InlineData("instance")]
    public void SqlIdentityNamesAreBoundedWithoutNormalizingTheirIdentity(string field)
    {
        Assert.Throws<ArgumentException>(() => Binding(textField: field, textValue: new string('x', 129)));
        _ = Binding(textField: field, textValue: new string('x', 128));
        Assert.Equal("", Binding().InstanceName);
        Assert.False(Enrollment().SameAuthorization(Enrollment(binding: Binding(textField: "server", textValue: "LAB"))));
    }

    [Theory]
    [InlineData(0, 1, 1)] [InlineData(65536, 1, 1)]
    [InlineData(1433, 0, 1)] [InlineData(1433, 1, 0)]
    public void BindingRequiresExplicitPortAndPositiveObjectIdentities(int port, int objectId, int certificateId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlAgentModuleBinding("lab", port, "lab",
            "lab", "lab", "", objectId, certificateId, SqlAgentModuleBinding.ApprovedDefinitionSha256,
            new string('a', 64), new string('b', 40)));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(9007199254740992)]
    public void EnrollmentRevisionMustBePositiveAndExactlyRepresentable(long revision) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Enrollment(revision));

    [Theory]
    [InlineData("s-1-5-21")] [InlineData("S-01-5-21")] [InlineData("S-1-05-21")]
    [InlineData("S-1-5-021")] [InlineData("S-1-5")]
    [InlineData("S-1-281474976710656-1")] [InlineData("S-1-5-4294967296")]
    [InlineData("S-1-5-+1")] [InlineData("S-1-5-1\n")]
    public void MonitorSidMustHaveCanonicalBoundedNumericComponents(string sid) =>
        Assert.Throws<ArgumentException>(() => Enrollment(sid: sid));

    [Fact]
    public void SidSubauthorityCountIsBoundedAndCanonicalLimitValuesAreAccepted()
    {
        _ = Enrollment(sid: "S-1-281474976710655-4294967295");
        _ = Enrollment(sid: "S-1-5-" + string.Join('-', Enumerable.Repeat("1", 15)));
        Assert.Throws<ArgumentException>(() => Enrollment(sid: "S-1-5-" + string.Join('-', Enumerable.Repeat("1", 16))));
        Assert.Throws<ArgumentException>(() => Enrollment(sid: new string('1', 185)));
    }

    [Theory]
    [InlineData("zero-evidence")] [InlineData("upper-evidence")] [InlineData("short-evidence")]
    [InlineData("empty-id")] [InlineData("reversed-window")] [InlineData("equal-window")]
    [InlineData("local-start")] [InlineData("local-expiry")]
    public void EnrollmentRejectsInvalidProvenanceAndClockWindows(string change)
    {
        Assert.Throws<ArgumentException>(() => new SqlAgentModuleEnrollment(Target, new(1), 1,
            change == "empty-id" ? Guid.Empty : EnrollmentId, "S-1-5-21-123-456", Binding(),
            change == "local-start" ? Start.ToOffset(TimeSpan.FromHours(1)) : Start,
            change switch { "equal-window" => Start, "reversed-window" => Start.AddTicks(-1),
                "local-expiry" => Start.AddHours(1).ToOffset(TimeSpan.FromHours(-1)), _ => Start.AddHours(1) },
            change switch { "zero-evidence" => new string('0', 64), "upper-evidence" => new string('A', 64),
                "short-evidence" => "abc", _ => new string('a', 64) }));
    }

    [Fact]
    public void AuthorizationWindowIsInclusiveAtStartAndExclusiveAtExpiry()
    {
        var enrollment = Enrollment();
        Assert.False(enrollment.IsActiveAt(Start.AddTicks(-1)));
        Assert.True(enrollment.IsActiveAt(Start));
        Assert.True(enrollment.IsActiveAt(enrollment.ExpiresAtUtc.AddTicks(-1)));
        Assert.False(enrollment.IsActiveAt(enrollment.ExpiresAtUtc));
        Assert.False(enrollment.IsActiveAt(Start.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void RevocationRetainsRevisionAndIdenticalReenrollmentCannotPassOldAuthorization()
    {
        var old = Enrollment();
        var revoke = new SqlAgentModuleEnrollmentChange(Target, new(1), 1, Guid.NewGuid(), null);
        var revoked = new SqlAgentModuleEnrollmentSnapshot(revoke.ExpectedRevision + 1, null);
        var reenable = new SqlAgentModuleEnrollmentChange(Target, new(1), revoked.Revision, Guid.NewGuid(), Enrollment(3));
        Assert.Null(revoked.Enrollment); Assert.Equal(2, revoked.Revision);
        Assert.False(old.SameAuthorization(reenable.Enrollment));
    }

    [Theory]
    [InlineData("target")] [InlineData("target-revision")] [InlineData("not-next")]
    [InlineData("negative")] [InlineData("overflow")] [InlineData("empty-id")]
    public void ChangeRequiresExactContextNextRevisionAndOperationId(string change)
    {
        Assert.ThrowsAny<ArgumentException>(() => new SqlAgentModuleEnrollmentChange(
            change == "target" ? new(Guid.NewGuid()) : Target, new(change == "target-revision" ? 2 : 1),
            change switch { "not-next" => 1, "negative" => -1, "overflow" => SqlAgentModuleEnrollment.MaximumRevision, _ => 0 },
            change == "empty-id" ? Guid.Empty : Guid.NewGuid(), Enrollment()));
    }

    [Fact]
    public void MaximumRevisionCanBeReachedButCannotAdvanceAgain()
    {
        var maximum = Enrollment(SqlAgentModuleEnrollment.MaximumRevision);
        _ = new SqlAgentModuleEnrollmentChange(Target, new(1), maximum.Revision - 1, Guid.NewGuid(), maximum);
        _ = new SqlAgentModuleEnrollmentSnapshot(maximum.Revision, maximum);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlAgentModuleEnrollmentChange(Target, new(1), maximum.Revision, Guid.NewGuid(), null));
    }

    [Fact]
    public void SnapshotCannotMislabelPresentOrRevokedAuthority()
    {
        _ = new SqlAgentModuleEnrollmentSnapshot(0, null);
        _ = new SqlAgentModuleEnrollmentSnapshot(5, null);
        Assert.Throws<ArgumentException>(() => new SqlAgentModuleEnrollmentSnapshot(0, Enrollment()));
        Assert.Throws<ArgumentException>(() => new SqlAgentModuleEnrollmentSnapshot(2, Enrollment()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlAgentModuleEnrollmentSnapshot(-1, null));
    }

    [Fact]
    public void DomainAuthorityHasNoMutablePropertiesOrPrivateValuesInDiagnosticText()
    {
        Type[] types = [typeof(SqlAgentModuleBinding), typeof(SqlAgentModuleEnrollment),
            typeof(SqlAgentModuleEnrollmentChange), typeof(SqlAgentModuleEnrollmentSnapshot), typeof(SqlAgentModuleCaptureResult)];
        foreach (Type type in types)
            Assert.All(type.GetProperties(BindingFlags.Public | BindingFlags.Instance), property => Assert.Null(property.SetMethod));
        Assert.Equal("SQL Agent module binding", Binding().ToString());
        Assert.Equal("SQL Agent module enrollment", Enrollment().ToString());
    }

    [Fact]
    public void CaptureCarriesOnlySanitizedAuthorizationProvenanceAndRetainsAllAdmissionGates()
    {
        var enrollment = Enrollment(); var capture = Capture();
        var result = new SqlAgentModuleCaptureResult(capture, enrollment);
        Assert.Same(capture, result.Capture);
        Assert.Equal(enrollment.Revision, result.EnrollmentRevision);
        Assert.Equal(enrollment.EnrollmentId, result.EnrollmentId);
        Assert.Equal(enrollment.EvidenceSha256, result.EvidenceSha256);
        Assert.False(result.ProductionAdmitted); Assert.False(result.ServiceSessionAssociationQualified);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, result.NativeQualification);
        Assert.Null(typeof(SqlAgentModuleCaptureResult).GetProperty("Binding"));
        Assert.Null(typeof(SqlAgentModuleCaptureResult).GetProperty("ExpectedWindowsSid"));
    }

    [Theory]
    [InlineData("target")] [InlineData("revision")] [InlineData("too-early")] [InlineData("expired")]
    public void CaptureCannotBePublishedUnderWrongAuthority(string change)
    {
        var capture = Capture(change == "target" ? new(Guid.NewGuid()) : Target,
            change == "revision" ? 2 : 1, change switch { "too-early" => Start.AddTicks(-1),
                "expired" => Start.AddHours(1), _ => Start });
        Assert.Throws<ArgumentException>(() => new SqlAgentModuleCaptureResult(capture, Enrollment()));
    }

    private static SqlAgentActivityCapture Capture(MonitoredInstanceId? target = null, long revision = 1, DateTimeOffset? observed = null)
    {
        var header = new SqlAgentActivityHeader(observed ?? Start, new SqlAgentServiceEvidence(0, null, null, null),
            new SqlAgentSessionEvidence(null, null));
        return new(target ?? Target, new(revision), new(Guid.NewGuid()), header, [], header, 0, false);
    }

    private static SqlAgentModuleEnrollment Enrollment(long revision = 1, SqlAgentModuleBinding? binding = null,
        string sid = "S-1-5-21-123-456") => new(Target, new(1), revision, EnrollmentId, sid,
            binding ?? Binding(), Start, Start.AddHours(1), new string('a', 64));

    private static SqlAgentModuleBinding Binding(string? change = null, string? textField = null, string? textValue = null)
    {
        string Text(string field, string original, string changed) => textField == field ? textValue! : change == field ? changed : original;
        return new(Text("endpoint", "lab", "other"), change == "port" ? 1444 : 1433,
            Text("certificate-host", "lab", "other"), Text("server", "lab", "other"), Text("machine", "lab", "other"),
            Text("instance", "", "OTHER"), change == "object" ? 2 : 1, change == "certificate" ? 2 : 1,
            Text("definition", SqlAgentModuleBinding.ApprovedDefinitionSha256, ""),
            Text("public-hash", new string('a', 64), new string('b', 64)), Text("thumbprint", new string('b', 40), new string('c', 40)));
    }
}
