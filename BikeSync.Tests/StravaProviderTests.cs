using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Tests;

public class StravaProviderTests
{
    private static PlatformAccount Account => new()
    {
        Platform = PlatformId.Strava,
        UserId = "777",
        DisplayName = "Test Rider",
        AccessToken = "test-token",
    };

    [Fact]
    public async Task ListActivities_MapsAndFiltersCycling()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            Assert.Contains("athlete/activities", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(FakeHttpMessageHandler.Json("""
                [
                  {
                    "id": 1001,
                    "name": "Morning Ride",
                    "sport_type": "Ride",
                    "start_date": "2026-05-01T06:00:00Z",
                    "start_date_local": "2026-05-01T14:00:00Z",
                    "distance": 42000.5,
                    "moving_time": 5400,
                    "elapsed_time": 5800,
                    "total_elevation_gain": 620,
                    "average_watts": 188.5,
                    "average_heartrate": 142.2,
                    "map": { "summary_polyline": "abc" }
                  },
                  {
                    "id": 1002,
                    "name": "Evening Run",
                    "sport_type": "Run",
                    "start_date": "2026-05-02T06:00:00Z",
                    "start_date_local": "2026-05-02T14:00:00Z",
                    "distance": 10000,
                    "moving_time": 3000,
                    "elapsed_time": 3100
                  }
                ]
                """));
        });

        var http = new HttpClient(handler);
        var provider = new StravaProvider(http);
        var result = await provider.ListActivitiesAsync(
            Account,
            ActivityQuery.ForPreset(DateRangePreset.Last90Days) with { CyclingOnly = true, MaxResults = 10 },
            CancellationToken.None);

        var activity = Assert.Single(result);
        Assert.Equal("Morning Ride", activity.Name);
        Assert.Equal("Ride", activity.SportType);
        Assert.Equal(42000.5, activity.DistanceMeters);
        Assert.Equal(620, activity.ElevationGainMeters);
        Assert.Equal(142, activity.AverageHeartRate);
        Assert.True(activity.HasGps);
        Assert.Equal(new DateTime(2026, 5, 1, 14, 0, 0), activity.LocalStartTime);
    }

    [Fact]
    public async Task DownloadActivity_GeneratesTcxFromStreams()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            Assert.Contains("/activities/1001/streams", request.RequestUri!.ToString());
            return Task.FromResult(FakeHttpMessageHandler.Json("""
                {
                  "time": { "data": [0, 10, 20] },
                  "distance": { "data": [0, 100, 200] },
                  "altitude": { "data": [10, 12, 13] },
                  "heartrate": { "data": [120, 125, 130] },
                  "cadence": { "data": [80, 82, 84] },
                  "watts": { "data": [200, 210, 220] },
                  "latlng": { "data": [[31.2, 121.4], [31.201, 121.401], [31.202, 121.402]] }
                }
                """));
        });

        var provider = new StravaProvider(new HttpClient(handler));
        var activity = new ActivitySummary
        {
            Id = "1001",
            Platform = PlatformId.Strava,
            AccountId = Account.Id,
            AccountName = Account.DisplayName,
            Name = "Morning Ride",
            StartTime = new DateTimeOffset(2026, 5, 1, 6, 0, 0, TimeSpan.Zero),
            ElapsedTimeSeconds = 20,
            DistanceMeters = 200,
        };

        var file = await provider.DownloadActivityAsync(Account, activity, CancellationToken.None);
        Assert.Equal(ActivityFileFormat.Tcx, file.Format);
        Assert.EndsWith(".tcx", file.FileName);

        var doc = XDocument.Load(new MemoryStream(file.Content));
        Assert.Equal(3, doc.Descendants().Count(e => e.Name.LocalName == "Trackpoint"));
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "Watts" && e.Value == "220");
    }

    [Fact]
    public async Task UploadActivity_PollsUntilReady()
    {
        var pollCount = 0;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Contains("/uploads", request.RequestUri!.AbsolutePath);
                return Task.FromResult(FakeHttpMessageHandler.Json(
                    """{ "id": "9001", "error": null, "status": "Your activity is still being processed." }"""));
            }

            pollCount++;
            return Task.FromResult(FakeHttpMessageHandler.Json(
                """{ "id": "9001", "error": null, "status": "Your activity is ready.", "activity_id": 555 }"""));
        });

        var provider = new StravaProvider(new HttpClient(handler));
        var file = new ActivityFile
        {
            Content = new byte[] { 1, 2, 3 },
            FileName = "ride.tcx",
            Format = ActivityFileFormat.Tcx,
        };

        var result = await provider.UploadActivityAsync(
            Account,
            file,
            new ImportOptions { NameOverride = "Test", SportType = "Ride" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Duplicate);
        Assert.Equal("555", result.RemoteId);
        Assert.Equal(1, pollCount);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post);
    }
}
