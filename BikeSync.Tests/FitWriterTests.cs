using BikeSync.Core.Providers;

namespace BikeSync.Tests;

public sealed class FitWriterTests
{
    [Fact]
    public void Generated_fit_passes_validation_and_preparation()
    {
        var start = DateTimeOffset.Now.AddHours(-1);
        var points = Enumerable.Range(0, 60)
            .Select(i => new FitTrackPoint(
                start.AddSeconds(i),
                31.2 + i * 0.0001,
                121.4 + i * 0.0001,
                i * 8.0,
                8.0,
                10,
                140,
                85,
                200,
                24))
            .ToArray();
        var fit = FitWriter.Write(new FitWorkout(
            start,
            start.AddSeconds(59),
            "Fit writer test",
            points,
            472,
            59));

        FitPreparer.Validate(fit, BikeSync.Core.Models.PlatformId.Xingzhe);
        var prepared = FitPreparer.Prepare(fit, TimeZoneInfo.Utc, 250);
        Assert.True(prepared.Content.Length > 12);
        FitPreparer.Validate(prepared.Content, BikeSync.Core.Models.PlatformId.IGpsport);
    }
}
