using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

public sealed class MyWhooshProvider : IPlatformProvider
{
    private readonly MyWhooshApi _api;

    public MyWhooshProvider(HttpClient http)
    {
        _api = new MyWhooshApi(http);
    }

    public PlatformId Platform => PlatformId.MyWhoosh;

    public async Task<AccountProfile> GetProfileAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        var profile = await _api.GetProfileAsync(token, cancellationToken).ConfigureAwait(false);
        return new AccountProfile(
            string.IsNullOrWhiteSpace(profile.UserId) ? account.UserId : profile.UserId,
            string.IsNullOrWhiteSpace(profile.DisplayName) ? account.DisplayName : profile.DisplayName);
    }

    public async Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        var rows = await _api.ListActivitiesAsync(token, query, cancellationToken).ConfigureAwait(false);
        return rows
            .Select(row => new ActivitySummary
            {
                Id = row.Id,
                Platform = PlatformId.MyWhoosh,
                AccountId = account.Id,
                AccountName = account.DisplayName,
                Name = row.Name,
                SportType = "VirtualRide",
                StartTime = row.StartTime,
                LocalStartTime = row.StartTime.DateTime,
                DistanceMeters = row.DistanceMeters,
                MovingTimeSeconds = row.DurationSeconds,
                ElapsedTimeSeconds = row.DurationSeconds,
                ElevationGainMeters = row.ElevationMeters,
                AveragePowerWatts = row.AveragePowerWatts,
                HasGps = false,
                SourceFileType = "FIT",
                WebUrl = "https://www.mywhoosh.com",
            })
            .ToArray();
    }

    public async Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        var raw = await _api.DownloadFitAsync(token, activity.Id, cancellationToken).ConfigureAwait(false);
        double? ftp = null;
        try
        {
            var profile = await _api.GetProfileAsync(token, cancellationToken).ConfigureAwait(false);
            if (profile.Ftp > 0)
            {
                ftp = profile.Ftp;
            }
        }
        catch (PlatformApiException)
        {
            // FTP is only needed to enrich NP/IF/TSS. The raw FIT is still valid.
        }

        return new ActivityFile
        {
            Content = raw,
            FileName = ActivityFile.SafeFileName(
                $"{activity.DisplayStartTime:yyyy-MM-dd}_{activity.Name}_{activity.Id}",
                "mywhoosh-activity") + ".fit",
            Format = ActivityFileFormat.Fit,
            SourceActivityId = activity.Id,
            SourceFtpWatts = ftp,
        };
    }

    public Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken) =>
        throw new PlatformConfigurationException(
            "MyWhoosh 不提供公开的活动上传接口，请选择 iGPSPORT 作为导入目标。");

    private static string RequireToken(PlatformAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            throw new PlatformApiException(
                PlatformId.MyWhoosh,
                401,
                "MyWhoosh 登录已失效，请在「平台连接」中重新登录。");
        }

        return account.AccessToken;
    }
}
