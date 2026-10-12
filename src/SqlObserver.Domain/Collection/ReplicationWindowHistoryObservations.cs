namespace SqlObserver.Domain.Collection;

/// <summary>Retained attempt accounting, never a verdict about live replication health.</summary>
public sealed record ReplicationWindowHistoryCoverage(int RunCount,int CompleteRunCount,int GapRunCount,
    int FailedRunCount,int LostRunCount,int MissingPayloadRunCount,int EmptyRunCount,int RetainedRowCount,
    int UnboundSourceRowCount,bool HasVisibilityGap);
public sealed record ReplicationWindowHistoryReceipt(Guid RunId,long SampleTargetRevision,DateTimeOffset CompletedAtUtc,
    string Outcome,string? LossKind,long? MinimumLostItems,int ExpectedRowCount,int StoredRowCount,
    DateTimeOffset? MonitorObservedAtUtc,string State,string Reason);
/// <summary>Opaque stored identity has no publication/subscription incarnation guarantee.</summary>
public sealed record ReplicationWindowHistoryRow(string RowIdentity,string? SubscriptionFingerprint,
    DateTimeOffset MonitorObservedAtUtc,DateTimeOffset CollectedAtUtc,string Role,string ReportedStatus,
    string? PendingCommands,double? LatencySeconds,string SourceCoverage,bool StateAvailable,
    string? Topology,decimal? RatePerSecond,long? LastSuccessAgeSeconds);

public static class ReplicationWindowHistoryValidation
{
    public const int MaximumPageSize=100,MaximumRuns=20480,MaximumRowsPerRun=2048,MaximumRetainedRows=250000;
    public static readonly TimeSpan MaximumWindow=TimeSpan.FromDays(7),CursorLifetime=TimeSpan.FromMinutes(15);
    public static void Id(Guid value)=>TempDbWindowHistoryValidation.Id(value);
    public static void Revision(long value)=>TempDbWindowHistoryValidation.Revision(value);
    public static void Utc(DateTimeOffset value)=>TempDbWindowHistoryValidation.Utc(value);
    public static void Digest(string value)=>TempDbWindowHistoryValidation.Digest(value);
    public static void Window(DateTimeOffset from,DateTimeOffset to)=>TempDbWindowHistoryValidation.Window(from,to);
    public static void Coverage(ReplicationWindowHistoryCoverage c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if(c.RunCount is <0 or >MaximumRuns||c.CompleteRunCount<0||c.GapRunCount<0||c.CompleteRunCount+c.GapRunCount!=c.RunCount||
            c.FailedRunCount<0||c.FailedRunCount>c.GapRunCount||c.LostRunCount<0||c.LostRunCount>c.GapRunCount||
            c.MissingPayloadRunCount<0||c.MissingPayloadRunCount>c.GapRunCount||c.EmptyRunCount<0||c.EmptyRunCount>c.CompleteRunCount||
            c.RetainedRowCount is <0 or >MaximumRetainedRows||c.UnboundSourceRowCount is <0 or >MaximumRetainedRows||c.RetainedRowCount+c.UnboundSourceRowCount>MaximumRetainedRows||
            c.GapRunCount+c.UnboundSourceRowCount>0&&!c.HasVisibilityGap)throw new ArgumentException("Invalid replication source accounting.");
    }
    public static void Receipt(ReplicationWindowHistoryReceipt r,DateTimeOffset from,DateTimeOffset to,DateTimeOffset cutoff,long currentRevision)
    {
        ArgumentNullException.ThrowIfNull(r);Id(r.RunId);Revision(r.SampleTargetRevision);Utc(r.CompletedAtUtc);
        if(r.SampleTargetRevision>currentRevision||r.CompletedAtUtc<from||r.CompletedAtUtc>=to||r.CompletedAtUtc>cutoff||
            r.Outcome is not("succeeded" or "partial" or "timed_out" or "transient_failure" or "permanent_failure" or "permission_denied" or "unsupported" or "output_invalid" or "lease_lost" or "circuit_open")||
            r.LossKind is not(null or "none" or "source_row_limit" or "response_byte_limit" or "visibility_incomplete" or "output_validation_failure" or "ingestion_rejection" or "blocking_graph_limit" or "duplicate_overlap")||
            r.MinimumLostItems is <0 or >int.MaxValue||r.ExpectedRowCount is <0 or >MaximumRowsPerRun||r.StoredRowCount is <0 or >MaximumRowsPerRun||
            r.State is not("available" or "partial" or "unavailable")||r.Reason is not("observed_rows" or "observed_empty" or "collection_failed" or "collection_loss" or "payload_missing" or "stored_count_mismatch" or "source_clock_skew" or "source_schema_unsupported" or "source_partial" or "collection_gap"))
            throw new ArgumentException("Invalid replication receipt.");
        if(r.MonitorObservedAtUtc is{} observed)Utc(observed);
        if(r.StoredRowCount==0&&r.MonitorObservedAtUtc is not null)throw new ArgumentException("Empty receipt fabricated an observation time.");
        if(r.State=="available"&&(r.Outcome!="succeeded"||r.LossKind!="none"||r.MinimumLostItems!=0||r.ExpectedRowCount!=r.StoredRowCount||
            r.Reason!=(r.StoredRowCount==0?"observed_empty":"observed_rows")||r.StoredRowCount>0&&(r.MonitorObservedAtUtc is null||r.MonitorObservedAtUtc>cutoff||
            (r.MonitorObservedAtUtc.Value-r.CompletedAtUtc).Duration()>TimeSpan.FromMinutes(5)))||
            r.State!="available"&&r.Reason is "observed_rows" or "observed_empty")throw new ArgumentException("Incomplete receipt was presented as available.");
    }
    public static void Row(ReplicationWindowHistoryRow r)
    {
        ArgumentNullException.ThrowIfNull(r);Digest(r.RowIdentity);if(r.SubscriptionFingerprint is{} identity)Digest(identity);
        Utc(r.MonitorObservedAtUtc);Utc(r.CollectedAtUtc);
        if(r.Role is not("unknown" or "publisher" or "subscriber" or "distributor")||
            r.ReportedStatus is not("unknown" or "healthy" or "warning" or "failed" or "initializing" or "disabled")||
            r.SourceCoverage is not("complete" or "local_summary" or "visibility_gap")||
            r.LatencySeconds is{} latency&&(!double.IsFinite(latency)||latency is <0 or >86400)||
            r.Topology is not null||r.RatePerSecond is not null||r.LastSuccessAgeSeconds is not null)
            throw new ArgumentException("Invalid legacy replication row.");
        if(r.PendingCommands is{} commands&&TempDbWindowHistoryValidation.Number(commands)>2000000000)
            throw new ArgumentException("Pending commands exceeds its source bound.");
    }
}
