using SqlObserver.Application.Ports;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;

namespace SqlObserver.UnitTests;

public sealed class SqlAgentModuleCaptureReaderTests
{
    private static readonly DateTimeOffset Now = M4TestData.RepositoryTime;
    private static readonly byte[] Token = [1, 1, 0, 0, 0, 0, 0, 5, 21, 0, 0, 0];
    private static readonly Guid ConnectionId = new("494b8c14-9dbf-4c55-bded-c656f25b89c1");
    private static readonly Guid ServerConnectionId = new("8e17fcc0-1563-4f88-9a6b-953c16f4c602");
    private static readonly Guid EnrollmentId = new("96c57f44-8208-49b7-b568-5e657b867ac4");
    private static readonly string[] ExpectedStages = ["resolve1", "open", "identity1", "metadata1", "resolve2", "capture", "metadata2", "identity2", "resolve3", "dispose"];

    [Fact]
    public async Task ExplicitEnrollmentAllowsDeniedDirectPermissionsWithoutChangingCaptureSemantics()
    {
        var state = new Fixture();
        SqlAgentModuleCaptureResult result = await state.Reader.ReadAsync(Request(), CancellationToken.None);
        Assert.Equal(ExpectedStages, state.Events);
        Assert.Equal(3, state.Resolver.Calls); Assert.Equal(1, state.Source.Captures); Assert.True(state.Source.Disposed);
        Assert.Equal(7, result.EnrollmentRevision); Assert.Equal(EnrollmentId, result.EnrollmentId);
        Assert.False(result.ProductionAdmitted); Assert.False(result.ServiceSessionAssociationQualified);
        Assert.Equal(SqlAgentActivityNativeQualification.Absent, result.NativeQualification);
        Assert.Equal(Request().RunId, result.Capture.RunId);
        Assert.Equal(SqlAgentActivityGap.ServiceNotRunning, SqlAgentActivityEvaluator.Evaluate(result.Capture).Gap);
        Assert.Equal("S-1-5-21", SqlServerAgentActivityModuleCaptureReader.CanonicalSid(Token));
    }

    [Fact]
    public async Task DistinctStableDriverAndServerConnectionIdentifiersBindThroughTheDriverSpid()
    {
        Assert.NotEqual(ConnectionId, ServerConnectionId);
        var state = new Fixture();
        var result = await state.Reader.ReadAsync(Request(), CancellationToken.None);
        Assert.Equal(Request().RunId, result.Capture.RunId);
        Assert.Equal(1, state.Source.Captures); Assert.True(state.Source.Disposed);
    }

    [Theory]
    [InlineData("empty-client")] [InlineData("zero-spid")] [InlineData("oversized-spid")]
    public async Task InvalidInitialDriverIdentityRefusesBeforeAttestation(string mutation)
    {
        var state = new Fixture();
        if (mutation == "empty-client") state.Source.ClientConnectionId = Guid.Empty;
        else state.Source.ServerProcessId = mutation == "zero-spid" ? 0 : 32768;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.DoesNotContain("identity1", state.Events); Assert.Equal(0, state.Source.Captures); Assert.True(state.Source.Disposed);
    }

