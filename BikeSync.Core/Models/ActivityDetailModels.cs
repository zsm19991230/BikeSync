namespace BikeSync.Core.Models;

/// <summary>One normalized sample from a FIT/GPX/TCX activity file.</summary>
public sealed record ActivityTrackPoint(
    DateTimeOffset? Timestamp,
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    double? SpeedMetersPerSecond,
    int? HeartRate,
    int? Cadence,
    int? PowerWatts,
    double? DistanceMeters);

/// <summary>Track samples plus the metrics derived from them.</summary>
public sealed class ActivityTrackData
{
    public static ActivityTrackData Empty { get; } = new(Array.Empty<ActivityTrackPoint>());

    public ActivityTrackData(IReadOnlyList<ActivityTrackPoint> points)
    {
        Points = points;

        var powers = points
            .Where(p => p.PowerWatts is not null)
            .Select(p => (double)p.PowerWatts!.Value)
            .ToArray();
        if (powers.Length > 0 && powers.Sum() > 0)
        {
            AveragePowerWatts = powers.Average();
            MaxPowerWatts = powers.Max();
        }

        var heartRates = points
            .Where(p => p.HeartRate is > 0)
            .Select(p => p.HeartRate!.Value)
            .ToArray();
        if (heartRates.Length > 0)
        {
            AverageHeartRate = (int)Math.Round(heartRates.Average(), MidpointRounding.AwayFromZero);
            MaxHeartRate = heartRates.Max();
        }

        var cadences = points
            .Where(p => p.Cadence is > 0)
            .Select(p => p.Cadence!.Value)
            .ToArray();
        if (cadences.Length > 0)
        {
            AverageCadence = (int)Math.Round(cadences.Average(), MidpointRounding.AwayFromZero);
        }

        var speeds = points
            .Where(p => p.SpeedMetersPerSecond is > 0)
            .Select(p => p.SpeedMetersPerSecond!.Value)
            .ToArray();
        if (speeds.Length > 0)
        {
            AverageSpeedMetersPerSecond = speeds.Average();
            MaxSpeedMetersPerSecond = speeds.Max();
        }

        double ascent = 0;
        double descent = 0;
        double? previousAltitude = null;
        foreach (var point in points)
        {
            if (point.AltitudeMeters is not { } altitude)
            {
                continue;
            }

            if (previousAltitude is { } previous)
            {
                var delta = altitude - previous;
                if (delta > 0)
                {
                    ascent += delta;
                }
                else
                {
                    descent -= delta;
                }
            }

            previousAltitude = altitude;
        }

        AscentMeters = ascent > 0 ? ascent : null;
        DescentMeters = descent > 0 ? descent : null;

        var first = points.FirstOrDefault(p => p.Timestamp is not null);
        var last = points.LastOrDefault(p => p.Timestamp is not null);
        StartTime = first?.Timestamp;
        EndTime = last?.Timestamp;
        GpsPointCount = points.Count(p => p.Latitude is not null && p.Longitude is not null);
    }

    public IReadOnlyList<ActivityTrackPoint> Points { get; }
    public int GpsPointCount { get; }
    public bool HasGps => GpsPointCount >= 2;
    public double? AveragePowerWatts { get; }
    public double? MaxPowerWatts { get; }
    public int? AverageHeartRate { get; }
    public int? MaxHeartRate { get; }
    public int? AverageCadence { get; }
    public double? AverageSpeedMetersPerSecond { get; }
    public double? MaxSpeedMetersPerSecond { get; }
    public double? AscentMeters { get; }
    public double? DescentMeters { get; }
    public DateTimeOffset? StartTime { get; }
    public DateTimeOffset? EndTime { get; }
}
