using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

public sealed class RideWithGpsProvider : IPlatformProvider
{
    private const string ApiBase = "https://ridewithgps.com/api/v1";

    private readonly HttpClient _http;

    public RideWithGpsProvider(HttpClient http)
    {
        _http = http;
    }

    public PlatformId Platform => PlatformId.RideWithGps;

    public async Task<AccountProfile> GetProfileAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ApiBase + "/users/current", account);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

        var root = doc.RootElement;
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            root = user;
        }

        var id = GetString(root, "id") ?? account.UserId;
        var name = GetString(root, "display_name") ?? GetString(root, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = string.Join(' ',
                new[] { GetString(root, "first_name"), GetString(root, "last_name") }
                    .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Ride with GPS 用户";
        }

        return new AccountProfile(id, name);
    }

    public async Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var results = new List<ActivitySummary>();
        var limit = Math.Clamp(query.MaxResults, 1, 1000);
        var page = 1;

        while (results.Count < limit && page <= 10)
        {
            var url = $"{ApiBase}/trips.json?page={page}&page_size=100";
            using var request = CreateRequest(HttpMethod.Get, url, account);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("trips", out var trips) ||
                trips.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var item in trips.EnumerateArray())
            {
                count++;
                var activity = MapActivity(item, account);
                if (activity is null)
                {
                    continue;
                }

                if (activity.StartTime < query.From || activity.StartTime > query.To)
                {
                    continue;
                }

                if (query.CyclingOnly && !activity.IsCycling)
                {
                    continue;
                }

                results.Add(activity);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            var hasNext = doc.RootElement.TryGetProperty("meta", out var meta) &&
                          meta.TryGetProperty("pagination", out var pagination) &&
                          pagination.TryGetProperty("next_page_url", out var next) &&
                          next.ValueKind != JsonValueKind.Null &&
                          !string.IsNullOrWhiteSpace(next.GetString());

            if (!hasNext || count < 100)
            {
                break;
            }

            page++;
        }

        return results.OrderByDescending(a => a.StartTime).Take(limit).ToArray();
    }

    public async Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            ActivityFileFormat.Fit,
            ActivityFileFormat.Gpx,
            ActivityFileFormat.Tcx,
        };

        PlatformApiException? lastError = null;
        foreach (var format in candidates)
        {
            var url = $"{ApiBase}/trips/{Uri.EscapeDataString(activity.Id)}.{format.Extension()}";
            using var request = CreateRequest(HttpMethod.Get, url, account);
            using var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotAcceptable or
                HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest)
            {
                lastError = new PlatformApiException(
                    Platform,
                    (int)response.StatusCode,
                    $"该活动没有 {format.Extension().ToUpperInvariant()} 文件。");
                continue;
            }

            var content = await HttpApi.ReadBytesAsync(response, Platform, cancellationToken)
                .ConfigureAwait(false);
            content = HttpApi.DecompressGzipIfNeeded(content);

            var name = ActivityFile.SafeFileName(activity.Name, $"rwgps-{activity.Id}");
            return new ActivityFile
            {
                Content = content,
                FileName = $"{name}-{activity.Id}.{format.Extension()}",
                Format = format,
                SourceActivityId = activity.Id,
            };
        }

        throw lastError ?? new PlatformApiException(Platform, 404, "无法下载该 Ride with GPS 活动文件。");
    }

    public async Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(file.Content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.Format.ContentType());
        multipart.Add(fileContent, "file", file.FileName);

        multipart.Add(new StringContent(
            options.NameOverride ?? Path.GetFileNameWithoutExtension(file.FileName),
            Encoding.UTF8), "name");

        if (!string.IsNullOrWhiteSpace(options.DescriptionOverride))
        {
            multipart.Add(new StringContent(options.DescriptionOverride, Encoding.UTF8), "description");
        }

        using var request = CreateRequest(HttpMethod.Post, ApiBase + "/trips.json", account);
        request.Content = multipart;

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateUploadException(response.StatusCode, body);
        }

        var taskUrl = response.Headers.Location?.ToString();
        string? initialTaskJson = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("task", out var task) &&
                    task.ValueKind == JsonValueKind.Object)
                {
                    taskUrl ??= GetString(task, "url");
                    if (GetString(task, "status")?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        initialTaskJson = task.GetRawText();
                    }
                }
            }
            catch (JsonException)
            {
                // The Location header is authoritative; ignore an unexpected preview body.
            }
        }

        if (initialTaskJson is not null)
        {
            return InterpretTask(initialTaskJson);
        }

        if (string.IsNullOrWhiteSpace(taskUrl))
        {
            return ImportResult.Failed("Ride with GPS 未返回上传任务地址。");
        }

        if (!Uri.TryCreate(taskUrl, UriKind.Absolute, out var taskUri))
        {
            taskUri = new Uri(new Uri("https://ridewithgps.com"), taskUrl);
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);

            using var statusRequest = CreateRequest(HttpMethod.Get, taskUri.ToString(), account);
            using var statusResponse = await _http.SendAsync(statusRequest, cancellationToken)
                .ConfigureAwait(false);
            using var doc = await HttpApi.ReadJsonAsync(statusResponse, Platform, cancellationToken)
                .ConfigureAwait(false);

            var root = doc.RootElement;
            if (root.TryGetProperty("task", out var task) && task.ValueKind == JsonValueKind.Object)
            {
                root = task;
            }

            var status = GetString(root, "status");
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                return InterpretTask(root.GetRawText());
            }
        }

        return ImportResult.Failed("Ride with GPS 处理超时，请稍后在平台中确认活动是否出现。");
    }

    private ImportResult InterpretTask(string taskJson)
    {
        using var doc = JsonDocument.Parse(taskJson);
        var root = doc.RootElement;

        if (root.TryGetProperty("items", out var items) &&
            items.ValueKind == JsonValueKind.Array &&
            items.GetArrayLength() > 0)
        {
            var first = items[0];
            var id = GetString(first, "item_id");
            var itemUrl = GetString(first, "item_url");
            return ImportResult.Ok(
                id,
                itemUrl,
                "已导入 Ride with GPS");
        }

        if (root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                var code = GetString(error, "code") ?? string.Empty;
                var message = GetString(error, "message") ?? "未知错误";
                if (code.Equals("duplicate", StringComparison.OrdinalIgnoreCase))
                {
                    return ImportResult.Duplicated("Ride with GPS 检测到重复活动，已跳过。");
                }

                return ImportResult.Failed("Ride with GPS 处理失败：" + message);
            }
        }

        return ImportResult.Failed("Ride with GPS 未创建活动，请检查文件是否包含时间信息。");
    }

    private static PlatformApiException CreateUploadException(HttpStatusCode status, string body)
    {
        var detail = body.Length > 300 ? body[..300] : body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                detail = string.Join("；", errors.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }
        catch (JsonException)
        {
            // Keep the raw excerpt.
        }

        return new PlatformApiException(
            PlatformId.RideWithGps,
            (int)status,
            "Ride with GPS 拒绝了上传请求。" +
            (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail));
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        PlatformAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.ApiKey) || string.IsNullOrWhiteSpace(account.AuthToken))
        {
            throw new PlatformConfigurationException("Ride with GPS API Key 或 Auth Token 缺失，请重新连接。");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("x-rwgps-api-key", account.ApiKey);
        request.Headers.TryAddWithoutValidation("x-rwgps-auth-token", account.AuthToken);
        return request;
    }

    private static ActivitySummary? MapActivity(JsonElement item, PlatformAccount account)
    {
        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var startText = GetString(item, "departed_at") ?? GetString(item, "created_at");
        var start = DateTimeOffset.TryParse(
            startText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.Now;

        var local = DateTime.TryParse(
            GetString(item, "departed_at"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedLocal)
            ? DateTime.SpecifyKind(parsedLocal, DateTimeKind.Unspecified)
            : (DateTime?)null;

        var sport = GetString(item, "activity_type") ?? "Ride";
        return new ActivitySummary
        {
            Id = id,
            Platform = PlatformId.RideWithGps,
            AccountId = account.Id,
            AccountName = account.DisplayName,
            Name = GetString(item, "name") ?? $"Ride with GPS 活动 {id}",
            SportType = sport,
            StartTime = start,
            LocalStartTime = local,
            DistanceMeters = GetDouble(item, "distance") ?? 0,
            MovingTimeSeconds = (int)(GetDouble(item, "moving_time") ?? 0),
            ElapsedTimeSeconds = (int)(GetDouble(item, "duration") ?? 0),
            ElevationGainMeters = GetDouble(item, "elevation_gain") ?? 0,
            AveragePowerWatts = GetDouble(item, "avg_watts"),
            AverageHeartRate = ToInt(GetDouble(item, "avg_hr")),
            HasGps = GetDouble(item, "first_lat") is not null,
            SourceFileType = "fit",
            WebUrl = GetString(item, "web_url") ?? $"https://ridewithgps.com/trips/{id}",
        };
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
