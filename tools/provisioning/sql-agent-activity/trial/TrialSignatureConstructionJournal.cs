using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlObserver.AgentActivityTrial;

public sealed record TrialSignatureConstructionBinding(TrialBaseline Baseline, Guid ConnectionId, int SessionId,
    string DbaSidHex, string AgentConfigurationSha256, string ForeignInventorySha256)
{ public override string ToString() => "Private physical signature-construction binding"; }
public sealed record TrialSignatureConstructionRefusal(string Reason, int? SqlNumber = null, byte? SqlState = null)
{ public override string ToString() => "Bounded signature-construction refusal"; }
public sealed record TrialSignatureRollbackVerification(TrialSnapshot ExactAbsentReadback, Guid ConnectionId, int SessionId,
    string DbaSidHex, string AgentConfigurationSha256, string ForeignInventorySha256,
    bool RollbackAcknowledged, bool TransactionClosed, bool ReadbackTransactionRollbackAcknowledged = false)
{ public override string ToString() => "Private independently verified construction rollback"; }
public sealed record TrialSignatureConstructionReceipt(int SchemaVersion, Guid DeploymentId, Guid FixtureId,
    string Phase, TrialSignatureConstructionBinding Binding, TrialSignatureDonorInstallation? Installation = null,
    TrialAttachedInvalidSignatureImage? AttachedInvalid = null, TrialSignatureConstructionRefusal? Refusal = null,
    TrialSignatureRollbackVerification? Rollback = null, string? BlockedFromPhase = null)
{ public override string ToString() => "Private rollback-only signature-construction receipt"; }
public enum TrialSignatureConstructionRecoveryAction { PreserveForInspection, AlreadyRollbackVerified }

