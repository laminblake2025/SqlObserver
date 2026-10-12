using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Pure synthetic contracts only. No filesystem owner, Windows launch/containment,
/// physical SQL drain, interruption, recovery or native qualification is established.</summary>
public sealed class TrialWorkerTerminationAuditContractTests
{
    private static readonly Guid FixtureId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Nonce = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private const string AdminSid = "S-1-5-21-1-2-3-1000", MonitorSid = "S-1-5-21-1-2-3-1001";
    private const string AdminHex = "010500000000000515000000010000000200000003000000e8030000";
    private const string MonitorHex = "010500000000000515000000010000000200000003000000e9030000";
    private static readonly DateTimeOffset Time = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JournalOptions = new() { WriteIndented = true };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ValidPureAuditRoundtripsWithExactEpochsAndLocalSqlClockWithoutProtectedProvenance(bool temporaryMonitor)
    {
        var fixture = Fixture(temporaryMonitor);
        TrialWorkerTerminationAudit.ValidateBinding(fixture.Audit, fixture.Expected);
        TrialWorkerTerminationAudit.ValidateJournalBinding(fixture.Journal, fixture.Expected);
        byte[] bytes = TrialWorkerTerminationAudit.Serialize(fixture.Audit, fixture.Expected);
        var parsed = TrialWorkerTerminationAudit.Parse(bytes, fixture.Expected);
        Assert.Equal(bytes, TrialWorkerTerminationAudit.Serialize(parsed.Receipt, fixture.Expected));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), parsed.Sha256);
        Assert.Equal(bytes.Length, parsed.ByteCount); Assert.False(parsed.ProtectedFileHandleVerified);
        Assert.False(fixture.Journal.ProtectedFileHandleVerified);
        Assert.Equal(TrialWorkerTerminationPreparationAction.PreserveForInspection,
            TrialWorkerTerminationAudit.SelectPreparationAction(parsed, fixture.Expected, fixture.Journal));
        Assert.Equal(DateTimeKind.Unspecified, parsed.Receipt.SqlDrain.Binding.OriginalMonitorConnectedLocal.Kind);
        Assert.Equal(fixture.Expected.Worker.StartedUtc, parsed.Receipt.Worker.StartedUtc);
        Assert.Contains("12:00:02.0000007Z", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("03:17:18.1230000", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(new[] { TrialWorkerTerminationPreparationAction.PreserveForInspection,
            TrialWorkerTerminationPreparationAction.EligibleForIndependentSqlValidation }, Enum.GetValues<TrialWorkerTerminationPreparationAction>());
        Assert.False(parsed.Receipt.RecoveryAuthority); Assert.Equal("Absent", parsed.Receipt.NativeQualification);
        Assert.DoesNotContain(MonitorSid, parsed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(AdminHex, fixture.Expected.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<TrialWorkerProcessEpoch>)parsed.Receipt.Scope.Processes).Add(fixture.Expected.Worker));
        Assert.Throws<NotSupportedException>(() => ((IList<TrialFact>)fixture.Journal.Receipt.Installed!.Facts).Clear());
    }

    [Theory]
    [InlineData("DeploymentId")] [InlineData("FixtureId")] [InlineData("LaunchNonce")]
    [InlineData("Supervisor.ProcessId")] [InlineData("Supervisor.StartedUtc")] [InlineData("Supervisor.ImageSha256")] [InlineData("Supervisor.TokenSid")]
    [InlineData("Coordinator.ProcessId")] [InlineData("Coordinator.StartedUtc")] [InlineData("Coordinator.ImageSha256")] [InlineData("Coordinator.TokenSid")]
    [InlineData("Worker.ProcessId")] [InlineData("Worker.StartedUtc")] [InlineData("Worker.ImageSha256")] [InlineData("Worker.TokenSid")]
    [InlineData("Journal.JournalSha256")] [InlineData("Journal.BaselineSha256")] [InlineData("Journal.InstalledSha256")]
    [InlineData("SqlDrain.Binding.MasterDatabaseGuid")] [InlineData("SqlDrain.Binding.MsdbDatabaseGuid")]
    [InlineData("SqlDrain.Binding.OriginalMonitorSqlConnectionId")] [InlineData("SqlDrain.Binding.OriginalMonitorSessionId")]
    [InlineData("SqlDrain.Binding.OriginalMonitorConnectedLocal")] [InlineData("SqlDrain.Binding.MonitorSidHex")]
    [InlineData("SqlDrain.Binding.OriginalMonitorConnectTimeSqlType")] [InlineData("SqlDrain.Binding.OriginalMonitorConnectTimeScale")]
    [InlineData("SqlDrain.Binding.ObserverSqlConnectionId")] [InlineData("SqlDrain.Binding.ObserverSessionId")]
    [InlineData("SqlDrain.Binding.ObserverSidHex")]
    public void EveryMaterialExpectedBindingMustMatchIndependentOriginalInput(string path)
    {
        var fixture = Fixture(); JsonObject root = AuditJson(fixture); var (node, key) = Member(root, path);
        JsonNode? value = node[key];
        if (key.EndsWith("Sha256", StringComparison.Ordinal)) node[key] = new string('f', 64);
        else if (key.EndsWith("SidHex", StringComparison.Ordinal)) node[key] = AdminHex == value!.GetValue<string>() ? MonitorHex : AdminHex;
        else if (key == "TokenSid") node[key] = "S-1-5-21-1-2-3-1002";
        else if (key == "StartedUtc") node[key] = "2026-10-07T12:00:02.0000008Z";
        else if (key == "OriginalMonitorConnectedLocal") node[key] = "2026-10-07T03:17:18.1270000";
        else if (key == "OriginalMonitorConnectTimeSqlType") node[key] = "datetime2";
        else if (key == "OriginalMonitorConnectTimeScale") node[key] = 7;
        else if (key.EndsWith("SessionId", StringComparison.Ordinal) || key == "ProcessId") node[key] = value!.GetValue<int>() + 1;
        else node[key] = Guid.Parse("99999999-9999-4999-8999-999999999999").ToString("D");
        Refuses(root, fixture);
    }

    [Theory]
    [InlineData("SourceQueryCompleted")] [InlineData("InterruptedBeforeFinalAcceptance")]
    [InlineData("CaptureAccepted")] [InlineData("PrototypeOnly")] [InlineData("RecoveryAuthority")]
    [InlineData("QualificationReplayed")] [InlineData("RecoveryMutationStarted")] [InlineData("AgentStarted")] [InlineData("JobOrDatabaseMutationStarted")]
    [InlineData("Scope.SupervisorOutsideBothScopes")] [InlineData("Scope.PrivateNonInheritableJob")] [InlineData("Scope.KillOnJobClose")]
    [InlineData("Scope.BreakawayFlagsPermitted")] [InlineData("Scope.BrokeredLaunchesPermitted")]
    [InlineData("Scope.CoordinatorCreatedSuspended")] [InlineData("Scope.WorkerCreatedSuspended")]
    [InlineData("Scope.CoordinatorAssignedBeforeResume")] [InlineData("Scope.WorkerAssignedBeforeResume")]
    [InlineData("Scope.CoordinatorMembershipVerified")] [InlineData("Scope.WorkerMembershipVerified")]
    [InlineData("Scope.CoordinatorHeldHandleExitConfirmed")] [InlineData("Scope.WorkerHeldHandleExitConfirmed")]
    [InlineData("Scope.ActualPrimaryTokensVerified")] [InlineData("Scope.NetCredentialsOnly")] [InlineData("Scope.ActiveProcessesQuerySucceeded")]
    [InlineData("SqlDrain.PhysicalIdentityVerified")] [InlineData("SqlDrain.SysadminVisibilityVerified")]
    [InlineData("SqlDrain.SameObserverSessionVerified")] [InlineData("SqlDrain.ReadTransactionRollbackAcknowledged")]
    [InlineData("SqlDrain.OriginalConnectionGone")] [InlineData("SqlDrain.OriginalRequestsGone")] [InlineData("SqlDrain.SqlKillIssued")]
    public void MissingContainmentOrDrainAndAnyMutationReplayOrPrototypeClaimRefuses(string path)
    {
        var fixture = Fixture(); JsonObject root = AuditJson(fixture); var (node, key) = Member(root, path);
        node[key] = !node[key]!.GetValue<bool>(); Refuses(root, fixture);
    }

    [Theory]
    [InlineData("Scope.ActiveProcesses")] [InlineData("Scope.UnknownOwnedProcesses")] [InlineData("Scope.HeldProcessExitsConfirmed")]
    [InlineData("SqlDrain.LiveMonitorSessions")] [InlineData("SqlDrain.LiveMonitorRequests")] [InlineData("SqlDrain.UnknownRelevantSessions")]
    public void ZeroCountsArePositiveObservationsAndAllHeldProcessExitsMustBeConfirmed(string path)
    {
        var fixture = Fixture();
        foreach (int value in new[] { -1, 1, int.MaxValue })
        { JsonObject root = AuditJson(fixture); var (node, key) = Member(root, path); node[key] = value; Refuses(root, fixture); }
    }

    [Theory]
    [InlineData("missing-root")] [InlineData("same-pid")] [InlineData("ledger-reordered")]
    [InlineData("epoch-changed")] [InlineData("unknown-descendant")] [InlineData("null-element")]
    public void CountAndMatchingRootBooleansCannotReplaceAnExactClosedHeldProcessLedger(string mutation)
    {
        var fixture = Fixture(); JsonObject root = AuditJson(fixture); JsonArray list = root["Scope"]!["Processes"]!.AsArray();
        switch (mutation)
        {
            case "missing-root": list.RemoveAt(1); break;
            case "same-pid": list[1]!["ProcessId"] = list[0]!["ProcessId"]!.GetValue<int>(); break;
            case "ledger-reordered": JsonNode first = list[0]!.DeepClone(); list.RemoveAt(0); list.Add(first); break;
            case "epoch-changed": list[1]!["StartedUtc"] = "2026-10-07T12:00:02.0000008Z"; break;
            case "unknown-descendant": list.Add(list[1]!.DeepClone()); break;
            default: list[1] = null; break;
        }
        Refuses(root, fixture);
    }

    [Theory]
    [InlineData("ScopeTerminatedAtUtc", "2026-10-07T12:00:02.0000007Z")]
    [InlineData("InterruptionRequestedAtUtc", "2026-10-07T12:00:02.0000007Z")]
    [InlineData("RecordedAtUtc", "2026-10-07T12:00:05.0000007Z")]
    [InlineData("SqlDrain.BeforeUtc", "2026-10-07T12:00:04.0000007Z")]
    [InlineData("SqlDrain.AfterUtc", "2026-10-07T12:00:16.0000008Z")]
    [InlineData("Supervisor.StartedUtc", "2026-10-07T12:00:00Z")]
    [InlineData("Worker.StartedUtc", "2026-10-07T12:00:02.0000007+00:00")]
    [InlineData("Worker.StartedUtc", "2026-10-07T13:00:02.0000007+01:00")]
    [InlineData("SqlDrain.Binding.OriginalMonitorConnectedLocal", "2026-10-07T03:17:18.1230000Z")]
    [InlineData("SqlDrain.Binding.OriginalMonitorConnectedLocal", "2026-10-07T03:17:18.123000")]
    public void ObservationOrderingAndLiteralClockNamespacesRemainExact(string path, string value)
    { var fixture = Fixture(); JsonObject root = AuditJson(fixture); var (node, key) = Member(root, path); node[key] = value; Refuses(root, fixture); }

    [Theory]
    [InlineData("")] [InlineData("Journal")] [InlineData("Supervisor")] [InlineData("Coordinator")] [InlineData("Worker")]
    [InlineData("Scope")] [InlineData("SqlDrain")] [InlineData("SqlDrain.Binding")]
    public void RecursiveJsonRejectsMissingUnknownCaseAndDuplicateMembersWithoutEcho(string path)
    {
        var fixture = Fixture();
        foreach (string change in new[] { "missing", "unknown", "case", "duplicate", "boolean-string" })
        {
            JsonObject root = AuditJson(fixture), node = ObjectAt(root, path); string key = node.First().Key; string json;
            if (change == "duplicate")
            {
                string literal = JsonSerializer.Serialize(key) + ":" + node[key]!.ToJsonString();
                json = root.ToJsonString().Replace(literal, literal + "," + literal, StringComparison.Ordinal);
            }
            else
            {
                if (change == "missing") node.Remove(key);
                else if (change == "unknown") node["Password"] = "private-canary";
                else if (change == "case") { node[key.ToLowerInvariant()] = node[key]?.DeepClone(); node.Remove(key); }
                else node[key] = "true";
                json = root.ToJsonString();
            }
            var error = Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.Parse(Encoding.UTF8.GetBytes(json), fixture.Expected));
            Assert.Null(error.InnerException); Assert.DoesNotContain("private-canary", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Kind", "TemporaryService")] [InlineData("ProducerContract", "harmless-owned-launch-prototype-v1")]
    [InlineData("NativeQualification", "Qualified")]
    public void HistoricalLaunchOrPrototypeMarkerCannotSupplyActualAuditAuthority(string key, string value)
    { var fixture = Fixture(); JsonObject root = AuditJson(fixture); root[key] = value; Refuses(root, fixture); }

    [Theory]
    [InlineData("prototype-contract")] [InlineData("same-nonce")] [InlineData("pending-journal")]
    [InlineData("net-only-assumed-sid")] [InlineData("same-monitor-admin")]
    [InlineData("local-relabeled-utc")] [InlineData("zero-digest")]
    public void CoherentMirroringDoesNotRelaxExpectedContractRequirements(string mutation)
    {
        var fixture = Fixture(); var expected = mutation switch
        {
            "prototype-contract" => fixture.Expected with { ProducerContract = "harmless-owned-launch-prototype-v1" },
            "same-nonce" => fixture.Expected with { LaunchNonce = fixture.Expected.FixtureId },
            "pending-journal" => fixture.Expected with { Journal = fixture.Expected.Journal with { Phase = "commit_pending" } },
            "net-only-assumed-sid" => fixture.Expected with { Worker = fixture.Expected.Worker with { TokenSid = "S-1-5-18" } },
            "same-monitor-admin" => fixture.Expected with { Worker = fixture.Expected.Worker with { TokenSid = AdminSid } },
            "local-relabeled-utc" => fixture.Expected with { SqlDrain = fixture.Expected.SqlDrain with { OriginalMonitorConnectedLocal = DateTime.SpecifyKind(fixture.Expected.SqlDrain.OriginalMonitorConnectedLocal, DateTimeKind.Utc) } },
            _ => fixture.Expected with { ProcessLedgerSha256 = new string('0', 64) },
        };
        var mirrored = fixture.Audit with { ProducerContract = expected.ProducerContract, LaunchNonce = expected.LaunchNonce,
            Journal = expected.Journal, Worker = expected.Worker, SqlDrain = fixture.Audit.SqlDrain with { Binding = expected.SqlDrain } };
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateBinding(mirrored, expected));
    }

    [Theory]
    [InlineData("worker")] [InlineData("coordinator")] [InlineData("descendant")]
    public void EvenCoherentlyPinnedLedgerCannotAdoptAnyProcessBornBeforeHeldSupervisor(string process)
    {
        var fixture = Fixture(); DateTimeOffset oldBirth = fixture.Expected.Supervisor.StartedUtc.AddTicks(-1);
        TrialWorkerProcessEpoch coordinator = process == "coordinator" ? fixture.Expected.Coordinator with { StartedUtc = oldBirth } : fixture.Expected.Coordinator;
        TrialWorkerProcessEpoch worker = process == "worker" ? fixture.Expected.Worker with { StartedUtc = oldBirth } : fixture.Expected.Worker;
        TrialWorkerProcessEpoch[] ledger = process == "descendant"
            ? [coordinator, worker, worker with { ProcessId = 103, StartedUtc = oldBirth }]
            : [coordinator, worker];
        var expected = fixture.Expected with { Coordinator = coordinator, Worker = worker, OwnedProcessCount = ledger.Length,
            ProcessLedgerSha256 = TrialWorkerTerminationAudit.ComputeProcessLedgerSha256(ledger) };
        var mirrored = fixture.Audit with { Coordinator = coordinator, Worker = worker,
            Scope = fixture.Audit.Scope with { Processes = ledger, HeldProcessExitsConfirmed = ledger.Length } };
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateBinding(mirrored, expected));
    }

    [Fact]
    public void SameJournalBytesSupplyRawDigestWhileFormattingDoesNotRewriteImmutableImageDigests()
    {
        var fixture = Fixture(); byte[] compact = JsonSerializer.SerializeToUtf8Bytes(fixture.Journal.Receipt);
        var other = TrialJournal.ParseReadWithDigest(compact, fixture.Expected.DeploymentId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(compact)), other.JournalSha256);
        Assert.NotEqual(fixture.Journal.JournalSha256, other.JournalSha256);
        Assert.Equal(fixture.Journal.BaselineSha256, other.BaselineSha256); Assert.Equal(fixture.Journal.InstalledSha256, other.InstalledSha256);
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateJournalBinding(other, fixture.Expected));
        Assert.False(other.ProtectedFileHandleVerified);
    }

    [Fact]
    public void CallerBuffersAndMutableSourceLedgerCannotAlterParsedCapsules()
    {
        var fixture = Fixture(); byte[] bytes = TrialWorkerTerminationAudit.Serialize(fixture.Audit, fixture.Expected);
        byte[] original = bytes.ToArray(); var parsed = TrialWorkerTerminationAudit.Parse(bytes, fixture.Expected);
        Array.Fill(bytes, (byte)'x');
        ((TrialWorkerProcessEpoch[])fixture.Audit.Scope.Processes)[0] = fixture.Expected.Coordinator with { ProcessId = 999 };
        TrialWorkerTerminationAudit.ValidateBinding(parsed.Receipt, fixture.Expected);
        Assert.Equal(original, TrialWorkerTerminationAudit.Serialize(parsed.Receipt, fixture.Expected));
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateBinding(fixture.Audit, fixture.Expected));
        byte[] journalBytes = JournalBytes(fixture.Journal.Receipt);
        var journal = TrialJournal.ParseReadWithDigest(journalBytes, fixture.Expected.DeploymentId);
        Array.Fill(journalBytes, (byte)'x');
        TrialWorkerTerminationAudit.ValidateJournalBinding(journal, fixture.Expected);
        Assert.Equal(fixture.Journal.JournalSha256, journal.JournalSha256);
        Assert.False(journal.ProtectedFileHandleVerified);
    }

    [Theory]
    [InlineData("prepared")] [InlineData("commit_pending")] [InlineData("remove_pending")] [InlineData("removed")]
    public void ValidLegacyJournalPhasesCannotEnterInstalledOnlyPreparationGate(string phase)
    {
        var fixture = Fixture(); var receipt = fixture.Journal.Receipt with { Phase = phase, Installed = phase == "prepared" ? null : fixture.Journal.Receipt.Installed };
        var read = TrialJournal.ParseReadWithDigest(JournalBytes(receipt), receipt.DeploymentId);
        var expected = fixture.Expected with { Journal = fixture.Expected.Journal with { JournalSha256 = read.JournalSha256 } };
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateJournalBinding(read, expected));
    }

    [Theory]
    [InlineData("schema1")] [InlineData("schema4")] [InlineData("unsigned")]
    public void GenuineOtherRecoveryContractsStayOutsideThisFirstPreparationGate(string kind)
    {
        var fixture = Fixture(); TrialReceipt receipt;
        if (kind == "schema1")
        { var (baseline, installed) = TrialFixture.Create(); receipt = new(1, baseline.DeploymentId, baseline.MonitorPrincipal, "installed", baseline, installed, null); }
        else if (kind == "schema4") receipt = fixture.Journal.Receipt with { SchemaVersion = 4 };
        else receipt = fixture.Journal.Receipt with { Phase = "unsigned", UnsignedSnapshot = fixture.Journal.Receipt.Installed! with
            { Facts = TrialFixture.Order(fixture.Journal.Receipt.Installed!.Facts.Where(item => item.Kind != "signature").Select(item => item.Kind == "signature_validity" ? item with { Value = "0:0" } : item)) } };
        var read = TrialJournal.ParseReadWithDigest(JournalBytes(receipt), receipt.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateJournalBinding(read, fixture.Expected));
    }

    [Theory]
    [InlineData("coherent-guid")] [InlineData("coherent-certificates")] [InlineData("diagnostic-change")]
    public void ValidChangedJournalCannotReplacePinnedOriginalBytesOrImages(string change)
    {
        var fixture = Fixture(); var receipt = fixture.Journal.Receipt;
        if (change == "coherent-guid")
        {
            var identity = receipt.Baseline.Snapshot.Identity with { MasterDatabaseGuid = Nonce };
            receipt = receipt with { Baseline = new(receipt.Baseline.Snapshot with { Identity = identity }), Installed = receipt.Installed! with { Identity = identity } };
        }
        else if (change == "coherent-certificates") receipt = receipt with { Installed = receipt.Installed! with
            { Facts = TrialFixture.Order(receipt.Installed!.Facts.Select(item => item.Kind == "certificate" && item.Key == "public_sha256" ? item with { Value = new string('c', 64) } : item)) } };
        else receipt = receipt with { FailureCode = "explicit_recovery" };
        var read = TrialJournal.ParseReadWithDigest(JournalBytes(receipt), receipt.DeploymentId);
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.ValidateJournalBinding(read, fixture.Expected));
    }

    [Theory]
    [InlineData("")] [InlineData("Baseline")] [InlineData("Baseline.Snapshot.Identity")]
    [InlineData("Installed")] [InlineData("Installed.Monitor")]
    public void AdditiveJournalReaderRejectsClosedSerializationCorruption(string path)
    {
        var fixture = Fixture();
        foreach (string change in new[] { "missing", "unknown", "case", "duplicate" })
        {
            JsonObject root = JsonNode.Parse(JournalBytes(fixture.Journal.Receipt))!.AsObject(), node = ObjectAt(root, path);
            string key = node.First().Key; string json;
            if (change == "duplicate")
            {
                string literal = JsonSerializer.Serialize(key) + ":" + node[key]!.ToJsonString();
                json = root.ToJsonString().Replace(literal, literal + "," + literal, StringComparison.Ordinal);
            }
            else
            {
                if (change == "missing") node.Remove(key);
                else if (change == "unknown") node["Password"] = "private-canary";
                else { node[key.ToLowerInvariant()] = node[key]?.DeepClone(); node.Remove(key); }
                json = root.ToJsonString();
            }
            var error = Assert.Throws<InvalidDataException>(() => TrialJournal.ParseReadWithDigest(Encoding.UTF8.GetBytes(json), fixture.Expected.DeploymentId));
            Assert.Null(error.InnerException); Assert.DoesNotContain("private-canary", error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BoundsAndMissingEvidencePreserveWithoutReplayOrMutation()
    {
        var fixture = Fixture();
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.Parse(Array.Empty<byte>(), fixture.Expected));
        Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.Parse(new byte[TrialWorkerTerminationAudit.MaximumBytes + 1], fixture.Expected));
        Assert.Throws<InvalidDataException>(() => TrialJournal.ParseReadWithDigest(Array.Empty<byte>(), fixture.Expected.DeploymentId));
        Assert.Throws<InvalidDataException>(() => TrialJournal.ParseReadWithDigest(new byte[262_145], fixture.Expected.DeploymentId));
        Assert.Equal(TrialWorkerTerminationPreparationAction.PreserveForInspection,
            TrialWorkerTerminationAudit.SelectPreparationAction(null!, fixture.Expected, fixture.Journal));
        var audit = TrialWorkerTerminationAudit.Parse(TrialWorkerTerminationAudit.Serialize(fixture.Audit, fixture.Expected), fixture.Expected);
        Assert.Equal(TrialWorkerTerminationPreparationAction.PreserveForInspection,
            TrialWorkerTerminationAudit.SelectPreparationAction(audit, fixture.Expected, null!));
    }

    private static AuditFixture Fixture(bool temporaryMonitor = false)
    {
        var (baseline, signed) = TrialDenyFixture.Create(temporaryMonitor);
        TrialSnapshot MonitorIdentity(TrialSnapshot snapshot) => snapshot with
        {
            Monitor = snapshot.Monitor with { SidHex = MonitorHex },
            Objects = TrialFixture.Order(snapshot.Objects.Select(item => item.Kind == "monitor_user" ? item with { Detail = MonitorHex + ":WINDOWS" } : item)),
            Facts = TrialFixture.Order(snapshot.Facts.Select(item => item.Kind == "monitor_mapping" ? item with { Value = item.Value.Replace(snapshot.Monitor.SidHex, MonitorHex, StringComparison.Ordinal) } : item)),
        };
        baseline = new(MonitorIdentity(baseline.Snapshot)); signed = MonitorIdentity(signed);
        var receipt = new TrialReceipt(3, baseline.DeploymentId, baseline.MonitorPrincipal, "installed", baseline, signed, null, InstallationPolicyVersion: 2);
        var journal = TrialJournal.ParseReadWithDigest(JournalBytes(receipt), baseline.DeploymentId);
        var binding = new TrialWorkerJournalBinding(3, 2, "installed", journal.JournalSha256, journal.BaselineSha256, journal.InstalledSha256!);
        var supervisor = new TrialWorkerProcessEpoch(100, Time.AddTicks(7), new string('a', 64), AdminSid);
        var coordinator = new TrialWorkerProcessEpoch(101, Time.AddSeconds(1).AddTicks(7), new string('b', 64), AdminSid);
        var worker = new TrialWorkerProcessEpoch(102, Time.AddSeconds(2).AddTicks(7), new string('c', 64), MonitorSid);
        TrialWorkerProcessEpoch[] processes = [coordinator, worker];
        var sql = new TrialWorkerSqlDrainBinding(baseline.Snapshot.Identity.MasterDatabaseGuid, baseline.Snapshot.Identity.MsdbDatabaseGuid,
            Guid.Parse("66666666-6666-4666-8666-666666666666"), 60, new DateTime(2026, 10, 7, 3, 17, 18, DateTimeKind.Unspecified).AddMilliseconds(123),
            "datetime", 3, MonitorHex, Guid.Parse("77777777-7777-4777-8777-777777777777"), 61, AdminHex);
        var expected = new TrialWorkerTerminationAuditExpected(baseline.DeploymentId, FixtureId, Nonce, TrialWorkerTerminationAudit.ProducerContract,
            binding, supervisor, coordinator, worker, TrialWorkerTerminationAudit.ComputeProcessLedgerSha256(processes), processes.Length, sql);
        var scope = new TrialWorkerOwnedScopeProof(SupervisorOutsideBothScopes: true, PrivateNonInheritableJob: true, KillOnJobClose: true,
            BreakawayFlagsPermitted: false, BrokeredLaunchesPermitted: false, CoordinatorCreatedSuspended: true, WorkerCreatedSuspended: true,
            CoordinatorAssignedBeforeResume: true, WorkerAssignedBeforeResume: true, CoordinatorMembershipVerified: true, WorkerMembershipVerified: true,
            CoordinatorHeldHandleExitConfirmed: true, WorkerHeldHandleExitConfirmed: true, ActualPrimaryTokensVerified: true, NetCredentialsOnly: false,
            ActiveProcessesQuerySucceeded: true, ActiveProcesses: 0, UnknownOwnedProcesses: 0, HeldProcessExitsConfirmed: 2, Processes: processes);
        var drain = new TrialWorkerSqlDrainProof(sql, Time.AddSeconds(5).AddTicks(7), Time.AddSeconds(6).AddTicks(7), true, true, true, true, true, true, 0, 0, 0, false);
        var audit = new TrialWorkerTerminationAuditReceipt(1, TrialWorkerTerminationAudit.Kind, TrialWorkerTerminationAudit.ProducerContract,
            baseline.DeploymentId, FixtureId, Nonce, binding, supervisor, coordinator, worker, scope, drain,
            Time.AddSeconds(3).AddTicks(7), Time.AddSeconds(4).AddTicks(7), Time.AddSeconds(5).AddTicks(7), Time.AddSeconds(7).AddTicks(7),
            true, true, false, false, false, false, false, false, false, "Absent");
        return new(expected, audit, journal);
    }
    private static byte[] JournalBytes(TrialReceipt receipt) => JsonSerializer.SerializeToUtf8Bytes(receipt, JournalOptions);
    private static JsonObject AuditJson(AuditFixture fixture) => JsonNode.Parse(TrialWorkerTerminationAudit.Serialize(fixture.Audit, fixture.Expected))!.AsObject();
    private static void Refuses(JsonObject root, AuditFixture fixture)
    {
        var error = Assert.Throws<InvalidDataException>(() => TrialWorkerTerminationAudit.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()), fixture.Expected));
        Assert.Null(error.InnerException); Assert.DoesNotContain(AdminSid, error.ToString(), StringComparison.Ordinal);
    }
    private static (JsonObject Node, string Key) Member(JsonObject root, string path)
    { int separator = path.LastIndexOf('.'); return separator < 0 ? (root, path) : (ObjectAt(root, path[..separator]), path[(separator + 1)..]); }
    private static JsonObject ObjectAt(JsonObject root, string path)
    { JsonNode node = root; foreach (string key in path.Split('.', StringSplitOptions.RemoveEmptyEntries)) node = node[key]!; return node.AsObject(); }
    private sealed record AuditFixture(TrialWorkerTerminationAuditExpected Expected, TrialWorkerTerminationAuditReceipt Audit, TrialJournalReadDigest Journal);
}
