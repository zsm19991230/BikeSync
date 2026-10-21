namespace BikeSync.Core.Models;

public enum ActivityFileFormat
{
    Fit,
    Gpx,
    Tcx,
    Kml,
}

public static class ActivityFileFormatExtensions
{
    public static string Extension(this ActivityFileFormat format) => format switch
    {
        ActivityFileFormat.Fit => "fit",
        ActivityFileFormat.Gpx => "gpx",
        ActivityFileFormat.Tcx => "tcx",
        ActivityFileFormat.Kml => "kml",
        _ => "fit",
    };

    public static string ContentType(this ActivityFileFormat format) => format switch
    {
        ActivityFileFormat.Fit => "application/vnd.ant.fit",
        ActivityFileFormat.Gpx => "application/gpx+xml",
        ActivityFileFormat.Tcx => "application/vnd.garmin.tcx+xml",
        ActivityFileFormat.Kml => "application/vnd.google-earth.kml+xml",
        _ => "application/octet-stream",
    };

    public static ActivityFileFormat? FromExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "fit" => ActivityFileFormat.Fit,
            "gpx" => ActivityFileFormat.Gpx,
            "tcx" => ActivityFileFormat.Tcx,
            "kml" => ActivityFileFormat.Kml,
            _ => null,
        };
    }
}

/// <summary>A normalized activity summary displayed in the library.</summary>
public sealed record ActivitySummary
{
    public required string Id { get; init; }
    public required PlatformId Platform { get; init; }
    public required Guid AccountId { get; init; }
    public required string AccountName { get; init; }
    public required string Name { get; init; }
    public string SportType { get; init; } = "Ride";
    public DateTimeOffset StartTime { get; init; }
    public DateTime? LocalStartTime { get; init; }
    public double DistanceMeters { get; init; }
    public int MovingTimeSeconds { get; init; }
    public int ElapsedTimeSeconds { get; init; }
    public double ElevationGainMeters { get; init; }
    public double? AveragePowerWatts { get; init; }
    public int? AverageHeartRate { get; init; }
    public bool HasGps { get; init; } = true;
    public string? SourceFileType { get; init; }
    public string? WebUrl { get; init; }

    public string PlatformName => PlatformCatalog.Get(Platform).DisplayName;
    public bool IsCycling => CyclingSports.IsCycling(SportType);
    public DateTime DisplayStartTime => LocalStartTime ?? StartTime.LocalDateTime;
    public TimeSpan MovingTime => TimeSpan.FromSeconds(Math.Max(0, MovingTimeSeconds));
    public TimeSpan ElapsedTime => TimeSpan.FromSeconds(Math.Max(0, ElapsedTimeSeconds));
}

public sealed class ActivityFile
{
    public required byte[] Content { get; init; }
    public required string FileName { get; init; }
    public required ActivityFileFormat Format { get; init; }
    public string? SourceActivityId { get; init; }
    /// <summary>Optional source FTP used to enrich FIT power metrics for iGPSPORT.</summary>
    public double? SourceFtpWatts { get; init; }
    public long SizeBytes => Content.LongLength;

    public string DataType => Format.Extension();

    public static string SafeFileName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name
            .Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray())
            .Trim()
            .Trim('.');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        return cleaned.Length > 90 ? cleaned[..90] : cleaned;
    }
}

public enum DateRangePreset
{
    Last30Days,
    Last90Days,
    ThisYear,
    Custom,
}

public sealed record ActivityQuery
{
    public DateTimeOffset From { get; init; } = DateTimeOffset.Now.AddDays(-90);
    public DateTimeOffset To { get; init; } = DateTimeOffset.Now;
    public DateRangePreset Preset { get; init; } = DateRangePreset.Last90Days;
    public bool CyclingOnly { get; init; } = true;
    public int MaxResults { get; init; } = 200;
    public string? Search { get; init; }

    public static ActivityQuery ForPreset(DateRangePreset preset, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var now = DateTimeOffset.Now;
        return preset switch
        {
            DateRangePreset.Last30Days => new ActivityQuery
            {
                Preset = preset,
                From = DateTimeOffset.Now.AddDays(-30),
                To = now,
            },
            DateRangePreset.Last90Days => new ActivityQuery
            {
                Preset = preset,
                From = DateTimeOffset.Now.AddDays(-90),
                To = now,
            },
            DateRangePreset.ThisYear => new ActivityQuery
            {
                Preset = preset,
                From = new DateTimeOffset(new DateTime(now.Year, 1, 1), TimeZoneInfo.Local.GetUtcOffset(new DateTime(now.Year, 1, 1))),
                To = now,
            },
            _ => new ActivityQuery
            {
                Preset = DateRangePreset.Custom,
                From = from ?? now.AddDays(-90),
                To = to ?? now,
            },
        };
    }
}
