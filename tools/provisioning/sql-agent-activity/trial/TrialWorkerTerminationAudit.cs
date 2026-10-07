using System.Buffers.Binary;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialWorkerProcessEpoch(int ProcessId, DateTimeOffset StartedUtc, string ImageSha256, string TokenSid)
{ public override string ToString() => "Private held worker process epoch"; }
public sealed record TrialWorkerJournalBinding(int SchemaVersion, int InstallationPolicyVersion, string Phase,
    string JournalSha256, string BaselineSha256, string InstalledSha256)
{ public override string ToString() => "Private original signed journal binding"; }
/// <summary>SQL connection IDs are server IDs, never SqlClient ClientConnectionId. The native
/// SQL connect_time is preserved as an offset-free local value, never relabelled UTC.</summary>
public sealed record TrialWorkerSqlDrainBinding(Guid MasterDatabaseGuid, Guid MsdbDatabaseGuid,
    Guid OriginalMonitorSqlConnectionId, int OriginalMonitorSessionId, DateTime OriginalMonitorConnectedLocal,
    string OriginalMonitorConnectTimeSqlType, int OriginalMonitorConnectTimeScale,
    string MonitorSidHex, Guid ObserverSqlConnectionId, int ObserverSessionId, string ObserverSidHex)
{ public override string ToString() => "Private independent physical SQL drain binding"; }
public sealed record TrialWorkerOwnedScopeProof(bool SupervisorOutsideBothScopes, bool PrivateNonInheritableJob,
    bool KillOnJobClose, bool BreakawayFlagsPermitted, bool BrokeredLaunchesPermitted,
    bool CoordinatorCreatedSuspended, bool WorkerCreatedSuspended, bool CoordinatorAssignedBeforeResume,
    bool WorkerAssignedBeforeResume, bool CoordinatorMembershipVerified, bool WorkerMembershipVerified,
    bool CoordinatorHeldHandleExitConfirmed, bool WorkerHeldHandleExitConfirmed,
    bool ActualPrimaryTokensVerified, bool NetCredentialsOnly, bool ActiveProcessesQuerySucceeded,
    int ActiveProcesses, int UnknownOwnedProcesses, int HeldProcessExitsConfirmed,
    IReadOnlyList<TrialWorkerProcessEpoch> Processes)
{ public override string ToString() => "Private two-root owned native scope proof"; }
/// <summary>Before/after are the independent supervisor's native UTC observation bracket.
/// They do not assign UTC to SQL connect_time or infer elapsed execution.</summary>
public sealed record TrialWorkerSqlDrainProof(TrialWorkerSqlDrainBinding Binding, DateTimeOffset BeforeUtc, DateTimeOffset AfterUtc,
    bool PhysicalIdentityVerified, bool SysadminVisibilityVerified, bool SameObserverSessionVerified,
    bool ReadTransactionRollbackAcknowledged, bool OriginalConnectionGone, bool OriginalRequestsGone,
    int LiveMonitorSessions, int LiveMonitorRequests, int UnknownRelevantSessions, bool SqlKillIssued)
{ public override string ToString() => "Private independent zero-live-monitor SQL observation"; }
public sealed record TrialWorkerTerminationAuditReceipt(int SchemaVersion, string Kind, string ProducerContract,
    Guid DeploymentId, Guid FixtureId, Guid LaunchNonce, TrialWorkerJournalBinding Journal,
    TrialWorkerProcessEpoch Supervisor, TrialWorkerProcessEpoch Coordinator, TrialWorkerProcessEpoch Worker,
    TrialWorkerOwnedScopeProof Scope, TrialWorkerSqlDrainProof SqlDrain, DateTimeOffset SourceReadCompletedAtUtc,
    DateTimeOffset InterruptionRequestedAtUtc, DateTimeOffset ScopeTerminatedAtUtc, DateTimeOffset RecordedAtUtc,
    bool SourceQueryCompleted, bool InterruptedBeforeFinalAcceptance, bool CaptureAccepted,
    bool PrototypeOnly, bool RecoveryAuthority, bool QualificationReplayed, bool RecoveryMutationStarted,
    bool AgentStarted, bool JobOrDatabaseMutationStarted, string NativeQualification)
{ public override string ToString() => "Private non-authorizing worker termination audit"; }

