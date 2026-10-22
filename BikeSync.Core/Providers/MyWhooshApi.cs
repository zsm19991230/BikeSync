using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

internal sealed record MyWhooshSession(
    string AccessToken,
    string DeviceId,
    string UserId,
    string DisplayName,
    double Ftp);

internal sealed record MyWhooshActivity(
    string Id,
    string Name,
    DateTimeOffset StartTime,
    double DistanceMeters,
    int DurationSeconds,
    double ElevationMeters,
    double? AveragePowerWatts);

/// <summary>
/// Minimal MyWhoosh Connect API client based on the public Android/web client flow.
/// Only the access token is persisted; the account password is never stored.
/// </summary>
internal sealed class MyWhooshApi
{
    private const string LoginUrl = "https://services.mywhoosh.com/http-service/api/login";
    private const string ForceLogoutUrl = "https://connectapp-api.mywhoosh.com/auth/force-logout";
    private const string ConnectBase = "https://connectapp-api.mywhoosh.com/";
    private const string WebBase = "https://service14.mywhoosh.com/v2/rider/profile/";
    private const string DownloadSecret = "D9436E508087E863";

    private readonly HttpClient _http;

    public MyWhooshApi(HttpClient http)
    {
        _http = http;
    }

    public static string NewDeviceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public async Task<MyWhooshSession> LoginAsync(
        string username,
        string password,
        string deviceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new PlatformConfigurationException("请输入 MyWhoosh 账号和密码。");
        }

        var body = new
        {
            UserName = username.Trim(),
            Password = password,
            Source = "connect",
            DeviceId = deviceId,
        };

        using var first = await PostLoginAsync(body, cancellationToken).ConfigureAwait(false);
        var loginType = ReadLoginType(first.RootElement);
        if (loginType == 4)
        {
            var whooshId = ReadText(first.RootElement, "WhooshId");
            if (string.IsNullOrWhiteSpace(whooshId))
            {
                throw new PlatformApiException(
                    PlatformId.MyWhoosh,
                    409,
                    "MyWhoosh 提示账号已在其他设备登录，但未返回账号标识，无法自动接管。");
            }

            using var logout = new HttpRequestMessage(HttpMethod.Post, ForceLogoutUrl)
            {
                Content = HttpApi.JsonContent(new { userId = whooshId }),
            };
            AddDeviceHeaders(logout);
            using var logoutResponse = await _http.SendAsync(logout, cancellationToken).ConfigureAwait(false);
            if (!logoutResponse.IsSuccessStatusCode)
            {
                throw await HttpApi.CreateExceptionAsync(
                    logoutResponse, PlatformId.MyWhoosh, cancellationToken).ConfigureAwait(false);
            }

            using var retry = await PostLoginAsync(body, cancellationToken).ConfigureAwait(false);
            first.Dispose();
            if (ReadLoginType(retry.RootElement) == 4)
            {
                throw new PlatformApiException(
                    PlatformId.MyWhoosh,
                    409,
                    "MyWhoosh 登录凭据冲突，请稍后重试。");
            }

            return await CompleteLoginAsync(retry.RootElement, deviceId, username, cancellationToken)
                .ConfigureAwait(false);
        }

