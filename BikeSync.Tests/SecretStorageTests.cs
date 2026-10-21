using BikeSync.Core.Models;
using BikeSync.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BikeSync.Tests;

public class SecretStorageTests
{
    [Fact]
    public async Task AccountStore_EncryptsSecretsAndRoundTrips()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var protector = new DpapiSecretProtector();
            var store = new JsonAccountStore(paths, protector, NullLogger<JsonAccountStore>.Instance);
            var account = new PlatformAccount
            {
                Platform = PlatformId.Strava,
                UserId = "42",
                DisplayName = "Rider",
                AccessToken = "access-secret-value",
                RefreshToken = "refresh-secret-value",
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            };

            await store.SaveAsync(new[] { account });
            var raw = await File.ReadAllTextAsync(paths.AccountsFile);
            Assert.DoesNotContain("access-secret-value", raw);
            Assert.DoesNotContain("refresh-secret-value", raw);
            Assert.Contains("protectedAccessToken", raw);

            var loaded = await store.LoadAsync();
            var roundTrip = Assert.Single(loaded);
            Assert.Equal("access-secret-value", roundTrip.AccessToken);
            Assert.Equal("refresh-secret-value", roundTrip.RefreshToken);
            Assert.Equal(PlatformId.Strava, roundTrip.Platform);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_EncryptsClientSecret()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var protector = new DpapiSecretProtector();
            var store = new JsonAppSettingsStore(paths, protector, NullLogger<JsonAppSettingsStore>.Instance);
            var settings = new AppSettings
            {
                DownloadDirectory = Path.Combine(root, "downloads"),
                Strava = new StravaAppSettings
                {
                    ClientId = "123",
                    ClientSecret = "client-secret-value",
                },
            };

            await store.SaveAsync(settings);
            var raw = await File.ReadAllTextAsync(paths.SettingsFile);
            Assert.DoesNotContain("client-secret-value", raw);

            var loaded = await store.LoadAsync(settings.DownloadDirectory);
            Assert.Equal("123", loaded.Strava.ClientId);
            Assert.Equal("client-secret-value", loaded.Strava.ClientSecret);
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
