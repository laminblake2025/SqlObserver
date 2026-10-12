namespace SqlObserver.AgentActivityTrial;

/// <summary>Immutable grant policies; existing receipts and omitted arguments retain ExecuteOnlyV1.</summary>
public enum TrialInstallationPolicy
{
    ExecuteOnlyV1 = 1,
    MetadataVerificationV2 = 2
}
