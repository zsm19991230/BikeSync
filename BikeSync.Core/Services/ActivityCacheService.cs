using System.Text.Json;
using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

public sealed class ActivityCacheService
{
    private readonly AppPaths _paths;
    private readonly ILogger<ActivityCacheService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, ActivityCacheEntry>? _entries;

    public ActivityCacheService(AppPaths paths, ILogger<ActivityCacheService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<ActivityCacheEntry?> GetAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _entries!.TryGetValue(accountId, out var entry) ? entry : null;
    }

    public async Task SaveAsync(
        PlatformAccount account,
        ActivityQuery query,
        IReadOnlyList<ActivitySummary> activities,
        CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        _entries![account.Id] = new ActivityCacheEntry
        {
            AccountId = account.Id,
            Platform = account.Platform,
            AccountName = account.DisplayName,
            FetchedAt = DateTimeOffset.Now,
            From = query.From,
            To = query.To,
            CyclingOnly = query.CyclingOnly,
            Activities = activities.ToList(),
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            await JsonFile.WriteAtomicAsync(
                _paths.ActivityCacheFile,
                new ActivityCacheFile
                {
                    Version = 1,
                    Entries = _entries.Values.OrderBy(e => e.Platform).ThenBy(e => e.AccountName).ToList(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the activity cache.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries is not null)
            {
                return;
            }

            ActivityCacheFile? file = null;
            try
            {
                file = await JsonFile.ReadAsync<ActivityCacheFile>(_paths.ActivityCacheFile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                JsonFile.ArchiveCorrupt(_paths.ActivityCacheFile);
                _logger.LogWarning(ex, "Activity cache was corrupt and was archived.");
            }

            _entries = file?.Entries?
                .Where(e => e.AccountId != Guid.Empty)
                .GroupBy(e => e.AccountId)
                .ToDictionary(g => g.Key, g => g.First())
                ?? new Dictionary<Guid, ActivityCacheEntry>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class ActivityCacheFile
    {
        public int Version { get; set; }
        public List<ActivityCacheEntry> Entries { get; set; } = new();
    }
}
