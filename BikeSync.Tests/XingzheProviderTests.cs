using System.Net;
using System.Text;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Tests;

public sealed class XingzheProviderTests
{
    [Fact]
    public async Task Lists_and_downloads_xingzhe_activity_as_fit()
    {
        var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var provider = new XingzheProvider(http);
        var account = new PlatformAccount
        {
            Platform = PlatformId.Xingzhe,
            UserId = "rider",
            DisplayName = "Rider",
            AuthToken = "sessionid=test-session",
        };
        var query = new ActivityQuery
        {
            From = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CyclingOnly = true,
            MaxResults = 50,
        };

        var activities = await provider.ListActivitiesAsync(account, query, CancellationToken.None);
        var activity = Assert.Single(activities);
        Assert.Equal("123", activity.Id);
        Assert.Equal("Morning Ride", activity.Name);
        Assert.Equal(205, activity.AveragePowerWatts);

        var file = await provider.DownloadActivityAsync(account, activity, CancellationToken.None);
        Assert.Equal(ActivityFileFormat.Fit, file.Format);
        Assert.True(file.Content.Length > 12);
        FitPreparer.Validate(file.Content, PlatformId.IGpsport);

        var upload = await provider.UploadActivityAsync(
            account,
            file,
            new ImportOptions(),
            CancellationToken.None);
        Assert.True(upload.Success);
        Assert.Equal("456", upload.RemoteId);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/api/v1/pgworkout/" => """
                    {"code":0,"data":{"data":[{"id":123,"title":"Morning Ride","start_time":1761000000000,"sport":3,"distance":10000,"duration":1800,"avg_speed":20,"avg_power":205}]}}
                    """,
                "/api/v1/pgworkout/123/" => """
                    {"code":0,"data":{"workout":{"id":123,"title":"Morning Ride","start_time":1761000000000,"end_time":1761001800000,"sport":3,"distance":10000,"duration":1800}}}
                    """,
                "/api/v1/pgworkout/123/stream/" => """
                    {"code":0,"data":{"timestamp":[1761000000000,1761000900000],"distance":[0,5000],"speed":[5,6],"altitude":[10,20],"heartrate":[140,145],"cadence":[80,85],"power":[200,210],"temperature":[24,24]}}
                    """,
                "/api/v1/fit/upload/" => """
                    {"code":0,"data":{"workout_id":456}}
                    """,
                "/api/v1/pgworkout/123/gpx/" => """
                    <?xml version="1.0"?><gpx><trk><trkseg>
                    <trkpt lat="31.2" lon="121.4"><ele>10</ele><time>2026-10-21T00:00:00Z</time></trkpt>
                    <trkpt lat="31.3" lon="121.5"><ele>20</ele><time>2026-10-21T00:15:00Z</time></trkpt>
                    </trkseg></trk></gpx>
                    """,
                _ => throw new InvalidOperationException("Unexpected URL " + request.RequestUri),
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
