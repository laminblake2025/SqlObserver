namespace SqlObserver.Domain.Collection;

/// <summary>Distinguishes job outcomes from step diagnostics without changing stored event identities.</summary>
public static class SqlAgentFailureSemantics
{
    public static bool IsJobOutcome(SqlAgentFailureObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.StepId == 0;
    }

    public static bool CountsAsJobFailure(SqlAgentFailureObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.StepId == 0 && observation.RunStatus == 0 && observation.FailureKind == AgentFailureKind.Failed;
    }
}
