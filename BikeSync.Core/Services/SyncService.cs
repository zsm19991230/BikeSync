using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

public sealed class SyncService
{
    private readonly AccountService _accounts;
    private readonly IProviderRegistry _providers;
    private readonly StravaOAuthService _stravaOAuth;
    private readonly AppSettingsService _settings;
    private readonly ISyncHistoryStore _history;
    private readonly ILogger<SyncService> _logger;

    public SyncService(
        AccountService accounts,
        IProviderRegistry providers,
        StravaOAuthService stravaOAuth,
        AppSettingsService settings,
        ISyncHistoryStore history,
        ILogger<SyncService> logger)
    {
        _accounts = accounts;
        _providers = providers;
        _stravaOAuth = stravaOAuth;
        _settings = settings;
        _history = history;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ActivitySummary>> FetchActivitiesAsync(
        Guid accountId,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var account = await GetFreshAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var provider = _providers.Get(account.Platform);
        var activities = await provider.ListActivitiesAsync(account, query, cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            activities = activities
                .Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        return activities
            .OrderByDescending(a => a.StartTime)
            .Take(Math.Clamp(query.MaxResults, 1, 1000))
            .ToArray();
    }

    public async Task<PlatformAccount> GetFreshAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var account = _accounts.Find(accountId) ??
                      throw new InvalidOperationException("平台账户不存在，请重新连接。");

        if (account.Platform != PlatformId.Strava || !account.IsAccessTokenExpired())
        {
            return account;
        }

        var refreshed = await _stravaOAuth.RefreshAsync(account, cancellationToken).ConfigureAwait(false);
        await _accounts.UpdateAsync(refreshed, cancellationToken).ConfigureAwait(false);
        return refreshed;
    }

    public async Task<SyncSummary> ImportAsync(
        SyncRequest request,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.SourceAccount.Id == request.TargetAccount.Id)
        {
            throw new InvalidOperationException("源平台和目标平台不能是同一个账户。");
        }

        if (request.Activities.Count == 0)
        {
            throw new InvalidOperationException("请先选择要导入的活动。");
        }

        var source = await GetFreshAccountAsync(request.SourceAccount.Id, cancellationToken)
            .ConfigureAwait(false);
        var target = await GetFreshAccountAsync(request.TargetAccount.Id, cancellationToken)
            .ConfigureAwait(false);
        var sourceProvider = _providers.Get(source.Platform);
        var targetProvider = _providers.Get(target.Platform);

        var results = new List<SyncItemResult>(request.Activities.Count);
        var total = request.Activities.Count;

        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activity = request.Activities[index];
            var itemNumber = index + 1;

            progress?.Report(new SyncProgress(
                index,
                total,
                activity.Id,
                activity.Name,
                $"正在从 {source.Descriptor.DisplayName} 获取活动文件",
                5));

            string? savedPath = null;
            ImportResult result;
            try
            {
                var file = await sourceProvider.DownloadActivityAsync(source, activity, cancellationToken)
                    .ConfigureAwait(false);

                if (request.Options.SaveLocalCopy && _settings.Current.SaveActivityCopies)
                {
                    savedPath = await TrySaveCopyAsync(file, activity, cancellationToken)
                        .ConfigureAwait(false);
                }

                progress?.Report(new SyncProgress(
                    index,
                    total,
                    activity.Id,
                    activity.Name,
                    $"正在上传到 {target.Descriptor.DisplayName}",
                    55));

                var description = request.Options.DescriptionOverride;
                if (string.IsNullOrWhiteSpace(description) &&
                    request.Options.AppendSourceToDescription)
                {
                    description = $"通过骑迹同步从 {source.Descriptor.DisplayName} 导入";
                    if (!string.IsNullOrWhiteSpace(activity.WebUrl))
                    {
                        description += $" · 原始活动 {activity.WebUrl}";
                    }
                }

                var uploadOptions = new ImportOptions
                {
                    SaveLocalCopy = request.Options.SaveLocalCopy,
                    AppendSourceToDescription = request.Options.AppendSourceToDescription,
                    NameOverride = request.Options.NameOverride ?? activity.Name,
                    DescriptionOverride = description,
                    SportType = request.Options.SportType ?? activity.SportType,
                    ExternalId = $"bikesync-{source.Platform.ToString().ToLowerInvariant()}-{SanitizeId(activity.Id)}",
                    MarkAsCommute = request.Options.MarkAsCommute,
                };

                result = await targetProvider
                    .UploadActivityAsync(target, file, uploadOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = ImportResult.Failed("平台请求超时，请稍后重试。");
            }
            catch (Exception ex) when (ex is PlatformApiException or PlatformConfigurationException or
                                           IOException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "Import failed for {Platform}/{ActivityId}.",
                    source.Platform,
                    activity.Id);
                result = ImportResult.Failed(ToFriendlyMessage(ex));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected import failure for {Platform}/{ActivityId}.",
                    source.Platform,
                    activity.Id);
                result = ImportResult.Failed("导入失败：" + ex.Message);
            }

            results.Add(new SyncItemResult
            {
                Activity = activity,
                Result = result,
                SavedFilePath = savedPath,
            });

            progress?.Report(new SyncProgress(
                itemNumber,
                total,
                activity.Id,
                activity.Name,
                result.Success ? "完成" : "失败",
                (int)Math.Round(itemNumber * 100.0 / total)));

            if (itemNumber < total)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);
            }
        }

        var summary = new SyncSummary
        {
            SourceAccount = source,
            TargetAccount = target,
            Items = results,
        };

        await _history.AppendAsync(new SyncHistoryEntry
        {
            SourcePlatform = source.Platform,
            SourceName = source.DisplayName,
            TargetPlatform = target.Platform,
            TargetName = target.DisplayName,
            Total = summary.Total,
            Succeeded = summary.Succeeded,
            Duplicates = summary.Duplicates,
            Failed = summary.Failed,
            FirstError = summary.FirstError,
        }, cancellationToken).ConfigureAwait(false);

        return summary;
    }

    private async Task<string?> TrySaveCopyAsync(
        ActivityFile file,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = _settings.ResolveDownloadDirectory();
            var safeName = ActivityFile.SafeFileName(activity.Name, "activity");
            var baseName = $"{activity.DisplayStartTime:yyyy-MM-dd}_{safeName}_{SanitizeId(activity.Id)}";
            var path = Path.Combine(directory, $"{baseName}.{file.Format.Extension()}");

            var counter = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(directory, $"{baseName}_{counter++}.{file.Format.Extension()}");
            }

            await File.WriteAllBytesAsync(path, file.Content, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save a local activity copy.");
            return null;
        }
    }

    private static string SanitizeId(string id)
    {
        var cleaned = new string(id
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-')
            .ToArray());
        return cleaned.Length > 48 ? cleaned[..48] : cleaned;
    }

    private static string ToFriendlyMessage(Exception exception) => exception switch
    {
        PlatformApiException api => api.Message,
        PlatformConfigurationException config => config.Message,
        IOException => "本地文件读写失败，请检查下载目录权限。",
        _ => exception.Message,
    };
}
