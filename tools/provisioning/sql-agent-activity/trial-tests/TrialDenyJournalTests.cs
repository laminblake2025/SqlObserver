using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlObserver.AgentActivityTrial.Tests;

public sealed class TrialDenyJournalTests
{
    private static readonly string[] Phases=["prepared","commit_pending","installed","deny_pending","denied","deny_restore_pending","deny_restored","remove_pending","removed"];
    public static IEnumerable<object[]> Transitions()
    {
        foreach(string from in Phases)foreach(string to in Phases)
        {
            bool allowed=from==to || (from,to) is
                ("prepared","commit_pending") or ("prepared","removed") or
                ("commit_pending","installed") or ("commit_pending","remove_pending") or ("commit_pending","removed") or
                ("installed","deny_pending") or ("installed","remove_pending") or
                ("deny_pending","denied") or ("deny_pending","deny_restore_pending") or ("deny_pending","deny_restored") or
                ("denied","deny_restore_pending") or ("deny_restore_pending","deny_restored") or
                ("deny_restored","remove_pending") or ("remove_pending","removed");
            yield return[from,to,allowed];
        }
    }
    [Theory]
    [MemberData(nameof(Transitions))]
    public void VersionFourAllowsOnlyWriteAheadTransitionsAndResolvedRecovery(string from,string to,bool allowed)
    {
        var before=TrialDenyFixture.Receipt(from);var next=TrialDenyFixture.Receipt(to);
        next=next with {Baseline=before.Baseline,Installed=before.Installed??(from=="prepared" && to=="commit_pending"?next.Installed:null),
            DeniedSnapshot=before.DeniedSnapshot??(from=="installed" && to=="deny_pending"?next.DeniedSnapshot:null)};
        if(allowed)TrialJournal.ValidateTransition(before,next);
        else Assert.Throws<InvalidDataException>(()=>TrialJournal.ValidateTransition(before,next));
    }
    [Fact]
    public void FullJourneyPreservesOriginalSignedAndDeniedPostimagesAfterRemoval()
    {
        var current=TrialDenyFixture.Receipt("prepared");
        foreach(string phase in Phases.Skip(1))
        {
            var next=TrialDenyFixture.Receipt(phase);
            TrialJournal.ValidateTransition(current,next);current=JsonSerializer.Deserialize<TrialReceipt>(JsonSerializer.Serialize(next))!;
            TrialJournal.ValidateReceipt(current,current.DeploymentId);Assert.Equal(4,current.SchemaVersion);Assert.Equal(2,current.InstallationPolicyVersion);Assert.Null(current.UnsignedSnapshot);
        }
        Assert.NotNull(current.Installed);Assert.NotNull(current.DeniedSnapshot);
        Assert.Contains(current.Installed!.PackagePermissions,row=>row.Scope=="msdb" && row.MajorId==100 && row.State=="G");
        Assert.Contains(current.DeniedSnapshot!.PackagePermissions,row=>row.Scope=="msdb" && row.MajorId==100 && row.State=="D");
    }
    [Theory]
    [InlineData("prepared")][InlineData("commit_pending")][InlineData("installed")][InlineData("remove_pending")][InlineData("removed")]
    public void DeniedPostimageCannotFirstAppearOutsideInstalledToPending(string phase)
    {
        var before=TrialDenyFixture.Receipt(phase,deniedImage:false);var (_,signed)=TrialDenyFixture.Create();
        var after=before with {DeniedSnapshot=AgentActivityTrialDenySql.ExpectedDenied(before.Baseline,signed)};
        Assert.Throws<InvalidDataException>(()=>TrialJournal.ValidateTransition(before,after));
    }
    [Theory]
    [InlineData("signed")][InlineData("denied")][InlineData("unsigned")][InlineData("policy")][InlineData("schema")][InlineData("coherent-identity")]
    public void RecordedPermissionProvenanceCannotBeDroppedOrReplaced(string mutation)
    {
        var before=TrialDenyFixture.Receipt("denied");var after=before with {Phase="deny_restore_pending"};
        var replacement=before.Installed!.Identity with {MasterDatabaseGuid=Guid.NewGuid()};
        after=mutation switch{
            "signed"=>after with {Installed=null},"denied"=>after with {DeniedSnapshot=null},
            "unsigned"=>after with {UnsignedSnapshot=before.Installed},"policy"=>after with {InstallationPolicyVersion=null},
            "schema"=>after with {SchemaVersion=3},
            _=>after with {Baseline=new(before.Baseline.Snapshot with {Identity=replacement}),Installed=before.Installed with {Identity=replacement},DeniedSnapshot=before.DeniedSnapshot! with {Identity=replacement}}};
        Assert.Throws<InvalidDataException>(()=>TrialJournal.ValidateTransition(before,after));
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)]
    public void LegacySchemasCannotAcquireDenyStatesOrFields(int schema)
    {
        var pending=TrialDenyFixture.Receipt("deny_pending") with {SchemaVersion=schema,InstallationPolicyVersion=schema==3?2:null};
        Assert.Throws<InvalidDataException>(()=>TrialJournal.ValidateReceipt(pending,pending.DeploymentId));
        Assert.Throws<InvalidDataException>(()=>TrialJournal.ValidateReceipt(pending with {Phase="removed"},pending.DeploymentId));
    }
    [Theory]
    [InlineData("signature_remove_pending")][InlineData("unsigned")][InlineData("unsigned_remove_pending")]
    public void DenySchemaDoesNotAuthorizeTheSeparateSignatureRemovalPolicy(string phase)
    {
        var receipt=TrialDenyFixture.Receipt("installed") with {Phase=phase,UnsignedSnapshot=TrialDenyFixture.Receipt("installed").Installed};
        Assert.Throws<InvalidDataException>(()=>TrialJournal.ValidateReceipt(receipt,receipt.DeploymentId));
    }
    [Theory]
    [InlineData("signed")][InlineData("denied")][InlineData("restored")]
    public void AllThreeCallbacksAreRequiredBeforeAnyConnectionOrModuleAction(string missing)
    {
        using var connection=new SqlConnection();Func<CancellationToken,Task<bool>> callback=_=>throw new InvalidOperationException("Must not run.");
        var error=Assert.Throws<ArgumentNullException>(()=>{_=TrialCoordinator.RunExplicitDenyAsync(connection,"TEST\\Monitor",Guid.NewGuid(),[],
            missing=="signed"?null!:callback,missing=="denied"?null!:callback,missing=="restored"?null!:callback,CancellationToken.None);});
        Assert.Equal("qualify"+char.ToUpperInvariant(missing[0])+missing[1..],error.ParamName);Assert.Equal(System.Data.ConnectionState.Closed,connection.State);
    }
    [Fact]
    public void NewReceiptShapeOmitsDeniedSnapshotUntilItHasWriteAheadProvenance()
    {
        var prepared=TrialDenyFixture.Receipt("prepared");using var initial=JsonDocument.Parse(JsonSerializer.Serialize(prepared));Assert.False(initial.RootElement.TryGetProperty("DeniedSnapshot",out _));
        using var pending=JsonDocument.Parse(JsonSerializer.Serialize(TrialDenyFixture.Receipt("deny_pending")));Assert.Equal(JsonValueKind.Object,pending.RootElement.GetProperty("DeniedSnapshot").ValueKind);Assert.False(pending.RootElement.TryGetProperty("UnsignedSnapshot",out _));
    }

    [Theory]
    [InlineData("The DENY trial receipt contract is invalid.")]
    [InlineData("The DENY source object identity is invalid.")]
    [InlineData("The DENY fixture requires its exact dbo-granted package permission.")]
    [InlineData("A denied postimage must be recorded before committing the DENY fixture.")]
    [InlineData("A schema4 DENY recovery receipt is required.")]
    [InlineData("An absent package contradicts the DENY recovery phase.")]
    public void DenyDiagnosticsExposeOnlyExactFixedValidatorConstants(string message)
    {
        var method=typeof(TrialCoordinator).GetMethod("FailureCode",BindingFlags.Static|BindingFlags.NonPublic)!;
        Assert.Equal("invariant:"+message,method.Invoke(null,[new InvalidDataException(message)]));
        Assert.Equal("InvalidDataException",method.Invoke(null,[new InvalidDataException(message+" TEST\\PrivateMonitor secret-endpoint private-certificate")]));
        Assert.Equal("InvalidOperationException",method.Invoke(null,[new InvalidOperationException(message)]));
    }
}
