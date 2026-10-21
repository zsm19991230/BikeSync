using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Services;

namespace BikeSync.Core.Providers;

public sealed class StravaProvider : IPlatformProvider
{
    private const string ApiBase = "https://www.strava.com/api/v3";
    private const string UploadUrl = ApiBase + "/uploads";

    private readonly HttpClient _http;

    public StravaProvider(HttpClient http)
    {
        _http = http;
    }

    public PlatformId Platform => PlatformId.Strava;

    public async Task<AccountProfile> GetProfileAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ApiBase + "/athlete", account);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var id = GetString(root, "id") ?? account.UserId;
        var name = string.Join(' ',
            new[] { GetString(root, "firstname"), GetString(root, "lastname") }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        return new AccountProfile(
            id,
            string.IsNullOrWhiteSpace(name) ? account.DisplayName : name,
            GetString(root, "profile"));
    }

    public async Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.MaxResults, 1, 1000);
        var perPage = 100;
        var results = new List<ActivitySummary>(Math.Min(limit, 200));

        for (var page = 1; results.Count < limit && page <= 10; page++)
        {
            var url = $"{ApiBase}/athlete/activities?after={query.From.ToUnixTimeSeconds()}" +
                      $"&before={query.To.ToUnixTimeSeconds()}&page={page}&per_page={perPage}";

            using var request = CreateRequest(HttpMethod.Get, url, account);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new PlatformApiException(Platform, 500, "Strava 返回了无法识别的活动列表。");
            }

            var count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                count++;
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
                if (results.Count >= limit)
                {
                    break;
                }
            }

            if (count < perPage)
            {
                break;
            }
        }

        return results;
    }

    public async Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var url = $"{ApiBase}/activities/{Uri.EscapeDataString(activity.Id)}/streams" +
                  "?keys=time,latlng,altitude,heartrate,cadence,watts,distance&key_by_type=true";

        using var request = CreateRequest(HttpMethod.Get, url, account);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

        var streams = ParseStreams(doc.RootElement, activity);
        if (streams.Count == 0)
        {
            throw new PlatformApiException(
                Platform,
                422,
                "Strava 只提供该活动的数据流，未返回可用轨迹；无法生成导入文件。");
        }

        var tcx = TcxBuilder.Build(activity, streams);
        var name = ActivityFile.SafeFileName(activity.Name, $"strava-{activity.Id}");
        return new ActivityFile
        {
            Content = tcx,
            FileName = $"{name}-{activity.Id}.tcx",
            Format = ActivityFileFormat.Tcx,
            SourceActivityId = activity.Id,
        };
    }

    public async Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(file.DataType, Encoding.UTF8), "data_type");
        multipart.Add(new StringContent(
            options.NameOverride ?? Path.GetFileNameWithoutExtension(file.FileName),
            Encoding.UTF8), "name");

        if (!string.IsNullOrWhiteSpace(options.DescriptionOverride))
        {
            multipart.Add(new StringContent(options.DescriptionOverride, Encoding.UTF8), "description");
        }

        if (!string.IsNullOrWhiteSpace(options.SportType))
        {
            var mapped = SportTypeMapper.ToStrava(options.SportType);
            if (mapped is not null)
            {
                multipart.Add(new StringContent(mapped, Encoding.UTF8), "sport_type");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.ExternalId))
        {
            multipart.Add(new StringContent(options.ExternalId, Encoding.UTF8), "external_id");
        }

        if (options.MarkAsCommute)
        {
            multipart.Add(new StringContent("1", Encoding.UTF8), "commute");
        }

        var fileContent = new ByteArrayContent(file.Content);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.Format.ContentType());
        multipart.Add(fileContent, "file", file.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl) { Content = multipart };
        request.Headers.Authorization = HttpApi.Bearer(RequireToken(account));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var doc = await HttpApi.ReadJsonAsync(response, Platform, cancellationToken).ConfigureAwait(false);

        var uploadId = GetString(doc.RootElement, "id") ?? GetString(doc.RootElement, "id_str");
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            return ImportResult.Failed("Strava 未返回上传任务编号。");
        }

        var initialError = GetString(doc.RootElement, "error");
        if (!string.IsNullOrWhiteSpace(initialError))
        {
            return InterpretUploadError(initialError);
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationToken).ConfigureAwait(false);

            using var statusRequest = CreateRequest(HttpMethod.Get, $"{UploadUrl}/{uploadId}", account);
            using var statusResponse = await _http.SendAsync(statusRequest, cancellationToken)
                .ConfigureAwait(false);
            using var statusDoc = await HttpApi.ReadJsonAsync(statusResponse, Platform, cancellationToken)
                .ConfigureAwait(false);

            var root = statusDoc.RootElement;
            var error = GetString(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
            {
                return InterpretUploadError(error);
            }

            var activityId = GetString(root, "activity_id");
            if (!string.IsNullOrWhiteSpace(activityId))
            {
                return ImportResult.Ok(
                    activityId,
                    $"https://www.strava.com/activities/{activityId}",
                    "已导入 Strava");
            }
        }

        return ImportResult.Failed("Strava 处理超时，请稍后在 Strava 中确认活动是否出现。");
    }

    private static ImportResult InterpretUploadError(string error)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(error, "<.*?>", string.Empty).Trim();
        if (clean.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
        {
            return ImportResult.Duplicated("Strava 检测到重复活动，已跳过。");
        }

        return ImportResult.Failed("Strava 处理失败：" + clean);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string url,
        PlatformAccount account)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = HttpApi.Bearer(RequireToken(account));
        return request;
    }

    private static string RequireToken(PlatformAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            throw new PlatformConfigurationException("Strava 访问令牌缺失，请重新连接。");
        }

        return account.AccessToken;
    }

    private static ActivitySummary? MapActivity(JsonElement item, PlatformAccount account)
    {
        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var startDate = GetDateTimeOffset(item, "start_date") ?? DateTimeOffset.Now;
        var localStart = GetLocalDateTime(item, "start_date_local");
        var sport = GetString(item, "sport_type") ?? GetString(item, "type") ?? "Ride";
        var hasGps = false;
        if (item.TryGetProperty("map", out var map) &&
            map.ValueKind == JsonValueKind.Object)
        {
            hasGps = !string.IsNullOrWhiteSpace(GetString(map, "summary_polyline"));
        }

        return new ActivitySummary
        {
            Id = id,
            Platform = PlatformId.Strava,
            AccountId = account.Id,
            AccountName = account.DisplayName,
            Name = GetString(item, "name") ?? $"Strava 活动 {id}",
            SportType = sport,
            StartTime = startDate,
            LocalStartTime = localStart,
            DistanceMeters = GetDouble(item, "distance") ?? 0,
            MovingTimeSeconds = (int)(GetDouble(item, "moving_time") ?? 0),
            ElapsedTimeSeconds = (int)(GetDouble(item, "elapsed_time") ?? 0),
            ElevationGainMeters = GetDouble(item, "total_elevation_gain") ?? 0,
            AveragePowerWatts = GetDouble(item, "average_watts"),
            AverageHeartRate = (int?)GetDouble(item, "average_heartrate"),
            HasGps = hasGps,
            SourceFileType = "stream",
            WebUrl = $"https://www.strava.com/activities/{id}",
        };
    }

    private static List<TcxTrackPoint> ParseStreams(JsonElement root, ActivitySummary activity)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new List<TcxTrackPoint>();
        }

        var time = ReadDoubleArray(root, "time");
        var distance = ReadDoubleArray(root, "distance");
        var altitude = ReadDoubleArray(root, "altitude");
        var heartRate = ReadDoubleArray(root, "heartrate");
        var cadence = ReadDoubleArray(root, "cadence");
        var watts = ReadDoubleArray(root, "watts");
        var latlng = ReadLatLngArray(root, "latlng");

        var count = new[] { time.Count, latlng.Count, distance.Count, altitude.Count, heartRate.Count }
            .Max();
        var points = new List<TcxTrackPoint>(count);

        for (var i = 0; i < count; i++)
        {
            var seconds = i < time.Count && double.IsFinite(time[i]) ? time[i] : i;
            var pointTime = activity.StartTime.ToUniversalTime().AddSeconds(seconds);
            var lat = i < latlng.Count && double.IsFinite(latlng[i].Lat) ? latlng[i].Lat : (double?)null;
            var lon = i < latlng.Count && double.IsFinite(latlng[i].Lng) ? latlng[i].Lng : (double?)null;

            points.Add(new TcxTrackPoint(
                pointTime,
                lat,
                lon,
                FiniteAt(altitude, i),
                FiniteAt(distance, i),
                ToNullableInt(heartRate, i),
                ToNullableInt(cadence, i),
                ToNullableInt(watts, i)));
        }

        return points;
    }

    private static double? FiniteAt(List<double> values, int index) =>
        index < values.Count && double.IsFinite(values[index]) ? values[index] : null;

    private static int? ToNullableInt(List<double> values, int index) =>
        index < values.Count && double.IsFinite(values[index])
            ? (int)Math.Round(values[index])
            : null;

    private static List<double> ReadDoubleArray(JsonElement root, string name)
    {
        var result = new List<double>();
        if (!root.TryGetProperty(name, out var stream) ||
            !stream.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
            {
                result.Add(value);
            }
            else
            {
                result.Add(double.NaN);
            }
        }

        return result;
    }

    private static List<(double Lat, double Lng)> ReadLatLngArray(JsonElement root, string name)
    {
        var result = new List<(double, double)>();
        if (!root.TryGetProperty(name, out var stream) ||
            !stream.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() >= 2)
            {
                var lat = element[0];
                var lng = element[1];
                result.Add((
                    lat.ValueKind == JsonValueKind.Number ? lat.GetDouble() : double.NaN,
                    lng.ValueKind == JsonValueKind.Number ? lng.GetDouble() : double.NaN));
            }
            else
            {
                result.Add((double.NaN, double.NaN));
            }
        }

        return result;
    }

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

    private static DateTimeOffset? GetDateTimeOffset(JsonElement element, string name)
    {
        var text = GetString(element, name);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : null;
    }

    private static DateTime? GetLocalDateTime(JsonElement element, string name)
    {
        var text = GetString(element, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Strava serializes start_date_local with a trailing Z even though the value
        // is already local wall-clock time. Strip it instead of shifting the timezone.
        var wallClock = text.TrimEnd('Z', 'z');
        return DateTime.TryParse(
            wallClock,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
            : null;
    }
}

public static class SportTypeMapper
{
    public static string? ToStrava(string? sport)
    {
        if (string.IsNullOrWhiteSpace(sport))
        {
            return null;
        }

        var value = sport.Trim();
        if (value.Contains("mountain", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("bike", StringComparison.OrdinalIgnoreCase))
        {
            return "MountainBikeRide";
        }

        if (value.Contains("gravel", StringComparison.OrdinalIgnoreCase))
        {
            return "GravelRide";
        }

        if (value.Contains("virtual", StringComparison.OrdinalIgnoreCase))
        {
            return "VirtualRide";
        }

        if (value.Contains("e-bike", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("ebike", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("electric", StringComparison.OrdinalIgnoreCase))
        {
            return "EBikeRide";
        }

        if (value.Contains("handcycle", StringComparison.OrdinalIgnoreCase))
        {
            return "Handcycle";
        }

        if (value.Contains("velomobile", StringComparison.OrdinalIgnoreCase))
        {
            return "Velomobile";
        }

        if (CyclingSports.IsCycling(value))
        {
            return "Ride";
        }

        return value;
    }
}
