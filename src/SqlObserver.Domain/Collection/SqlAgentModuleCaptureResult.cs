namespace SqlObserver.Domain.Collection;

/// <summary>Bounded source evidence with sanitized authorization provenance. Does not admit running or elapsed-time metrics.</summary>
public sealed class SqlAgentModuleCaptureResult
{
    public SqlAgentModuleCaptureResult(SqlAgentActivityCapture capture, SqlAgentModuleEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(enrollment);
        if (capture.TargetId != enrollment.TargetId || capture.TargetRevision != enrollment.TargetRevision ||
            !enrollment.IsActiveAt(capture.Before.SourceObservedAtUtc) || !enrollment.IsActiveAt(capture.After.SourceObservedAtUtc))
            throw new ArgumentException("Module capture does not belong to the authorized target and time window.", nameof(capture));
        Capture = capture; EnrollmentRevision = enrollment.Revision; EnrollmentId = enrollment.EnrollmentId;
        EvidenceSha256 = enrollment.EvidenceSha256;
    }

    public SqlAgentActivityCapture Capture { get; }
    public long EnrollmentRevision { get; }
    public Guid EnrollmentId { get; }
    public string EvidenceSha256 { get; }
    public SqlAgentActivityNativeQualification NativeQualification => Capture.NativeQualification;
    public bool ProductionAdmitted => Capture.ProductionAdmitted;
    public bool ServiceSessionAssociationQualified => Capture.ServiceSessionAssociationQualified;
    public override string ToString() => "SQL Agent module source evidence";
}