/// <summary>Immutable expectations must come from independently held trusted supervisor/process
/// handles, its closed launch ledger, the original protected journal and independent physical SQL
/// observation. Never construct these expectations by copying worker/audit fields. Digests alone
/// cannot establish that provenance. Prototype launch observations are not acceptable sources.</summary>
public sealed record TrialWorkerTerminationAuditExpected(Guid DeploymentId, Guid FixtureId, Guid LaunchNonce,
    string ProducerContract, TrialWorkerJournalBinding Journal, TrialWorkerProcessEpoch Supervisor,
    TrialWorkerProcessEpoch Coordinator, TrialWorkerProcessEpoch Worker, string ProcessLedgerSha256,
    int OwnedProcessCount, TrialWorkerSqlDrainBinding SqlDrain)
{ public override string ToString() => "Private independently held audit expectations"; }

public sealed class TrialWorkerTerminationAuditRead
{
    internal TrialWorkerTerminationAuditRead(TrialWorkerTerminationAuditReceipt receipt, string sha256, int byteCount, bool protectedFileHandleVerified)
    { Receipt = receipt; Sha256 = sha256; ByteCount = byteCount; ProtectedFileHandleVerified = protectedFileHandleVerified; }
    public TrialWorkerTerminationAuditReceipt Receipt { get; }
    public string Sha256 { get; }
    public int ByteCount { get; }
    public bool ProtectedFileHandleVerified { get; }
    public override string ToString() => "Private bounded worker audit read";
}
public enum TrialWorkerTerminationPreparationAction { PreserveForInspection, EligibleForIndependentSqlValidation }

/// <summary>Preparation gate only. No method connects to SQL, mutates a journal, launches/stops a
/// process, authorizes recovery or replays qualification. Protected origins are established only
/// by held-file ACL reads; pure parsing cannot manufacture either origin. Even eligible evidence
/// still requires the future independently reviewed SQL/lock/session/live-image gateway.</summary>
public static class TrialWorkerTerminationAudit
{
    public const int MaximumBytes = 65_536;
    public const string Kind = "OwnedNativeLaunchScope";
    public const string ProducerContract = "actual-monitor-two-root-interruption-v1";
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize(TrialWorkerTerminationAuditReceipt receipt, TrialWorkerTerminationAuditExpected expected)
    {
        ValidateBinding(receipt, expected);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        if (bytes.Length is <= 0 or > MaximumBytes) { CryptographicOperations.ZeroMemory(bytes); throw Invalid(); }
        return bytes;
    }

    /// <summary>Pure parsing exercises bindings only; it does not authenticate an owner or
    /// material supervisor provenance and cannot produce protected-file eligibility.</summary>
    public static TrialWorkerTerminationAuditRead Parse(ReadOnlyMemory<byte> input, TrialWorkerTerminationAuditExpected expected)
        => ParseCore(input, expected, protectedFileHandleVerified: false);

