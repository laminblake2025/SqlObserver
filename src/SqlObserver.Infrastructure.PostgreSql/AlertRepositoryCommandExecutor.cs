using SqlObserver.Application.Ports;
using SqlObserver.Domain.Auditing;

namespace SqlObserver.Infrastructure.PostgreSql;

/// <summary>Inspectable command seam used by production repository contract tests.</summary>
public sealed record AlertRepositoryAdminCommand(
    string Statement,
    IReadOnlyDictionary<string, object?> Parameters,
    AdministrativeAuditEnvelope Audit,
    RepositoryCallTimeout Timeout);

/// <summary>
/// Executes an alert administrative command while preserving the repository's
/// SQL, transaction, parameter, timeout, and row-mapping boundary.
/// </summary>
public interface IAlertRepositoryCommandExecutor
{
    ValueTask<AdministrativeAuditReceipt> ExecuteAsync(
        AlertRepositoryAdminCommand command,
        CancellationToken cancellationToken);
}
