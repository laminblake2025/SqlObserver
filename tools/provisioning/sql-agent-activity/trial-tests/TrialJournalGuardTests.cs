using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>In-memory ACL checks only. No journal creation, ProgramData writes or ACL changes.</summary>
public sealed class TrialJournalGuardTests
{
    [Theory]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    public void ExactProtectedAdministratorSystemAclIsAccepted(WellKnownSidType owner)
    {
        FileSecurity security = ExactAcl(); security.SetOwner(new SecurityIdentifier(owner, null));
        Invoke("ValidateAcl", security, false);
    }

    [Theory]
    [InlineData("unprotected")]
    [InlineData("foreign-owner")]
    [InlineData("extra-reader")]
    [InlineData("missing-system")]
    [InlineData("partial-rights")]
    [InlineData("deny")]
    public void JournalAclRefusesBroaderOrIncompleteProtection(string mutation)
    {
        FileSecurity security = ExactAcl(); var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        switch (mutation)
        {
            case "unprotected": security.SetAccessRuleProtection(false, false); break;
            case "foreign-owner": security.SetOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)); break;
            case "extra-reader": security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Read, AccessControlType.Allow)); break;
            case "missing-system": security.PurgeAccessRules(system); break;
            case "partial-rights": security.SetAccessRule(new FileSystemAccessRule(system, FileSystemRights.Read, AccessControlType.Allow)); break;
            case "deny": security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.Read, AccessControlType.Deny)); break;
        }
        Assert.Throws<UnauthorizedAccessException>(() => Invoke("ValidateAcl", security, false));
    }

    [Fact]
    public void DirectoryRequiresExactChildInheritanceAndNoInheritOnlyFlags()
    {
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        foreach (var kind in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(kind, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        Invoke("ValidateAcl", security, true);
        security.SetAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => Invoke("ValidateAcl", security, true));
    }

    [Fact]
    public void DirectoryGuardRefusesFileRulesWithoutChildInheritance() =>
        Assert.Throws<UnauthorizedAccessException>(() => Invoke("ValidateAcl", ExactAcl(), true));

    [Fact]
    public void InvalidPathIsRejectedBeforeFilesystemAccess() =>
        Assert.Throws<ArgumentException>(() => Invoke("RejectReparseAncestors", "invalid\0path"));

    [Fact]
    public void EmptyDeploymentCannotReachProgramDataCreationOrOpen()
    {
        Assert.Throws<ArgumentException>(() => TrialJournal.Create(Guid.Empty));
        Assert.Throws<ArgumentException>(() => TrialJournal.Open(Guid.Empty));
    }

    private static FileSecurity ExactAcl()
    {
        var security = new FileSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static void Invoke(string method, params object[] arguments)
    {
        var target = typeof(TrialJournal).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The reviewed journal guard was removed.");
        try { target.Invoke(null, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
