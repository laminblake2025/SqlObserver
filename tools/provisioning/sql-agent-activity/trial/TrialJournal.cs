using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialReceipt(int SchemaVersion, Guid DeploymentId, string MonitorPrincipal,
    string Phase, TrialBaseline Baseline, TrialSnapshot? Installed, string? FailureCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrialSnapshot? UnsignedSnapshot = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? InstallationPolicyVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TrialSnapshot? DeniedSnapshot = null);

public enum TrialDenyRecoveryAction { BaselineRestored, RemoveSigned, RestoreThenRemove }

/// <summary>Digest and parsed metadata from the same bounded bytes. Pure parsing does not
/// authenticate a filesystem owner; only ReadWithDigest verifies the held file's ACL.</summary>
public sealed class TrialJournalReadDigest
{
    internal TrialJournalReadDigest(TrialReceipt receipt, string journalSha256, string baselineSha256,
        string? installedSha256, int byteCount, bool protectedFileHandleVerified)
    { Receipt = receipt; JournalSha256 = journalSha256; BaselineSha256 = baselineSha256;
        InstalledSha256 = installedSha256; ByteCount = byteCount; ProtectedFileHandleVerified = protectedFileHandleVerified; }
    public TrialReceipt Receipt { get; }
    public string JournalSha256 { get; }
    public string BaselineSha256 { get; }
    public string? InstalledSha256 { get; }
    public int ByteCount { get; }
    public bool ProtectedFileHandleVerified { get; }
    public override string ToString() => "Private same-byte trial journal read";
}

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

    /// <summary>Additive qualified-reader seam. The owner/ACL is checked on the opened
    /// FileStream handle; that same bounded stream supplies both parsing and hashing.
    /// This method does not authorize recovery or change legacy Read behavior.</summary>
    public TrialJournalReadDigest ReadWithDigest()
    {
        ValidateDirectory(directory); RejectReparseAncestors(ReceiptPath);
        using var stream = new FileStream(ReceiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateAcl(stream.GetAccessControl(), isDirectory: false);
        if (stream.Length is <= 0 or > MaximumBytes) throw DigestReadInvalid();
        byte[] bytes = new byte[checked((int)stream.Length)];
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw DigestReadInvalid();
            return ParseDigestCore(bytes, deploymentId, protectedFileHandleVerified: true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>Pure same-byte parser for contract validation only. It cannot establish
    /// protected journal provenance and never returns a protected-file read origin.</summary>
    public static TrialJournalReadDigest ParseReadWithDigest(ReadOnlyMemory<byte> bytes, Guid deploymentId)
        => ParseDigestCore(bytes, deploymentId, protectedFileHandleVerified: false);

    // Always recompute semantic image digests at selection, even though this seam copies
    // parsed collections. A stored origin/hash can never cover a changed typed image.
    internal static void ValidateDigestCapsule(TrialJournalReadDigest read)
    {
        if (read is null || read.ByteCount is <= 0 or > MaximumBytes) throw DigestReadInvalid();
        ValidateReceipt(read.Receipt, read.Receipt.DeploymentId);
        string baseline = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(read.Receipt.Baseline, Options)));
        string? installed = read.Receipt.Installed is null ? null : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(read.Receipt.Installed, Options)));
        if (baseline != read.BaselineSha256 || installed != read.InstalledSha256) throw DigestReadInvalid();
    }

    private static TrialJournalReadDigest ParseDigestCore(ReadOnlyMemory<byte> input, Guid deploymentId, bool protectedFileHandleVerified)
    {
        if (input.Length is <= 0 or > MaximumBytes || deploymentId == Guid.Empty) throw DigestReadInvalid();
        byte[] bytes = input.ToArray();
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDigestDuplicates(document.RootElement);
            TrialReceipt receipt = JsonSerializer.Deserialize<TrialReceipt>(bytes, Options) ?? throw DigestReadInvalid();
            ValidateReceipt(receipt, deploymentId);
            using JsonDocument canonical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(receipt, Options));
            if (!JsonElement.DeepEquals(document.RootElement, canonical.RootElement)) throw DigestReadInvalid();
            receipt = receipt with { Baseline = new(FreezeDigestSnapshot(receipt.Baseline.Snapshot)),
                Installed = receipt.Installed is null ? null : FreezeDigestSnapshot(receipt.Installed),
                UnsignedSnapshot = receipt.UnsignedSnapshot is null ? null : FreezeDigestSnapshot(receipt.UnsignedSnapshot),
                DeniedSnapshot = receipt.DeniedSnapshot is null ? null : FreezeDigestSnapshot(receipt.DeniedSnapshot) };
            return new(receipt, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt.Baseline, Options))),
                receipt.Installed is null ? null : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt.Installed, Options))),
                bytes.Length, protectedFileHandleVerified);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or FormatException)
        { throw DigestReadInvalid(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static TrialSnapshot FreezeDigestSnapshot(TrialSnapshot snapshot) => snapshot with
    { Objects = Array.AsReadOnly(snapshot.Objects.ToArray()), PackagePermissions = Array.AsReadOnly(snapshot.PackagePermissions.ToArray()),
        MonitorPermissions = Array.AsReadOnly(snapshot.MonitorPermissions.ToArray()), Facts = Array.AsReadOnly(snapshot.Facts.ToArray()) };
    private static void RejectDigestDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw DigestReadInvalid(); RejectDigestDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in value.EnumerateArray()) RejectDigestDuplicates(item);
    }
    private static InvalidDataException DigestReadInvalid() => new("Private same-byte trial journal evidence is malformed; values withheld.");

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
        if (receipt.SchemaVersion is not (1 or 2 or 3 or 4) || deploymentId == Guid.Empty || receipt.DeploymentId != deploymentId ||
            receipt.Baseline is null || string.IsNullOrWhiteSpace(receipt.MonitorPrincipal) ||
            receipt.Phase is not ("prepared" or "commit_pending" or "installed" or "remove_pending" or "removed" or
                "signature_remove_pending" or "unsigned" or "unsigned_remove_pending" or
                "deny_pending" or "denied" or "deny_restore_pending" or "deny_restored"))
            throw new InvalidDataException("The trial receipt contract is invalid.");
        TrialInstallationPolicy policy = ResolveInstallationPolicy(receipt);
        bool unsignedPhase = receipt.Phase is "signature_remove_pending" or "unsigned" or "unsigned_remove_pending";
        bool denyPhase = receipt.Phase is "deny_pending" or "denied" or "deny_restore_pending" or "deny_restored";
        if (receipt.SchemaVersion != 4 && (denyPhase || receipt.DeniedSnapshot is not null) ||
            receipt.SchemaVersion == 4 && (unsignedPhase || receipt.UnsignedSnapshot is not null) ||
            denyPhase && (receipt.Installed is null || receipt.DeniedSnapshot is null) ||
            receipt.DeniedSnapshot is not null && (receipt.Installed is null || receipt.Phase is "prepared" or "commit_pending" or "installed"))
            throw new InvalidDataException("The DENY trial receipt contract is invalid.");
        if (receipt.SchemaVersion == 1 && (unsignedPhase || receipt.UnsignedSnapshot is not null) ||
            unsignedPhase && (receipt.Installed is null || receipt.UnsignedSnapshot is null) ||
            receipt.UnsignedSnapshot is not null && (receipt.Installed is null || receipt.Phase is "prepared" or "commit_pending" or "installed"))
            throw new InvalidDataException("The unsigned trial receipt contract is invalid.");
        if (receipt.Phase is "commit_pending" or "installed" or "remove_pending" && receipt.Installed is null)
            throw new InvalidDataException("The trial receipt requires its exact installed postimage.");
        if (receipt.Baseline.Snapshot is null || receipt.Baseline.Snapshot.Monitor is null ||
            receipt.Baseline.DeploymentId != deploymentId ||
            !string.Equals(receipt.Baseline.MonitorPrincipal, receipt.MonitorPrincipal, StringComparison.Ordinal) ||
            receipt.Phase == "prepared" && receipt.Installed is not null ||
            receipt.FailureCode is { Length: > 256 })
            throw new InvalidDataException("The trial receipt provenance is invalid.");
        AgentActivityTrialSql.ValidateBaselineRestored(receipt.Baseline, receipt.Baseline);
        if (receipt.Installed is not null) AgentActivityTrialSql.ValidateInstalled(receipt.Baseline, receipt.Installed, policy);
        if (receipt.UnsignedSnapshot is not null) AgentActivityTrialSql.ValidateUnsigned(receipt.Baseline, receipt.Installed!, receipt.UnsignedSnapshot, policy);
        if (receipt.DeniedSnapshot is not null) AgentActivityTrialDenySql.ValidateDenied(receipt.Baseline, receipt.Installed!, receipt.DeniedSnapshot);
    }

    public static void ValidateTransition(TrialReceipt before, TrialReceipt after)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        ValidateReceipt(before, before.DeploymentId); ValidateReceipt(after, before.DeploymentId);
        if (before.Installed is null && after.Installed is not null &&
            (before.Phase != "prepared" || after.Phase != "commit_pending"))
            throw new InvalidDataException("An installed postimage must be recorded before committing installation.");
        if (before.UnsignedSnapshot is null && after.UnsignedSnapshot is not null &&
            (before.SchemaVersion is not (2 or 3) || before.Phase != "installed" || after.Phase != "signature_remove_pending"))
            throw new InvalidDataException("An unsigned postimage must be recorded before committing signature removal.");
        if (before.DeniedSnapshot is null && after.DeniedSnapshot is not null &&
            (before.SchemaVersion != 4 || before.Phase != "installed" || after.Phase != "deny_pending"))
            throw new InvalidDataException("A denied postimage must be recorded before committing the DENY fixture.");
        if (before.SchemaVersion != after.SchemaVersion || before.InstallationPolicyVersion != after.InstallationPolicyVersion ||
            !string.Equals(before.MonitorPrincipal, after.MonitorPrincipal, StringComparison.Ordinal) ||
            !JsonSerializer.SerializeToUtf8Bytes(before.Baseline, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.Baseline, Options)) ||
            before.Installed is not null && !JsonSerializer.SerializeToUtf8Bytes(before.Installed, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.Installed, Options)) ||
            before.UnsignedSnapshot is not null && !JsonSerializer.SerializeToUtf8Bytes(before.UnsignedSnapshot, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.UnsignedSnapshot, Options)) ||
            before.DeniedSnapshot is not null && !JsonSerializer.SerializeToUtf8Bytes(before.DeniedSnapshot, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(after.DeniedSnapshot, Options)))
            throw new InvalidDataException("Trial provenance cannot be changed.");
        bool allowed = before.Phase == after.Phase || (before.Phase, after.Phase) is
            ("prepared", "commit_pending") or ("prepared", "removed") or
            ("commit_pending", "installed") or ("commit_pending", "remove_pending") or ("commit_pending", "removed") or
            ("installed", "remove_pending") or ("installed", "removed") or ("remove_pending", "removed") or
            ("installed", "signature_remove_pending") or
            ("signature_remove_pending", "unsigned") or ("signature_remove_pending", "remove_pending") or
            ("signature_remove_pending", "unsigned_remove_pending") or ("signature_remove_pending", "removed") or
            ("unsigned", "unsigned_remove_pending") or ("unsigned", "removed") or ("unsigned_remove_pending", "removed");
        if (before.SchemaVersion == 4)
            allowed = before.Phase == after.Phase || (before.Phase, after.Phase) is
                ("prepared", "commit_pending") or ("prepared", "removed") or
                ("commit_pending", "installed") or ("commit_pending", "remove_pending") or ("commit_pending", "removed") or
                ("installed", "deny_pending") or ("installed", "remove_pending") or
                ("deny_pending", "denied") or ("deny_pending", "deny_restore_pending") or ("deny_pending", "deny_restored") or
                ("denied", "deny_restore_pending") or ("deny_restore_pending", "deny_restored") or
                ("deny_restored", "remove_pending") or ("remove_pending", "removed");
        if (!allowed) throw new InvalidDataException("The trial journal transition is invalid.");
    }

    internal static TrialInstallationPolicy ResolveInstallationPolicy(TrialReceipt receipt) =>
        (receipt.SchemaVersion, receipt.InstallationPolicyVersion) switch
        {
            (1 or 2, null) => TrialInstallationPolicy.ExecuteOnlyV1,
            (3 or 4, 2) => TrialInstallationPolicy.MetadataVerificationV2,
            _ => throw new InvalidDataException("The trial receipt installation policy is invalid."),
        };

    /// <summary>Pure recovery decision using only the write-ahead receipt and complete live inventory.
    /// A pending COMMIT allows its two exact possible images, never arbitrary drift.</summary>
    public static TrialDenyRecoveryAction SelectDenyRecoveryAction(TrialReceipt receipt, TrialSnapshot actual)
    {
        ValidateReceipt(receipt, receipt.DeploymentId);
        ArgumentNullException.ThrowIfNull(actual);
        if (receipt.SchemaVersion != 4) throw new InvalidDataException("A schema4 DENY recovery receipt is required.");
        if (actual.Objects.Count == 0)
        {
            AgentActivityTrialSql.ValidateBaselineRestored(receipt.Baseline, new(actual));
            if (receipt.Phase is not ("prepared" or "commit_pending" or "remove_pending" or "removed"))
                throw new InvalidDataException("An absent package contradicts the DENY recovery phase.");
            return TrialDenyRecoveryAction.BaselineRestored;
        }
        if (receipt.Installed is null || receipt.Phase is "prepared" or "removed")
            throw new InvalidDataException("Present SQL objects have no matching owned installed receipt.");
        if (receipt.Phase is "denied")
        {
            AgentActivityTrialSql.ValidateExactSnapshot(receipt.DeniedSnapshot!, actual);
            return TrialDenyRecoveryAction.RestoreThenRemove;
        }
        if (receipt.Phase is "deny_pending" or "deny_restore_pending")
        {
            try
            {
                AgentActivityTrialSql.ValidateExactSnapshot(receipt.Installed, actual);
                return TrialDenyRecoveryAction.RemoveSigned;
            }
            catch (InvalidDataException)
            {
                AgentActivityTrialSql.ValidateExactSnapshot(receipt.DeniedSnapshot!, actual);
                return TrialDenyRecoveryAction.RestoreThenRemove;
            }
        }
        AgentActivityTrialSql.ValidateExactSnapshot(receipt.Installed, actual);
        return TrialDenyRecoveryAction.RemoveSigned;
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
