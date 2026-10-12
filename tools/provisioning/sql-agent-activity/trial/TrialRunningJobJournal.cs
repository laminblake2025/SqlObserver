using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.AgentActivityTrial;

public sealed record RunningJobAgentEpoch(string ServiceName, int ProcessId, DateTimeOffset ProcessStartedUtc,
    DateTimeOffset ServiceStartupUtc, int AgentSessionId, DateTime AgentStartedLocal);
public sealed record RunningJobReceipt(int SchemaVersion, Guid DeploymentId, Guid FixtureId, string Phase,
    RunningJobBaseline Baseline, RunningJobOwnership? Ownership = null, RunningJobAgentEpoch? AgentEpoch = null,
    RunningJobExecutionWitness? ExecutionWitness = null, string? FailureCode = null, string? BlockedFromPhase = null);
public enum RunningJobRecoveryAction { PreserveForInspection, AcknowledgeJobAbsent, AcknowledgeDatabaseAbsent, AlreadyClean }

/// <summary>Private write-ahead evidence. Pending actions are never permissions to repeat an action.</summary>
public sealed class TrialRunningJobJournal
{
    private const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private readonly string directory;
    private readonly Guid deploymentId;
    private readonly Guid fixtureId;
    private readonly SecurityIdentifier owner;
    private TrialRunningJobJournal(string directory, Guid deploymentId, Guid fixtureId, SecurityIdentifier owner)
    { this.directory = directory; this.deploymentId = deploymentId; this.fixtureId = fixtureId; this.owner = owner; }
    public string ReceiptPath => Path.Combine(directory, "receipt.json");

    public static TrialRunningJobJournal Create(Guid deploymentId, Guid fixtureId)
    {
        ValidateIds(deploymentId, fixtureId);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User ?? throw Invalid();
        string root = Root(); RejectReparse(root);
        if (!Directory.Exists(root)) CreateDirectory(root, owner);
        ValidateDirectory(root, owner);
        // An unresolved or unreadable sibling is a refusal, never an automatic recovery request.
        foreach (string sibling in Directory.EnumerateDirectories(root))
        {
            ValidateDirectory(sibling, owner);
            string name = Path.GetFileName(sibling);
            if (name.Length != 73 || name[36] != '_' || !Guid.TryParseExact(name[..36], "D", out Guid oldDeployment) ||
                !Guid.TryParseExact(name[37..], "D", out Guid oldFixture)) throw Invalid();
            var old = new TrialRunningJobJournal(sibling, oldDeployment, oldFixture, owner);
            if (old.Read().Phase != "cleanup_verified") throw Invalid();
        }
        if (Directory.EnumerateFiles(root).Any()) throw Invalid();
        string path = Path.Combine(root, deploymentId.ToString("D") + "_" + fixtureId.ToString("D"));
        if (Directory.Exists(path) || File.Exists(path)) throw Invalid();
        CreateDirectory(path, owner); ValidateDirectory(path, owner);
        return new(path, deploymentId, fixtureId, owner);
    }

    public static TrialRunningJobJournal Open(Guid deploymentId, Guid fixtureId)
    {
        ValidateIds(deploymentId, fixtureId);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier owner = identity.User ?? throw Invalid();
        string root = Root(); ValidateDirectory(root, owner);
        string path = Path.Combine(root, deploymentId.ToString("D") + "_" + fixtureId.ToString("D"));
        ValidateDirectory(path, owner);
        return new(path, deploymentId, fixtureId, owner);
    }

    public RunningJobReceipt Read()
    {
        ValidateDirectory(directory, owner); ValidateFile(ReceiptPath, owner);
        var info = new FileInfo(ReceiptPath);
        if (info.Length is <= 0 or > MaximumBytes) throw Invalid();
        byte[] bytes = File.ReadAllBytes(ReceiptPath);
        if (bytes.Length != info.Length) throw Invalid();
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        string[] expectedKeys = ["SchemaVersion", "DeploymentId", "FixtureId", "Phase", "Baseline", "Ownership", "AgentEpoch", "ExecutionWitness", "FailureCode", "BlockedFromPhase"];
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Select(item => item.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(expectedKeys.Order(StringComparer.Ordinal))) throw Invalid();
        RunningJobReceipt receipt = JsonSerializer.Deserialize<RunningJobReceipt>(bytes, Options) ?? throw Invalid();
        ValidateReceipt(receipt, deploymentId, fixtureId);
        return receipt;
    }

