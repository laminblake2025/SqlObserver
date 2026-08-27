using SqlObserver.Application.Ports;
using SqlObserver.Domain.Deployment;

namespace SqlObserver.Application.Services;

/// <summary>
/// Maps sanitized deployment facts to the fixed ADR-0017 v1 contract. No activation or mutation
/// decision is possible in this service.
/// </summary>
public sealed class DeploymentSecurityAssessmentService : IDeploymentSecurityAssessmentService
{
    private static readonly HashSet<string> UnsafeCodes = new(StringComparer.Ordinal)
    {
        "unsafe",
        "bypass",
        "anonymous",
        "inline_secret",
        "sensitive_content_enable",
        "sensitive_content_enabled",
        "sensitive_enable_unsafe",
        "trust_server",
        "trust_server_certificate",
        "clear_text",
        "cleartext",
        "anonymous_auth",
        "validation_bypass",
    };
    private readonly Func<DateTimeOffset> _clock;
    private readonly IDeploymentSecurityObservationPort? _observationPort;

    public DeploymentSecurityAssessmentService(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DeploymentSecurityAssessmentService(IDeploymentSecurityObservationPort observationPort, Func<DateTimeOffset>? clock = null)
    {
        _observationPort = observationPort ?? throw new ArgumentNullException(nameof(observationPort));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<DeploymentSecurityAssessment> AssessAsync(DeploymentSecurityAssessmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, DeploymentSecurityObservation> byId;
        try
        {
            IReadOnlyList<DeploymentSecurityObservation>? supplied = request.Observations;
            if (_observationPort is not null && request.Observations.Count == 0)
                supplied = await _observationPort.ObserveAsync(cancellationToken).ConfigureAwait(false);
            byId = ValidateObservations(supplied, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            byId = new Dictionary<string, DeploymentSecurityObservation>(StringComparer.Ordinal)
            {
                ["mcp_endpoint"] = new DeploymentSecurityObservation("mcp_endpoint", DeploymentSecurityObservationDisposition.Unsafe, "observation_failed"),
            };
        }
        DateTimeOffset evaluatedAt = _clock();
        if (evaluatedAt.Offset != TimeSpan.Zero) evaluatedAt = evaluatedAt.ToUniversalTime();
        var checks = new List<DeploymentSecurityAssessmentCheck>(DeploymentSecurityCheckCatalog.OrderedIds.Count);
        foreach (string id in DeploymentSecurityCheckCatalog.OrderedIds)
        {
            // Owner policy, topology, secret-store policy, and certificate lifecycle are owner-
            // and lab-dependent in ADR-0017 and therefore remain blocked in this local v1.
            if (id is "owner_policy" or "identity_topology" or "secret_store_policy" or "certificate_lifecycle")
            {
                checks.Add(new DeploymentSecurityAssessmentCheck(id, DeploymentSecurityCheckStatus.Blocked, id + "_unresolved"));
                continue;
            }

            if (!byId.TryGetValue(id, out DeploymentSecurityObservation? observation))
            {
                checks.Add(new DeploymentSecurityAssessmentCheck(id, DeploymentSecurityCheckStatus.Blocked, "observation_missing"));
                continue;
            }

            (DeploymentSecurityCheckStatus status, string code) = observation.Disposition switch
            {
                DeploymentSecurityObservationDisposition.Accepted when IsUnsafeCode(observation.Code) => (DeploymentSecurityCheckStatus.Failed, observation.Code),
                DeploymentSecurityObservationDisposition.Accepted => (DeploymentSecurityCheckStatus.Passed, observation.Code),
                DeploymentSecurityObservationDisposition.Unsafe => (DeploymentSecurityCheckStatus.Failed, observation.Code),
                DeploymentSecurityObservationDisposition.Ambiguous => (DeploymentSecurityCheckStatus.Blocked, "observation_ambiguous"),
                _ => (DeploymentSecurityCheckStatus.Blocked, "observation_missing"),
            };
            checks.Add(new DeploymentSecurityAssessmentCheck(id, status, code));
        }

        return new DeploymentSecurityAssessment(checks, evaluatedAt);
    }

    private static bool IsUnsafeCode(string code) => UnsafeCodes.Contains(code.Replace('-', '_'));

    private static Dictionary<string, DeploymentSecurityObservation> ValidateObservations(
        IReadOnlyList<DeploymentSecurityObservation>? supplied,
        CancellationToken cancellationToken)
    {
        if (supplied is null) throw new InvalidDataException("Observations are missing.");
        var result = new Dictionary<string, DeploymentSecurityObservation>(DeploymentSecurityAssessmentRequest.MaximumObservations, StringComparer.Ordinal);
        using IEnumerator<DeploymentSecurityObservation> enumerator = supplied.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext()) break;
            if (result.Count >= DeploymentSecurityAssessmentRequest.MaximumObservations)
                throw new InvalidDataException("Observation bound exceeded.");
            DeploymentSecurityObservation? observation = enumerator.Current;
            if (observation is null || !Enum.IsDefined(observation.Disposition) || !IsSafeToken(observation.CheckId) || !IsSafeToken(observation.Code) ||
                !DeploymentSecurityCheckCatalog.OrderedIds.Contains(observation.CheckId, StringComparer.Ordinal) || result.ContainsKey(observation.CheckId))
                throw new InvalidDataException("Observation set is invalid.");
            result.Add(observation.CheckId, observation);
        }
        return result;
    }

    private static bool IsSafeToken(string value) => value.Length is > 0 and <= 64 && value[0] is >= 'a' and <= 'z' &&
        value.Skip(1).All(static c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}
