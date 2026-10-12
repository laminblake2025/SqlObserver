using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Images;
using Npgsql;
using Testcontainers.PostgreSql;

namespace SqlObserver.IntegrationTests.PostgreSql;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IsolatedClockPostgreSqlCollectionDefinition : ICollectionFixture<IsolatedClockPostgreSqlFixture>
{
    public const string Name = "PostgreSQL 18.4 isolated clock";
}

// Only this disposable container sees the offset. No external repository option
// is consulted, and the qualification image must already exist on the daemon.
public sealed partial class IsolatedClockPostgreSqlFixture : IAsyncLifetime
{
    public const string ImageSetting = "SQLOBSERVER_ISOLATED_CLOCK_POSTGRES_IMAGE";
    private const string ClockControl = "/opt/sqlobserver-clock/clock-control";
    private const int MaximumOffsetDays = 36500;
    private static readonly TimeSpan VerificationTolerance = TimeSpan.FromSeconds(2);
    private readonly PostgreSqlContainer _container;
    private int _offsetDays;
    private int _advancing;
    private bool _initialized;
    private bool _poisoned;

    public IsolatedClockPostgreSqlFixture()
    {
        string? image = Environment.GetEnvironmentVariable(ImageSetting);
        if (image is null || !ImageNamePattern().IsMatch(image))
            throw new InvalidOperationException($"{ImageSetting} must identify the prebuilt isolated-clock image.");

        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _container = new PostgreSqlBuilder(image)
            .WithImagePullPolicy(PullPolicy.Never)
            .WithDatabase("sqlobserver_isolated_clock_admin")
            .WithUsername("postgres")
            .WithPassword(password)
            .WithCleanUp(true)
            .Build();
    }

