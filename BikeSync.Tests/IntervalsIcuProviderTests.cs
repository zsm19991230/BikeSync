using System.IO.Compression;
using System.Net;
using System.Text;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Tests;

public class IntervalsIcuProviderTests
{
    private static PlatformAccount Account => new()
    {
        Platform = PlatformId.IntervalsIcu,
        UserId = "i123",
        DisplayName = "Test Rider",
        ApiKey = "icu-key",
    };

    [Fact]
    public async Task ListActivities_SkipsStravaStubsAndMapsFields()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            Assert.Contains("/athlete/0/activities", request.RequestUri!.ToString());
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            return Task.FromResult(FakeHttpMessageHandler.Json("""
                [
                  {
                    "id": "i100",
                    "name": "Hill repeats",
                    "type": "Ride",
                    "start_date_local": "2026-05-03T07:10:00",
                    "start_date": "2026-05-03T07:10:00Z",
                    "distance": 35000,
                    "moving_time": 4200,
                    "elapsed_time": 4500,
                    "total_elevation_gain": 500,
                    "icu_average_watts": 201,
                    "average_heartrate": 148,
                    "file_type": "fit"
                  },
                  { "id": "strava-stub", "type": "Ride" },
                  {
                    "id": "i101",
                    "name": "Recovery spin",
                    "type": "Ride",
                    "start_date_local": "2026-05-04T07:10:00",
                    "distance": 10000,
                    "moving_time": 1800,
                    "elapsed_time": 1900,
                    "total_elevation_gain": 20
                  }
                ]
                """));
        });

        var provider = new IntervalsIcuProvider(new HttpClient(handler));
        var result = await provider.ListActivitiesAsync(
            Account,
            ActivityQuery.ForPreset(DateRangePreset.Last90Days),
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        var hill = result.Single(a => a.Id == "i100");
        Assert.Equal(201, hill.AveragePowerWatts);
        Assert.Equal(148, hill.AverageHeartRate);
        Assert.Equal("fit", hill.SourceFileType);
        Assert.DoesNotContain(result, a => a.Id == "strava-stub");
    }

    [Fact]
    public async Task DownloadActivity_DecompressesGzip()
    {
        var payload = new byte[] { 0x0E, 0x10, 0x00, 0x00 };
        var gzipped = Gzip(payload);
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(FakeHttpMessageHandler.Bytes(gzipped, "application/octet-stream")));

        var provider = new IntervalsIcuProvider(new HttpClient(handler));
        var activity = new ActivitySummary
        {
            Id = "i100",
            Platform = PlatformId.IntervalsIcu,
            AccountId = Account.Id,
            AccountName = Account.DisplayName,
            Name = "Hill repeats",
            SourceFileType = "fit",
        };

        var file = await provider.DownloadActivityAsync(Account, activity, CancellationToken.None);
        Assert.Equal(payload, file.Content);
        Assert.Equal(ActivityFileFormat.Fit, file.Format);
        Assert.EndsWith(".fit", file.FileName);
    }

    [Fact]
    public async Task UploadActivity_ReturnsCreatedActivity()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            Assert.Contains("name=Hill", request.RequestUri!.Query);
            Assert.Contains("repeats", request.RequestUri.Query);
            return Task.FromResult(FakeHttpMessageHandler.Json(
                """{ "icu_athlete_id": "i123", "id": "upload-1", "activities": [{ "id": "i200" }] }""",
                HttpStatusCode.Created));
        });

        var provider = new IntervalsIcuProvider(new HttpClient(handler));
        var file = new ActivityFile
        {
            Content = new byte[] { 1, 2, 3 },
            FileName = "ride.fit",
            Format = ActivityFileFormat.Fit,
        };

        var result = await provider.UploadActivityAsync(
            Account,
            file,
            new ImportOptions { NameOverride = "Hill repeats" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("i200", result.RemoteId);
    }

    private static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(input);
        }

        return output.ToArray();
    }
}
