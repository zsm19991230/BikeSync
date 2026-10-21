using System.Globalization;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

public sealed class IntervalsIcuProvider : IPlatformProvider
{
    private const string ApiBase = "https://intervals.icu/api/v1";

    private readonly HttpClient _http;

    public IntervalsIcuProvider(HttpClient http)
    {
        _http = http;
    }

    public PlatformId Platform => PlatformId.IntervalsIcu;

    public async Task<AccountProfile> GetProfileAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ApiBase + "/athlete/0", account);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        if (root.TryGetProperty("athlete", out var athlete) &&
            athlete.ValueKind == JsonValueKind.Object)
        {
            root = athlete;
        }

        var id = GetString(root, "id") ?? account.UserId;
        var name = GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.Join(' ',
                new[] { GetString(root, "firstname"), GetString(root, "lastname") }
                    .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "intervals.icu 运动员";
        }

        return new AccountProfile(id, name, GetString(root, "profile_medium"));
    }

    public async Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var oldest = query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newest = query.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var limit = Math.Clamp(query.MaxResults, 1, 1000);

        var url = $"{ApiBase}/athlete/0/activities?oldest={oldest}&newest={newest}&limit={limit}";
        using var request = CreateRequest(HttpMethod.Get, url, account);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new PlatformApiException(Platform, 500, "intervals.icu 返回了无法识别的活动列表。");
        }

        var results = new List<ActivitySummary>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var activity = MapActivity(item, account);
            if (activity is null)
            {
                continue;
            }

            if (query.CyclingOnly && !activity.IsCycling)
            {
                continue;
            }

            results.Add(activity);
        }

        return results
            .OrderByDescending(a => a.StartTime)
            .Take(limit)
            .ToArray();
    }

    public async Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var originalUrl = $"{ApiBase}/activity/{Uri.EscapeDataString(activity.Id)}/file";
        byte[] content;
        try
        {
            content = await DownloadBytesAsync(originalUrl, account, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException ex) when (ex.StatusCode is 400 or 404 or 415)
        {
            // Original files are unavailable for Strava-sourced activities; try the
            // Intervals.icu generated FIT file instead.
            var fitUrl = $"{ApiBase}/activity/{Uri.EscapeDataString(activity.Id)}/fit-file" +
                         "?power=true&hr=true";
            try
            {
                content = await DownloadBytesAsync(fitUrl, account, cancellationToken).ConfigureAwait(false);
                activity = activity with { SourceFileType = "fit" };
            }
            catch (PlatformApiException fallback)
            {
                throw new PlatformApiException(
                    Platform,
                    fallback.StatusCode,
                    "无法下载该活动的原始文件。若活动来自 Strava 同步，intervals.icu 不提供文件下载。",
                    fallback.Detail,
                    fallback);
            }
        }

        content = HttpApi.DecompressGzipIfNeeded(content);
        var format = ActivityFileFormatExtensions.FromExtension(activity.SourceFileType) ??
                     DetectFormat(content) ??
                     ActivityFileFormat.Fit;
        var name = ActivityFile.SafeFileName(activity.Name, $"intervals-{activity.Id}");

        return new ActivityFile
        {
            Content = content,
            FileName = $"{name}-{activity.Id}.{format.Extension()}",
            Format = format,
            SourceActivityId = activity.Id,
        };
    }

    public async Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.NameOverride))
        {
            query.Add("name=" + Uri.EscapeDataString(options.NameOverride));
        }

        if (!string.IsNullOrWhiteSpace(options.DescriptionOverride))
        {
            query.Add("description=" + Uri.EscapeDataString(options.DescriptionOverride));
        }

        if (!string.IsNullOrWhiteSpace(options.ExternalId))
        {
            query.Add("external_id=" + Uri.EscapeDataString(options.ExternalId));
        }

        var url = $"{ApiBase}/athlete/0/activities";
        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(file.Content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.Format.ContentType());
        multipart.Add(fileContent, "file", file.FileName);

        using var request = CreateRequest(HttpMethod.Post, url, account);
        request.Content = multipart;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        string? activityId = null;
        var count = 0;
        if (root.TryGetProperty("activities", out var activities) &&
            activities.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in activities.EnumerateArray())
            {
                count++;
                activityId ??= GetString(item, "id");
            }
        }

        if (count > 0)
        {
            return ImportResult.Ok(
                activityId,
                activityId is null ? null : $"https://intervals.icu/activities/{activityId}",
                "已导入 intervals.icu");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            return ImportResult.Duplicated("intervals.icu 检测到重复文件，已跳过。");
        }

        return ImportResult.Ok(null, null, "已提交到 intervals.icu");
    }

    private async Task<byte[]> DownloadBytesAsync(
        string url,
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, url, account);
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        return await HttpApi.ReadBytesAsync(response, Platform, cancellationToken).ConfigureAwait(false);
    }

    private static ActivityFileFormat? DetectFormat(byte[] content)
    {
        if (content.Length >= 12 &&
            content[8] == (byte)'F' && content[9] == (byte)'I' &&
            content[10] == (byte)'T')
        {
            return ActivityFileFormat.Fit;
        }

        if (content.Length > 4)
        {
            var prefix = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 600));
            if (prefix.Contains("<gpx", StringComparison.OrdinalIgnoreCase))
            {
                return ActivityFileFormat.Gpx;
            }

            if (prefix.Contains("<TrainingCenterDatabase", StringComparison.OrdinalIgnoreCase))
            {
                return ActivityFileFormat.Tcx;
            }
        }

        return null;
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        PlatformAccount account)
    {
        var key = account.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new PlatformConfigurationException("intervals.icu API Key 缺失，请重新连接。");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = HttpApi.Basic("API_KEY", key);
        return request;
    }

    private static ActivitySummary? MapActivity(JsonElement item, PlatformAccount account)
    {
        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var localText = GetString(item, "start_date_local");
        if (string.IsNullOrWhiteSpace(localText) && !item.TryGetProperty("name", out _))
        {
            // Strava activities are returned as empty stubs by the list endpoint.
            return null;
        }

        var local = TryParseLocal(localText);
        var utc = TryParseUtc(GetString(item, "start_date"));
        if (utc is null && local is not null)
        {
            utc = new DateTimeOffset(local.Value, TimeZoneInfo.Local.GetUtcOffset(local.Value));
        }

        var start = utc ?? DateTimeOffset.Now;
        var sport = GetString(item, "type") ??
                    GetString(item, "sub_type") ??
                    "Ride";
        var power = GetDouble(item, "icu_average_watts") ?? GetDouble(item, "average_watts");

        return new ActivitySummary
        {
            Id = id,
            Platform = PlatformId.IntervalsIcu,
            AccountId = account.Id,
            AccountName = account.DisplayName,
            Name = GetString(item, "name") ?? $"intervals.icu 活动 {id}",
            SportType = sport,
            StartTime = start,
            LocalStartTime = local,
            DistanceMeters = GetDouble(item, "distance") ?? GetDouble(item, "icu_distance") ?? 0,
            MovingTimeSeconds = (int)(GetDouble(item, "moving_time") ?? 0),
            ElapsedTimeSeconds = (int)(GetDouble(item, "elapsed_time") ?? 0),
            ElevationGainMeters = GetDouble(item, "total_elevation_gain") ?? 0,
            AveragePowerWatts = power,
            AverageHeartRate = ToInt(GetDouble(item, "average_heartrate")),
            HasGps = true,
            SourceFileType = GetString(item, "file_type") ?? "fit",
            WebUrl = $"https://intervals.icu/activities/{id}",
        };
    }

    private static DateTime? TryParseLocal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParse(
            text.TrimEnd('Z', 'z'),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            : null;
    }

    private static DateTimeOffset? TryParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static int? ToInt(double? value) =>
        value is { } number && double.IsFinite(number) ? (int)Math.Round(number) : null;

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}
