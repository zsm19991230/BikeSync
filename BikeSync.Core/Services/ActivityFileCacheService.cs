using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

/// <summary>
/// Keeps downloaded activity files on disk so opening the detail view again
/// (or opening another activity from the same account) does not re-download them.
/// </summary>
public sealed class ActivityFileCacheService
{
    private readonly AppPaths _paths;
    private readonly ILogger<ActivityFileCacheService> _logger;

    public ActivityFileCacheService(AppPaths paths, ILogger<ActivityFileCacheService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<ActivityFile?> TryReadAsync(
        Guid accountId,
        string activityId,
        CancellationToken cancellationToken)
    {
        var directory = _paths.ActivityFileCacheDirectory;
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var prefix = BuildPrefix(accountId, activityId);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, prefix + ".*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate the activity file cache.");
            return null;
        }

        foreach (var path in files)
        {
            var format = ActivityFileFormatExtensions.FromExtension(Path.GetExtension(path));
            if (format is null)
            {
                continue;
            }

            try
            {
                var content = await File.ReadAllBytesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (content.Length == 0)
                {
                    continue;
                }

                return new ActivityFile
                {
                    Content = content,
                    FileName = Path.GetFileName(path),
                    Format = format.Value,
                    SourceActivityId = activityId,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not read a cached activity file.");
            }
        }

        return null;
    }

    public async Task SaveAsync(
        Guid accountId,
        string activityId,
        ActivityFile file,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_paths.ActivityFileCacheDirectory);
            var path = Path.Combine(
                _paths.ActivityFileCacheDirectory,
                BuildPrefix(accountId, activityId) + "." + file.Format.Extension());
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".new";
            await File.WriteAllBytesAsync(tempPath, file.Content, cancellationToken)
                .ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not cache the activity file.");
        }
    }

    private static string BuildPrefix(Guid accountId, string activityId)
    {
        var cleaned = new string(activityId
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray());
        if (cleaned.Length > 64)
        {
            cleaned = cleaned[..64];
        }

        return $"{accountId:N}-{cleaned}";
    }
}
