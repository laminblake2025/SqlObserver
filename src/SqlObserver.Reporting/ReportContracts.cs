using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlObserver.Domain.Authorization;
using SqlObserver.Domain.Coordination;

namespace SqlObserver.Reporting;

public static class ReportContract
{
    public const int Version = 1;
    public const int RequestBytes = 64 * 1024;
    public const int PageRows = 200;
    public const int HtmlRows = 2_000;
    public const int TotalRows = 10_000;
    public const int ResponseBytes = 1 * 1024 * 1024;
    public const int MaterializationBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan RepositoryTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TotalOperationTimeout = TimeSpan.FromSeconds(15);
    public const int MaximumActorConcurrency = 2;
    public const int MaximumGlobalConcurrency = 16;
    public static readonly TimeSpan MaterializationRetention = TimeSpan.FromHours(24);
}

public enum ReportKind { InstanceHealth, PerformanceWindow, IncidentEvidence, CapacityReadiness }

public static class ReportKindExtensions
{
    public static string ToWire(this ReportKind value) => value switch
    {
        ReportKind.InstanceHealth => "instance-health",
        ReportKind.PerformanceWindow => "performance-window",
        ReportKind.IncidentEvidence => "incident-evidence",
        ReportKind.CapacityReadiness => "capacity-readiness",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static bool TryParse(string? value, out ReportKind kind)
    {
        kind = value switch
        {
            "instance-health" => ReportKind.InstanceHealth,
            "performance-window" => ReportKind.PerformanceWindow,
            "incident-evidence" => ReportKind.IncidentEvidence,
            "capacity-readiness" => ReportKind.CapacityReadiness,
            _ => (ReportKind)(-1)
        };
        return (int)kind >= 0;
    }
}

public sealed record ReportSectionDefinition(string Key, string Title, IReadOnlyList<string> Columns)
{
    public ReportSectionDefinition(string key, string title, params string[] columns) : this(key, title, (IReadOnlyList<string>)columns) { }
}

public sealed record ReportDefinition(ReportKind Kind, int Version, string Title, IReadOnlyList<ReportSectionDefinition> Sections)
{
    public string ReportKind => Kind.ToWire();
}

public static class ReportCatalog
{
    private static readonly ReadOnlyCollection<ReportDefinition> Definitions = new(new[]
    {
        new ReportDefinition(ReportKind.InstanceHealth, ReportContract.Version, "Instance Health", new[]
        { new ReportSectionDefinition("health", "Health", "observedAtUtc", "metric", "value", "state") }),
        new ReportDefinition(ReportKind.PerformanceWindow, ReportContract.Version, "Performance Window", new[]
        { new ReportSectionDefinition("performance", "Performance", "observedAtUtc", "metric", "value", "coverage") }),
        new ReportDefinition(ReportKind.IncidentEvidence, ReportContract.Version, "Incident Evidence", new[]
        { new ReportSectionDefinition("incidents", "Incidents", "occurredAtUtc", "severity", "eventKind", "visibility") }),
        new ReportDefinition(ReportKind.CapacityReadiness, ReportContract.Version, "Capacity/Readiness", new[]
        { new ReportSectionDefinition("capacity", "Capacity", "observedAtUtc", "metric", "value", "state") })
    });