    /// <summary>The caller independently selects this supervisor-owned artifact path. Both its
    /// parent and the opened file require the exact expected individual token SID plus SYSTEM,
    /// protected ACLs, and no reparse ancestor. No worker-supplied path is accepted as authority.</summary>
    public static TrialWorkerTerminationAuditRead ReadProtected(string path, TrialWorkerTerminationAuditExpected expected)
    {
        ValidateExpected(expected);
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) throw Invalid();
        string full = Path.GetFullPath(path), parent = Path.GetDirectoryName(full) ?? throw Invalid();
        RejectReparse(full);
        var owner = new SecurityIdentifier(expected.Supervisor.TokenSid);
        if (!owner.IsAccountSid() || owner.Equals(SystemSid())) throw Invalid();
        ValidateAcl(new DirectoryInfo(parent).GetAccessControl(), owner, directory: true);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateAcl(stream.GetAccessControl(), owner, directory: false);
        if (stream.Length is <= 0 or > MaximumBytes) throw Invalid();
        byte[] bytes = new byte[checked((int)stream.Length)];
        try
        {
            stream.ReadExactly(bytes); if (stream.ReadByte() != -1) throw Invalid();
            return ParseCore(bytes, expected, protectedFileHandleVerified: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static TrialWorkerTerminationAuditRead ParseCore(ReadOnlyMemory<byte> input, TrialWorkerTerminationAuditExpected expected, bool protectedFileHandleVerified)
    {
        if (input.Length is <= 0 or > MaximumBytes) throw Invalid();
        byte[] bytes = input.ToArray();
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicates(document.RootElement);
            TrialWorkerTerminationAuditReceipt receipt = JsonSerializer.Deserialize<TrialWorkerTerminationAuditReceipt>(bytes, Options) ?? throw Invalid();
            ValidateBinding(receipt, expected);
            using JsonDocument canonical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(receipt, Options));
            if (!JsonElement.DeepEquals(document.RootElement, canonical.RootElement)) throw Invalid();
            receipt = receipt with { Scope = receipt.Scope with { Processes = Array.AsReadOnly(receipt.Scope.Processes.ToArray()) } };
            return new(receipt, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, protectedFileHandleVerified);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Pure structural/provenance comparison. Calling this does not authenticate
    /// independent expectation sources or protected artifacts.</summary>
    public static void ValidateBinding(TrialWorkerTerminationAuditReceipt receipt, TrialWorkerTerminationAuditExpected expected)
    {
        ValidateExpected(expected);
        if (receipt is null || receipt.SchemaVersion != 1 || receipt.Kind != Kind || receipt.ProducerContract != ProducerContract ||
            receipt.DeploymentId != expected.DeploymentId || receipt.FixtureId != expected.FixtureId || receipt.LaunchNonce != expected.LaunchNonce ||
            receipt.Journal != expected.Journal || receipt.Supervisor != expected.Supervisor || receipt.Coordinator != expected.Coordinator ||
            receipt.Worker != expected.Worker || receipt.SqlDrain is null || receipt.SqlDrain.Binding != expected.SqlDrain ||
            receipt.Scope is null || !receipt.SourceQueryCompleted || !receipt.InterruptedBeforeFinalAcceptance || receipt.CaptureAccepted ||
            receipt.PrototypeOnly || receipt.RecoveryAuthority || receipt.QualificationReplayed || receipt.RecoveryMutationStarted ||
            receipt.AgentStarted || receipt.JobOrDatabaseMutationStarted || receipt.NativeQualification != "Absent") throw Invalid();
        TrialWorkerOwnedScopeProof scope = receipt.Scope;
        if (!scope.SupervisorOutsideBothScopes || !scope.PrivateNonInheritableJob || !scope.KillOnJobClose || scope.BreakawayFlagsPermitted ||
            scope.BrokeredLaunchesPermitted || !scope.CoordinatorCreatedSuspended || !scope.WorkerCreatedSuspended ||
            !scope.CoordinatorAssignedBeforeResume || !scope.WorkerAssignedBeforeResume || !scope.CoordinatorMembershipVerified || !scope.WorkerMembershipVerified ||
            !scope.CoordinatorHeldHandleExitConfirmed || !scope.WorkerHeldHandleExitConfirmed ||
            !scope.ActualPrimaryTokensVerified || scope.NetCredentialsOnly || !scope.ActiveProcessesQuerySucceeded || scope.ActiveProcesses != 0 ||
            scope.UnknownOwnedProcesses != 0 || scope.Processes is null || scope.Processes.Count != expected.OwnedProcessCount ||
            scope.HeldProcessExitsConfirmed != expected.OwnedProcessCount || ComputeProcessLedgerSha256(scope.Processes) != expected.ProcessLedgerSha256 ||
            scope.Processes.Count(item => item == expected.Coordinator) != 1 || scope.Processes.Count(item => item == expected.Worker) != 1 ||
            scope.Processes.Any(item => item.ProcessId == expected.Supervisor.ProcessId)) throw Invalid();
        TrialWorkerSqlDrainProof sql = receipt.SqlDrain;
        if (!sql.PhysicalIdentityVerified || !sql.SysadminVisibilityVerified || !sql.SameObserverSessionVerified ||
            !sql.ReadTransactionRollbackAcknowledged || !sql.OriginalConnectionGone || !sql.OriginalRequestsGone ||
            sql.LiveMonitorSessions != 0 || sql.LiveMonitorRequests != 0 || sql.UnknownRelevantSessions != 0 || sql.SqlKillIssued) throw Invalid();
        Utc(receipt.SourceReadCompletedAtUtc); Utc(receipt.InterruptionRequestedAtUtc); Utc(receipt.ScopeTerminatedAtUtc); Utc(receipt.RecordedAtUtc);
        Utc(sql.BeforeUtc); Utc(sql.AfterUtc);
        if (expected.Supervisor.StartedUtc > expected.Coordinator.StartedUtc || expected.Supervisor.StartedUtc > expected.Worker.StartedUtc ||
            expected.Coordinator.StartedUtc > receipt.SourceReadCompletedAtUtc || expected.Worker.StartedUtc > receipt.SourceReadCompletedAtUtc ||
            scope.Processes.Any(item => item.StartedUtc < expected.Supervisor.StartedUtc || item.StartedUtc > receipt.InterruptionRequestedAtUtc) ||
            receipt.SourceReadCompletedAtUtc > receipt.InterruptionRequestedAtUtc || receipt.InterruptionRequestedAtUtc > receipt.ScopeTerminatedAtUtc ||
            receipt.ScopeTerminatedAtUtc > sql.BeforeUtc || sql.BeforeUtc > sql.AfterUtc || sql.AfterUtc > receipt.RecordedAtUtc ||
            sql.AfterUtc - sql.BeforeUtc > TimeSpan.FromSeconds(10)) throw Invalid();
    }

    public static TrialWorkerTerminationPreparationAction SelectPreparationAction(TrialWorkerTerminationAuditRead audit,
        TrialWorkerTerminationAuditExpected expected, TrialJournalReadDigest journal)
    {
        try
        {
            if (audit is null || journal is null) return TrialWorkerTerminationPreparationAction.PreserveForInspection;
            ValidateBinding(audit.Receipt, expected);
            ValidateJournalBinding(journal, expected);
            if (!audit.ProtectedFileHandleVerified || !journal.ProtectedFileHandleVerified)
                return TrialWorkerTerminationPreparationAction.PreserveForInspection;
            return TrialWorkerTerminationPreparationAction.EligibleForIndependentSqlValidation;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException or FormatException or OverflowException)
        { return TrialWorkerTerminationPreparationAction.PreserveForInspection; }
    }

    /// <summary>Pure same-byte journal binding check. It cannot establish an owner or SQL
    /// ownership and does not turn a parsed byte result into protected provenance.</summary>
    public static void ValidateJournalBinding(TrialJournalReadDigest journal, TrialWorkerTerminationAuditExpected expected)
    {
        ValidateExpected(expected);
        if (journal is null) throw Invalid();
        TrialJournal.ValidateDigestCapsule(journal);
        TrialJournal.ValidateReceipt(journal.Receipt, expected.DeploymentId);
        if (journal.Receipt.SchemaVersion != 3 || journal.Receipt.InstallationPolicyVersion != 2 || journal.Receipt.Phase != "installed" ||
            journal.Receipt.Installed is null || journal.Receipt.UnsignedSnapshot is not null || journal.Receipt.DeniedSnapshot is not null ||
            journal.JournalSha256 != expected.Journal.JournalSha256 || journal.BaselineSha256 != expected.Journal.BaselineSha256 ||
            journal.InstalledSha256 != expected.Journal.InstalledSha256 ||
            journal.Receipt.Baseline.Snapshot.Identity.MasterDatabaseGuid != expected.SqlDrain.MasterDatabaseGuid ||
            journal.Receipt.Baseline.Snapshot.Identity.MsdbDatabaseGuid != expected.SqlDrain.MsdbDatabaseGuid ||
            !string.Equals(journal.Receipt.Baseline.Snapshot.Monitor.SidHex, expected.SqlDrain.MonitorSidHex, StringComparison.OrdinalIgnoreCase)) throw Invalid();
    }

    public static string ComputeProcessLedgerSha256(IReadOnlyList<TrialWorkerProcessEpoch> processes)
    {
        if (processes is null || processes.Count is < 2 or > 32) throw Invalid();
        int previous = 0;
        foreach (TrialWorkerProcessEpoch process in processes)
        { Epoch(process); if (process.ProcessId <= previous) throw Invalid(); previous = process.ProcessId; }
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(processes, Options)));
    }

