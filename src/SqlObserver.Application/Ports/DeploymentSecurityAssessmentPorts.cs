using SqlObserver.Domain.Deployment;

namespace SqlObserver.Application.Ports;

/// <summary>Sanitized input to the ADR-0017 assessment. Values are facts, never source material.</summary>
public sealed class DeploymentSecurityAssessmentRequest
{
    public const int MaximumObservations = 12;

    public DeploymentSecurityAssessmentRequest(IReadOnlyList<DeploymentSecurityObservation>? observations = null)
    {
        var copy = new List<DeploymentSecurityObservation>(MaximumObservations);
        try
        {
            if (observations is not null)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                using IEnumerator<DeploymentSecurityObservation> enumerator = observations.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    // Check before reading Current so an infinite or oversized source is bounded.
                    if (copy.Count >= MaximumObservations)
                        throw new ArgumentException("The security observation bound was exceeded.", nameof(observations));
                    DeploymentSecurityObservation? observation = enumerator.Current;
                    if (observation is null || !Enum.IsDefined(observation.Disposition) ||
                        !IsSafeToken(observation.CheckId) || !IsSafeToken(observation.Code) ||
                        !DeploymentSecurityCheckCatalog.OrderedIds.Contains(observation.CheckId, StringComparer.Ordinal) ||
                        !ids.Add(observation.CheckId))
                        throw new ArgumentException("The security observations are invalid.", nameof(observations));
                    copy.Add(observation);
                }
            }

            Observations = Array.AsReadOnly(copy.ToArray());
        }
        catch (Exception)
        {
            // Provider/enumerator exception details must never reach the contract boundary.
            throw new ArgumentException("The security observations could not be validated.", nameof(observations));
        }
    }

    public IReadOnlyList<DeploymentSecurityObservation> Observations { get; }

    private static bool IsSafeToken(string value) => value.Length is > 0 and <= 64 && value[0] is >= 'a' and <= 'z' &&
        value.Skip(1).All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

public interface IDeploymentSecurityAssessmentService
{
    ValueTask<DeploymentSecurityAssessment> AssessAsync(DeploymentSecurityAssessmentRequest request, CancellationToken cancellationToken);
}

/// <summary>Optional adapter boundary for future evidence providers. It returns only safe facts.</summary>
public interface IDeploymentSecurityObservationPort
{
    ValueTask<IReadOnlyList<DeploymentSecurityObservation>> ObserveAsync(CancellationToken cancellationToken);
}