    public TimeSpan Offset => TimeSpan.FromDays(Volatile.Read(ref _offsetDays));

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ClockProbe probe = await ProbeAsync(CancellationToken.None);
        VerifyProbe(probe, 0);
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        DateTimeOffset repositoryTime = await ReadClockAsync(connection, CancellationToken.None);
        EnsureNear(repositoryTime - DateTimeOffset.UtcNow, TimeSpan.Zero, "Initial PostgreSQL clock is not real time.");
        _initialized = true;
    }

    public async Task DisposeAsync()
    {
        _initialized = false;
        await _container.DisposeAsync();
    }

    public async Task<RepositoryTestDatabase> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (Volatile.Read(ref _advancing) != 0)
            throw new InvalidOperationException("Database creation cannot overlap an isolated clock advance.");
        string databaseName = $"sqlobserver_{Guid.NewGuid():N}";
        string adminConnectionString = _container.GetConnectionString();
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"CREATE DATABASE \"{databaseName}\" TEMPLATE template0 ENCODING 'UTF8';", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false,
            ApplicationName = "SqlObserver.IsolatedClockPostgreSqlTests",
        };
        NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString.ConnectionString);
        return new RepositoryTestDatabase(_container, databaseName,
            connectionString.ConnectionString, adminConnectionString, source);
    }

    // Offsets are cumulative within the collection: callers must sample native
    // clock_timestamp(), rather than assume a later test starts at offset zero.
    public async Task AdvanceAsync(TimeSpan amount, CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        if (amount <= TimeSpan.Zero || amount.Ticks % TimeSpan.TicksPerDay != 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Only positive whole-day advances are permitted.");
        long days = amount.Ticks / TimeSpan.TicksPerDay;
        if (days > MaximumOffsetDays || days + Volatile.Read(ref _offsetDays) > MaximumOffsetDays)
            throw new ArgumentOutOfRangeException(nameof(amount), "The cumulative isolated offset cannot exceed 36500 days.");
        if (Interlocked.CompareExchange(ref _advancing, 1, 0) != 0)
            throw new InvalidOperationException("An isolated clock advance is already active.");

        try
        {
            int nextDays = checked(_offsetDays + (int)days);
            await using var connection = new NpgsqlConnection(_container.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            DateTimeOffset hostBefore = DateTimeOffset.UtcNow;
            long stopwatchBefore = Stopwatch.GetTimestamp();
            long repositoryBeforeStart = Stopwatch.GetTimestamp();
            DateTimeOffset repositoryBefore = await ReadClockAsync(connection, cancellationToken);
            long repositoryBeforeEnd = Stopwatch.GetTimestamp();
            long probeBeforeStart = Stopwatch.GetTimestamp();
            ClockProbe before = await ProbeAsync(cancellationToken);
            long probeBeforeEnd = Stopwatch.GetTimestamp();
            VerifyProbe(before, _offsetDays);

            // Argument vector only, fixed executable, canonical decimal day count;
            // no shell command, bind mount, host clock call or extra capability.
            var result = await _container.ExecAsync(
                new[] { ClockControl, "set-days", nextDays.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("The owned container refused its forward clock-file update.");

            // Reuse the same PostgreSQL connection: this verifies clock-file
            // reload in an already running native backend, not only new processes.
            long repositoryAfterStart = Stopwatch.GetTimestamp();
            DateTimeOffset repositoryAfter = await ReadClockAsync(connection, cancellationToken);
            long repositoryAfterEnd = Stopwatch.GetTimestamp();
            long probeAfterStart = Stopwatch.GetTimestamp();
            ClockProbe after = await ProbeAsync(cancellationToken);
            long probeAfterEnd = Stopwatch.GetTimestamp();
            TimeSpan hostElapsed = Stopwatch.GetElapsedTime(stopwatchBefore);
            TimeSpan hostWallElapsed = DateTimeOffset.UtcNow - hostBefore;
            VerifyProbe(after, nextDays);
            EnsureNear(hostWallElapsed, hostElapsed, "Host wall time changed independently of host monotonic time.");
            EnsureWithin(repositoryAfter - repositoryBefore - amount,
                Stopwatch.GetElapsedTime(repositoryBeforeEnd, repositoryAfterStart),
                Stopwatch.GetElapsedTime(repositoryBeforeStart, repositoryAfterEnd),
                "An existing PostgreSQL backend did not observe the isolated wall-clock advance.");
            TimeSpan kernelElapsed = Nanoseconds(after.KernelRealtimeNs - before.KernelRealtimeNs);
            EnsureWithin(kernelElapsed,
                Stopwatch.GetElapsedTime(probeBeforeEnd, probeAfterStart),
                Stopwatch.GetElapsedTime(probeBeforeStart, probeAfterEnd),
                "The container's unmodified kernel wall clock changed unexpectedly.");
            EnsureNear(Nanoseconds(after.MonotonicNs - before.MonotonicNs), kernelElapsed,
                "Container monotonic time was changed by the isolated wall-clock advance.");
            EnsureNear(Nanoseconds(after.KernelMonotonicNs - before.KernelMonotonicNs), kernelElapsed,
                "Container native monotonic time changed independently of real elapsed time.");
            Volatile.Write(ref _offsetDays, nextDays);
        }
        catch
        {
            // A cancelled/failed exec may have already written the file. Never
            // allow a later test to treat an uncertain offset as a verified clock.
            _poisoned = true;
            throw;
        }
        finally
        {
            Volatile.Write(ref _advancing, 0);
        }
    }

    private void EnsureUsable()
    {
        if (!_initialized || _poisoned)
            throw new InvalidOperationException("The isolated-clock fixture is not initialized or its clock verification failed.");
    }

    private async Task<ClockProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var result = await _container.ExecAsync(new[] { ClockControl, "probe" }, cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The prebuilt image's isolated clock probe refused its environment.");
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        if (root.GetProperty("contract").GetString() != "isolated-forward-days-v1")
            throw new InvalidOperationException("The prebuilt image has an unsupported isolated clock contract.");
        return new ClockProbe(root.GetProperty("offsetDays").GetInt32(),
            ParseNanoseconds(root, "fakeRealtimeNs"), ParseNanoseconds(root, "kernelRealtimeNs"),
            ParseNanoseconds(root, "monotonicNs"), ParseNanoseconds(root, "kernelMonotonicNs"));
    }

    private static long ParseNanoseconds(JsonElement value, string name) =>
        long.Parse(value.GetProperty(name).GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);

    private static void VerifyProbe(ClockProbe probe, int expectedDays)
    {
        if (probe.OffsetDays != expectedDays)
            throw new InvalidOperationException("The isolated clock file has an unexpected cumulative offset.");
        EnsureNear(Nanoseconds(probe.FakeRealtimeNs - probe.KernelRealtimeNs), TimeSpan.FromDays(expectedDays),
            "The preload wall-clock offset does not match the verified clock file.");
        EnsureNear(Nanoseconds(probe.MonotonicNs - probe.KernelMonotonicNs), TimeSpan.Zero,
            "The preload changed native monotonic time.");
    }

    private static TimeSpan Nanoseconds(long nanoseconds) => TimeSpan.FromTicks(nanoseconds / 100);

    private static void EnsureNear(TimeSpan actual, TimeSpan expected, string message)
    {
        if ((actual - expected).Duration() > VerificationTolerance)
            throw new InvalidOperationException(message);
    }

    private static void EnsureWithin(TimeSpan actual, TimeSpan lower, TimeSpan upper, string message)
    {
        // Exec and SQL transport may be slow. Bracket the actual native sample
        // rather than equating its sampling instant with command completion.
        if (actual < lower - VerificationTolerance || actual > upper + VerificationTolerance)
            throw new InvalidOperationException(message);
    }

    private static async Task<DateTimeOffset> ReadClockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp();", connection);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateTime { Kind: DateTimeKind.Utc } timestamp
            ? new DateTimeOffset(timestamp)
            : throw new InvalidOperationException("PostgreSQL returned an invalid native UTC clock value.");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._:/@-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageNamePattern();

    private sealed record ClockProbe(int OffsetDays, long FakeRealtimeNs, long KernelRealtimeNs,
        long MonotonicNs, long KernelMonotonicNs);
}