/// <summary>Private rollback-only construction evidence. No phase or recovery result authorizes
/// a commit, SQL mutation, worker, Agent start, fresh connection, or replay of a pending action.</summary>
public sealed class TrialSignatureConstructionJournal
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, MaxDepth = 32, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
    };
    private static readonly string[] Phases = ["prepared", "transaction_pending", "signed_observed", "attachment_attempt_pending",
        "attached_invalid_observed", "attachment_refused", "rollback_pending", "rollback_verified", "blocked"];
    private static readonly string[] RootKeys = ["SchemaVersion", "DeploymentId", "FixtureId", "Phase", "Binding",
        "Installation", "AttachedInvalid", "Refusal", "Rollback", "BlockedFromPhase"];
    private readonly string directory;
    private readonly Guid deploymentId;
    private readonly Guid fixtureId;
    private readonly SecurityIdentifier owner;

    private TrialSignatureConstructionJournal(string directory, Guid deploymentId, Guid fixtureId, SecurityIdentifier owner)
    { this.directory = directory; this.deploymentId = deploymentId; this.fixtureId = fixtureId; this.owner = owner; }
    public string ReceiptPath => Path.Combine(directory, "receipt.json");

    public static TrialSignatureConstructionJournal Create(Guid deploymentId, Guid fixtureId)
    {
        ValidateIds(deploymentId, fixtureId); RequireWindows();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(); SecurityIdentifier owner = RequireIndividualOwner(identity);
        string root = Root(); RejectReparse(root);
        if (!Directory.Exists(root)) CreateDirectory(root, owner);
        ValidateDirectory(root, owner);
        if (Directory.EnumerateFiles(root).Any()) throw Invalid();
        foreach (string sibling in Directory.EnumerateDirectories(root))
        {
            ValidateDirectory(sibling, owner);
            string name = Path.GetFileName(sibling);
            if (name.Length != 73 || name[36] != '_' || !CanonicalId(name[..36], out Guid oldDeployment) ||
                !CanonicalId(name[37..], out Guid oldFixture)) throw Invalid();
            if (oldDeployment == deploymentId || oldFixture == fixtureId) throw Invalid();
            var prior = new TrialSignatureConstructionJournal(sibling, oldDeployment, oldFixture, owner);
            if (prior.Read().Phase != "rollback_verified") throw Invalid();
        }
        string path = Path.Combine(root, deploymentId.ToString("D") + "_" + fixtureId.ToString("D"));
        if (File.Exists(path) || Directory.Exists(path)) throw Invalid();
        CreateDirectory(path, owner); ValidateDirectory(path, owner);
        return new(path, deploymentId, fixtureId, owner);
    }

    /// <summary>Read existing evidence only. Opening never selects or performs recovery.</summary>
    public static TrialSignatureConstructionJournal Open(Guid deploymentId, Guid fixtureId)
    {
        ValidateIds(deploymentId, fixtureId); RequireWindows();
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(); SecurityIdentifier owner = RequireIndividualOwner(identity);
        ValidateDirectory(Root(), owner);
        string path = Path.Combine(Root(), deploymentId.ToString("D") + "_" + fixtureId.ToString("D"));
        ValidateDirectory(path, owner); RequireFileInventory(path);
        return new(path, deploymentId, fixtureId, owner);
    }

    public TrialSignatureConstructionReceipt Read()
    {
        ValidateDirectory(directory, owner); RequireFileInventory(directory); ValidateFile(ReceiptPath, owner);
        using var stream = new FileStream(ReceiptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw Invalid();
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw Invalid();
        try { return Deserialize(bytes, deploymentId, fixtureId); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Save(TrialSignatureConstructionReceipt receipt)
    {
        ValidateDirectory(directory, owner); RequireFileInventory(directory);
        if (File.Exists(ReceiptPath)) ValidateTransition(Read(), receipt);
        else { ValidateReceipt(receipt, deploymentId, fixtureId); if (receipt.Phase != "prepared") throw Invalid(); }
        byte[] bytes = Serialize(receipt);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            var security = new FileSecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
            foreach (SecurityIdentifier sid in new[] { owner, SystemSid() })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            using (FileStream stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.WriteThrough, security))
            { stream.Write(bytes); stream.Flush(true); }
            ValidateFile(temporary, owner);
            if (!File.ReadAllBytes(temporary).AsSpan().SequenceEqual(bytes)) throw Invalid();
            if (File.Exists(ReceiptPath)) File.Replace(temporary, ReceiptPath, null);
            else File.Move(temporary, ReceiptPath);
            ValidateDirectory(directory, owner); RequireFileInventory(directory); ValidateFile(ReceiptPath, owner);
            if (!File.ReadAllBytes(ReceiptPath).AsSpan().SequenceEqual(bytes)) throw Invalid();
        }
        // A failed atomic write may leave a private pending artifact. Preserve it and
        // refuse its unknown inventory; never erase uncertain evidence or auto-recover.
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void RecordPhase(string phase)
    {
        TrialSignatureConstructionReceipt before = Read();
        Save(before with { Phase = phase, BlockedFromPhase = phase == "blocked" ? before.BlockedFromPhase ?? before.Phase : null });
    }

    public static byte[] Serialize(TrialSignatureConstructionReceipt receipt)
    {
        if (receipt is null) throw Invalid();
        ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        if (bytes.Length is <= 0 or > MaximumBytes) { CryptographicOperations.ZeroMemory(bytes); throw Invalid(); }
        return bytes;
    }

    public static TrialSignatureConstructionReceipt Deserialize(ReadOnlyMemory<byte> bytes, Guid deploymentId, Guid fixtureId)
    {
        try
        {
            ValidateIds(deploymentId, fixtureId);
            if (bytes.Length is <= 0 or > MaximumBytes) throw Invalid();
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(RootKeys.Order(StringComparer.Ordinal))) throw Invalid();
            TrialSignatureConstructionReceipt receipt = JsonSerializer.Deserialize<TrialSignatureConstructionReceipt>(bytes.Span, Options) ?? throw Invalid();
            ValidateReceipt(receipt, deploymentId, fixtureId);
            // Read-only/computed members of nested public records must also match their
            // typed value. This closes missing or ignored nested serialization fields.
            using JsonDocument canonical = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(receipt, Options));
            if (!JsonElement.DeepEquals(document.RootElement, canonical.RootElement)) throw Invalid();
            return receipt;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException)
        { throw Invalid(); }
    }

    public static void ValidateReceipt(TrialSignatureConstructionReceipt receipt, Guid deploymentId, Guid fixtureId)
    {
        if (receipt is null) throw Invalid(); ValidateIds(deploymentId, fixtureId);
        if (receipt.SchemaVersion != 1 || receipt.DeploymentId != deploymentId || receipt.FixtureId != fixtureId ||
            !Phases.Contains(receipt.Phase, StringComparer.Ordinal)) throw Invalid();
        if (receipt.Phase == "blocked" ? receipt.BlockedFromPhase is null || receipt.BlockedFromPhase is "blocked" or "rollback_verified" ||
            !Phases.Contains(receipt.BlockedFromPhase, StringComparer.Ordinal) : receipt.BlockedFromPhase is not null) throw Invalid();
        TrialSignatureConstructionBinding binding = receipt.Binding;
        if (binding is null || binding.Baseline is null || binding.Baseline.Snapshot is null || binding.ConnectionId == Guid.Empty ||
            binding.SessionId is <= 0 or > 32767 || !WindowsSidHex(binding.DbaSidHex) || !Digest(binding.AgentConfigurationSha256) ||
            !Digest(binding.ForeignInventorySha256) || binding.Baseline.DeploymentId != deploymentId ||
            string.Equals(binding.DbaSidHex, binding.Baseline.Snapshot.Monitor?.SidHex, StringComparison.OrdinalIgnoreCase)) throw Invalid();
        ValidateSnapshotStructure(binding.Baseline.Snapshot);
        AgentActivityTrialSql.ValidateBaselineRestored(binding.Baseline, binding.Baseline);
        if (receipt.Installation is { } installation)
        {
            ValidateSnapshotStructure(installation.Installed);
            TrialAttachedInvalidSignatureSql.ValidateInstallation(binding.Baseline, installation);
        }
        if (receipt.AttachedInvalid is { } attached)
        {
            if (receipt.Installation is null) throw Invalid();
            ValidateSnapshotStructure(attached.Snapshot);
            TrialAttachedInvalidSignatureSql.ValidateAttachedInvalid(binding.Baseline, receipt.Installation, attached);
        }
        if (receipt.Refusal is { } refusal) ValidateRefusal(refusal);
        string phase = receipt.BlockedFromPhase ?? receipt.Phase;
        if (phase is "prepared" or "transaction_pending" && (receipt.Installation is not null || receipt.AttachedInvalid is not null))
        {
            // A valid captured postimage may be preserved when its write-ahead phase
            // could not be acknowledged. It supplies no authority to resume an action.
            if (receipt.Phase != "blocked" || phase != "transaction_pending" || receipt.AttachedInvalid is not null) throw Invalid();
        }
        if (phase is "signed_observed" or "attachment_attempt_pending" or "attached_invalid_observed" or "attachment_refused" && receipt.Installation is null) throw Invalid();
        if (phase == "attached_invalid_observed" && receipt.AttachedInvalid is null) throw Invalid();
        if (phase == "attachment_refused" && (receipt.AttachedInvalid is not null || receipt.Refusal is null)) throw Invalid();
        if (receipt.AttachedInvalid is not null && phase is not ("attached_invalid_observed" or "rollback_pending" or "rollback_verified") &&
            !(receipt.Phase == "blocked" && phase == "attachment_attempt_pending")) throw Invalid();
        if (receipt.Refusal is not null && receipt.Phase != "blocked" && phase is not ("attachment_refused" or "rollback_pending" or "rollback_verified") &&
            !(receipt.Refusal.Reason == "probe_failed" && phase is "transaction_pending" or "signed_observed" or "attachment_attempt_pending")) throw Invalid();
        if (receipt.Phase == "prepared" && (receipt.Refusal is not null || receipt.Rollback is not null)) throw Invalid();
        if (receipt.Phase == "rollback_verified")
        {
            if (receipt.Rollback is null) throw Invalid(); ValidateRollback(binding, receipt.Rollback);
        }
        else if (receipt.Rollback is not null) throw Invalid();
    }

    public static void ValidateTransition(TrialSignatureConstructionReceipt before, TrialSignatureConstructionReceipt after)
    {
        if (before is null || after is null) throw Invalid();
        ValidateReceipt(before, before.DeploymentId, before.FixtureId); ValidateReceipt(after, before.DeploymentId, before.FixtureId);
        if (!Same(before.Binding, after.Binding) || before.Installation is not null && !Same(before.Installation, after.Installation) ||
            before.AttachedInvalid is not null && !Same(before.AttachedInvalid, after.AttachedInvalid) ||
            before.Refusal is not null && !Same(before.Refusal, after.Refusal)) throw Invalid();
        if (before.Phase is "blocked" or "rollback_verified")
        { if (!Same(before, after)) throw Invalid(); return; }
        if (before.Phase == after.Phase)
        {
            if (Same(before, after)) return;
            // A failure breadcrumb may be added once without claiming that a new
            // observation phase succeeded. The known provenance stays immutable.
            if (before.Refusal is not null || after.Refusal?.Reason != "probe_failed" ||
                before.Phase is not ("transaction_pending" or "signed_observed" or "attachment_attempt_pending") ||
                !Same(before with { Refusal = after.Refusal }, after)) throw Invalid();
            return;
        }
        if (after.Phase == "blocked")
        {
            if (after.BlockedFromPhase != before.Phase || after.Rollback is not null ||
                before.Installation is null && after.Installation is not null && before.Phase != "transaction_pending" ||
                before.AttachedInvalid is null && after.AttachedInvalid is not null && before.Phase != "attachment_attempt_pending") throw Invalid();
            return;
        }
        if (before.Refusal is not null && before.Phase != "rollback_pending" && after.Phase != "rollback_pending") throw Invalid();
        bool normal = (before.Phase, after.Phase) is ("prepared", "transaction_pending") or ("transaction_pending", "signed_observed") or
            ("signed_observed", "attachment_attempt_pending") or ("attachment_attempt_pending", "attached_invalid_observed") or
            ("attachment_attempt_pending", "attachment_refused") or ("rollback_pending", "rollback_verified");
        bool rollback = after.Phase == "rollback_pending" && before.Phase is "transaction_pending" or "signed_observed" or
            "attachment_attempt_pending" or "attached_invalid_observed" or "attachment_refused";
        if (!normal && !rollback) throw Invalid();
        if (before.Installation is null && after.Installation is not null && (before.Phase, after.Phase) != ("transaction_pending", "signed_observed") ||
            before.AttachedInvalid is null && after.AttachedInvalid is not null && (before.Phase, after.Phase) != ("attachment_attempt_pending", "attached_invalid_observed") ||
            before.Refusal is null && after.Refusal is not null && after.Phase is not ("attachment_refused" or "rollback_pending")) throw Invalid();
    }

    /// <summary>No returned action permits mutation, reconnection, commit or rollback replay.</summary>
    public static TrialSignatureConstructionRecoveryAction SelectRecoveryAction(TrialSignatureConstructionReceipt receipt)
    {
        if (receipt is null) throw Invalid(); ValidateReceipt(receipt, receipt.DeploymentId, receipt.FixtureId);
        return receipt.Phase == "rollback_verified" ? TrialSignatureConstructionRecoveryAction.AlreadyRollbackVerified : TrialSignatureConstructionRecoveryAction.PreserveForInspection;
    }

    private static void ValidateRollback(TrialSignatureConstructionBinding binding, TrialSignatureRollbackVerification proof)
    {
        if (!proof.RollbackAcknowledged || !proof.TransactionClosed || !proof.ReadbackTransactionRollbackAcknowledged || proof.ExactAbsentReadback is null ||
            proof.ConnectionId != binding.ConnectionId || proof.SessionId != binding.SessionId || proof.DbaSidHex != binding.DbaSidHex ||
            proof.AgentConfigurationSha256 != binding.AgentConfigurationSha256 || proof.ForeignInventorySha256 != binding.ForeignInventorySha256) throw Invalid();
        ValidateSnapshotStructure(proof.ExactAbsentReadback);
        AgentActivityTrialSql.ValidateBaselineRestored(binding.Baseline, new(proof.ExactAbsentReadback));
    }
    private static void ValidateSnapshotStructure(TrialSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Identity is null || snapshot.Monitor is null ||
            snapshot.Objects is null || snapshot.PackagePermissions is null || snapshot.MonitorPermissions is null || snapshot.Facts is null ||
            snapshot.Objects.Any(item => item is null) || snapshot.PackagePermissions.Any(item => item is null) ||
            snapshot.MonitorPermissions.Any(item => item is null) || snapshot.Facts.Any(item => item is null)) throw Invalid();
    }
    private static void ValidateRefusal(TrialSignatureConstructionRefusal refusal)
    {
        if (refusal.Reason is not ("sql_import_refused" or "attached_invalid_image_unproven" or "probe_failed")) throw Invalid();
        bool sqlPair = refusal.SqlNumber is not null && refusal.SqlState is not null;
        if ((refusal.SqlNumber is null) != (refusal.SqlState is null) ||
            refusal.SqlNumber is 0 or < -2 or > 999999 || refusal.SqlNumber == -1 ||
            refusal.Reason == "sql_import_refused" && !sqlPair ||
            refusal.Reason == "attached_invalid_image_unproven" && sqlPair) throw Invalid();
    }
    private static bool WindowsSidHex(string value)
    {
        if (value is not { Length: >= 24 and <= 136 } || value.Length % 2 != 0 || !value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        byte[] sid = Convert.FromHexString(value); return sid[0] == 1 && sid[1] is >= 1 and <= 15 && sid.Length == 8 + sid[1] * 4;
    }
    private static bool Digest(string value) => value is { Length: 64 } && value.Any(character => character != '0') && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Same<T>(T first, T second) => JsonSerializer.SerializeToUtf8Bytes(first, Options).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(second, Options));
    private static void ValidateIds(Guid deploymentId, Guid fixtureId) { if (deploymentId == Guid.Empty || fixtureId == Guid.Empty || deploymentId == fixtureId) throw Invalid(); }
    private static bool CanonicalId(string text, out Guid id) => Guid.TryParseExact(text, "D", out id) && id != Guid.Empty && id.ToString("D") == text;
    private static InvalidDataException Invalid() => new("Private rollback-only signature construction evidence is invalid or unresolved; values withheld.");
    private static void RequireWindows() { if (!OperatingSystem.IsWindows()) throw Invalid(); }
    private static string Root() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlObserver", "AgentActivitySignatureConstructionTrial");
    private static SecurityIdentifier SystemSid() => new(WellKnownSidType.LocalSystemSid, null);
    private static SecurityIdentifier RequireIndividualOwner(WindowsIdentity identity)
    { SecurityIdentifier owner = identity.User ?? throw Invalid(); if (owner.Equals(SystemSid()) || !owner.IsAccountSid()) throw Invalid(); return owner; }
    private static void RequireFileInventory(string path)
    {
        if (Directory.EnumerateDirectories(path).Any() || Directory.EnumerateFiles(path).Any(file => Path.GetFileName(file) != "receipt.json")) throw Invalid();
    }
    private static void RejectReparse(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        { if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid(); current = Path.GetDirectoryName(current); }
    }
    private static void CreateDirectory(string path, SecurityIdentifier owner)
    {
        var security = new DirectorySecurity(); security.SetOwner(owner); security.SetAccessRuleProtection(true, false);
        foreach (SecurityIdentifier sid in new[] { owner, SystemSid() }) security.AddAccessRule(new FileSystemAccessRule(sid,
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }
    private static void ValidateDirectory(string path, SecurityIdentifier owner)
    { RejectReparse(path); if (!Directory.Exists(path)) throw Invalid(); ValidateAcl(new DirectoryInfo(path).GetAccessControl(), owner, true); }
    private static void ValidateFile(string path, SecurityIdentifier owner)
    { RejectReparse(path); if (!File.Exists(path)) throw Invalid(); ValidateAcl(new FileInfo(path).GetAccessControl(), owner, false); }
    private static void ValidateAcl(FileSystemSecurity security, SecurityIdentifier owner, bool directory)
    {
        if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw Invalid();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.IdentityReference is not SecurityIdentifier sid || !seen.Add(sid.Value) || rule.IsInherited || rule.AccessControlType != AccessControlType.Allow ||
                rule.FileSystemRights != FileSystemRights.FullControl || !sid.Equals(owner) && !sid.Equals(SystemSid()) ||
                rule.InheritanceFlags != (directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None) || rule.PropagationFlags != PropagationFlags.None) throw Invalid();
        }
        if (!seen.SetEquals([owner.Value, SystemSid().Value])) throw Invalid();
    }
    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
