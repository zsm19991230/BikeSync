using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Core.Services;

/// <summary>
/// Extracts a normalized GPS/telemetry track from the activity files returned by the
/// platform providers. Parsing is best-effort: a malformed optional section must not
/// prevent the detail view from showing the summary metrics.
/// </summary>
public static class ActivityTrackReader
{
    public static ActivityTrackData Read(
        byte[] content,
        ActivityFileFormat format,
        DateTimeOffset fallbackStart)
    {
        try
        {
            var points = format switch
            {
                ActivityFileFormat.Fit => ReadFit(content, fallbackStart),
                ActivityFileFormat.Gpx => ReadGpx(content, fallbackStart),
                ActivityFileFormat.Tcx => ReadTcx(content, fallbackStart),
                ActivityFileFormat.Kml => ReadKml(content, fallbackStart),
                _ => new List<ActivityTrackPoint>(),
            };

            return new ActivityTrackData(points);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or FormatException or
                                       IOException or ArgumentException)
        {
            return ActivityTrackData.Empty;
        }
    }

    private static List<ActivityTrackPoint> ReadFit(byte[] content, DateTimeOffset fallbackStart)
    {
        var records = FitPreparer.ReadRecords(content, fallbackStart);
        return records
            .Select(record => new ActivityTrackPoint(
                record.Timestamp,
                record.Latitude,
                record.Longitude,
                record.AltitudeMeters,
                record.SpeedMetersPerSecond,
                record.HeartRate,
                record.Cadence,
                record.PowerWatts,
                record.DistanceMeters))
            .ToList();
    }

    private static List<ActivityTrackPoint> ReadGpx(byte[] content, DateTimeOffset fallbackStart)
    {
        using var stream = new MemoryStream(content, writable: false);
        var document = XDocument.Load(stream);
        var points = new List<ActivityTrackPoint>();
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "trkpt"))
        {
            var latitude = ParseDouble(element.Attribute("lat")?.Value);
            var longitude = ParseDouble(element.Attribute("lon")?.Value);
            var altitude = ParseDouble(ChildValue(element, "ele"));
            var time = ParseTime(ChildValue(element, "time")) ?? fallbackStart.AddSeconds(points.Count);

            var heartRate = ParseFirstInt(element, "hr", "heartrate");
            var cadence = ParseFirstInt(element, "cad", "cadence");
            var power = ParseFirstInt(element, "power", "watts");

            points.Add(new ActivityTrackPoint(
                time,
                NormalizeLatitude(latitude),
                NormalizeLongitude(longitude),
                altitude,
                null,
                heartRate,
                cadence,
                power,
                null));
        }

        return points;
    }

    private static List<ActivityTrackPoint> ReadTcx(byte[] content, DateTimeOffset fallbackStart)
    {
        using var stream = new MemoryStream(content, writable: false);
        var document = XDocument.Load(stream);
        var points = new List<ActivityTrackPoint>();
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "Trackpoint"))
        {
            var position = element.Descendants().FirstOrDefault(e => e.Name.LocalName == "Position");
            var latitude = ParseDouble(position is null
                ? null
                : DescendantValue(position, "LatitudeDegrees"));
            var longitude = ParseDouble(position is null
                ? null
                : DescendantValue(position, "LongitudeDegrees"));
            var altitude = ParseDouble(ChildValue(element, "AltitudeMeters"));
            var distance = ParseDouble(ChildValue(element, "DistanceMeters"));
            var time = ParseTime(ChildValue(element, "Time")) ?? fallbackStart.AddSeconds(points.Count);

            var heartRateElement = element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "HeartRateBpm");
            var heartRate = ParseInt(heartRateElement is null
                ? null
                : DescendantValue(heartRateElement, "Value"));
            var cadence = ParseInt(ChildValue(element, "Cadence"));
            var watts = ParseInt(element.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Watts")?.Value);

            points.Add(new ActivityTrackPoint(
                time,
                NormalizeLatitude(latitude),
                NormalizeLongitude(longitude),
                altitude,
                null,
                heartRate,
                cadence,
                watts,
                distance));
        }

        return points;
    }

    private static List<ActivityTrackPoint> ReadKml(byte[] content, DateTimeOffset fallbackStart)
    {
        using var stream = new MemoryStream(content, writable: false);
        var document = XDocument.Load(stream);
        var points = new List<ActivityTrackPoint>();
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "coordinates"))
        {
            foreach (var tuple in element.Value.Split((char[]?)null,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tuple.Split(',');
                if (parts.Length < 2)
                {
                    continue;
                }

                var longitude = ParseDouble(parts[0]);
                var latitude = ParseDouble(parts[1]);
                var altitude = parts.Length > 2 ? ParseDouble(parts[2]) : null;
                if (latitude is null || longitude is null)
                {
                    continue;
                }

                points.Add(new ActivityTrackPoint(
                    fallbackStart.AddSeconds(points.Count),
                    NormalizeLatitude(latitude),
                    NormalizeLongitude(longitude),
                    altitude,
                    null,
                    null,
                    null,
                    null,
                    null));
            }
        }

        return points;
    }

    private static string? ChildValue(XElement element, string localName) =>
        element.Elements()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static string? DescendantValue(XElement element, string localName) =>
        element.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static int? ParseFirstInt(XElement element, params string[] localNames)
    {
        foreach (var localName in localNames)
        {
            if (ElementValue(element, localName) is { } value && ParseInt(value) is { } parsed)
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ElementValue(XElement root, string localName) =>
        root.Descendants()
            .FirstOrDefault(e => string.Equals(e.Name.LocalName, localName,
                StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static double? NormalizeLatitude(double? latitude) =>
        latitude is >= -90 and <= 90 ? latitude : null;

    private static double? NormalizeLongitude(double? longitude) =>
        longitude is >= -180 and <= 180 ? longitude : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        double.IsFinite(parsed)
            ? parsed
            : null;

    private static int? ParseInt(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        double.IsFinite(parsed)
            ? (int)Math.Round(parsed)
            : null;

    private static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }
}
