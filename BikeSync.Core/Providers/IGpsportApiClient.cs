using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

internal sealed class IGpsportApiClient
{
    private const string BaseUrl = "https://prod.zh.igpsport.com";
    private const string Origin = "https://app.igpsport.cn";
    private const string ActivityPath = "/service/web-gateway/web-analyze/activity/";

    private readonly HttpClient _http;
    private readonly IGpsportSigner _signer;
    private readonly string? _token;
    private string? _accessKey;
    private string? _secretKey;
    private long _clockOffsetSeconds;
    private bool _clockOffsetLoaded;

    public IGpsportApiClient(HttpClient http, IGpsportSigner signer, string? token = null)
    {
        _http = http;
        _signer = signer;
        _token = token;
    }

    public async Task<string> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new PlatformConfigurationException("请输入 iGPSPORT 账号和密码。");
        }

        var data = await SendAsync(
            HttpMethod.Post,
            "/service/auth/account/login",
            new { appId = "igpsport-web", username = username.Trim(), password },
            authenticated: false,
            cancellationToken).ConfigureAwait(false);
        var token = ReadText(data, "access_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new PlatformApiException(PlatformId.IGpsport, 401, "iGPSPORT 登录响应缺少访问令牌。");
        }

        return token;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await QueryActivitiesAsync(1, 1, null, null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadActivityAsync(
        string activityId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activityId) ||
            !activityId.All(char.IsAsciiDigit))
        {
            throw new PlatformApiException(PlatformId.IGpsport, 400, "iGPSPORT 活动 ID 无效。");
        }

        var data = await SendAsync(
            HttpMethod.Get,
            ActivityPath + "getDownloadUrl/" + activityId,
            null,
            authenticated: true,
            cancellationToken).ConfigureAwait(false);
        var url = data.ValueKind == JsonValueKind.String
            ? data.GetString()
            : ReadText(data, "url") ?? ReadText(data, "downloadUrl") ?? ReadText(data, "signedUrl");
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new PlatformApiException(PlatformId.IGpsport, 502, "iGPSPORT 未返回有效的活动下载地址。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", "BikeSync/1.1 (Windows; iGPSPORT web)");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await HttpApi.ReadBytesAsync(response, PlatformId.IGpsport, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<JsonElement> QueryActivitiesAsync(
        int page,
        int pageSize,
        string? title,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var query = new StringBuilder(
            $"{ActivityPath}queryMyActivity?reqType=1&sportType=0&pageNo={page}&pageSize={pageSize}&sort=1");
        if (!string.IsNullOrWhiteSpace(title))
        {
            query.Append("&title=").Append(Uri.EscapeDataString(title));
        }

        if (from is { } begin)
        {
            query.Append("&beginTime=").Append(begin.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (to is { } end)
        {
            query.Append("&endTime=").Append(end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        var data = await SendAsync(
            HttpMethod.Get,
            query.ToString(),
            null,
            authenticated: true,
            cancellationToken).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Null)
        {
            return JsonDocument.Parse("{\"rows\":[],\"totalRows\":0}").RootElement.Clone();
        }

        return data;
    }

    public async Task<(string Marker, string? RideId)> UploadAsync(
        byte[] fit,
        string title,
        CancellationToken cancellationToken)
    {
        if (fit.Length == 0)
        {
            throw new PlatformConfigurationException("活动文件为空，无法上传。");
        }

        if (fit.Length > 20 * 1024 * 1024)
        {
            throw new PlatformConfigurationException("FIT 文件超过 20 MB，iGPSPORT 拒绝接收。");
        }

        var marker = "BikeSync_" + Guid.NewGuid().ToString("N")[..12];
        var fileName = marker + ".fit";
        var grant = await SendAsync(
            HttpMethod.Get,
            "/service/sportg/third-party-server/oss/getSignedUrl?fileExtension=.fit",
            null,
            authenticated: true,
            cancellationToken).ConfigureAwait(false);
        var signedUrl = ReadText(grant, "signedUrl");
        var ossId = ReadText(grant, "ossId");
        if (string.IsNullOrWhiteSpace(signedUrl) || !signedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(ossId))
        {
            throw new PlatformApiException(PlatformId.IGpsport, 502, "iGPSPORT 未返回有效的文件上传地址。");
        }

        using (var upload = new HttpRequestMessage(HttpMethod.Put, signedUrl))
        {
            upload.Content = new ByteArrayContent(fit);
            upload.Content.Headers.ContentType = null;
            using var uploadResponse = await _http.SendAsync(upload, cancellationToken).ConfigureAwait(false);
            if (!uploadResponse.IsSuccessStatusCode)
            {
                throw await HttpApi.CreateExceptionAsync(
                    uploadResponse, PlatformId.IGpsport, cancellationToken).ConfigureAwait(false);
            }
        }

        await SendAsync(
            HttpMethod.Post,
            ActivityPath + "uploadByOss",
            new { fileName, ossName = ossId },
            authenticated: true,
            cancellationToken).ConfigureAwait(false);

        string? rideId = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 3), cancellationToken)
                .ConfigureAwait(false);
            var data = await QueryActivitiesAsync(1, 20, marker, null, null, cancellationToken)
                .ConfigureAwait(false);
            rideId = FindRideId(data, marker);
            if (!string.IsNullOrWhiteSpace(rideId))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(rideId))
        {
            return (marker, null);
        }

        var finalTitle = string.IsNullOrWhiteSpace(title) ? "MyWhoosh 骑行" : title.Trim();
        if (finalTitle.Length > 80)
        {
            finalTitle = finalTitle[..80];
        }

        try
        {
            await SendAsync(
                HttpMethod.Put,
                ActivityPath + "update",
                new { rideId, title = finalTitle },
                authenticated: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PlatformApiException)
        {
            // The upload itself succeeded; a rename failure must not turn it into a failure.
        }

        return (marker, rideId);
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        var serialized = body is null ? string.Empty : JsonSerializer.Serialize(body);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await EnsureKeysAsync(cancellationToken).ConfigureAwait(false);
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _clockOffsetSeconds;
            var timestampText = timestamp.ToString(CultureInfo.InvariantCulture);
            var nonce = Guid.NewGuid().ToString();
            var signature = _signer.Sign(method.Method, NormalizePath(path), timestampText, nonce, serialized);

            using var request = new HttpRequestMessage(method, BaseUrl + path);
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
            request.Headers.TryAddWithoutValidation("User-Agent", "BikeSync/1.1 (Windows; iGPSPORT web)");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-Hans");
            request.Headers.TryAddWithoutValidation("TimeZone", ResolveTimeZone());
            request.Headers.TryAddWithoutValidation("Qiwu-App-Version", "8.07.08");
            request.Headers.TryAddWithoutValidation("X-Platform", "web");
            request.Headers.TryAddWithoutValidation("X-Access-Key", _accessKey);
            request.Headers.TryAddWithoutValidation("X-Timestamp", timestampText);
            request.Headers.TryAddWithoutValidation("X-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-Signature", signature);
            if (!string.IsNullOrEmpty(_token) && authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

            if (!string.IsNullOrEmpty(serialized))
            {
                request.Content = new StringContent(serialized, Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw HttpApi.CreateException(PlatformId.IGpsport, response.StatusCode, raw);
                }

                throw new PlatformApiException(PlatformId.IGpsport, (int)response.StatusCode, "iGPSPORT 返回了无法识别的数据。");
            }

            using (document)
            {
                var root = document.RootElement;
                var code = ReadInt(root, "code");
                var isAuthFailure = response.StatusCode == HttpStatusCode.Unauthorized || code == 401;
                if (attempt == 0 && response.StatusCode == HttpStatusCode.Forbidden &&
                    code is >= 1020 and <= 1022)
                {
                    _accessKey = null;
                    _secretKey = null;
                    continue;
                }

                if (!response.IsSuccessStatusCode || code != 0)
                {
                    var serverMessage = ReadText(root, "message") ?? ReadText(root, "msg");
                    var unauthenticated = isAuthFailure ||
                                          (response.StatusCode == HttpStatusCode.Forbidden &&
                                           (code == 0 || string.Equals(serverMessage, "success", StringComparison.OrdinalIgnoreCase)));
                    var message = unauthenticated
                        ? "iGPSPORT 登录已失效或无权执行此操作，请在「平台连接」中重新登录。"
                        : serverMessage ?? "iGPSPORT 请求失败。";
                    throw new PlatformApiException(
                        PlatformId.IGpsport,
                        unauthenticated ? 401 : (int)response.StatusCode,
                        message,
                        raw.Length > 500 ? raw[..500] : raw);
                }

                if (!root.TryGetProperty("data", out var data) ||
                    data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return JsonDocument.Parse("{}").RootElement.Clone();
                }

                return data.Clone();
            }
        }

        throw new PlatformApiException(PlatformId.IGpsport, 502, "iGPSPORT 签名密钥刷新后仍然失败。");
    }

    private async Task EnsureKeysAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessKey) && !string.IsNullOrWhiteSpace(_secretKey))
        {
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BaseUrl + "/service/edge-core/api/public/key");
        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("User-Agent", "BikeSync/1.1 (Windows; iGPSPORT web)");
        request.Headers.TryAddWithoutValidation("X-Platform", "web");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var document = await HttpApi.ReadJsonAsync(response, PlatformId.IGpsport, cancellationToken)
            .ConfigureAwait(false);
        var data = document.RootElement.TryGetProperty("data", out var d) ? d : document.RootElement;
        _accessKey = ReadText(data, "access_key");
        _secretKey = ReadText(data, "secret_key");
        if (string.IsNullOrWhiteSpace(_accessKey) || string.IsNullOrWhiteSpace(_secretKey))
        {
            throw new PlatformApiException(PlatformId.IGpsport, 502, "iGPSPORT 公钥接口返回不完整。");
        }

        _signer.Initialize(_secretKey);
        if (!_clockOffsetLoaded)
        {
            _clockOffsetLoaded = true;
            try
            {
                using var timestampRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    BaseUrl + "/service/edge-core/api/public/timestamp");
                timestampRequest.Headers.TryAddWithoutValidation("Origin", Origin);
                timestampRequest.Headers.TryAddWithoutValidation("User-Agent", "BikeSync/1.1 (Windows; iGPSPORT web)");
                timestampRequest.Headers.TryAddWithoutValidation("X-Platform", "web");
                using var timestampResponse = await _http.SendAsync(timestampRequest, cancellationToken)
                    .ConfigureAwait(false);
                using var timestampDoc = await HttpApi.ReadJsonAsync(
                    timestampResponse, PlatformId.IGpsport, cancellationToken).ConfigureAwait(false);
                var timestamp = timestampDoc.RootElement.TryGetProperty("data", out var t)
                    ? t
                    : timestampDoc.RootElement;
                if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out var server))
                {
                    _clockOffsetSeconds = server - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or PlatformApiException or JsonException)
            {
                // Clock skew is optional; the local timestamp still works when the clock is correct.
            }
        }
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.StartsWith('/') ? path : "/" + path;
        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        return normalized.StartsWith("/service", StringComparison.Ordinal)
            ? normalized
            : "/service" + normalized;
    }

    private static string ResolveTimeZone()
    {
        var local = TimeZoneInfo.Local;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local.Id, out var iana) &&
            !string.IsNullOrWhiteSpace(iana))
        {
            return iana;
        }

        return local.Id;
    }

    private static string? FindRideId(JsonElement data, string marker)
    {
        if (!data.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? found = null;
        foreach (var row in rows.EnumerateArray())
        {
            var title = ReadText(row, "title") ?? string.Empty;
            if (!title.Contains(marker, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null; // Ambiguous marker; never guess which activity to rename.
            }

            found = ReadText(row, "rideId") ?? ReadText(row, "id");
        }

        return found;
    }

    public static string? ReadJwtSubject(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return ReadText(document.RootElement, "sub") ??
                   ReadText(document.RootElement, "memberId");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? ReadJwtExpiry(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.TryGetProperty("exp", out var exp) &&
                exp.TryGetInt64(out var seconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Ignore malformed optional expiry.
        }

        return null;
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
}
