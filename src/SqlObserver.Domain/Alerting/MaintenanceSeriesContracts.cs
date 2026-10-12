using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlObserver.Domain.Alerting;

public enum MaintenanceSeriesFrequency { Daily = 1, Weekly = 2, Monthly = 3 }
public enum MaintenanceFoldPolicy { Earlier = 1, Later = 2 }

/// <summary>A finite civil schedule. Time zone rules are resolved only by the server.</summary>
public sealed class MaintenanceSeriesDefinition
{
    public static string RequireReason(string reason) => DomainValidation.RequireSafeText(reason, nameof(reason), 512);

    public MaintenanceSeriesDefinition(string timeZoneId, DateOnly startDate, DateOnly endDate,
        TimeOnly localStartTime, int durationMinutes, MaintenanceSeriesFrequency frequency, int interval,
        IReadOnlyList<int> weekdays, int? dayOfMonth, string gapPolicy, MaintenanceFoldPolicy foldPolicy, string reason)
    {
        Reason = DomainValidation.RequireSafeText(reason, nameof(reason), 512);
        if (gapPolicy != "skip") throw new ArgumentException("The explicit gap policy must be skip.", nameof(gapPolicy));
        GapPolicy = gapPolicy;
        ArgumentNullException.ThrowIfNull(weekdays);
        if (!Enum.IsDefined(frequency) || !Enum.IsDefined(foldPolicy)) throw new ArgumentOutOfRangeException(nameof(frequency));
        if (startDate.Year > 9994 || endDate < startDate || endDate > startDate.AddYears(5))
            throw new ArgumentException("A finite end date within five years of the anchor is required.", nameof(endDate));
        if (localStartTime.Ticks % TimeSpan.TicksPerMinute != 0) throw new ArgumentException("The local start must use HH:mm precision.", nameof(localStartTime));
        if (durationMinutes is < 1 or > 10080) throw new ArgumentOutOfRangeException(nameof(durationMinutes));
        int maximumInterval = frequency switch { MaintenanceSeriesFrequency.Daily => 365, MaintenanceSeriesFrequency.Weekly => 52, _ => 12 };
        if (interval < 1 || interval > maximumInterval) throw new ArgumentOutOfRangeException(nameof(interval));
        if (weekdays.Count > 7 || weekdays.Any(static day => day is < 0 or > 6) || weekdays.Distinct().Count() != weekdays.Count)
            throw new ArgumentException("Weekdays must be unique Sunday-zero values.", nameof(weekdays));
        if (frequency == MaintenanceSeriesFrequency.Weekly ? weekdays.Count == 0 || dayOfMonth is not null : weekdays.Count != 0)
            throw new ArgumentException("Only weekly schedules accept a nonempty weekday selection.", nameof(weekdays));
        if (frequency == MaintenanceSeriesFrequency.Monthly ? dayOfMonth is not (>= 1 and <= 31) : dayOfMonth is not null)
            throw new ArgumentException("Only monthly schedules require a day of month.", nameof(dayOfMonth));
        _ = MaintenanceSeriesExpansion.ResolveZone(timeZoneId);
        TimeZoneId = timeZoneId;
        StartDate = startDate; EndDate = endDate; LocalStartTime = localStartTime; DurationMinutes = durationMinutes;
        Frequency = frequency; Interval = interval; Weekdays = new ReadOnlyCollection<int>(weekdays.Order().ToArray());
        DayOfMonth = dayOfMonth; FoldPolicy = foldPolicy;
    }
    public string TimeZoneId { get; }
    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }
    public TimeOnly LocalStartTime { get; }
    public int DurationMinutes { get; }
    public MaintenanceSeriesFrequency Frequency { get; }
    public int Interval { get; }
    public IReadOnlyList<int> Weekdays { get; }
    public int? DayOfMonth { get; }
    public MaintenanceFoldPolicy FoldPolicy { get; }
    public string GapPolicy { get; }
    public string Reason { get; }

    public string CanonicalPayload => string.Join('|', Frequency.ToString().ToLowerInvariant(), Interval.ToString(CultureInfo.InvariantCulture),
        StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        LocalStartTime.ToString("HH:mm", CultureInfo.InvariantCulture), DurationMinutes.ToString(CultureInfo.InvariantCulture), TimeZoneId,
        string.Join(',', Weekdays), DayOfMonth?.ToString(CultureInfo.InvariantCulture) ?? "<null>", GapPolicy, FoldPolicy.ToString().ToLowerInvariant(), Reason);
    public string DefinitionDigest => MaintenanceSeriesExpansion.Digest(CanonicalPayload);
}

