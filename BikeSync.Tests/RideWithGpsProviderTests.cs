using System.Net;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Tests;

public class RideWithGpsProviderTests
{
    private static PlatformAccount Account => new()
    {
        Platform = PlatformId.RideWithGps,
        UserId = "77",
        DisplayName = "Test Rider",
        ApiKey = "rwgps-key",
        AuthToken = "rwgps-token",
    };

    [Fact]
    public async Task ListActivities_MapsTripSummaryAndFiltersDates()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            Assert.Equal("rwgps-key", request.Headers.GetValues("x-rwgps-api-key").Single());
            Assert.Equal("rwgps-token", request.Headers.GetValues("x-rwgps-auth-token").Single());
            return Task.FromResult(FakeHttpMessageHandler.Json("""
                {
                  "trips": [
                    {
                      "id": 2001,
                      "name": "Coastal loop",
                      "activity_type": "Ride",
                      "departed_at": "2026-05-01T07:00:00Z",
                      "distance": 52000,
                      "duration": 6000,
                      "moving_time": 5600,
                      "elevation_gain": 410,
                      "avg_watts": 175,
                      "avg_hr": 139,
                      "first_lat": 31.2,
                      "web_url": "https://ridewithgps.com/trips/2001"
                    }
                  ],
                  "meta": { "pagination": { "next_page_url": null } }
                }
                """));
        });

        var provider = new RideWithGpsProvider(new HttpClient(handler));
        var result = await provider.ListActivitiesAsync(
            Account,
            new ActivityQuery
            {
                From = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
                To = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            },
            CancellationToken.None);

        var trip = Assert.Single(result);
        Assert.Equal("Coastal loop", trip.Name);
        Assert.Equal(52000, trip.DistanceMeters);
        Assert.Equal(175, trip.AveragePowerWatts);
        Assert.True(trip.HasGps);
    }

    [Fact]
    public async Task UploadActivity_PollsTaskAndReturnsItem()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var response = FakeHttpMessageHandler.Json(
                    """{ "task": { "id": 5, "status": "pending", "url": "https://ridewithgps.com/api/v1/tasks/5" } }""",
                    HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("https://ridewithgps.com/api/v1/tasks/5");
                return Task.FromResult(response);
            }

            Assert.Contains("/tasks/5", request.RequestUri!.ToString());
            return Task.FromResult(FakeHttpMessageHandler.Json("""
                {
                  "task": {
                    "id": 5,
                    "status": "completed",
                    "items": [ { "item_type": "trip", "item_id": 2001, "item_url": "https://ridewithgps.com/trips/2001" } ],
                    "errors": []
                  }
                }
                """));
        });

        var provider = new RideWithGpsProvider(new HttpClient(handler));
        var file = new ActivityFile
        {
            Content = new byte[] { 1, 2, 3 },
            FileName = "ride.fit",
            Format = ActivityFileFormat.Fit,
        };

        var result = await provider.UploadActivityAsync(
            Account,
            file,
            new ImportOptions { NameOverride = "Coastal loop" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("2001", result.RemoteId);
        Assert.Equal("https://ridewithgps.com/trips/2001", result.RemoteUrl);
    }
}