    [Theory]
    [InlineData("identity1", true)] [InlineData("identity1", false)]
    [InlineData("metadata1", true)] [InlineData("metadata1", false)]
    [InlineData("resolve2", true)] [InlineData("resolve2", false)]
    [InlineData("capture", true)] [InlineData("capture", false)]
    [InlineData("metadata2", true)] [InlineData("metadata2", false)]
    [InlineData("identity2", true)] [InlineData("identity2", false)]
    [InlineData("resolve3", true)] [InlineData("resolve3", false)]
    public async Task DriverConnectionOrSpidDriftAtEveryStageDiscardsOutput(string stage, bool changeClientId)
    {
        var state = new Fixture();
        state.OnEvent = value =>
        {
            if (value != stage) return;
            if (changeClientId) state.Source.ClientConnectionId = Guid.NewGuid();
            else state.Source.ServerProcessId = 52;
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.Equal(stage is "identity1" or "metadata1" or "resolve2" ? 0 : 1, state.Source.Captures);
        Assert.True(state.Source.Disposed);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task RevocationAtEveryResolveDiscardsOutputAndNeverFallsBack(int at)
    {
        var state = new Fixture();
        state.Resolver.Resolve = call => call == at ? new SqlAgentModuleEnrollmentSnapshot(8, null) : Snapshot();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.Equal(at == 1 ? 0 : 1, state.Source.Opens);
        Assert.Equal(at == 3 ? 1 : 0, state.Source.Captures);
        Assert.Equal(at > 1, state.Source.Disposed);
    }

    [Theory]
    [InlineData("revision")] [InlineData("id")] [InlineData("sid")] [InlineData("evidence")]
    [InlineData("window")] [InlineData("binding")] [InlineData("target")] [InlineData("target-revision")]
    public async Task SameTargetIsInsufficientWhenAuthorityChangesAfterCapture(string mutation)
    {
        var state = new Fixture();
        SqlAgentModuleEnrollment changed = Enrollment(mutation);
        state.Resolver.Resolve = call => call == 3 ? new(changed.Revision, changed) : Snapshot();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.Equal(1, state.Source.Captures); Assert.True(state.Source.Disposed);
    }

    [Theory]
    [InlineData("absent")] [InlineData("future")] [InlineData("expired")] [InlineData("sid")]
    [InlineData("host")] [InlineData("port")] [InlineData("certificate")] [InlineData("target")]
    public async Task InvalidInitialAuthorityNeverOpensSql(string mutation)
    {
        var state = new Fixture();
        var enrolled = Enrollment(mutation);
        state.Resolver.Resolve = _ => mutation == "absent" ? null : new(enrolled.Revision, enrolled);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.Equal(0, state.Source.Opens);
    }

    [Theory]
    [InlineData("expired-profile")] [InlineData("future-profile")] [InlineData("sysadmin")]
    [InlineData("unencrypted")] [InlineData("sql-auth")] [InlineData("rejected-profile")]
    public async Task UnsafeOrStaleProfileNeverResolvesOrOpens(string mutation)
    {
        var state = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request(mutation), CancellationToken.None));
        Assert.Equal(0, state.Source.Opens); Assert.Equal(0, state.Resolver.Calls);
    }