    private static void ValidateExpected(TrialWorkerTerminationAuditExpected expected)
    {
        if (expected is null || expected.DeploymentId == Guid.Empty || expected.FixtureId == Guid.Empty || expected.LaunchNonce == Guid.Empty ||
            expected.DeploymentId == expected.FixtureId || expected.DeploymentId == expected.LaunchNonce || expected.FixtureId == expected.LaunchNonce ||
            expected.ProducerContract != ProducerContract || expected.Journal is null || expected.SqlDrain is null ||
            expected.Journal.SchemaVersion != 3 || expected.Journal.InstallationPolicyVersion != 2 || expected.Journal.Phase != "installed" ||
            !Digest(expected.Journal.JournalSha256) || !Digest(expected.Journal.BaselineSha256) || !Digest(expected.Journal.InstalledSha256) ||
            !Digest(expected.ProcessLedgerSha256) || expected.OwnedProcessCount is < 2 or > 32) throw Invalid();
        Epoch(expected.Supervisor); Epoch(expected.Coordinator); Epoch(expected.Worker);
        if (expected.Supervisor.ProcessId == expected.Coordinator.ProcessId || expected.Supervisor.ProcessId == expected.Worker.ProcessId ||
            expected.Coordinator.ProcessId == expected.Worker.ProcessId || expected.Supervisor.TokenSid != expected.Coordinator.TokenSid ||
            expected.Supervisor.TokenSid == expected.Worker.TokenSid) throw Invalid();
        TrialWorkerSqlDrainBinding sql = expected.SqlDrain;
        if (sql.MasterDatabaseGuid == Guid.Empty || sql.MsdbDatabaseGuid == Guid.Empty || sql.MasterDatabaseGuid == sql.MsdbDatabaseGuid ||
            sql.OriginalMonitorSqlConnectionId == Guid.Empty || sql.ObserverSqlConnectionId == Guid.Empty ||
            sql.OriginalMonitorSqlConnectionId == sql.ObserverSqlConnectionId || sql.OriginalMonitorSessionId is <= 0 or > 32767 ||
            sql.ObserverSessionId is <= 0 or > 32767 || sql.ObserverSessionId == sql.OriginalMonitorSessionId ||
            sql.OriginalMonitorConnectedLocal.Kind != DateTimeKind.Unspecified || sql.OriginalMonitorConnectedLocal.Year < 1753 ||
            sql.OriginalMonitorConnectTimeSqlType != "datetime" || sql.OriginalMonitorConnectTimeScale != 3 ||
            sql.MonitorSidHex != SidHex(expected.Worker.TokenSid) || sql.ObserverSidHex != SidHex(expected.Supervisor.TokenSid)) throw Invalid();
    }
    private static void Epoch(TrialWorkerProcessEpoch process)
    {
        if (process is null || process.ProcessId <= 0 || !Digest(process.ImageSha256)) throw Invalid();
        Utc(process.StartedUtc); _ = SidHex(process.TokenSid);
    }
    private static void Utc(DateTimeOffset value) { if (value.Offset != TimeSpan.Zero || value.Year < 1753) throw Invalid(); }
    private static bool Digest(string? value) => value is { Length: 64 } && value.Any(character => character != '0') &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string SidHex(string value)
    {
        if (value is null || value.Length > 184) throw Invalid();
        string[] parts = value.Split('-');
        if (parts.Length is < 4 or > 18 || parts[0] != "S" || parts[1] != "1") throw Invalid();
        byte[] bytes = new byte[8 + (parts.Length - 3) * 4]; bytes[0] = 1; bytes[1] = checked((byte)(parts.Length - 3));
        for (int i = 2; i < parts.Length; i++)
        {
            if (!ulong.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out ulong number) ||
                number > (i == 2 ? 281474976710655UL : uint.MaxValue) || number.ToString(CultureInfo.InvariantCulture) != parts[i]) throw Invalid();
            if (i == 2) for (int offset = 0; offset < 6; offset++) bytes[7 - offset] = (byte)(number >> (offset * 8));
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (i - 3) * 4), (uint)number);
        }
        // The actual monitor and supervisor must be individual Windows account SIDs.
        if (parts.Length != 8 || parts[2] != "5" || parts[3] != "21") throw Invalid();
        return Convert.ToHexStringLower(bytes);
    }
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, MaxDepth = 16, PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
        options.Converters.Add(new UtcConverter()); options.Converters.Add(new LocalConverter()); return options;
    }
    private sealed class UtcConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (!DateTimeOffset.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value) ||
                value.ToString(Format, CultureInfo.InvariantCulture) != text) throw new JsonException();
            Utc(value); return value;
        }
        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        { Utc(value); writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture)); }
    }
    private sealed class LocalConverter : JsonConverter<DateTime>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (!DateTime.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime value) ||
                value.Kind != DateTimeKind.Unspecified || value.Year < 1753 || value.ToString(Format, CultureInfo.InvariantCulture) != text) throw new JsonException();
            return value;
        }
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        { if (value.Kind != DateTimeKind.Unspecified || value.Year < 1753) throw Invalid(); writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture)); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement value in element.EnumerateArray()) RejectDuplicates(value);
    }
    private static void RejectReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid(); current = Path.GetDirectoryName(current); }
    }
    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);
    private static void ValidateAcl(FileSystemSecurity security, SecurityIdentifier owner, bool directory)
    {
        if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !seen.Add(sid.Value) || rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl ||
                !sid.Equals(owner) && !sid.Equals(SystemSid()) || rule.PropagationFlags != PropagationFlags.None ||
                rule.InheritanceFlags != (directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None)) throw Invalid();
        }
        if (!seen.SetEquals([owner.Value, SystemSid().Value])) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Private worker termination preparation evidence is invalid; values withheld.");
}
