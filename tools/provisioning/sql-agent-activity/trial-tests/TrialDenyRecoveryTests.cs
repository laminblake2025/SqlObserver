namespace SqlObserver.AgentActivityTrial.Tests;

/// <summary>Pure uncertain-COMMIT decisions. Native process/SQL interruption remains separate qualification.</summary>
public sealed class TrialDenyRecoveryTests
{
    [Theory]
    [InlineData("deny_pending",false)][InlineData("deny_pending",true)]
    [InlineData("deny_restore_pending",false)][InlineData("deny_restore_pending",true)]
    public void UnacknowledgedCommitAcceptsOnlyItsExactBeforeOrAfterImage(string phase,bool denied)
    {
        var receipt=TrialDenyFixture.Receipt(phase);var actual=denied?receipt.DeniedSnapshot!:receipt.Installed!;
        Assert.Equal(denied?TrialDenyRecoveryAction.RestoreThenRemove:TrialDenyRecoveryAction.RemoveSigned,
            TrialJournal.SelectDenyRecoveryAction(receipt,actual));
        var next=receipt with {Phase=denied?"deny_restore_pending":"deny_restored"};TrialJournal.ValidateTransition(receipt,next);
        if(denied){var restored=next with {Phase="deny_restored"};TrialJournal.ValidateTransition(next,restored);next=restored;}
        Assert.Equal(TrialDenyRecoveryAction.RemoveSigned,TrialJournal.SelectDenyRecoveryAction(next,next.Installed!));
        var remove=next with {Phase="remove_pending"};TrialJournal.ValidateTransition(next,remove);
        Assert.Equal(TrialDenyRecoveryAction.BaselineRestored,TrialJournal.SelectDenyRecoveryAction(remove,remove.Baseline.Snapshot));
        TrialJournal.ValidateTransition(remove,remove with {Phase="removed"});
    }
    [Theory]
    [InlineData("denied",true)][InlineData("deny_restored",false)][InlineData("installed",false)]
    public void ConfirmedPhasesPermitOnlyTheirSingleRecordedImage(string phase,bool denied)
    {
        var receipt=TrialDenyFixture.Receipt(phase);var actual=denied?receipt.DeniedSnapshot!:receipt.Installed!;
        Assert.Equal(denied?TrialDenyRecoveryAction.RestoreThenRemove:TrialDenyRecoveryAction.RemoveSigned,TrialJournal.SelectDenyRecoveryAction(receipt,actual));
        var opposite=denied?receipt.Installed!:AgentActivityTrialDenySql.ExpectedDenied(receipt.Baseline,receipt.Installed!);
        Assert.Throws<InvalidDataException>(()=>TrialJournal.SelectDenyRecoveryAction(receipt,opposite));
    }
    [Theory]
    [InlineData("prepared")][InlineData("commit_pending")][InlineData("remove_pending")][InlineData("removed")]
    public void ExactAbsentBaselineCanResolveOnlyThePhasesThatMayHaveNoCommittedPackage(string phase)
    {
        var receipt=TrialDenyFixture.Receipt(phase);Assert.Equal(TrialDenyRecoveryAction.BaselineRestored,TrialJournal.SelectDenyRecoveryAction(receipt,receipt.Baseline.Snapshot));
    }
    [Theory]
    [InlineData("installed")][InlineData("deny_pending")][InlineData("denied")][InlineData("deny_restore_pending")][InlineData("deny_restored")]
    public void ExternalRemovalDuringPermissionQualificationDoesNotMasqueradeAsProvenanceBoundCleanup(string phase)
    {
        var receipt=TrialDenyFixture.Receipt(phase);Assert.Throws<InvalidDataException>(()=>TrialJournal.SelectDenyRecoveryAction(receipt,receipt.Baseline.Snapshot));
    }
    [Theory]
    [InlineData("deny_pending","grantor")][InlineData("deny_pending","source")][InlineData("deny_pending","signature")]
    [InlineData("deny_pending","monitor")][InlineData("deny_pending","identity")]
    [InlineData("deny_restore_pending","grantor")][InlineData("deny_restore_pending","source")][InlineData("deny_restore_pending","signature")]
    [InlineData("deny_restore_pending","monitor")][InlineData("deny_restore_pending","identity")]
    public void UncertainCommitDoesNotAuthorizeNormalizationOfUnrelatedDrift(string phase,string mutation)
    {
        var receipt=TrialDenyFixture.Receipt(phase);var actual=TrialDenyFixture.Drift(receipt.DeniedSnapshot!,mutation);
        Assert.Throws<InvalidDataException>(()=>TrialJournal.SelectDenyRecoveryAction(receipt,actual));
    }
    [Fact]
    public void RemovedReceiptCannotAdoptNewPresentObjectsWithReusedDeploymentIdentity()
    {
        var receipt=TrialDenyFixture.Receipt("removed");Assert.Throws<InvalidDataException>(()=>TrialJournal.SelectDenyRecoveryAction(receipt,receipt.Installed!));
    }
    [Theory]
    [InlineData("deny_pending")][InlineData("deny_restore_pending")]
    public void CorruptJournalDeniedImageIsRejectedBeforeInspectingEitherLiveImage(string phase)
    {
        var receipt=TrialDenyFixture.Receipt(phase);var corrupt=receipt with {DeniedSnapshot=TrialDenyFixture.Drift(receipt.DeniedSnapshot!,"signature")};
        Assert.Throws<InvalidDataException>(()=>TrialJournal.SelectDenyRecoveryAction(corrupt,receipt.Installed!));
        Assert.Throws<InvalidDataException>(()=>TrialJournal.SelectDenyRecoveryAction(corrupt,receipt.DeniedSnapshot!));
    }
}