public sealed record MaintenanceSeriesOccurrence(Guid WindowId, DateOnly LocalDate, DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc, int UtcOffsetMinutes, bool Ambiguous, string ExpansionVersion);
public sealed record MaintenanceSeriesSkippedDate(string LocalDate, string Reason);
public sealed record MaintenanceSeriesExpansionResult(IReadOnlyList<MaintenanceSeriesOccurrence> Occurrences,
    IReadOnlyList<MaintenanceSeriesSkippedDate> SkippedDates);

public static class MaintenanceSeriesExpansion
{
    public const string Version = "civil-v1";
    public const int MaximumOccurrences = 2048;

    public static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 128 ||
            timeZoneId.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '_' and not '-' and not '+') ||
            timeZoneId.StartsWith("Etc/GMT", StringComparison.Ordinal) ||
            !(timeZoneId == "UTC" || TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out _)))
            throw new ArgumentException("A supported IANA named time zone is required.", nameof(timeZoneId));
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("The named time zone is unavailable on this server.", nameof(timeZoneId), error); }
    }

    public static Guid WindowId(Guid seriesId, DateOnly localDate)
    {
        if (seriesId == Guid.Empty) throw new ArgumentException("Series identity is required.", nameof(seriesId));
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{seriesId:D}|{localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }
    public static MaintenanceSeriesExpansionResult Expand(Guid seriesId, MaintenanceSeriesDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (seriesId == Guid.Empty) throw new ArgumentException("Series identity is required.", nameof(seriesId));
        TimeZoneInfo zone = ResolveZone(definition.TimeZoneId);
        var rows = new List<MaintenanceSeriesOccurrence>();
        var skipped = new List<MaintenanceSeriesSkippedDate>();
        if (definition.Frequency == MaintenanceSeriesFrequency.Monthly)
        {
            int months = (definition.EndDate.Year - definition.StartDate.Year) * 12 + definition.EndDate.Month - definition.StartDate.Month;
            DateOnly firstMonth = new(definition.StartDate.Year, definition.StartDate.Month, 1);
            for (int month = 0; month <= months; month += definition.Interval)
            {
                DateOnly candidate = firstMonth.AddMonths(month);
                int requestedDay = definition.DayOfMonth!.Value;
                if (requestedDay > DateTime.DaysInMonth(candidate.Year, candidate.Month))
                    skipped.Add(new($"{candidate.Year:D4}-{candidate.Month:D2}-{requestedDay:D2}", "missing_month_day"));
            }
        }
        int anchorWeekStart = definition.StartDate.DayNumber - (int)definition.StartDate.DayOfWeek;
        for (int day = definition.StartDate.DayNumber; day <= definition.EndDate.DayNumber; day++)
        {
            DateOnly date = DateOnly.FromDayNumber(day);
            bool matches = definition.Frequency switch
            {
                MaintenanceSeriesFrequency.Daily => (day - definition.StartDate.DayNumber) % definition.Interval == 0,
                MaintenanceSeriesFrequency.Weekly => ((day - anchorWeekStart) / 7) % definition.Interval == 0 && definition.Weekdays.Contains((int)date.DayOfWeek),
                _ => ((date.Year - definition.StartDate.Year) * 12 + date.Month - definition.StartDate.Month) % definition.Interval == 0 && date.Day == definition.DayOfMonth,
            };
            if (!matches) continue;
            DateTime local = date.ToDateTime(definition.LocalStartTime, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local)) { skipped.Add(new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "nonexistent_local_time")); continue; }
            bool fold = zone.IsAmbiguousTime(local);
            TimeSpan offset = fold ? (definition.FoldPolicy == MaintenanceFoldPolicy.Earlier ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetAmbiguousTimeOffsets(local).Min())
                : zone.GetUtcOffset(local);
            DateTimeOffset start = new DateTimeOffset(local, offset).ToUniversalTime();
            rows.Add(new(WindowId(seriesId, date), date, start, start.AddMinutes(definition.DurationMinutes), checked((int)offset.TotalMinutes), fold, Version));
            if (rows.Count > MaximumOccurrences) throw new ArgumentException("The schedule exceeds 2,048 actual occurrences.", nameof(definition));
        }
        return new(rows.AsReadOnly(), skipped.AsReadOnly());
    }

    public static string Digest(string canonicalPayload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPayload))).ToLowerInvariant();
}
