using BikeSync.Core.Models;
using BikeSync.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BikeSync.Tests;

public sealed class ActivityCacheTests
{
    [Fact]
    public async Task Cache_round_trips_activity_summaries()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-cache", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            // A stale fixed temp name must not block the atomic write.
            Directory.CreateDirectory(paths.ActivityCacheFile + ".tmp");
            var service = new ActivityCacheService(paths, NullLogger<ActivityCacheService>.Instance);
            var account = new PlatformAccount
            {
                Platform = PlatformId.MyWhoosh,
                UserId = "rider-1",
                DisplayName = "Cache Rider",
            };
            var query = ActivityQuery.ForPreset(DateRangePreset.Last30Days);
            var activity = new ActivitySummary
            {
                Id = "act-1",
                Platform = PlatformId.MyWhoosh,
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Name = "Cached ride",
                StartTime = DateTimeOffset.Now.AddHours(-2),
                DistanceMeters = 32100,
                MovingTimeSeconds = 3600,
            };

            await service.SaveAsync(account, query, new[] { activity });
            var restored = await service.GetAsync(account.Id);

            Assert.NotNull(restored);
            Assert.Equal(account.Id, restored!.AccountId);
            Assert.Single(restored.Activities);
            Assert.Equal("act-1", restored.Activities[0].Id);
            Assert.Equal(32100, restored.Activities[0].DistanceMeters);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
