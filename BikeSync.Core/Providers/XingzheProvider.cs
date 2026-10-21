using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

public sealed class XingzheProvider : IPlatformProvider
{
    private const string BaseUrl = "https://www.imxingzhe.com";
    private const string WorkoutApi = BaseUrl + "/api/v1/pgworkout/";
    private const string UploadUrl = BaseUrl + "/api/v1/fit/upload/";

    private readonly HttpClient _http;

    public XingzheProvider(HttpClient http)
    {
        _http = http;
    }

    public PlatformId Platform => PlatformId.Xingzhe;

    public Task<AccountProfile> GetProfileAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        RequireCookie(account);
        return Task.FromResult(new AccountProfile(account.UserId, account.DisplayName));
    }

    public async Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var cookie = RequireCookie(account);
        var activities = new List<ActivitySummary>();
        const int pageSize = 1000;
        for (var offset = 0; offset < 5000 && activities.Count < query.MaxResults; offset += pageSize)
        {
            using var document = await GetJsonAsync(
                $"{WorkoutApi}?offset={offset}&limit={pageSize}",
                cookie,
                cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("code", out var codeNode))
            {
                var code = ReadInt(root, "code");
                if (code != 0)
                {
                    var message = ReadText(root, "msg") ?? ReadText(root, "message") ?? "行者接口返回错误。";
                    throw new PlatformApiException(
                        PlatformId.Xingzhe,
                        code == 400 ? 401 : 502,
                        "行者活动列表获取失败：" + message);
                }
            }

            if (!TryGetRows(root, out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var batch = 0;
            foreach (var row in rows.EnumerateArray())
            {
                batch++;
                var id = ReadText(row, "id");
                var startValue = ReadLong(row, "start_time");
                if (string.IsNullOrWhiteSpace(id) || startValue is null || startValue <= 0)
                {
                    continue;
                }

                var start = FromUnixMilliseconds(startValue.Value).ToLocalTime();
                if (start < query.From.ToLocalTime() || start > query.To.ToLocalTime())
                {
                    continue;
                }

                var sport = MapSport(ReadInt(row, "sport"));
                if (query.CyclingOnly && !CyclingSports.IsCycling(sport))
                {
                    continue;
                }

                var name = ReadText(row, "title") ?? "行者骑行";
                if (!string.IsNullOrWhiteSpace(query.Search) &&
                    !name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                activities.Add(new ActivitySummary
                {
                    Id = id,
                    Platform = PlatformId.Xingzhe,
                    AccountId = account.Id,
                    AccountName = account.DisplayName,
                    Name = name,
                    SportType = sport,
                    StartTime = start,
                    LocalStartTime = start.DateTime,
                    DistanceMeters = Math.Max(0, ReadDouble(row, "distance")),
                    MovingTimeSeconds = (int)Math.Max(0, ReadDouble(row, "duration")),
                    ElapsedTimeSeconds = (int)Math.Max(0, ReadDouble(row, "duration")),
                    AveragePowerWatts = null,
                    HasGps = !string.Equals(sport, "IndoorCycling", StringComparison.OrdinalIgnoreCase),
                    SourceFileType = "FIT",
                    WebUrl = BaseUrl,
                });

                if (activities.Count >= query.MaxResults)
                {
                    break;
                }
            }

            if (batch < pageSize)
            {
                break;
            }
        }

        return activities.OrderByDescending(a => a.StartTime).ToArray();
    }

    public async Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var cookie = RequireCookie(account);
        var detail = await TryGetJsonAsync($"{WorkoutApi}{activity.Id}/", cookie, cancellationToken)
            .ConfigureAwait(false);
        var stream = await TryGetJsonAsync($"{WorkoutApi}{activity.Id}/stream/", cookie, cancellationToken)
            .ConfigureAwait(false);
        var gpx = await TryGetStringAsync($"{WorkoutApi}{activity.Id}/gpx/", cookie, cancellationToken)
            .ConfigureAwait(false);

        var points = BuildPoints(stream, gpx, detail);
        var start = ReadDetailDate(detail, "start_time") ?? activity.StartTime;
        var end = ReadDetailDate(detail, "end_time") ??
                  (points.Count > 0 ? points[^1].Timestamp : start.AddSeconds(activity.ElapsedTimeSeconds));
        var distance = ReadDetailDouble(detail, "distance");
        if (distance <= 0 && points.Count > 0)
        {
            distance = points.LastOrDefault()?.DistanceMeters ?? activity.DistanceMeters;
        }

        if (distance <= 0)
        {
            distance = activity.DistanceMeters;
        }

        var duration = ReadDetailDouble(detail, "duration");
        if (duration <= 0)
        {
            duration = Math.Max(0, (end - start).TotalSeconds);
        }

        if (duration <= 0)
        {
            duration = activity.MovingTimeSeconds;
        }

        var title = ReadDetailText(detail, "title") ?? activity.Name;
        var sport = ReadDetailInt(detail, "sport");
        var fit = FitWriter.Write(
            new FitWorkout(
                start,
                end,
                title,
                points,
                distance,
                duration),
            sport is 3 or 11 or 12 ? (byte)2 : (byte)0);

        return new ActivityFile
        {
            Content = fit,
            FileName = ActivityFile.SafeFileName(
                $"xingzhe_{start:yyyy-MM-dd}_{activity.Id}",
                "xingzhe-activity") + ".fit",
            Format = ActivityFileFormat.Fit,
            SourceActivityId = activity.Id,
        };
    }

    public async Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var cookie = RequireCookie(account);
        if (file.Format != ActivityFileFormat.Fit)
        {
            return ImportResult.Failed("行者仅接受 FIT 文件；请从 MyWhoosh、iGPSPORT 或其他 FIT 数据源导入。");
        }

        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(file.Content))
            .ToLowerInvariant();
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(file.Content);
        fileContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(new StringContent("undefined"), "file_source");
        form.Add(new StringContent(file.FileName), "fit_filename");
        form.Add(new StringContent(md5), "md5");
        form.Add(new StringContent(file.FileName), "name");
        form.Add(new StringContent("3"), "sport");
        form.Add(fileContent, "fit_file", file.FileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadUrl)
        {
            Content = form,
        };
        request.Headers.TryAddWithoutValidation("Cookie", EnsureWebToken(cookie));
        request.Headers.TryAddWithoutValidation("Referer", "https://www.imxingzhe.com/");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                return ImportResult.Failed("行者登录已失效，请在「平台连接」中重新登录。");
            }

            return ImportResult.Failed("行者返回了无法识别的数据：" + Shorten(body));
        }

        using (document)
        {
            var root = document.RootElement;
            var code = ReadInt(root, "code");
            if (code == 9006)
            {
                return ImportResult.Duplicated("行者已存在该活动，已跳过。");
            }

            var dataNode = root.TryGetProperty("data", out var data) ? data : default;
            var serverId = dataNode.ValueKind == JsonValueKind.Object
                ? ReadText(dataNode, "workout_id") ?? ReadText(dataNode, "id")
                : null;
            if (code == 0 && !string.IsNullOrWhiteSpace(serverId))
            {
                return ImportResult.Ok(
                    serverId,
                    "https://www.imxingzhe.com/",
                    "已导入行者。");
            }

            if (code == 0)
            {
                return ImportResult.Ok(
                    null,
                    null,
                    "已提交行者，平台正在处理。");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ImportResult.Failed("行者登录已失效，请在「平台连接」中重新登录。");
            }

            var message = ReadText(root, "error_message") ?? ReadText(root, "message") ??
                          "行者上传失败。";
            return ImportResult.Failed(message + (string.IsNullOrWhiteSpace(body) ? string.Empty : " " + Shorten(body)));
        }
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        string cookie,
        CancellationToken cancellationToken)
    {
        var body = await GetStringAsync(url, cookie, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PlatformApiException(PlatformId.Xingzhe, 502, "行者返回了无法识别的数据。", body, ex);
        }
    }

    private async Task<JsonDocument?> TryGetJsonAsync(
        string url,
        string cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetJsonAsync(url, cookie, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException)
        {
            return null;
        }
    }

    private async Task<string?> TryGetStringAsync(
        string url,
        string cookie,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetStringAsync(url, cookie, cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException)
        {
            return null;
        }
    }

    private async Task<string> GetStringAsync(
        string url,
        string cookie,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BikeSync/1.1");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new PlatformApiException(
                PlatformId.Xingzhe,
                (int)response.StatusCode,
                "行者请求失败（HTTP " + (int)response.StatusCode + "）。",
                body);
        }

        if (body.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            body.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformApiException(
                PlatformId.Xingzhe,
                401,
                "行者登录已失效，请在「平台连接」中重新登录。");
        }

        return body;
    }

    private static List<FitTrackPoint> BuildPoints(
        JsonDocument? stream,
        string? gpx,
        JsonDocument? detail)
    {
        var gpxPoints = ParseGpx(gpx);
        if (stream is null)
        {
            return gpxPoints;
        }

        var data = stream.RootElement.TryGetProperty("data", out var dataNode)
            ? dataNode
            : default;
        if (data.ValueKind != JsonValueKind.Object)
        {
            return gpxPoints;
        }

        var timestamps = ReadLongArray(data, "timestamp");
        if (timestamps.Length == 0)
        {
            return gpxPoints;
        }

        var distances = ReadDoubleArray(data, "distance");
        var speeds = ReadDoubleArray(data, "speed");
        var altitudes = ReadDoubleArray(data, "altitude");
        var heartRates = ReadIntArray(data, "heartrate");
        var cadences = ReadIntArray(data, "cadence");
        var powers = ReadIntArray(data, "power");
        var temperatures = ReadIntArray(data, "temperature");

        var result = new List<FitTrackPoint>(timestamps.Length);
        var cumulative = 0d;
        for (var index = 0; index < timestamps.Length; index++)
        {
            var timestamp = FromUnixMilliseconds(timestamps[index]).ToLocalTime();
            var gpxPoint = gpxPoints.Count == timestamps.Length
                ? gpxPoints[index]
                : FindNearest(gpxPoints, timestamp);
            var distance = ValueAt(distances, index);
            if (distance is > 0)
            {
                cumulative = distance.Value;
            }
            else if (index > 0)
            {
                var previous = result[^1];
                if (previous.Latitude is { } lat1 && previous.Longitude is { } lon1 &&
                    gpxPoint?.Latitude is { } lat2 && gpxPoint.Longitude is { } lon2)
                {
                    cumulative += Haversine(lat1, lon1, lat2, lon2);
                }
            }

            var speed = ValueAt(speeds, index);
            if (speed is null or <= 0 && index > 0)
            {
                var seconds = (timestamp - result[^1].Timestamp).TotalSeconds;
                if (seconds > 0)
                {
                    speed = Math.Max(0, (cumulative - (result[^1].DistanceMeters ?? 0)) / seconds);
                }
            }

            result.Add(new FitTrackPoint(
                timestamp,
                gpxPoint?.Latitude,
                gpxPoint?.Longitude,
                distance ?? cumulative,
                speed,
                ValueAt(altitudes, index),
                IntAt(heartRates, index),
                IntAt(cadences, index),
                IntAt(powers, index),
                IntAt(temperatures, index)));
        }

        return result.Count == 0 ? gpxPoints : result;
    }

    private static List<FitTrackPoint> ParseGpx(string? gpx)
    {
        if (string.IsNullOrWhiteSpace(gpx) || !gpx.Contains("<gpx", StringComparison.OrdinalIgnoreCase))
        {
            return new List<FitTrackPoint>();
        }

        try
        {
            var document = XDocument.Parse(gpx);
            var points = new List<FitTrackPoint>();
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "trkpt"))
            {
                if (!double.TryParse(
                        element.Attribute("lat")?.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var lat) ||
                    !double.TryParse(
                        element.Attribute("lon")?.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var lon))
                {
                    continue;
                }

                var timeValue = element.Elements().FirstOrDefault(e => e.Name.LocalName == "time")?.Value;
                var time = DateTimeOffset.TryParse(
                    timeValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed)
                    ? parsed.ToLocalTime()
                    : DateTimeOffset.Now;
                var altitudeValue = element.Elements().FirstOrDefault(e => e.Name.LocalName == "ele")?.Value;
                var altitude = double.TryParse(
                    altitudeValue,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var ele)
                    ? ele
                    : (double?)null;
                points.Add(new FitTrackPoint(time, lat, lon, null, null, altitude, null, null, null, null));
            }

            return points;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new List<FitTrackPoint>();
        }
    }

    private static FitTrackPoint? FindNearest(IReadOnlyList<FitTrackPoint> points, DateTimeOffset time)
    {
        if (points.Count == 0)
        {
            return null;
        }

        return points.MinBy(p => Math.Abs((p.Timestamp - time).TotalSeconds));
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double radius = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static bool TryGetRows(JsonElement root, out JsonElement rows)
    {
        rows = default;
        if (!root.TryGetProperty("data", out var data))
        {
            return false;
        }

        if (data.TryGetProperty("data", out rows))
        {
            return true;
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            rows = data;
            return true;
        }

        return false;
    }

    private static string RequireCookie(PlatformAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AuthToken))
        {
            throw new PlatformApiException(
                PlatformId.Xingzhe,
                401,
                "行者登录已失效，请在「平台连接」中重新登录。");
        }

        return account.AuthToken;
    }

    private static string EnsureWebToken(string cookie)
    {
        var withoutToken = System.Text.RegularExpressions.Regex.Replace(
            cookie,
            @"(?:^|;\s*)_XingzheWeb_Token=[^;]*",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return withoutToken.Trim(';', ' ') + "; _XingzheWeb_Token=true";
    }

    private static string Shorten(string value) =>
        value.Length > 180 ? value[..180] + "…" : value;

    private static string MapSport(int sport) => sport switch
    {
        1 => "Hike",
        2 => "Run",
        3 => "Ride",
        5 => "Swim",
        6 => "Ski",
        8 => "Workout",
        11 => "IndoorCycling",
        12 => "VirtualRide",
        _ => "Other",
    };

    private static DateTimeOffset FromUnixMilliseconds(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value < 10_000_000_000 ? value * 1000 : value);

    private static DateTimeOffset? ReadDetailDate(JsonDocument? document, string name)
    {
        var value = ReadDetailLong(document, name);
        return value is > 0 ? FromUnixMilliseconds(value.Value).ToLocalTime() : null;
    }

    private static string? ReadDetailText(JsonDocument? document, string name) =>
        document is not null && TryGetDetail(document.RootElement, out var detail)
            ? ReadText(detail, name)
            : null;

    private static double ReadDetailDouble(JsonDocument? document, string name) =>
        document is not null && TryGetDetail(document.RootElement, out var detail)
            ? ReadDouble(detail, name)
            : 0;

    private static int ReadDetailInt(JsonDocument? document, string name) =>
        document is not null && TryGetDetail(document.RootElement, out var detail)
            ? ReadInt(detail, name)
            : 0;

    private static long? ReadDetailLong(JsonDocument? document, string name) =>
        document is not null && TryGetDetail(document.RootElement, out var detail)
            ? ReadLong(detail, name)
            : null;

    private static bool TryGetDetail(JsonElement root, out JsonElement detail)
    {
        detail = default;
        if (!root.TryGetProperty("data", out var data))
        {
            return false;
        }

        if (data.TryGetProperty("workout", out detail))
        {
            return true;
        }

        detail = data;
        return true;
    }

    private static string? ReadText(JsonElement element, string name)
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

    private static int ReadInt(JsonElement element, string name)
    {
        var text = ReadText(element, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        var text = ReadText(element, name);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        var text = ReadText(element, name);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static double[] ReadDoubleArray(JsonElement data, string name) =>
        ReadArray(data, name)
            .Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0)
            .ToArray();

    private static int[] ReadIntArray(JsonElement data, string name) =>
        ReadArray(data, name)
            .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0)
            .ToArray();

    private static long[] ReadLongArray(JsonElement data, string name) =>
        ReadArray(data, name)
            .Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0)
            .ToArray();

    private static string[] ReadArray(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return array.EnumerateArray()
            .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? string.Empty)
            .ToArray();
    }

    private static double? ValueAt(double[] values, int index) =>
        index >= 0 && index < values.Length ? values[index] : null;

    private static int? IntAt(int[] values, int index) =>
        index >= 0 && index < values.Length ? values[index] : null;
}