    public void Save(RunningJobReceipt receipt)
    {
        ValidateReceipt(receipt, deploymentId, fixtureId); ValidateDirectory(directory, owner);
        if (File.Exists(ReceiptPath)) ValidateTransition(Read(), receipt);
        else if (receipt.Phase != "prepared") throw Invalid();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        if (bytes.Length is <= 0 or > MaximumBytes) throw Invalid();
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            var security = new FileSecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier sid in new[] { owner, System })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (FileStream stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.WriteThrough, security))
            { stream.Write(bytes); stream.Flush(true); }
            ValidateFile(temporary, owner);
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw Invalid();
            if (File.Exists(ReceiptPath)) File.Replace(temporary, ReceiptPath, null);
            else File.Move(temporary, ReceiptPath);
            ValidateFile(ReceiptPath, owner);
            if (!File.ReadAllBytes(ReceiptPath).AsSpan().SequenceEqual(bytes)) throw Invalid();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void RecordPhase(string phase, RunningJobAgentEpoch? epoch = null, RunningJobExecutionWitness? execution = null)
    {
        RunningJobReceipt before = Read();
        Save(before with { Phase = phase, AgentEpoch = epoch ?? before.AgentEpoch, ExecutionWitness = execution ?? before.ExecutionWitness,
            BlockedFromPhase = phase == "cleanup_blocked" ? before.BlockedFromPhase ?? before.Phase : null });
    }

    public static void ValidateReceipt(RunningJobReceipt receipt, Guid deploymentId, Guid fixtureId)
    {
        ArgumentNullException.ThrowIfNull(receipt); ValidateIds(deploymentId, fixtureId);
        if (receipt.SchemaVersion != 1 || receipt.DeploymentId != deploymentId || receipt.FixtureId != fixtureId ||
            !Phases.Contains(receipt.Phase, StringComparer.Ordinal) || receipt.FailureCode is { Length: > 128 }) throw Invalid();
        if (receipt.Phase == "cleanup_blocked" ? receipt.BlockedFromPhase is null || receipt.BlockedFromPhase == "cleanup_blocked" ||
            !Phases.Contains(receipt.BlockedFromPhase, StringComparer.Ordinal) : receipt.BlockedFromPhase is not null) throw Invalid();
        TrialRunningJobWitness.ValidateBaseline(receipt.Baseline);
        if (receipt.Baseline.FixtureId != fixtureId || receipt.Baseline.ModuleBaseline.DeploymentId != deploymentId) throw Invalid();
        if (receipt.Ownership is not null)
        {
            TrialRunningJobWitness.ValidateOwnership(receipt.Ownership, allowDatabaseOnly: true);
            if (!Same(receipt.Baseline, receipt.Ownership.Baseline)) throw Invalid();
        }
        if (receipt.Phase is "prepared" or "database_create_pending" && receipt.Ownership is not null) throw Invalid();
        if (receipt.Phase is not ("prepared" or "database_create_pending" or "cleanup_blocked") && receipt.Ownership is null) throw Invalid();
        if (receipt.Ownership is { JobId: var job } && job == Guid.Empty && receipt.Phase is not
            ("database_created" or "job_create_pending" or "database_drop_pending" or "database_absent" or "cleanup_verified" or "cleanup_blocked")) throw Invalid();
        if (receipt.AgentEpoch is { } epoch)
        {
            string expectedService = receipt.Baseline.ModuleBaseline.Snapshot.Identity.InstanceName.Length == 0 ? "SQLSERVERAGENT" :
                "SQLAgent$" + receipt.Baseline.ModuleBaseline.Snapshot.Identity.InstanceName;
            if (string.IsNullOrWhiteSpace(epoch.ServiceName) || epoch.ServiceName.Length > 128 || epoch.ServiceName.Any(char.IsControl) ||
                epoch.ServiceName != expectedService ||
                epoch.ProcessId <= 0 || epoch.ProcessStartedUtc.Offset != TimeSpan.Zero || epoch.ServiceStartupUtc.Offset != TimeSpan.Zero ||
                epoch.ProcessStartedUtc.Year < 1753 || epoch.ServiceStartupUtc.Year < 1753 || epoch.AgentSessionId <= receipt.Baseline.OriginalAgentSessionId ||
                epoch.AgentStartedLocal.Kind != DateTimeKind.Unspecified || epoch.AgentStartedLocal.Year < 1753) throw Invalid();
        }
        if (receipt.Phase is "agent_running" or "job_start_pending" or "running_witness" or "running_capture_verified" or "release_pending" or
            "completed_witness" or "completed_capture_verified" or "agent_stop_pending" or "agent_stopped" && receipt.AgentEpoch is null) throw Invalid();
        if (receipt.Phase is "prepared" or "database_create_pending" or "database_created" or "job_create_pending" or "job_created" &&
            (receipt.AgentEpoch is not null || receipt.ExecutionWitness is not null)) throw Invalid();
        if (receipt.Phase is "running_witness" or "running_capture_verified" or "completed_witness" or "completed_capture_verified" && receipt.ExecutionWitness is null) throw Invalid();
        if (receipt.ExecutionWitness is { } execution)
        {
            if (receipt.Ownership is null || execution.FixtureId != fixtureId || execution.JobId != receipt.Ownership.JobId ||
                execution.ExecutionNonce != receipt.Baseline.ExecutionNonce || execution.DatabaseId != receipt.Ownership.DatabaseId ||
                execution.DatabaseGuid != receipt.Ownership.DatabaseGuid) throw Invalid();
            TrialRunningJobWitness.ValidateExecutionWitness(receipt.Ownership, execution);
            if (receipt.AgentEpoch is null || execution.AgentSessionId != receipt.AgentEpoch.AgentSessionId || execution.AgentStartedLocal != receipt.AgentEpoch.AgentStartedLocal) throw Invalid();
            if (receipt.Phase is "running_witness" or "running_capture_verified" &&
                (!execution.LiveOwnedRequest || execution.Released || execution.StepStartedUtc is null || execution.StepCompletedUtc is not null ||
                    execution.Activity is not { StartExecutionLocal: not null, StopExecutionLocal: null })) throw Invalid();
            if (receipt.Phase is "completed_witness" or "completed_capture_verified" &&
                (execution.LiveOwnedRequest || !execution.Released || execution.StepCompletedUtc is null || !execution.SuccessfulHistory ||
                    execution.Activity is not { StartExecutionLocal: not null, StopExecutionLocal: not null })) throw Invalid();
            if (receipt.Phase is "job_delete_pending" or "job_absent" or "database_drop_pending" or "database_absent" or
                "agent_stop_pending" or "agent_stopped" or "cleanup_verified") TrialRunningJobWitness.ValidateCleanupExecution(receipt.Ownership, execution);
        }
    }

    public static void ValidateTransition(RunningJobReceipt before, RunningJobReceipt after)
    {
        ValidateReceipt(before, before.DeploymentId, before.FixtureId); ValidateReceipt(after, before.DeploymentId, before.FixtureId);
        if (!Same(before.Baseline, after.Baseline) || before.AgentEpoch is not null && !Same(before.AgentEpoch, after.AgentEpoch) ||
            before.Ownership is not null && after.Ownership is null) throw Invalid();
        if (before.Ownership is { } old && after.Ownership is { } next && !Same(old, next))
        {
            if (old.JobId != Guid.Empty || next.JobId == Guid.Empty || before.Phase != "job_create_pending" || after.Phase != "job_create_pending" ||
                !Same(old with { JobId = next.JobId, JobDefinitionSha256 = next.JobDefinitionSha256, StepCommandSha256 = next.StepCommandSha256 }, next)) throw Invalid();
        }
        if (before.Phase == "cleanup_verified" && !Same(before, after)) throw Invalid();
        if (after.Phase == "cleanup_blocked" && after.BlockedFromPhase != (before.BlockedFromPhase ?? before.Phase)) throw Invalid();
        if (before.Phase == after.Phase || after.Phase == "cleanup_blocked") return;
        int first = Array.IndexOf(Phases, before.Phase), second = Array.IndexOf(Phases, after.Phase);
        bool normal = second == first + 1 && before.Phase != "cleanup_blocked";
        bool earlyCleanup = after.Phase == "job_delete_pending" && before.Phase is "job_created" or "agent_running" or "completed_witness" or "completed_capture_verified" ||
            after.Phase == "database_drop_pending" && before.Phase is "database_created" or "job_absent" ||
            after.Phase == "cleanup_verified" && before.Phase == "database_absent" && before.AgentEpoch is null;
        if (!normal && !earlyCleanup) throw Invalid();
    }

    public static RunningJobRecoveryAction SelectRecoveryAction(RunningJobReceipt receipt, bool exactJobAbsent, bool exactDatabaseAbsent)
    {
        ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId);
        if (receipt.Phase == "cleanup_verified" && exactJobAbsent && exactDatabaseAbsent) return RunningJobRecoveryAction.AlreadyClean;
        string phase = receipt.BlockedFromPhase ?? receipt.Phase;
        if (phase == "job_delete_pending" && exactJobAbsent) return RunningJobRecoveryAction.AcknowledgeJobAbsent;
        if (phase == "database_drop_pending" && exactDatabaseAbsent && exactJobAbsent) return RunningJobRecoveryAction.AcknowledgeDatabaseAbsent;
        return RunningJobRecoveryAction.PreserveForInspection;
    }

    private static readonly string[] Phases = ["prepared", "database_create_pending", "database_created", "job_create_pending", "job_created",
        "agent_start_pending", "agent_running", "job_start_pending", "running_witness", "running_capture_verified", "release_pending",
        "completed_witness", "completed_capture_verified", "job_delete_pending", "job_absent", "database_drop_pending", "database_absent",
        "agent_stop_pending", "agent_stopped", "cleanup_verified", "cleanup_blocked"];
    private static bool Same<T>(T first, T second) => JsonSerializer.SerializeToUtf8Bytes(first, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second, Options));
    private static void ValidateIds(Guid deploymentId, Guid fixtureId)
    { if (!OperatingSystem.IsWindows() || deploymentId == Guid.Empty || fixtureId == Guid.Empty) throw Invalid(); }
    private static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlObserver", "AgentActivityRunningTrial");
    private static InvalidDataException Invalid() => new("The private running-job ownership journal is invalid or unresolved; values withheld.");
    private static void RejectReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid(); current = Path.GetDirectoryName(current); }
    }
    private static void CreateDirectory(string path, SecurityIdentifier owner)
    {
        var security = new DirectorySecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
        foreach (SecurityIdentifier sid in new[] { owner, System }) security.AddAccessRule(new FileSystemAccessRule(sid,
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
    private static void ValidateDirectory(string path, SecurityIdentifier owner)
    {
        RejectReparse(path); if (!Directory.Exists(path)) throw Invalid();
        var security = new DirectoryInfo(path).GetAccessControl();
        ValidateAcl(security, owner, directory: true);
    }
    private static void ValidateFile(string path, SecurityIdentifier owner)
    {
        RejectReparse(path); if (!File.Exists(path)) throw Invalid();
        ValidateAcl(new FileInfo(path).GetAccessControl(), owner, directory: false);
    }
    private static void ValidateAcl(FileSystemSecurity security, SecurityIdentifier owner, bool directory)
    {
        if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw Invalid();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count != 2 || rules.Cast<FileSystemAccessRule>().Any(rule => rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
            rule.FileSystemRights != FileSystemRights.FullControl || !rule.IdentityReference.Equals(owner) && !rule.IdentityReference.Equals(System) ||
            rule.InheritanceFlags != (directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None) ||
            rule.PropagationFlags != PropagationFlags.None)) throw Invalid();
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
