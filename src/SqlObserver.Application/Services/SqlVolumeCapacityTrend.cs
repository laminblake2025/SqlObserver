using SqlObserver.Domain.Collection;

namespace SqlObserver.Application.Services;

public sealed record SqlVolumeDailyCapacitySample(
    string VolumeKey, DateTimeOffset ObservedAtUtc, long TotalBytes, long AvailableBytes);

public sealed record SqlVolumeCapacityTrend(
    string VolumeKey, string State, int SampleDays, double? DaysUntilFull);

/// <summary>Conservative linear estimate from complete daily SQL-target capacity snapshots.</summary>
public static class SqlVolumeCapacityTrendEstimator
{
    public static SqlVolumeCapacityTrend Estimate(SqlVolumeObservation current,
        IReadOnlyList<SqlVolumeDailyCapacitySample> samples)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(samples);
        if (current.TotalBytes is null || current.AvailableBytes is null)
            return new(current.VolumeKey, "capacity_unknown", 0, null);
        if (current.AvailableBytes == 0)
            return new(current.VolumeKey, "full", 0, 0);
        if (samples.Any(sample => sample.VolumeKey != current.VolumeKey ||
                                  sample.TotalBytes != current.TotalBytes ||
                                  sample.AvailableBytes < 0 || sample.AvailableBytes > sample.TotalBytes))
            return new(current.VolumeKey, "capacity_changed", samples.Count, null);
        if (samples.Count < 5 ||
            samples[^1].ObservedAtUtc - samples[0].ObservedAtUtc < TimeSpan.FromDays(4) ||
            current.ObservedAtUtc - samples[^1].ObservedAtUtc > TimeSpan.FromDays(1))
            return new(current.VolumeKey, "insufficient_history", samples.Count, null);

        double origin = samples[0].ObservedAtUtc.ToUnixTimeSeconds() / 86400d;
        double meanX = samples.Average(sample => sample.ObservedAtUtc.ToUnixTimeSeconds() / 86400d - origin);
        double meanY = samples.Average(sample => (double)sample.AvailableBytes);
        double xx = 0, xy = 0, yy = 0;
        foreach (SqlVolumeDailyCapacitySample sample in samples)
        {
            double x = sample.ObservedAtUtc.ToUnixTimeSeconds() / 86400d - origin - meanX;
            double y = sample.AvailableBytes - meanY;
            xx += x * x;
            xy += x * y;
            yy += y * y;
        }
        if (xx <= 0 || yy <= 0)
            return new(current.VolumeKey, "not_depleting", samples.Count, null);
        double slope = xy / xx;
        if (slope >= 0)
            return new(current.VolumeKey, "not_depleting", samples.Count, null);
        double fit = xy * xy / (xx * yy);
        if (!double.IsFinite(fit) || fit < .8)
            return new(current.VolumeKey, "unstable_trend", samples.Count, null);
        double days = current.AvailableBytes.Value / -slope;
        if (!double.IsFinite(days) || days > 3650)
            return new(current.VolumeKey, "beyond_horizon", samples.Count, null);
        return new(current.VolumeKey, "estimated", samples.Count, Math.Max(0, days));
    }
}