    public static IReadOnlyList<ReportDefinition> All => Definitions;
    public static ReportDefinition Get(ReportKind kind) => Definitions.Single(x => x.Kind == kind);
}

public sealed record ReportRequest(string ReportKind, Guid OperationId, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null)
{
    public ReportKind ParsedKind => ReportKindExtensions.TryParse(ReportKind, out ReportKind value) ? value : throw new ArgumentException("Unknown report kind.", nameof(ReportKind));
}

public sealed record ReportCreateRequest(Guid TargetId, ReportRequest Request, AuthorizationContext Authorization);
public sealed record ReportRun(Guid RunId, Guid TargetId, long TargetRevision, ReportKind Kind, int DefinitionVersion, DateTimeOffset SnapshotUtc, DateTimeOffset ExpiresAtUtc, string ParameterDigest, string State);
public sealed record ReportRow(long Ordinal, IReadOnlyDictionary<string, string?> Values);
public sealed record ReportSectionPage(ReportRun Run, string Section, IReadOnlyList<ReportRow> Rows, string? Cursor, bool HasMore);
public sealed record ReportAuditEvent(Guid TargetId, Guid? RunId, string ActorSid, string ActivityKind, string Outcome, string SafeDetail = "");
public sealed record ReportExportAudit(Guid TargetId, Guid RunId, string ActorSid, string Section, string Format, int RowCount);
public interface IReportAuditPort
{
    ValueTask AppendAsync(ReportAuditEvent audit, CancellationToken cancellationToken);
    ValueTask AppendExportAsync(ReportExportAudit exportRequest, CancellationToken cancellationToken)
        => AppendAsync(new ReportAuditEvent(exportRequest.TargetId, exportRequest.RunId, exportRequest.ActorSid, "export", "succeeded"), cancellationToken);
}

/// <summary>Reports are available only to the three target-scoped operational roles.</summary>
public static class ReportAuthorization
{
    public static bool CanAccess(Guid targetId, AuthorizationContext authorization)
    {
        if (targetId == Guid.Empty || authorization is null) return false;
        var target = new SqlObserver.Domain.Telemetry.MonitoredInstanceId(targetId);
        return authorization.CanAccess(ApplicationRole.Viewer, target)
            || authorization.CanAccess(ApplicationRole.Operator, target)
            || authorization.CanAccess(ApplicationRole.TargetAdministrator, target);
    }
}

public static class ReportRequestValidation
{
    public static void Validate(ReportCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        if (request.TargetId == Guid.Empty || request.Request.OperationId == Guid.Empty) throw new ArgumentException("Target and operation identifiers are required.");
        if (!ReportKindExtensions.TryParse(request.Request.ReportKind, out ReportKind kind)) throw new ArgumentException("Unknown report kind.");
        DateTimeOffset? from = request.Request.FromUtc; DateTimeOffset? to = request.Request.ToUtc;
        if (kind == ReportKind.InstanceHealth && (from is not null || to is not null)) throw new ArgumentException("Instance health does not accept a window.");
        if (kind != ReportKind.InstanceHealth && (from is null || to is null || from.Value.Offset != TimeSpan.Zero || to!.Value.Offset != TimeSpan.Zero || to <= from)) throw new ArgumentException("Reports require a UTC half-open window.");
        if (from is not null && to is not null)
        {
            TimeSpan max = kind == ReportKind.CapacityReadiness ? TimeSpan.FromDays(31) : TimeSpan.FromDays(7);
            if (to - from > max) throw new ArgumentException("The report window exceeds its bound.");
        }
        if (!ReportAuthorization.CanAccess(request.TargetId, request.Authorization)) throw new UnauthorizedAccessException();
    }

    public static byte[] CanonicalDigest(ReportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Deliberately omit operationId: retries with a different id are the same
        // logical request and are rejected only when the idempotency key conflicts.
        string canonical = request.FromUtc is { } from && request.ToUtc is { } to
            ? $"{{\"fromUtc\":\"{from:O}\",\"reportKind\":\"{request.ReportKind}\",\"toUtc\":\"{to:O}\"}}"
            : $"{{\"reportKind\":\"{request.ReportKind}\"}}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }
}

public interface IReportRepository
{
    ValueTask<ReportRun> CreateAsync(Guid targetId, ReportRequest request, string actorSid, CancellationToken cancellationToken);
    ValueTask<ReportRun?> GetRunAsync(Guid targetId, Guid runId, CancellationToken cancellationToken);
    ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, CancellationToken cancellationToken);
}

public interface IReportExpiryRepository
{
    ValueTask<int> ExpireAsync(int maximumRows, WorkerLeaseIdentity lease, CancellationToken cancellationToken);
}

public interface IReportService
{
    ValueTask<ReportRun> CreateAsync(ReportCreateRequest request, CancellationToken cancellationToken);
    ValueTask<ReportRun> GetRunAsync(Guid targetId, Guid runId, AuthorizationContext authorization, CancellationToken cancellationToken, bool auditTerminal = true);
    ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, AuthorizationContext authorization, CancellationToken cancellationToken, bool auditTerminal = true);
    ValueTask<IReadOnlyList<ReportRow>> ReadAllAsync(Guid targetId, Guid runId, string section, int maximumRows, int maximumBytes, AuthorizationContext authorization, CancellationToken cancellationToken, string exportFormat = "export", bool auditTerminal = true);
    ValueTask CompleteExportAsync(Guid targetId, Guid runId, string section, string format, int rowCount, AuthorizationContext authorization, CancellationToken cancellationToken, bool auditTerminal = true);
    ValueTask RecordTerminalAsync(Guid targetId, Guid? runId, string activityKind, string outcome, AuthorizationContext authorization, CancellationToken cancellationToken);
    ValueTask<ReportCatalogResult> CatalogAsync(CancellationToken cancellationToken);
}

