using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Services;

public sealed record ActivityDetail(ActivityFile File, ActivityTrackData Track);

/// <summary>Downloads the activity file and turns it into a detail-friendly track.</summary>
public sealed class ActivityDetailService
{
    private readonly IProviderRegistry _providers;
    private readonly ActivityFileCacheService _files;

    public ActivityDetailService(IProviderRegistry providers, ActivityFileCacheService files)
    {
        _providers = providers;
        _files = files;
    }

    public async Task<ActivityDetail> LoadAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var file = await _files.TryReadAsync(account.Id, activity.Id, cancellationToken)
            .ConfigureAwait(false);

        if (file is null)
        {
            var provider = _providers.Get(account.Platform);
            file = await provider.DownloadActivityAsync(account, activity, cancellationToken)
                .ConfigureAwait(false);
            await _files.SaveAsync(account.Id, activity.Id, file, cancellationToken)
                .ConfigureAwait(false);
        }

        var track = ActivityTrackReader.Read(file.Content, file.Format, activity.StartTime);
        return new ActivityDetail(file, track);
    }
}