        return await CompleteLoginAsync(first.RootElement, deviceId, username, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<MyWhooshSession> CompleteLoginAsync(
        JsonElement root,
        string deviceId,
        string username,
        CancellationToken cancellationToken)
    {
        if (ReadLoginType(root) != 1)
        {
            var message = ReadText(root, "Message") ?? "账号或密码不正确。";
            throw new PlatformApiException(PlatformId.MyWhoosh, 401, "MyWhoosh 登录失败：" + message);
        }

        var token = ReadText(root, "AccessToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new PlatformApiException(PlatformId.MyWhoosh, 401, "MyWhoosh 登录响应缺少访问令牌。");
        }

        var profile = await GetProfileAsync(token, cancellationToken).ConfigureAwait(false);
        return new MyWhooshSession(
            token,
            deviceId,
            profile.UserId,
            string.IsNullOrWhiteSpace(profile.DisplayName) ? username.Trim() : profile.DisplayName,
            profile.Ftp);
    }

    private async Task<JsonDocument> PostLoginAsync(object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
        {
            Content = HttpApi.JsonContent(body),
        };
        AddDeviceHeaders(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await HttpApi.ReadJsonAsync(response, PlatformId.MyWhoosh, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(string UserId, string DisplayName, double Ftp)> GetProfileAsync(
        string token,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ConnectBase + "users/get-my-profile", token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await HttpApi.ReadJsonAsync(response, PlatformId.MyWhoosh, cancellationToken)
            .ConfigureAwait(false);
        var data = document.RootElement.TryGetProperty("data", out var d) ? d : document.RootElement;
        var userId = ReadText(data, "userId") ?? ReadText(data, "whooshId") ?? string.Empty;
        var first = ReadText(data, "firstName");
        var last = ReadText(data, "lastName");
        var display = string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(display))
        {
            display = ReadText(data, "name") ?? ReadText(data, "userName") ?? string.Empty;
        }

        var ftp = 0d;
        if (data.TryGetProperty("stats", out var stats))
        {
            ftp = ReadDouble(stats, "ftp");
        }

        return (userId, display.Trim(), ftp);
    }

    public async Task<IReadOnlyList<MyWhooshActivity>> ListActivitiesAsync(
        string token,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var activities = new List<MyWhooshActivity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        const int maxPages = 50;
        for (var page = 1; page <= maxPages && activities.Count < query.MaxResults; page++)
        {
            using var request = CreateRequest(
                HttpMethod.Post,
                WebBase + "activities",
                token);
            request.Content = HttpApi.JsonContent(new { type = 0, page, sortDate = "DESC" });
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var document = await HttpApi.ReadJsonAsync(response, PlatformId.MyWhoosh, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
            {
                throw new PlatformApiException(PlatformId.MyWhoosh, 502, "MyWhoosh 返回活动列表失败。");
            }

            var data = root.TryGetProperty("data", out var d) ? d : root;
            if (!data.TryGetProperty("results", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                throw new PlatformApiException(PlatformId.MyWhoosh, 502, "MyWhoosh 活动列表结构异常。");
            }

            var batch = 0;
            foreach (var row in rows.EnumerateArray())
            {
                batch++;
                var id = ReadText(row, "id");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                {
                    continue;
                }

                var start = ReadDate(row, "startDatetime") ?? ReadDate(row, "createdAt");
                if (start is null)
                {
                    continue;
                }

                if (start < query.From.ToUniversalTime() || start > query.To.ToUniversalTime())
                {
                    continue;
                }

                var name = ReadText(row, "title") ?? "MyWhoosh 骑行";
                if (!string.IsNullOrWhiteSpace(query.Search) &&
                    !name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                activities.Add(new MyWhooshActivity(
                    id,
                    name,
                    start.Value.ToLocalTime(),
                    Math.Max(0, ReadDouble(row, "distance") * 1000),
                    ParseDuration(ReadText(row, "rideDuration")),
                    Math.Max(0, ReadDouble(row, "elevation")),
                    HttpApi.ReadAveragePower(row)));

                if (activities.Count >= query.MaxResults)
                {
                    break;
                }
            }

            var totalPages = ReadInt(data, "totalPages");
            if (batch == 0 || (totalPages > 0 && page >= totalPages))
            {
                break;
            }
        }

        return activities;
    }

    /// <summary>Returns the activity detail object exposed by the Connect API.</summary>
    public async Task<JsonElement> GetActivityDetailAsync(
        string token,
        string activityId,
        CancellationToken cancellationToken)
    {
        ValidateActivityId(activityId);
        using var request = CreateRequest(
            HttpMethod.Get,
            ConnectBase + "activities/" + Uri.EscapeDataString(activityId),
            token);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await HttpApi.ReadJsonAsync(response, PlatformId.MyWhoosh, cancellationToken)
            .ConfigureAwait(false);
        var data = document.RootElement.TryGetProperty("data", out var d)
            ? d
            : document.RootElement;
        return data.Clone();
    }

    public async Task<byte[]> DownloadFitAsync(
        string token,
        string activityId,
        CancellationToken cancellationToken)
    {
        ValidateActivityId(activityId);

        var detailData = await GetActivityDetailAsync(token, activityId, cancellationToken)
            .ConfigureAwait(false);
        var fileId = ReadText(detailData, "userFileId");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new PlatformApiException(PlatformId.MyWhoosh, 404, "该活动没有可下载的原始 FIT 文件。");
        }

        var encrypted = EncryptFileRequest(fileId);
        using var downloadRequest = CreateRequest(
            HttpMethod.Post,
            WebBase + "download-activity-file",
            token);
        downloadRequest.Content = new StringContent(
            JsonSerializer.Serialize(encrypted),
            Encoding.UTF8,
            "application/json");
        using var downloadResponse = await _http.SendAsync(downloadRequest, cancellationToken).ConfigureAwait(false);
        using var download = await HttpApi.ReadJsonAsync(downloadResponse, PlatformId.MyWhoosh, cancellationToken)
            .ConfigureAwait(false);
        var url = ReadText(download.RootElement, "data");
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformApiException(PlatformId.MyWhoosh, 502, "MyWhoosh 未返回有效的 FIT 下载地址。");
        }

        using var fileRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var fileResponse = await _http.SendAsync(fileRequest, cancellationToken).ConfigureAwait(false);
        var raw = await HttpApi.ReadBytesAsync(fileResponse, PlatformId.MyWhoosh, cancellationToken)
            .ConfigureAwait(false);
        FitPreparer.Validate(raw, PlatformId.MyWhoosh);
        return raw;
    }

    public static string EncryptFileRequest(string fileId)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new { fileId });
        var salt = RandomNumberGenerator.GetBytes(8);
        var material = new List<byte>(48);
        var previous = Array.Empty<byte>();
        while (material.Count < 48)
        {
            var input = new byte[previous.Length + Encoding.UTF8.GetByteCount(DownloadSecret) + salt.Length];
            previous.CopyTo(input, 0);
            var secretBytes = Encoding.UTF8.GetBytes(DownloadSecret);
            secretBytes.CopyTo(input, previous.Length);
            salt.CopyTo(input, previous.Length + secretBytes.Length);
            previous = MD5.HashData(input);
            material.AddRange(previous);
        }

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = material.Take(32).ToArray();
        aes.IV = material.Skip(32).Take(16).ToArray();
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        var output = new byte[16 + cipher.Length];
        Encoding.ASCII.GetBytes("Salted__").CopyTo(output, 0);
        salt.CopyTo(output, 8);
        cipher.CopyTo(output, 16);
        return Convert.ToBase64String(output);
    }

    private static void ValidateActivityId(string activityId)
    {
        if (string.IsNullOrWhiteSpace(activityId) ||
            !activityId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'))
        {
            throw new PlatformApiException(PlatformId.MyWhoosh, 400, "MyWhoosh 活动 ID 无效。");
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        AddDeviceHeaders(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void AddDeviceHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("deviceType", "ANDROID");
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/5.0.0-alpha.2");
    }

    private static int ReadLoginType(JsonElement root)
    {
        if (!root.TryGetProperty("LoginType", out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => 0,
        };
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

    private static double ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed)
            ? parsed
            : 0;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        var text = ReadText(element, name);
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

    private static int ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var hours) ||
            !int.TryParse(parts[1], out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return 0;
        }

        return (int)Math.Max(0, hours * 3600 + minutes * 60 + seconds);
    }
}