public sealed record ReportCatalogResult(int Version, IReadOnlyList<ReportDefinition> Reports);

public sealed class ReportService(IReportRepository repository, IReportAuditPort audit) : IReportService
{
    private static readonly SemaphoreSlim GlobalCapacity = new(ReportContract.MaximumGlobalConcurrency, ReportContract.MaximumGlobalConcurrency);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ActorCapacity = new(StringComparer.Ordinal);
    public async ValueTask<ReportRun> CreateAsync(ReportCreateRequest request, CancellationToken cancellationToken)
    {
        CancellationToken operationToken = cancellationToken;
        try { ReportRequestValidation.Validate(request); }
        catch (UnauthorizedAccessException) { await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw; }
        catch (ArgumentException exception)
        {
            // Validation failures are terminal and must be observable without
            // allowing malformed requests to reach the repository.
            await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "failure", "failed", exception.Message), cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (!ReportAuthorization.CanAccess(request.TargetId, request.Authorization)) { await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw new UnauthorizedAccessException(); }
        SemaphoreSlim actor = ActorCapacity.GetOrAdd(request.Authorization.ActorSid.Value, static _ => new SemaphoreSlim(ReportContract.MaximumActorConcurrency, ReportContract.MaximumActorConcurrency));
        bool globalAcquired = GlobalCapacity.Wait(0, CancellationToken.None); bool actorAcquired = globalAcquired && actor.Wait(0, CancellationToken.None);
        if (!actorAcquired) { if (globalAcquired) GlobalCapacity.Release(); await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "failure", "failed", "capacity"), cancellationToken).ConfigureAwait(false); throw new ReportCapacityException(); }
        try { ReportRun result = await repository.CreateAsync(request.TargetId, request.Request, request.Authorization.ActorSid.Value, operationToken).ConfigureAwait(false); await AuditAsync(new ReportAuditEvent(request.TargetId, result.RunId, request.Authorization.ActorSid.Value, "create", "succeeded"), operationToken).ConfigureAwait(false); return result; }
        catch (TimeoutException) { await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw new TimeoutException("Report creation timed out."); }
        catch (ReportLimitException) { await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "oversize", "failed"), CancellationToken.None).ConfigureAwait(false); throw; }
        catch (ReportAuditException) { throw; }
        catch (Exception) { await AuditAsync(new ReportAuditEvent(request.TargetId, null, request.Authorization.ActorSid.Value, "failure", "failed"), CancellationToken.None).ConfigureAwait(false); throw; }
        finally { actor.Release(); GlobalCapacity.Release(); }
    }

    public async ValueTask<ReportRun> GetRunAsync(Guid targetId, Guid runId, AuthorizationContext authorization, CancellationToken cancellationToken, bool auditTerminal = true)
    {
        CancellationToken operationToken = cancellationToken;
        if (!CanAccess(targetId, authorization)) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw new UnauthorizedAccessException(); } ReportRun? run;
        try { run = await repository.GetRunAsync(targetId, runId, operationToken).ConfigureAwait(false); } catch (TimeoutException) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw; } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw new TimeoutException("Report read timed out."); } catch (ReportAuditException) { throw; } catch (Exception) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "failure", "failed"), CancellationToken.None).ConfigureAwait(false); throw; }
        if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "read", run is null ? "failed" : "succeeded"), operationToken).ConfigureAwait(false);
        return run ?? throw new KeyNotFoundException("Report run was not found or has expired.");
    }

    public async ValueTask<ReportSectionPage> ReadPageAsync(Guid targetId, Guid runId, string section, long afterOrdinal, int limit, AuthorizationContext authorization, CancellationToken cancellationToken, bool auditTerminal = true)
    {
        CancellationToken operationToken = cancellationToken;
        if (!CanAccess(targetId, authorization)) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw new UnauthorizedAccessException(); }
        if (section is null or { Length: > 64 } || afterOrdinal < 0 || limit < 1) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "failure", "failed"), cancellationToken).ConfigureAwait(false); throw new ArgumentException("Invalid page bounds."); }
        if (limit > ReportContract.PageRows) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "oversize", "failed"), cancellationToken).ConfigureAwait(false); throw new ReportLimitException("A report page may contain at most 200 rows."); }
        ReportSectionPage result; try { result = await repository.ReadPageAsync(targetId, runId, section, afterOrdinal, limit, operationToken).ConfigureAwait(false); } catch (TimeoutException) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw; } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw new TimeoutException("Report page read timed out."); } catch (ReportAuditException) { throw; } catch (Exception) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "failure", "failed"), CancellationToken.None).ConfigureAwait(false); throw; } if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "read", "succeeded"), operationToken).ConfigureAwait(false); return result;
    }

    public async ValueTask<IReadOnlyList<ReportRow>> ReadAllAsync(Guid targetId, Guid runId, string section, int maximumRows, int maximumBytes, AuthorizationContext authorization, CancellationToken cancellationToken, string exportFormat = "export", bool auditTerminal = true)
    {
        CancellationToken operationToken = cancellationToken;
        if (!CanAccess(targetId, authorization)) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw new UnauthorizedAccessException(); } if (maximumRows is < 1 or > ReportContract.TotalRows || maximumBytes is < 1 or > ReportContract.MaterializationBytes) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "oversize", "failed"), cancellationToken).ConfigureAwait(false); throw new ReportLimitException("Export bounds are invalid."); }
        var rows = new List<ReportRow>(); long after = 0;
        while (rows.Count < maximumRows)
        {
            ReportSectionPage page; try { page = await repository.ReadPageAsync(targetId, runId, section, after, Math.Min(ReportContract.PageRows, maximumRows - rows.Count), operationToken).ConfigureAwait(false); } catch (TimeoutException) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw; } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "timeout", "failed"), CancellationToken.None).ConfigureAwait(false); throw new TimeoutException("Report export timed out."); } catch (ReportAuditException) { throw; } catch (Exception) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "failure", "failed"), CancellationToken.None).ConfigureAwait(false); throw; } rows.AddRange(page.Rows.Take(Math.Min(ReportContract.PageRows, maximumRows - rows.Count))); if (!page.HasMore || page.Rows.Count == 0) break; after = rows[^1].Ordinal;
            if (rows.Sum(static row => row.Values.Sum(static pair => pair.Value is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(pair.Value))) > maximumBytes) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "oversize", "failed"), CancellationToken.None).ConfigureAwait(false); throw new ReportLimitException("Report export exceeds its byte bound."); }
        }
        if (rows.Count > maximumRows) throw new ReportLimitException("Report export exceeds its row bound.");
        if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "read", "succeeded"), operationToken).ConfigureAwait(false);
        return rows;
    }

    public async ValueTask CompleteExportAsync(Guid targetId, Guid runId, string section, string format, int rowCount, AuthorizationContext authorization, CancellationToken cancellationToken, bool auditTerminal = true)
    {
        if (!CanAccess(targetId, authorization)) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw new UnauthorizedAccessException(); }
        if (format is not ("html" or "csv") || section.Length is < 1 or > 64 || rowCount is < 0 or > ReportContract.TotalRows) { if (auditTerminal) await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "failure", "failed"), cancellationToken).ConfigureAwait(false); throw new ArgumentException("Invalid export completion."); }
        await AuditExportAsync(new ReportExportAudit(targetId, runId, authorization.ActorSid.Value, section, format, rowCount), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecordTerminalAsync(Guid targetId, Guid? runId, string activityKind, string outcome, AuthorizationContext authorization, CancellationToken cancellationToken)
    {
        if (!CanAccess(targetId, authorization)) { await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, "deny", "denied"), cancellationToken).ConfigureAwait(false); throw new UnauthorizedAccessException(); }
        await AuditAsync(new ReportAuditEvent(targetId, runId, authorization.ActorSid.Value, activityKind, outcome), cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<ReportCatalogResult> CatalogAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ReportCatalogResult(ReportContract.Version, ReportCatalog.All));
    private async ValueTask AuditAsync(ReportAuditEvent activity, CancellationToken cancellationToken) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(2)); try { await audit.AppendAsync(activity, timeout.Token).ConfigureAwait(false); } catch (Exception exception) when (exception is not ReportAuditException) { throw new ReportAuditException("Report terminal audit failed.", exception); } }
    private async ValueTask AuditExportAsync(ReportExportAudit export, CancellationToken cancellationToken) { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(2)); try { await audit.AppendExportAsync(export, timeout.Token).ConfigureAwait(false); } catch (Exception exception) when (exception is not ReportAuditException) { throw new ReportAuditException("Report terminal audit failed.", exception); } }
    private static bool CanAccess(Guid targetId, AuthorizationContext authorization) => ReportAuthorization.CanAccess(targetId, authorization);
}

public sealed class ReportCapacityException : Exception;
public sealed class ReportAuditException(string message, Exception? inner = null) : Exception(message, inner);