    [Theory]
    [InlineData("old-major")] [InlineData("linux")] [InlineData("express")]
    public async Task UnsupportedProfileNeverOpens(string mutation)
    {
        var state = new Fixture();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await state.Reader.ReadAsync(Request(mutation), CancellationToken.None));
        Assert.Equal(0, state.Source.Opens);
    }

    [Theory]
    [InlineData("metadata1", "unsigned")] [InlineData("metadata2", "unsigned")]
    [InlineData("metadata1", "session")] [InlineData("metadata2", "session")]
    [InlineData("metadata1", "connection")] [InlineData("metadata2", "connection")]
    [InlineData("identity1", "server")] [InlineData("identity2", "server")]
    [InlineData("identity1", "machine")] [InlineData("identity2", "instance")]
    [InlineData("identity1", "empty-server-connection")] [InlineData("identity2", "empty-server-connection")]
    [InlineData("identity2", "server-connection")]
    [InlineData("identity1", "server-spid")] [InlineData("identity2", "server-spid")]
    public async Task MetadataAndPropertyDriftCannotPublish(string stage, string mutation)
    {
        var state = new Fixture();
        state.Source.MutateAt = stage; state.Source.Mutation = mutation;
        Exception? error = await Record.ExceptionAsync(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.True(error is UnauthorizedAccessException or InvalidDataException);
        Assert.Equal(stage.EndsWith('1') ? 0 : 1, state.Source.Captures); Assert.True(state.Source.Disposed);
    }

    [Theory]
    [InlineData("resolve1")] [InlineData("resolve2")] [InlineData("resolve3")]
    [InlineData("open")] [InlineData("identity1")] [InlineData("metadata1")]
    [InlineData("capture")] [InlineData("metadata2")] [InlineData("identity2")] [InlineData("dispose")]
    public async Task OneOriginalDeadlineIncludesEveryStageAndDisposal(string stage)
    {
        var state = new Fixture(); state.OnEvent = value => { if (value == stage) state.Clock.Advance(TimeSpan.FromSeconds(10)); };
        await Assert.ThrowsAsync<TimeoutException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.Equal(state.Source.Opens > 0, state.Source.Disposed);
        Assert.True(state.Source.Budgets.All(value => value <= TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("resolve1")] [InlineData("resolve2")] [InlineData("resolve3")]
    [InlineData("metadata1")] [InlineData("capture")] [InlineData("metadata2")]
    public async Task CancellationNeverPublishesBufferedCapture(string stage)
    {
        using var cancelled = new CancellationTokenSource();
        var state = new Fixture(); state.OnEvent = value => { if (value == stage) cancelled.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await state.Reader.ReadAsync(Request(), cancelled.Token));
        Assert.Equal(state.Source.Opens > 0, state.Source.Disposed);
    }

    [Fact]
    public async Task MetadataFailureDisposesOwnedSessionAndDoesNotAttemptCapture()
    {
        var state = new Fixture(); state.OnEvent = value => { if (value == "metadata1") throw new InvalidDataException("Safe injected failure."); };
        await Assert.ThrowsAsync<InvalidDataException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.True(state.Source.Disposed); Assert.Equal(0, state.Source.Captures);
    }

    [Theory]
    [InlineData("metadata1", -2, typeof(TimeoutException))]
    [InlineData("metadata2", -2, typeof(TimeoutException))]
    [InlineData("metadata1", 229, typeof(UnauthorizedAccessException))]
    [InlineData("metadata2", 229, typeof(UnauthorizedAccessException))]
    public async Task MetadataPermissionAndDeadlineCategoriesSurviveThePublicReader(string stage, int number, Type exceptionType)
    {
        var state = new Fixture();
        state.OnEvent = value => { if (value == stage) throw SqlAgentModuleMetadataProbe.ClassifySqlFailure(number); };
        var error = await Record.ExceptionAsync(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.NotNull(error); Assert.Equal(exceptionType, error.GetType()); Assert.Null(error.InnerException);
        Assert.True(state.Source.Disposed); Assert.Equal(stage == "metadata1" ? 0 : 1, state.Source.Captures);
    }

    [Fact]
    public async Task MissingExplicitCertificateHostNeverOpensSql()
    {
        var state = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await state.Reader.ReadAsync(Request("no-certificate"), CancellationToken.None));
        Assert.Equal(0, state.Source.Opens);
    }

    [Fact]
    public async Task CaptureContextCannotBeReboundByASource()
    {
        var state = new Fixture(); state.Source.MutateAt = "capture";
        await Assert.ThrowsAsync<InvalidDataException>(async () => await state.Reader.ReadAsync(Request(), CancellationToken.None));
        Assert.True(state.Source.Disposed);
    }

    [Fact]
    public async Task RemainingBudgetsShrinkAcrossResolverAndCommands()
    {
        var state = new Fixture(); state.OnEvent = _ => state.Clock.Advance(TimeSpan.FromMilliseconds(10));
        _ = await state.Reader.ReadAsync(Request(), CancellationToken.None);
        Assert.True(state.Source.Budgets.Zip(state.Source.Budgets.Skip(1), (before, after) => after < before).All(value => value));
    }

    [Theory]
    [InlineData("empty")] [InlineData("revision")] [InlineData("count")] [InlineData("length")]
    public void InvalidNativeTokenBinaryCannotSupplyAnEnrollmentIdentity(string mutation)
    {
        byte[] sid = Token.ToArray();
        if (mutation == "empty") sid = [];
        else if (mutation == "revision") sid[0] = 2;
        else if (mutation == "count") sid[1] = 16;
        else sid = [.. sid, 0];
        Assert.Throws<UnauthorizedAccessException>(() => SqlServerAgentActivityModuleCaptureReader.CanonicalSid(sid));
    }

    private static SqlAgentModuleEnrollmentSnapshot Snapshot() => new(7, Enrollment());
    private static SqlAgentModuleEnrollment Enrollment(string mutation = "") => new(
        mutation == "target" ? new MonitoredInstanceId(Guid.NewGuid()) : M4TestData.TargetId,
        mutation == "target-revision" ? new ObservationTargetRevision(8) : M4TestData.TargetRevision,
        mutation == "revision" ? 8 : 7, mutation == "id" ? Guid.NewGuid() : EnrollmentId,
        mutation == "sid" ? "S-1-5-22" : "S-1-5-21", Binding(mutation),
        mutation == "future" ? Now.AddSeconds(1) : Now.AddMinutes(-1),
        mutation == "expired" ? Now.AddTicks(-1) : mutation == "window" ? Now.AddMinutes(2) : Now.AddMinutes(1),
        new string(mutation == "evidence" ? 'd' : 'e', 64));
    private static SqlAgentModuleBinding Binding(string mutation = "") => new(
        mutation == "host" ? "other.example" : "sql.test.example", mutation == "port" ? 1434 : 1433,
        mutation == "certificate" ? "other.example" : "sql.test.example", "SQLTEST", "SQLTEST", "", 123, 456,
        SqlAgentModuleBinding.ApprovedDefinitionSha256, new string(mutation == "binding" ? 'd' : 'b', 64), new string('c', 40));
    private static CollectorExecutionRequest Request(string mutation = "")
    {
        var template = M4TestData.CreateProfile(majorVersion: mutation == "old-major" ? 16 : 17,
            platform: mutation == "linux" ? SqlServerPlatform.Linux : SqlServerPlatform.Windows,
            edition: mutation == "express" ? SqlServerEngineEdition.Express : SqlServerEngineEdition.Enterprise);
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, template.CollectorManifestVersion,
            template.OutputSchemaVersion, template.ServerIdentity,
            mutation is "sysadmin" or "unencrypted" or "sql-auth" or "rejected-profile" ? CapabilityDiscoveryOutcome.SecurityPolicyRejected : template.Outcome,
            mutation switch { "sysadmin" or "rejected-profile" => CapabilityDiscoveryReason.ExcessivePrivilege,
                "unencrypted" => CapabilityDiscoveryReason.TransportNotEncrypted, "sql-auth" => CapabilityDiscoveryReason.AuthenticationSchemeMismatch, _ => template.Reason },
            mutation == "sql-auth" ? SqlServerAuthenticationScheme.SqlAuthentication : template.AuthenticationScheme,
            mutation != "unencrypted", mutation is "sysadmin" or "rejected-profile", template.Capabilities,
            [new(new("server.view-security-state"), PermissionEvidenceScope.Server, PermissionEvidenceOutcome.Denied),
             new(new("msdb.syssessions.select"), PermissionEvidenceScope.Database, PermissionEvidenceOutcome.Denied)],
            template.DiscoveryDuration, template.EvidenceBytes, mutation == "future-profile" ? Now.AddSeconds(1) : template.CheckedAtUtc,
            mutation == "expired-profile" ? Now : template.ValidUntilUtc);
        var policy = new SqlServerConnectionPolicy(new(new("sql.test.example"), tcpPort: 1433), new(TimeSpan.FromSeconds(1)),
            mutation == "no-certificate" ? null : new("sql.test.example"));
        var original = M4TestData.CreateExecutionRequest();
        return new(original.RunId, original.TargetId, original.TargetRevision, policy, profile, original.Attempt, new(TimeSpan.FromSeconds(30)));
    }

    private sealed class Fixture
    {
        internal List<string> Events { get; } = [];
        internal Action<string>? OnEvent { get; set; }
        internal Clock Clock { get; } = new();
        internal Resolver Resolver { get; }
        internal Source Source { get; }
        internal SqlServerAgentActivityModuleCaptureReader Reader { get; }
        internal Fixture()
        {
            Resolver = new Resolver(Record); Source = new Source(Record);
            Reader = new(Resolver, Source, () => Token.ToArray(), Clock);
        }
        private void Record(string stage) { Events.Add(stage); OnEvent?.Invoke(stage); }
    }
    private sealed class Resolver(Action<string> record) : ISqlAgentModuleEnrollmentResolver
    {
        internal int Calls { get; private set; }
        internal Func<int, SqlAgentModuleEnrollmentSnapshot?> Resolve { get; set; } = _ => Snapshot();
        public ValueTask<SqlAgentModuleEnrollmentSnapshot?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken token)
        { record("resolve" + ++Calls); token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Resolve(Calls)); }
    }
    private sealed class Source(Action<string> record) : ISqlAgentModuleSource, ISqlAgentModuleSession
    {
        internal int Opens { get; private set; }
        internal int Captures { get; private set; }
        internal bool Disposed { get; private set; }
        internal string? MutateAt { get; set; }
        internal string? Mutation { get; set; }
        internal List<TimeSpan> Budgets { get; } = [];
        private int identities, metadata;
        public Guid ClientConnectionId { get; set; } = ConnectionId;
        public int ServerProcessId { get; set; } = 51;
        public void RequireConnection() { }
        public ValueTask<ISqlAgentModuleSession> OpenAsync(CollectorExecutionRequest request, TimeSpan budget, CancellationToken token)
        { Opens++; Budgets.Add(budget); record("open"); return ValueTask.FromResult<ISqlAgentModuleSession>(this); }
        public ValueTask<SqlAgentModuleIdentity> AttestAsync(ReadOnlyMemory<byte> sid, TimeSpan budget, CancellationToken token)
        {
            Assert.Equal(Token, sid.ToArray()); Budgets.Add(budget); string stage = "identity" + ++identities; record(stage); token.ThrowIfCancellationRequested();
            var value = new SqlAgentModuleIdentity(ServerConnectionId, 51, "SQLTEST", "SQLTEST", null, Now.UtcDateTime);
            if (MutateAt == stage) value = Mutation switch
            {
                "server" => value with { ServerName = "OTHER" }, "machine" => value with { MachineName = "OTHER" },
                "empty-server-connection" => value with { ServerConnectionId = Guid.Empty },
                "server-connection" => value with { ServerConnectionId = Guid.NewGuid() },
                "server-spid" => value with { SessionId = 52 }, _ => value with { InstanceName = "OTHER" }
            };
            return ValueTask.FromResult(value);
        }
        public ValueTask<SqlAgentModuleMetadataReceipt> MetadataAsync(SqlAgentModuleMetadataPins pins, TimeSpan budget, CancellationToken token)
        {
            Budgets.Add(budget); string stage = "metadata" + ++metadata; record(stage); token.ThrowIfCancellationRequested();
            var value = new SqlAgentModuleMetadataReceipt(pins.ObjectId, pins.CertificateId, pins.DefinitionSha256, pins.PublicCertificateSha256,
                pins.ThumbprintHex, SqlAgentModuleMetadataSignatureState.SignedVerified, 51, Now.UtcDateTime, ConnectionId);
            if (MutateAt == stage) value = Mutation switch
            { "unsigned" => value with { State = SqlAgentModuleMetadataSignatureState.Unsigned }, "session" => value with { SessionId = 52 }, _ => value with { ClientConnectionId = Guid.NewGuid() } };
            return ValueTask.FromResult(value);
        }
        public ValueTask<SqlAgentActivityCapture> CaptureAsync(CollectorExecutionRequest request, TimeSpan budget, CancellationToken token)
        {
            Captures++; Budgets.Add(budget); record("capture"); token.ThrowIfCancellationRequested();
            var before = new SqlAgentActivityHeader(Now, new(1, 1, 0, null), new(null, null));
            return ValueTask.FromResult(new SqlAgentActivityCapture(request.TargetId, request.TargetRevision,
                MutateAt == "capture" ? new CollectorRunId(Guid.NewGuid()) : request.RunId, before, [], before, 0, false));
        }
        public ValueTask DisposeAsync() { Disposed = true; record("dispose"); return ValueTask.CompletedTask; }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(ticks);
        internal void Advance(TimeSpan value) => ticks += value.Ticks;
    }
}
