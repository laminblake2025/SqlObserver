using System.Text.Json;
using SqlObserver.Collector;
using SqlObserver.Collector.Abstractions;
using SqlObserver.Collectors;
using SqlObserver.Domain.Collection;
using SqlObserver.Domain.Capabilities;
using SqlObserver.Domain.Hosts;
using SqlObserver.Domain.Targets;
using SqlObserver.Domain.Telemetry;
using SqlObserver.Infrastructure.SqlServer;
using SqlObserver.Infrastructure.Windows;

namespace SqlObserver.UnitTests;

public sealed class CpuPairedCaptureSourceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ActualCandidateProducesOnlyEligibilityAndNeverPromotesRetainedRawProvenance()
    {
        var fixture = new Fixture(); var source = fixture.Source();
        var result = await source.ReadAsync(fixture.Request, CancellationToken.None);
        Assert.True(result.CandidateLocalAssociation); Assert.Equal(PairedCpuState.Partial, result.State);
        Assert.Equal(CpuPairedCaptureGap.SourceQualificationAbsent, result.Reason);
        Assert.True(result.NumericalEligibility!.SqlProcessEligible); Assert.True(result.NumericalEligibility.OperatingSystemEligible);
        Assert.True(result.NumericalEligibility.SameNativeInterval);
        Assert.Null(result.SqlProcessPercent); Assert.Null(result.OperatingSystemPercent);
        Assert.Null(result.NumericalEligibility.SqlProcessPercent); Assert.Null(result.NumericalEligibility.OperatingSystemPercent);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.NativeQualification);
        Assert.False(result.TargetComparisonAdmitted); Assert.False(result.HardwareIdentityVerified);
        Assert.Same(fixture.Native.Cpu!.Before, result.CpuBefore); Assert.Same(fixture.Native.Cpu.After, result.CpuAfter);
        Assert.Equal(PairedCpuSqlProcessWitness.Unavailable, result.CpuBefore!.Binding.SqlProcessWitness);
        Assert.Equal(6, result.SourceFrames); Assert.InRange(result.EstimatedSizeBytes, 1, CpuPairedCaptureObservation.MaximumCaptureBytes);
    }

    [Fact]
    public async Task PrivacySafeReceiptPreservesExactNumbersAndActualFilteredSourceAccounting()
    {
        var fixture = new Fixture(); var result = await fixture.Source().ReadAsync(fixture.Request, CancellationToken.None);
        string json = JsonSerializer.Serialize(result, JsonOptions);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("51000", json, StringComparison.Ordinal); Assert.DoesNotContain("sqlservr", json, StringComparison.Ordinal);
        Assert.DoesNotContain("serverEndpoint", json, StringComparison.Ordinal); Assert.DoesNotContain("processName", json, StringComparison.Ordinal);
        Assert.Contains("9223372036854775807", json, StringComparison.Ordinal);
        Assert.Equal(15, result.TcpBefore!.RowCount4); Assert.Equal(2, result.TcpBefore.MatchingRows.Count);
        Assert.Equal(CpuPairedSocketDirection.ServerToClient, result.TcpBefore.MatchingRows[0].Direction);
        Assert.Equal(CpuPairedSocketDirection.ClientToServer, result.TcpBefore.MatchingRows[1].Direction);
        Assert.Equal(1024, result.Bytes.TcpBeforeHelperBytes); Assert.Equal(2048, result.Bytes.CpuPairHelperBytes);
        Assert.Equal(1152, result.Bytes.TcpAfterHelperBytes);
        Assert.Equal(CpuPairedCaptureObservation.SqlLogicalResponseUpperBoundBytes, result.Bytes.SqlLogicalResponseUpperBoundBytes);
        Assert.Equal(fixture.Before.SourceObservedAtUtc, result.SqlBefore!.SourceObservedAtUtc);
        Assert.Equal(fixture.Native.Cpu!.IdProcessBefore, result.IdProcessBefore);
    }

    [Fact]
    public async Task MissingOneSocketDirectionCannotUnlockEvenValidPdhArithmetic()
    {
        var fixture = new Fixture(); var after = fixture.Tcp(after: true, serverOnly: true);
        var native = fixture.Native with { AfterTcp = new("partial", "source_qualification_absent", after) };
        var result = await fixture.Source(native).ReadAsync(fixture.Request, CancellationToken.None);
        Assert.False(result.CandidateLocalAssociation); Assert.Equal(CpuTcpAssociationGap.ReverseSocketMissing, result.CandidateReason);
        Assert.Null(result.NumericalEligibility); Assert.Equal(CpuPairedCaptureGap.CandidateAssociationRejected, result.Reason);
        Assert.Same(native.Cpu!.Before!.ProcessCounter, result.CpuBefore!.ProcessCounter);
    }

    [Fact]
    public async Task IndividualCounterFailurePreservesIndependentOsEligibilityWithoutComparison()
    {
        var fixture = new Fixture(); var cpu = fixture.Native.Cpu!;
        var native = fixture.Native with { Cpu = cpu with { Before = fixture.Cpu(false, missingProcess: true), ProcessReason = "process_counter_unavailable" } };
        var result = await fixture.Source(native).ReadAsync(fixture.Request, CancellationToken.None);
        Assert.True(result.CandidateLocalAssociation); Assert.False(result.NumericalEligibility!.SqlProcessEligible);
        Assert.True(result.NumericalEligibility.OperatingSystemEligible); Assert.False(result.NumericalEligibility.SameNativeInterval);
        Assert.Equal(PairedCpuGapReason.SourceUnavailable, result.NumericalEligibility.SqlProcessReason);
        Assert.Equal("process_counter_unavailable", result.ProcessReadReason);
        Assert.Equal(CpuPairedCaptureGap.NumericalEvidenceIncomplete, result.Reason);
    }

    [Fact]
    public async Task DifferentIndividualPdhAnchorsNeverBecomeOnePairedInterval()
    {
        var fixture = new Fixture(); var after = fixture.Cpu(true, osAnchor: 26_000_000);
        var native = fixture.Native with { Cpu = fixture.Native.Cpu! with { After = after } };
        var result = await fixture.Source(native).ReadAsync(fixture.Request, CancellationToken.None);
        Assert.True(result.NumericalEligibility!.SqlProcessEligible); Assert.True(result.NumericalEligibility.OperatingSystemEligible);
        Assert.False(result.NumericalEligibility.SameNativeInterval);
        Assert.Equal(25_000_000L, result.NumericalEligibility.SqlToNative100ns);
        Assert.Equal(26_000_000L, result.NumericalEligibility.OsToNative100ns); Assert.Null(result.SqlProcessPercent);
    }

    [Fact]
    public async Task CompletedIncompleteCaptureRetainsKnownFramesAndFixedSourceRefusal()
    {
        var fixture = new Fixture(); var native = fixture.Native with { Cpu = new("unavailable", "permission_denied") };
        var result = await fixture.Source(native).ReadAsync(fixture.Request, CancellationToken.None);
        Assert.Equal(4, result.SourceFrames); Assert.Equal(PairedCpuState.Unavailable, result.State);
        Assert.Equal(CpuTcpAssociationGap.NativeCaptureIncomplete, result.CandidateReason);
        Assert.Equal("permission_denied", result.CpuPairReadReason); Assert.Null(result.Bytes.CpuPairHelperBytes);
        Assert.Null(result.NumericalEligibility); Assert.NotNull(result.SqlAfter);
    }

    [Theory]
    [InlineData("missing", CpuPairedCaptureGap.HostBindingUnavailable)]
    [InlineData("denied", CpuPairedCaptureGap.HostBindingDenied)]
    [InlineData("revision", CpuPairedCaptureGap.HostBindingBoundary)]
    [InlineData("target", CpuPairedCaptureGap.HostBindingBoundary)]
    [InlineData("capability", CpuPairedCaptureGap.HostCpuCapabilityUnavailable)]
    public async Task PersistedHostRefusalDoesNotOpenSqlOrNativeSources(string mode, CpuPairedCaptureGap expected)
    {
        var fixture = new Fixture(); int captures = 0;
        HostTarget? host = mode == "missing" ? null : fixture.Host(mode);
        var source = new PairedCpuCaptureSource(new Resolver(host), (_, _, _) =>
        { captures++; return ValueTask.FromResult(fixture.Bracket()); });
        var result = await source.ReadAsync(fixture.Request, CancellationToken.None);
        Assert.Equal(expected, result.Reason); Assert.Equal(0, captures); Assert.Equal(0, result.SourceFrames);
        Assert.Null(result.HostBindingRevision); Assert.False(result.CandidateLocalAssociation);
    }

    [Fact]
    public async Task ResolutionConsumesTheOriginalBudgetInsteadOfRenewingTheSqlTimeout()
    {
        var fixture = new Fixture(); var clock = new Clock(); TimeSpan? actual = null;
        var resolver = new Resolver(fixture.Host(), () => clock.Advance(TimeSpan.FromMilliseconds(200)));
        var source = new PairedCpuCaptureSource(resolver, (request, _, _) =>
        { actual = request.Timeout.Value; return ValueTask.FromResult(fixture.Bracket()); }, clock);
        _ = await source.ReadAsync(fixture.Request, CancellationToken.None);
        Assert.Equal(fixture.Request.Timeout.Value - TimeSpan.FromMilliseconds(200), actual);
    }

    [Fact]
    public async Task ExhaustedOriginalBudgetNeverStartsTheBracketingProvider()
    {
        var fixture = new Fixture(); var clock = new Clock(); int captures = 0;
        var source = new PairedCpuCaptureSource(new Resolver(fixture.Host(), () => clock.Advance(fixture.Request.Timeout.Value)),
            (_, _, _) => { captures++; return ValueTask.FromResult(fixture.Bracket()); }, clock);
        await Assert.ThrowsAsync<TimeoutException>(async () => await source.ReadAsync(fixture.Request, CancellationToken.None));
        Assert.Equal(0, captures);
    }

    [Fact]
    public async Task PreCancellationPerformsNeitherResolutionNorNativeWork()
    {
        var fixture = new Fixture(); var resolver = new Resolver(fixture.Host());
        var source = new PairedCpuCaptureSource(resolver, (_, _, _) => throw new InvalidOperationException("must not start"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ReadAsync(fixture.Request, cancelled.Token));
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task CancelledResolverCannotReturnLateEvidenceOrStartNativeSampling()
    {
        var fixture = new Fixture(); var held = new TaskCompletionSource<HostTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int captures = 0; var resolver = new HeldResolver(held.Task);
        var source = new PairedCpuCaptureSource(resolver, (_, _, _) => { captures++; return ValueTask.FromResult(fixture.Bracket()); });
        using var cancel = new CancellationTokenSource(); var task = source.ReadAsync(fixture.Request, cancel.Token).AsTask();
        await resolver.Started.Task; cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        held.SetResult(fixture.Host()); Assert.Equal(0, captures);
    }

    [Fact]
    public async Task CancellationAfterOwnedProviderCompletionCannotAcceptTheLateCapture()
    {
        var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        var source = new PairedCpuCaptureSource(new Resolver(fixture.Host()), (_, _, _) =>
        { cancel.Cancel(); return ValueTask.FromResult(fixture.Bracket()); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ReadAsync(fixture.Request, cancel.Token));
    }

    [Fact]
    public async Task TerminationUnprovenPropagatesAndCannotBecomeAnIncompleteUsableReceipt()
    {
        var fixture = new Fixture(); var source = new PairedCpuCaptureSource(new Resolver(fixture.Host()),
            (_, _, _) => throw new CpuNativeTerminationUnprovenException());
        await Assert.ThrowsAsync<CpuNativeTerminationUnprovenException>(async () => await source.ReadAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task ForeignExecutionAndDifferentResolvedHostEvidenceAreRejected()
    {
        var fixture = new Fixture(); var other = new Fixture(new CollectorRunId(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await fixture.Source(other.Native).ReadAsync(fixture.Request, CancellationToken.None));
        var source = new PairedCpuCaptureSource(new Resolver(fixture.Host(fingerprint: new string('d', 64))),
            (_, _, _) => ValueTask.FromResult(fixture.Bracket()));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await source.ReadAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task RetainedSocketRowsCannotBeMutatedThroughTheProviderArray()
    {
        var fixture = new Fixture(); var result = await fixture.Source().ReadAsync(fixture.Request, CancellationToken.None);
        Assert.IsAssignableFrom<IReadOnlyList<CpuPairedSocketRow>>(result.TcpBefore!.MatchingRows);
        Assert.Throws<NotSupportedException>(() => ((IList<CpuPairedSocketRow>)result.TcpBefore.MatchingRows)[0] = new(CpuPairedSocketDirection.Other, 0, 0));
    }

    [Fact]
    public async Task ReceiptRejectsContradictoryEligibilityAndMalformedRetainedSqlMetadata()
    {
        var fixture = new Fixture(); var value = await fixture.Source().ReadAsync(fixture.Request, CancellationToken.None);
        Assert.Throws<ArgumentException>(() => Copy(value, value.NumericalEligibility! with { SqlFromNative100ns = null }));
        Assert.Throws<ArgumentException>(() => Copy(value, value.NumericalEligibility! with { SameNativeInterval = false }));
        Assert.Throws<ArgumentException>(() => Copy(value, value.NumericalEligibility, value.SqlBefore! with { Ordinal = 2 }));
        Assert.Throws<ArgumentException>(() => Copy(value, value.NumericalEligibility, value.SqlBefore! with { ServerSessionId = 0 }));
    }

    [Fact]
    public async Task OriginalBudgetAlsoFencesStoppedProviderThatReturnsAfterExpiry()
    {
        var fixture = new Fixture(); var clock = new Clock();
        var source = new PairedCpuCaptureSource(new Resolver(fixture.Host()), (_, _, _) =>
        { clock.Advance(fixture.Request.Timeout.Value); return ValueTask.FromResult(fixture.Bracket()); }, clock);
        await Assert.ThrowsAsync<TimeoutException>(async () => await source.ReadAsync(fixture.Request, CancellationToken.None));
    }

    [Fact]
    public async Task AdapterCompletedCandidateIsOnePrivateReceiptWithoutInventedMetricsOrLoss()
    {
        var fixture = new Fixture(); var collector = new PairedCpuCaptureCollector(fixture.Source());
        var result = await collector.CollectAsync(AdapterRequest(fixture), CancellationToken.None);
        Assert.Equal("cpu.paired-capture", collector.Manifest.Id.Value);
        Assert.Equal(CollectorOutputKind.CpuPairedCapture, collector.Manifest.OutputKind);
        Assert.Equal(1, PairedCpuCaptureCollector.OutputContract.MaxCpuPairedCaptureObservations);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome); Assert.Equal(CollectorRunReason.Completed, result.Reason);
        Assert.Equal(1, result.Payload.ItemCount); Assert.Empty(result.Payload.Metrics); Assert.False(result.Loss.HasLoss);
        Assert.NotNull(result.Payload.CpuPairedCapture); Assert.False(result.Payload.CpuPairedCapture.TargetComparisonAdmitted);
        Assert.Equal(6, result.Accounting.SourceRowsRead);
        Assert.Equal(result.Payload.EstimatedSizeBytes, result.Accounting.OutputBytes);
        Assert.Equal(CpuPairedCaptureOutputValidation.AccountedResponseBytes(result.Payload.CpuPairedCapture), result.Accounting.ResponseBytes);
    }

    [Fact]
    public async Task AdapterUnavailableHostReceiptIsCompletedEvidenceWithoutFabricatedLoss()
    {
        var fixture = new Fixture(); var source = new PairedCpuCaptureSource(new Resolver(null), (_, _, _) => throw new InvalidOperationException());
        var result = await new PairedCpuCaptureCollector(source).CollectAsync(AdapterRequest(fixture), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome); Assert.False(result.Loss.HasLoss);
        Assert.Equal(CpuPairedCaptureGap.HostBindingUnavailable, result.Payload.CpuPairedCapture!.Reason);
        Assert.Equal(0, result.Accounting.SourceRowsRead); Assert.Equal(0, result.Accounting.ResponseBytes);
        Assert.Null(result.Payload.CpuPairedCapture.SqlProcessPercent);
    }

    [Theory]
    [InlineData("timeout", CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded)]
    [InlineData("invalid", CollectorRunOutcome.OutputInvalid, CollectorRunReason.OutputValidationFailed)]
    [InlineData("cleanup", CollectorRunOutcome.TimedOut, CollectorRunReason.DeadlineExceeded)]
    [InlineData("permission", CollectorRunOutcome.PermissionDenied, CollectorRunReason.RequiredPermissionMissing)]
    [InlineData("platform", CollectorRunOutcome.Unsupported, CollectorRunReason.TargetUnsupported)]
    [InlineData("io", CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure)]
    [InlineData("ownership", CollectorRunOutcome.PermanentFailure, CollectorRunReason.PermanentTargetFailure)]
    public async Task AdapterFailuresCannotCarryUsableCaptures(string failure, CollectorRunOutcome outcome, CollectorRunReason reason)
    {
        var fixture = new Fixture(); var collector = new PairedCpuCaptureCollector((_, _) =>
            throw failure switch { "timeout" => new TimeoutException(), "cleanup" => new CpuNativeTerminationUnprovenException(),
                "permission" => new UnauthorizedAccessException(), "platform" => new PlatformNotSupportedException(),
                "io" => new IOException(), "ownership" => new InvalidOperationException(), _ => new InvalidDataException() });
        var result = await collector.CollectAsync(AdapterRequest(fixture), CancellationToken.None);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(reason, result.Reason); Assert.Equal(0, result.Payload.ItemCount);
        Assert.Null(result.Payload.CpuPairedCapture);
        Assert.Equal(outcome == CollectorRunOutcome.OutputInvalid ? CollectorLossKind.OutputValidationFailure : CollectorLossKind.None, result.Loss.Kind);
    }

    [Fact]
    public async Task AdapterCallerCancellationAfterProviderReturnsDiscardsReceipt()
    {
        var fixture = new Fixture(); var capture = await fixture.Source().ReadAsync(fixture.Request, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var collector = new PairedCpuCaptureCollector((_, _) => { cancel.Cancel(); return ValueTask.FromResult(capture); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await collector.CollectAsync(AdapterRequest(fixture), cancel.Token));
    }

    [Fact]
    public async Task AdapterRejectsForeignRunInsteadOfRecordingItsCandidate()
    {
        var fixture = new Fixture(); var other = new Fixture(new CollectorRunId(Guid.NewGuid()));
        var foreign = await other.Source().ReadAsync(other.Request, CancellationToken.None);
        var result = await new PairedCpuCaptureCollector((_, _) => ValueTask.FromResult(foreign)).CollectAsync(AdapterRequest(fixture), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.OutputInvalid, result.Outcome); Assert.Equal(0, result.Payload.ItemCount);
        Assert.Equal(CollectorLossKind.OutputValidationFailure, result.Loss.Kind);
    }

    [Fact]
    public async Task GenericOutputContractRejectsMixedOrdinaryMetricsAlongsidePrivateCapture()
    {
        var fixture = new Fixture(); var capture = await fixture.Source().ReadAsync(fixture.Request, CancellationToken.None);
        var payload = new CollectorPayload([new(new(Guid.NewGuid()), fixture.Request.TargetId,
            new("host.cpu.percent"), new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero), 1)], cpuPairedCapture: capture);
        Assert.Throws<InvalidDataException>(() => CpuPairedCaptureOutputValidation.Validate(payload,
            fixture.Request.TargetId, fixture.Request.TargetRevision, fixture.Request.RunId, CollectorRunOutcome.Succeeded,
            new(capture.SourceFrames, payload.ItemCount, checked((int)CpuPairedCaptureOutputValidation.AccountedResponseBytes(capture)), payload.EstimatedSizeBytes),
            CollectorLossEvidence.None));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task AdapterMissingCapabilityOrPermissionNeverCallsTheSource(bool missingCapability)
    {
        var fixture = new Fixture(); int reads = 0;
        var collector = new PairedCpuCaptureCollector((_, _) => { reads++; throw new InvalidOperationException(); });
        var result = await collector.CollectAsync(AdapterRequest(fixture, missingCapability, !missingCapability), CancellationToken.None);
        Assert.Equal(missingCapability ? CollectorRunOutcome.Unsupported : CollectorRunOutcome.PermissionDenied, result.Outcome);
        Assert.Equal(0, reads); Assert.Null(result.Payload.CpuPairedCapture);
    }

    [Theory]
    [InlineData("unencrypted")] [InlineData("sysadmin")] [InlineData("authentication")]
    public async Task ClaimedCapabilityFlagsCannotOverrideActualUnsafeDiscoveryProfile(string unsafeProfile)
    {
        var fixture = new Fixture(); int reads = 0;
        var collector = new PairedCpuCaptureCollector((_, _) => { reads++; throw new InvalidOperationException(); });
        var result = await collector.CollectAsync(AdapterRequest(fixture, unsafeProfile: unsafeProfile), CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.PermissionDenied, result.Outcome); Assert.Equal(0, reads);
        Assert.Null(result.Payload.CpuPairedCapture);
    }

    [Fact]
    public async Task ActualGenericValidatorAcceptsTheCompletedPrivateCaptureRegistration()
    {
        var fixture = new Fixture(); var request = AdapterRequest(fixture);
        var collector = new PairedCpuCaptureCollector(fixture.Source());
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        new CollectorOutputValidator(PairedCpuCaptureCollector.OutputContract).Validate(collector.Manifest, request, result);
        Assert.Null(result.Payload.CpuPairedCapture!.SqlProcessPercent);
        Assert.Equal(PairedCpuNativeQualification.Absent, result.Payload.CpuPairedCapture.NativeQualification);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ActualGenericValidatorRejectsCrossKindDeclarationAndNonCpuContract(bool crossKind)
    {
        var fixture = new Fixture(); var request = AdapterRequest(fixture);
        var collector = new PairedCpuCaptureCollector(fixture.Source());
        var result = await collector.CollectAsync(request, CancellationToken.None);
        Assert.Equal(CollectorRunOutcome.Succeeded, result.Outcome);
        var manifest = crossKind ? M4TestData.CreateManifest(id: "cpu.paired-capture", outputKind: CollectorOutputKind.Metrics,
            maxRows: CpuPairedCaptureObservation.MaximumSourceFrames,
            maxResponseBytes: CpuPairedCaptureOutputValidation.MaximumResponseBytes) : collector.Manifest;
        var contract = crossKind ? PairedCpuCaptureCollector.OutputContract : M4TestData.CreateOutputContract();
        Assert.Throws<InvalidDataException>(() => new CollectorOutputValidator(contract).Validate(manifest, request, result));
    }

    [Fact]
    public async Task ActualGenericValidatorRejectsCaptureWhenExecutionRunWasSwapped()
    {
        var fixture = new Fixture(); var request = AdapterRequest(fixture);
        var collector = new PairedCpuCaptureCollector(fixture.Source());
        var result = await collector.CollectAsync(request, CancellationToken.None);
        var otherExecution = new CollectorExecutionRequest(new(Guid.NewGuid()), request.TargetId, request.TargetRevision,
            request.ConnectionPolicy, request.CapabilityProfile, request.Attempt, request.Timeout);
        Assert.Throws<InvalidDataException>(() => new CollectorOutputValidator(PairedCpuCaptureCollector.OutputContract)
            .Validate(collector.Manifest, otherExecution, result));
        Assert.Same(fixture.Native.Cpu!.Before, result.Payload.CpuPairedCapture!.CpuBefore);
    }

    private static CollectorExecutionRequest AdapterRequest(Fixture fixture, bool missingCapability = false, bool denied = false,
        string? unsafeProfile = null)
    {
        var template = fixture.Request.CapabilityProfile; var manifest = new PairedCpuCaptureCollector((_, _) => throw new InvalidOperationException()).Manifest;
        var profile = new CapabilityProfile(template.TargetId, template.TargetRevision, template.CollectorId, 1, 1,
            template.ServerIdentity, unsafeProfile is null ? template.Outcome : CapabilityDiscoveryOutcome.SecurityPolicyRejected,
            unsafeProfile switch
            {
                "unencrypted" => CapabilityDiscoveryReason.TransportNotEncrypted,
                "sysadmin" => CapabilityDiscoveryReason.ExcessivePrivilege,
                "authentication" => CapabilityDiscoveryReason.AuthenticationSchemeMismatch,
                _ => template.Reason,
            },
            unsafeProfile == "authentication" ? SqlServerAuthenticationScheme.Unknown : template.AuthenticationScheme,
            unsafeProfile != "unencrypted" && template.TransportEncrypted,
            unsafeProfile == "sysadmin" || template.IsSysAdmin,
            manifest.RequiredCapabilities.Where((_, index) => !missingCapability || index != 0)
                .Select(id => new CapabilityEvidence(id, CapabilityAvailability.Available, CapabilityEvidenceReason.Verified)).ToArray(),
            [new(new("server.view-performance-state"), PermissionEvidenceScope.Server,
                denied ? PermissionEvidenceOutcome.Denied : PermissionEvidenceOutcome.Granted)],
            template.DiscoveryDuration, template.EvidenceBytes, template.CheckedAtUtc, template.ValidUntilUtc);
        var request = fixture.Request;
        return new(request.RunId, request.TargetId, request.TargetRevision, request.ConnectionPolicy, profile, request.Attempt, request.Timeout);
    }

    private static CpuPairedCaptureObservation Copy(CpuPairedCaptureObservation value, CpuPairedNumericalEligibility? eligibility,
        CpuPairedSqlEvidence? sql = null) => new(value.TargetId, value.TargetRevision, value.RunId,
        value.HostBindingRevision, value.HostProfileRevision, value.Reason, value.CandidateReason, sql ?? value.SqlBefore,
        value.SqlAfter, value.TcpBefore, value.TcpAfter, value.CpuBefore, value.CpuAfter, value.IdProcessBefore,
        value.IdProcessAfter, value.FrameBefore, value.FrameAfter, value.ExecutableFingerprintBefore,
        value.ExecutableFingerprintAfter, value.ProcessStatusBefore, value.ProcessStatusAfter, value.OsStatusBefore,
        value.OsStatusAfter, eligibility, value.Bytes);

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        internal void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }
    private sealed class Resolver(HostTarget? host, Action? onRead = null) : IHostTargetResolver
    {
        internal int Calls { get; private set; }
        public ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; onRead?.Invoke(); return ValueTask.FromResult(host); }
    }
    private sealed class HeldResolver(Task<HostTarget?> held) : IHostTargetResolver
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<HostTarget?> ResolveAsync(MonitoredInstanceId targetId, ObservationTargetRevision revision, CancellationToken cancellationToken)
        { Started.SetResult(); return new(held); }
    }
    private sealed class Fixture
    {
        private static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        private static readonly string HostFingerprint = new('a', 64), Machine = new('b', 64), Logical = new('c', 64);
        private const long Boot = 130_000_000_000_000_000, Creation = Boot + 10_000_000;
        private readonly Guid query = Guid.NewGuid(), connection = Guid.NewGuid(), client = Guid.NewGuid(), server = Guid.NewGuid();
        internal CollectorExecutionRequest Request { get; }
        internal SqlCpuTargetWitness Before => Sql(false);
        internal WindowsCpuAssociationSamplingResult Native { get; }
        internal Fixture(CollectorRunId? run = null)
        {
            var template = M4TestData.CreateExecutionRequest();
            Request = new(run ?? template.RunId, template.TargetId, template.TargetRevision, template.ConnectionPolicy,
                template.CapabilityProfile, template.Attempt, new(TimeSpan.FromSeconds(1)));
            var raw = new WindowsTargetCpuPairReadResult("partial", "source_qualification_absent", Cpu(false), Cpu(true),
                new(0x10000, 0, 42, 0, Boot), new(0x10000, 0, 42, 0, Boot + 10_000_000),
                "sqlservr", "sqlservr", Logical, Logical)
            { BeforeWitness = new(Creation, Creation, long.MaxValue, 4, 4, 1, false, 258),
                AfterWitness = new(Creation, Creation, long.MaxValue, 4, 4, 1, false, 258), HelperOutputBytes = 2048 };
            Native = new(new("partial", "source_qualification_absent", Tcp(false)) { HelperOutputBytes = 1024 }, raw,
                new("partial", "source_qualification_absent", Tcp(true)) { HelperOutputBytes = 1152 });
        }
        internal HostTarget Host(string mode = "", string? fingerprint = null)
        {
            var target = mode == "target" ? new MonitoredInstanceId(Guid.NewGuid()) : Request.TargetId;
            var revision = mode == "revision" ? Request.TargetRevision.Next() : Request.TargetRevision;
            var state = mode == "denied" ? HostBindingState.Denied : HostBindingState.Bound;
            return new(new(target, HostIdentityFingerprint.Parse(fingerprint ?? HostFingerprint), new(3), state),
                new(target, revision, new(3), mode == "capability" ? HostMetricCapability.Memory : HostMetricCapability.Cpu, state), revision);
        }
        internal PairedCpuCaptureSource Source(WindowsCpuAssociationSamplingResult? native = null) => new(new Resolver(Host()),
            (_, _, _) => ValueTask.FromResult(Bracket(native)));
        internal SqlCpuWitnessBracket<WindowsCpuAssociationSamplingResult> Bracket(WindowsCpuAssociationSamplingResult? native = null) =>
            new(Sql(false), native ?? Native, Sql(true), SqlCpuWitnessGap.None);
        private SqlCpuTcpConnectionEvidence TcpConnection() => new(server, 17, "127.0.0.1", 1433, "127.0.0.1", 51000, 99);
        private SqlCpuTargetWitness Sql(bool after) => new(Request.TargetId, Request.TargetRevision, Request.RunId,
            connection, client, after ? 2 : 1, At.AddSeconds(after ? 4 : 0),
            new(HostFingerprint, Logical, 42, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified).AddTicks(7), 1000, after ? 5000 : 1001),
            SqlCpuPhysicalTransport.Tcp, after ? 200 : 100, after ? 300 : 200, 4, TcpConnection(), 2);
        private PairedCpuBinding Binding() => new(Request.TargetId, Request.TargetRevision, Request.RunId, HostFingerprint, 42);
        private static PairedCpuHostEvidence HostEvidence() => new(HostFingerprint, Machine, Boot, PairedCpuHostWitness.NativeMachineAndBoot);
        internal CpuTcpNativeFrame Tcp(bool after, bool serverOnly = false)
        {
            var tuple = TcpConnection(); CpuNativeTcpRow[] rows = [new(tuple.ServerEndpoint!, tuple.ClientEndpoint!, 5, 42),
                new(tuple.ClientEndpoint!, tuple.ServerEndpoint!, 5, 99)];
            return new(Binding(), At.AddSeconds(after ? 3 : 1), after ? 30_000_000 : 10_000_000, HostEvidence(),
                new(Creation, Creation, long.MaxValue, 0, 258), new(Creation + 10_000_000, Creation + 10_000_000, long.MaxValue, 0, 258),
                0, 0, 15, 0, serverOnly ? [rows[0]] : rows);
        }
        internal PairedCpuEndpoint Cpu(bool after, bool missingProcess = false, long? osAnchor = null) => new(Binding(), query,
            At.AddSeconds(after ? 2.5 : 1.5), after ? 25_000_000 : 15_000_000, 10_000_000, HostEvidence(), Creation, 4,
            missingProcess ? null : new(PdhCpuRawCounter.ProcessTimerType, 0, after ? 5_000_000 : 1_000_000,
                after ? 25_000_000 : 15_000_000, Boot + (after ? 10_000_000 : 0)),
            new(PdhCpuRawCounter.OsIdleInverseTimerType, 0, after ? 6_000_000 : 1_000_000,
                osAnchor ?? (after ? 25_000_000 : 15_000_000), Boot + (after ? 10_000_000 : 0)));
    }
}
