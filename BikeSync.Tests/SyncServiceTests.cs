using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BikeSync.Tests;

public class SyncServiceTests
{
    [Fact]
    public async Task ImportAsync_DownloadsUploadsSavesAndRecordsHistory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            var accountStore = new InMemoryAccountStore();
            var accounts = new AccountService(accountStore, NullLogger<AccountService>.Instance);
            await accounts.InitializeAsync();

            var settingsStore = new InMemorySettingsStore();
            var settings = new AppSettingsService(settingsStore, paths);
            await settings.LoadAsync();
            var current = settings.Current;
            current.DownloadDirectory = Path.Combine(root, "exports");
            current.SaveActivityCopies = true;
            await settings.SaveAsync(current);

            var sourceAccount = await accounts.UpsertAsync(new PlatformAccount
            {
                Platform = PlatformId.IntervalsIcu,
                UserId = "i1",
                DisplayName = "Source",
                ApiKey = "key",
            });
            var targetAccount = await accounts.UpsertAsync(new PlatformAccount
            {
                Platform = PlatformId.RideWithGps,
                UserId = "r1",
                DisplayName = "Target",
                ApiKey = "key",
                AuthToken = "token",
            });

            var sourceProvider = new FakeProvider(PlatformId.IntervalsIcu);
            var targetProvider = new FakeProvider(PlatformId.RideWithGps);
            var registry = new ProviderRegistry(new IPlatformProvider[] { sourceProvider, targetProvider });
            var history = new InMemoryHistoryStore();
            var service = new SyncService(
                accounts,
                registry,
                new StravaOAuthService(new HttpClient(), settings),
                settings,
                history,
                NullLogger<SyncService>.Instance);

            var activities = new[]
            {
                new ActivitySummary
                {
                    Id = "i100",
                    Platform = PlatformId.IntervalsIcu,
                    AccountId = sourceAccount.Id,
                    AccountName = sourceAccount.DisplayName,
                    Name = "Hill repeats",
                    StartTime = DateTimeOffset.Now.AddDays(-2),
                    DistanceMeters = 30000,
                    MovingTimeSeconds = 3600,
                    WebUrl = "https://intervals.icu/activities/i100",
                },
                new ActivitySummary
                {
                    Id = "i101",
                    Platform = PlatformId.IntervalsIcu,
                    AccountId = sourceAccount.Id,
                    AccountName = sourceAccount.DisplayName,
                    Name = "Recovery",
                    StartTime = DateTimeOffset.Now.AddDays(-1),
                    DistanceMeters = 15000,
                    MovingTimeSeconds = 2400,
                },
            };

            var progress = new List<SyncProgress>();
            var summary = await service.ImportAsync(
                new SyncRequest
                {
                    SourceAccount = sourceAccount,
                    TargetAccount = targetAccount,
                    Activities = activities,
                    Options = new ImportOptions { SaveLocalCopy = true },
                },
                new InlineProgress<SyncProgress>(progress.Add),
                CancellationToken.None);

            Assert.Equal(2, summary.Total);
            Assert.Equal(2, summary.Succeeded);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(2, targetProvider.UploadCount);
            Assert.Equal(2, sourceProvider.DownloadCount);
            Assert.All(summary.Items, item => Assert.NotNull(item.SavedFilePath));
            Assert.All(summary.Items, item => Assert.True(File.Exists(item.SavedFilePath)));
            var historyEntry = Assert.Single(history.Entries);
            Assert.Equal(2, historyEntry.Total);
            Assert.Equal(2, historyEntry.Succeeded);
            Assert.NotEmpty(progress);
            Assert.Contains(progress, p => p.Percent == 100);
            Assert.All(targetProvider.UploadedOptions,
                options => Assert.Contains("intervals.icu", options.DescriptionOverride));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeProvider : IPlatformProvider
    {
        public FakeProvider(PlatformId platform)
        {
            Platform = platform;
        }

        public PlatformId Platform { get; }
        public int DownloadCount { get; private set; }
        public int UploadCount { get; private set; }
        public List<ImportOptions> UploadedOptions { get; } = new();

        public Task<AccountProfile> GetProfileAsync(
            PlatformAccount account,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AccountProfile(account.UserId, account.DisplayName));

        public Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
            PlatformAccount account,
            ActivityQuery query,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ActivitySummary>>(Array.Empty<ActivitySummary>());

        public Task<ActivityFile> DownloadActivityAsync(
            PlatformAccount account,
            ActivitySummary activity,
            CancellationToken cancellationToken)
        {
            DownloadCount++;
            return Task.FromResult(new ActivityFile
            {
                Content = new byte[] { 1, 2, 3, 4 },
                FileName = $"{activity.Id}.fit",
                Format = ActivityFileFormat.Fit,
                SourceActivityId = activity.Id,
            });
        }

        public Task<ImportResult> UploadActivityAsync(
            PlatformAccount account,
            ActivityFile file,
            ImportOptions options,
            CancellationToken cancellationToken)
        {
            UploadCount++;
            UploadedOptions.Add(options);
            return Task.FromResult(ImportResult.Ok("remote-" + UploadCount, null));
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }

    private sealed class InMemoryHistoryStore : ISyncHistoryStore
    {
        public List<SyncHistoryEntry> Entries { get; } = new();

        public Task<IReadOnlyList<SyncHistoryEntry>> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SyncHistoryEntry>>(Entries.ToArray());

        public Task AppendAsync(
            SyncHistoryEntry entry,
            CancellationToken cancellationToken = default)
        {
            Entries.Insert(0, entry);
            return Task.CompletedTask;
        }
    }
}
