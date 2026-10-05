using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialReceipt(int SchemaVersion, Guid DeploymentId, string MonitorPrincipal,
    string Phase, TrialBaseline Baseline, TrialSnapshot? Installed, string? FailureCode);

/// <summary>Administrator/SYSTEM-only write-ahead record. Contains no signing password.</summary>
public sealed class TrialJournal
{
    private const int MaximumBytes = 262_144;
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };
    private readonly string directory;
    private readonly Guid deploymentId;

    private TrialJournal(string directory, Guid deploymentId)
    { this.directory = directory; this.deploymentId = deploymentId; }

    public string ReceiptPath => Path.Combine(directory, "receipt.json");

    public static TrialJournal Create(Guid deploymentId)
    {
        if (deploymentId == Guid.Empty) throw new ArgumentException("A deployment identity is required.", nameof(deploymentId));
        string root = Root();
        RejectReparseAncestors(root);
        if (!Directory.Exists(root)) CreateProtectedDirectory(root);
        ValidateDirectory(root);
        string path = Path.Combine(root, deploymentId.ToString("D"));
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("A trial journal already exists; use recovery.");
        CreateProtectedDirectory(path);
        ValidateDirectory(path);
        return new TrialJournal(path, deploymentId);
    }

    public static TrialJournal Open(Guid deploymentId)
    {
        if (deploymentId == Guid.Empty) throw new ArgumentException("A deployment identity is required.", nameof(deploymentId));
        string root = Root();
        ValidateDirectory(root);
        string path = Path.Combine(root, deploymentId.ToString("D"));
        ValidateDirectory(path);
        return new TrialJournal(path, deploymentId);
    }

    public TrialReceipt Read()
    {
        ValidateDirectory(directory);
        ValidateFile(ReceiptPath);
        byte[] bytes = File.ReadAllBytes(ReceiptPath);
        if (bytes.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The trial receipt size is invalid.");
        TrialReceipt receipt = JsonSerializer.Deserialize<TrialReceipt>(bytes, Options)
            ?? throw new InvalidDataException("The trial receipt is empty.");
        ValidateReceipt(receipt, deploymentId);
        return receipt;
    }

    public void Save(TrialReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateReceipt(receipt, deploymentId);
        ValidateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The trial receipt exceeds its bound.");
        if (File.Exists(ReceiptPath))
        {
            TrialReceipt previous = Read();
            ValidateTransition(previous, receipt);
        }
        else if (receipt.Phase != "prepared") throw new InvalidDataException("The first journal record must be prepared.");

        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            var security = new FileSecurity();
            security.SetOwner(Administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier sid in new[] { Administrators, System })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (FileStream stream = new FileInfo(temporary).Create(FileMode.CreateNew,
                FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            ValidateFile(temporary);
            if (File.Exists(ReceiptPath)) File.Replace(temporary, ReceiptPath, destinationBackupFileName: null);
            else File.Move(temporary, ReceiptPath);
            ValidateFile(ReceiptPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void ValidateReceipt(TrialReceipt receipt, Guid deploymentId)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != 1 || deploymentId == Guid.Empty || receipt.DeploymentId != deploymentId ||
            receipt.Baseline is null || string.IsNullOrWhiteSpace(receipt.MonitorPrincipal) ||
            receipt.Phase is not ("prepared" or "commit_pending" or "installed" or "remove_pending" or "removed"))
            throw new InvalidDataException("The trial receipt contract is invalid.");
        if (receipt.Phase is "commit_pending" or "installed" or "remove_pending" && receipt.Installed is null)
            throw new InvalidDataException("The trial receipt requires its exact installed postimage.");
        if (receipt.Baseline.Snapshot is null || receipt.Baseline.Snapshot.Monitor is null ||
            receipt.Baseline.DeploymentId != deploymentId ||
            !string.Equals(receipt.Baseline.MonitorPrincipal, receipt.MonitorPrincipal, StringComparison.Ordinal) ||
            receipt.Phase == "prepared" && receipt.Installed is not null ||
            receipt.FailureCode is { Length: > 256 })
            throw new InvalidDataException("The trial receipt provenance is invalid.");
        AgentActivityTrialSql.ValidateBaselineRestored(receipt.Baseline, receipt.Baseline);
        if (receipt.Installed is not null) AgentActivityTrialSql.ValidateInstalled(receipt.Baseline, receipt.Installed);
    }

    public static void ValidateTransition(TrialReceipt before, TrialReceipt after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        ValidateReceipt(before, before.DeploymentId); ValidateReceipt(after, before.DeploymentId);
        if (before.Installed is null && after.Installed is not null &&
            (before.Phase != "prepared" || after.Phase != "commit_pending"))
            throw new InvalidDataException("An installed postimage must be recorded before committing installation.");
        if (!string.Equals(before.MonitorPrincipal, after.MonitorPrincipal, StringComparison.Ordinal) ||
            !JsonSerializer.SerializeToUtf8Bytes(before.Baseline, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.Baseline, Options)) ||
            before.Installed is not null && !JsonSerializer.SerializeToUtf8Bytes(before.Installed, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.Installed, Options)))
            throw new InvalidDataException("Trial provenance cannot be changed.");
        bool allowed = before.Phase == after.Phase || (before.Phase, after.Phase) is
            ("prepared", "commit_pending") or ("prepared", "removed") or
            ("commit_pending", "installed") or ("commit_pending", "remove_pending") or ("commit_pending", "removed") or
            ("installed", "remove_pending") or ("installed", "removed") or ("remove_pending", "removed");
        if (!allowed) throw new InvalidDataException("The trial journal transition is invalid.");
    }

    private static string Root()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlObserverAgentActivityTrials");
    }

    private static void CreateProtectedDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier sid in new[] { Administrators, System })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    private static void ValidateDirectory(string path)
    {
        RejectReparseAncestors(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The protected trial directory is missing.");
        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
        ValidateAcl(security, isDirectory: true);
    }

    private static void ValidateFile(string path)
    {
        RejectReparseAncestors(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("The trial receipt file is invalid.");
        ValidateAcl(info.GetAccessControl(), isDirectory: false);
    }

    private static void ValidateAcl(FileSystemSecurity security, bool isDirectory)
    {
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        if (owner is null || !owner.Equals(Administrators) && !owner.Equals(System) || !security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The trial journal owner or inheritance is invalid.");
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count != 2) throw new UnauthorizedAccessException("The trial journal ACL is not exact.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.FileSystemRights != FileSystemRights.FullControl ||
                rule.IsInherited || rule.PropagationFlags != PropagationFlags.None ||
                rule.InheritanceFlags != (isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None) ||
                !rule.IdentityReference.Equals(Administrators) && !rule.IdentityReference.Equals(System) ||
                !seen.Add(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("The trial journal ACL is not exact.");
        }
    }

    private static void RejectReparseAncestors(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse paths are not permitted for trial evidence.");
            current = Path.GetDirectoryName(current);
        }
    }
}
