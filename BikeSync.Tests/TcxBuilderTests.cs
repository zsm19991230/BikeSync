using System.Xml.Linq;
using BikeSync.Core.Models;
using BikeSync.Core.Services;

namespace BikeSync.Tests;

public class TcxBuilderTests
{
    private static readonly XNamespace Tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";
    private static readonly XNamespace Tpx = "http://www.garmin.com/xmlschemas/ActivityExtension/v2";

    [Fact]
    public void Build_ProducesTrackpointsAndExtensions()
    {
        var activity = new ActivitySummary
        {
            Id = "1",
            Platform = PlatformId.Strava,
            AccountId = Guid.NewGuid(),
            AccountName = "tester",
            Name = "Morning Ride",
            StartTime = new DateTimeOffset(2026, 5, 1, 6, 0, 0, TimeSpan.Zero),
            DistanceMeters = 2000,
            ElapsedTimeSeconds = 3600,
        };

        var points = new[]
        {
            new TcxTrackPoint(
                new DateTimeOffset(2026, 5, 1, 6, 0, 0, TimeSpan.Zero),
                31.2, 121.4, 10, 0, 120, 80, 210),
            new TcxTrackPoint(
                new DateTimeOffset(2026, 5, 1, 6, 0, 10, TimeSpan.Zero),
                31.201, 121.401, 12, 100, 125, 82, 230),
        };

        var bytes = TcxBuilder.Build(activity, points);
        var document = XDocument.Load(new MemoryStream(bytes));
        var trackpoints = document.Descendants(Tcx + "Trackpoint").ToList();

        Assert.Equal(2, trackpoints.Count);
        Assert.Equal("Biking", document.Descendants(Tcx + "Activity").First().Attribute("Sport")!.Value);
        Assert.Equal(120, (int)trackpoints[0].Element(Tcx + "HeartRateBpm")!.Element(Tcx + "Value")!);
        Assert.Equal(210, (int)trackpoints[0].Element(Tcx + "Extensions")!.Element(Tpx + "TPX")!.Element(Tpx + "Watts")!);
        Assert.Equal(31.2, (double)trackpoints[0].Descendants(Tcx + "LatitudeDegrees").First(), 3);
    }

    [Fact]
    public void Build_WithNoPoints_Throws()
    {
        var activity = new ActivitySummary
        {
            Id = "1",
            Platform = PlatformId.Strava,
            AccountId = Guid.NewGuid(),
            AccountName = "tester",
            Name = "Empty",
        };

        Assert.Throws<InvalidOperationException>(() => TcxBuilder.Build(activity, Array.Empty<TcxTrackPoint>()));
    }
}
