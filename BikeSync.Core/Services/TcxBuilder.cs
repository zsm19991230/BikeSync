using System.Globalization;
using System.Text;
using System.Xml.Linq;
using BikeSync.Core.Models;

namespace BikeSync.Core.Services;

public sealed record TcxTrackPoint(
    DateTimeOffset Time,
    double? Latitude,
    double? Longitude,
    double? AltitudeMeters,
    double? DistanceMeters,
    int? HeartRate,
    int? Cadence,
    int? Watts);

/// <summary>
/// Builds a Garmin TCX 2.0 activity from normalized streams. This is used when a
/// platform exposes telemetry but not the original device file (notably Strava's API).
/// </summary>
public static class TcxBuilder
{
    private static readonly XNamespace Ns = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Tpx = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    public static byte[] Build(ActivitySummary activity, IReadOnlyList<TcxTrackPoint> points)
    {
        if (points.Count == 0)
        {
            throw new InvalidOperationException("该活动没有可用的时间序列数据，无法生成 TCX 文件。");
        }

        var ordered = points.OrderBy(p => p.Time).ToArray();
        var start = ordered[0].Time.ToUniversalTime();
        var last = ordered[^1].Time.ToUniversalTime();
        var elapsedSeconds = Math.Max(1, (last - start).TotalSeconds);
        var totalTime = activity.ElapsedTimeSeconds > 0
            ? activity.ElapsedTimeSeconds
            : (int)Math.Round(elapsedSeconds);
        var distance = activity.DistanceMeters > 0
            ? activity.DistanceMeters
            : ordered.LastOrDefault(p => p.DistanceMeters.HasValue)?.DistanceMeters ?? 0;

        var track = new XElement(Ns + "Track");
        foreach (var point in ordered)
        {
            var trackPoint = new XElement(Ns + "Trackpoint",
                new XElement(Ns + "Time", FormatTime(point.Time)));

            if (point.Latitude is { } lat && point.Longitude is { } lon &&
                lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
            {
                trackPoint.Add(new XElement(Ns + "Position",
                    new XElement(Ns + "LatitudeDegrees", FormatNumber(lat)),
                    new XElement(Ns + "LongitudeDegrees", FormatNumber(lon))));
            }

            if (point.AltitudeMeters is { } altitude)
            {
                trackPoint.Add(new XElement(Ns + "AltitudeMeters", FormatNumber(altitude)));
            }

            if (point.DistanceMeters is { } pointDistance and >= 0)
            {
                trackPoint.Add(new XElement(Ns + "DistanceMeters", FormatNumber(pointDistance)));
            }

            if (point.HeartRate is > 0)
            {
                trackPoint.Add(new XElement(Ns + "HeartRateBpm",
                    new XElement(Ns + "Value", point.HeartRate.Value)));
            }

            if (point.Cadence is >= 0)
            {
                trackPoint.Add(new XElement(Ns + "Cadence", point.Cadence.Value));
            }

            if (point.Watts is > 0)
            {
                trackPoint.Add(new XElement(Ns + "Extensions",
                    new XElement(Tpx + "TPX",
                        new XElement(Tpx + "Watts", point.Watts.Value))));
            }

            track.Add(trackPoint);
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "TrainingCenterDatabase",
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XElement(Ns + "Activities",
                    new XElement(Ns + "Activity",
                        new XAttribute("Sport", "Biking"),
                        new XElement(Ns + "Id", FormatTime(start)),
                        new XElement(Ns + "Lap",
                            new XAttribute("StartTime", FormatTime(start)),
                            new XElement(Ns + "TotalTimeSeconds", FormatNumber(totalTime)),
                            new XElement(Ns + "DistanceMeters", FormatNumber(distance)),
                            new XElement(Ns + "Calories", 0),
                            new XElement(Ns + "Intensity", "Active"),
                            new XElement(Ns + "TriggerMethod", "Manual"),
                            track),
                        new XElement(Ns + "Creator",
                            new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                            new XAttribute(Xsi + "type", "Device_t"),
                            new XElement(Ns + "Name", "BikeSync 骑迹同步"),
                            new XElement(Ns + "UnitId", 0),
                            new XElement(Ns + "ProductID", 0),
                            new XElement(Ns + "Version",
                                new XElement(Ns + "VersionMajor", 2),
                                new XElement(Ns + "VersionMinor", 0)))))));

        using var memory = new MemoryStream();
        using (var writer = new StreamWriter(memory, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            document.Save(writer, SaveOptions.None);
        }

        return memory.ToArray();
    }

    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
