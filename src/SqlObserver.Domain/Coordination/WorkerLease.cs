namespace SqlObserver.Domain.Coordination;

/// <summary>A stable, bounded identity for one independently coordinated unit of work.</summary>
public sealed record WorkerLeaseKey
{
    public const int MaximumLength = 256;

    public WorkerLeaseKey(string value)
    {
        Value = DomainValidation.RequireAsciiToken(
            value,
            nameof(value),
            MaximumLength,
            static character => DomainValidation.IsAsciiLetter(character) ||
                DomainValidation.IsAsciiDigit(character) ||
                character is '.' or '_' or '-' or ':' or '/',
            requireLeadingLetter: true);
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>The unique execution that owns a lease, not a reusable machine name.</summary>
public sealed record WorkerExecutionId
{
    public WorkerExecutionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A worker execution identifier cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString("D");
}

/// <summary>A positive, monotonically increasing repository-issued fencing value.</summary>
public sealed record FencingToken
{
    public FencingToken(long value)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "A fencing token must be positive.");
        }

        Value = value;
    }

    public long Value { get; }
}

/// <summary>A deliberately short lease time-to-live.</summary>
public sealed record WorkerLeaseDuration
{
    public static readonly TimeSpan Minimum = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Maximum = TimeSpan.FromMinutes(10);

    public WorkerLeaseDuration(TimeSpan value)
    {
        if (value < Minimum || value > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A worker lease duration must be between {Minimum} and {Maximum}.");
        }

        Value = value;
    }

    public TimeSpan Value { get; }
}

/// <summary>The key, unique owner, and fencing value required for lease-protected work.</summary>
public sealed record WorkerLeaseIdentity
{
    public WorkerLeaseIdentity(
        WorkerLeaseKey key,
        WorkerExecutionId owner,
        FencingToken fencingToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(fencingToken);

        Key = key;
        Owner = owner;
        FencingToken = fencingToken;
    }

    public WorkerLeaseKey Key { get; }

    public WorkerExecutionId Owner { get; }

    public FencingToken FencingToken { get; }
}

/// <summary>A repository-time lease. Worker wall-clock time is not part of ownership.</summary>
public sealed class WorkerLease
{
    public WorkerLease(
        WorkerLeaseIdentity identity,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);

        acquiredAtUtc = DomainValidation.RequireUtc(acquiredAtUtc, nameof(acquiredAtUtc));
        renewedAtUtc = DomainValidation.RequireUtc(renewedAtUtc, nameof(renewedAtUtc));
        expiresAtUtc = DomainValidation.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));

        if (renewedAtUtc < acquiredAtUtc)
        {
            throw new ArgumentException("Renewal cannot precede acquisition.", nameof(renewedAtUtc));
        }

        if (expiresAtUtc <= renewedAtUtc)
        {
            throw new ArgumentException("Expiry must follow the latest renewal.", nameof(expiresAtUtc));
        }

        _ = new WorkerLeaseDuration(expiresAtUtc - renewedAtUtc);

        Identity = identity;
        AcquiredAtUtc = acquiredAtUtc;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public WorkerLeaseIdentity Identity { get; }

    public DateTimeOffset AcquiredAtUtc { get; }

    public DateTimeOffset RenewedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public WorkerLeaseDuration TimeToLive => new(ExpiresAtUtc - RenewedAtUtc);
}

public enum LeaseAcquisitionStatus
{
    Acquired = 1,
    Contended = 2,
}

/// <summary>A bounded acquire response that never guesses ownership.</summary>
public sealed class LeaseAcquisitionResult
{
    private LeaseAcquisitionResult(
        LeaseAcquisitionStatus status,
        WorkerLease? lease,
        DateTimeOffset repositoryTimeUtc)
    {
        repositoryTimeUtc = DomainValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));

        if (lease is not null &&
            (repositoryTimeUtc < lease.AcquiredAtUtc || repositoryTimeUtc >= lease.ExpiresAtUtc))
        {
            throw new ArgumentException(
                "An acquired lease must be current at the reported repository time.",
                nameof(repositoryTimeUtc));
        }

        Status = status;
        Lease = lease;
        RepositoryTimeUtc = repositoryTimeUtc;
    }

    public LeaseAcquisitionStatus Status { get; }

    public WorkerLease? Lease { get; }

    public DateTimeOffset RepositoryTimeUtc { get; }

    public static LeaseAcquisitionResult Acquired(WorkerLease lease, DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new LeaseAcquisitionResult(LeaseAcquisitionStatus.Acquired, lease, repositoryTimeUtc);
    }

    public static LeaseAcquisitionResult Contended(DateTimeOffset repositoryTimeUtc) =>
        new(LeaseAcquisitionStatus.Contended, lease: null, repositoryTimeUtc);
}

public enum LeaseRenewalStatus
{
    Renewed = 1,
    OwnershipLost = 2,
}

/// <summary>A renewal response; ownership loss carries no usable lease.</summary>
public sealed class LeaseRenewalResult
{
    private LeaseRenewalResult(
        LeaseRenewalStatus status,
        WorkerLease? lease,
        DateTimeOffset repositoryTimeUtc)
    {
        repositoryTimeUtc = DomainValidation.RequireUtc(repositoryTimeUtc, nameof(repositoryTimeUtc));

        if (lease is not null &&
            (repositoryTimeUtc < lease.RenewedAtUtc || repositoryTimeUtc >= lease.ExpiresAtUtc))
        {
            throw new ArgumentException(
                "A renewed lease must be current at the reported repository time.",
                nameof(repositoryTimeUtc));
        }

        Status = status;
        Lease = lease;
        RepositoryTimeUtc = repositoryTimeUtc;
    }

    public LeaseRenewalStatus Status { get; }

    public WorkerLease? Lease { get; }

    public DateTimeOffset RepositoryTimeUtc { get; }

    public static LeaseRenewalResult Renewed(WorkerLease lease, DateTimeOffset repositoryTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new LeaseRenewalResult(LeaseRenewalStatus.Renewed, lease, repositoryTimeUtc);
    }

    public static LeaseRenewalResult OwnershipLost(DateTimeOffset repositoryTimeUtc) =>
        new(LeaseRenewalStatus.OwnershipLost, lease: null, repositoryTimeUtc);
}

public enum LeaseOwnershipStatus
{
    Current = 1,
    NotCurrent = 2,
}

public enum LeaseReleaseStatus
{
    Released = 1,
    NotOwned = 2,
}
