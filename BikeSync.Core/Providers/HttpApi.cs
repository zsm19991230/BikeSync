using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

internal static class HttpApi
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Activity list payloads from the unofficial clients do not share a single
    // schema; try the common names before falling back to a shallow nested search.
    private static readonly string[] AveragePowerKeys =
    {
        "avgPower", "averagePower", "avgPowerWatts", "averagePowerWatts",
        "avg_watts", "average_watts", "icu_average_watts",
        "avg_power", "average_power", "avgWatts", "averageWatts",
    };

    private static readonly string[] LoosePowerKeys = { "power", "watts" };

    /// <summary>
    /// Reads an average power value from a platform summary payload. Returns null
    /// when the payload only exposes unrelated power fields or no power data.
    /// </summary>
    public static double? ReadAveragePower(JsonElement element, int depth = 0)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 2)
        {
            return null;
        }

        foreach (var key in AveragePowerKeys)
        {
            if (TryGetProperty(element, key, out var value) && TryReadNumber(value, out var number))
            {
                return number;
            }
        }

        if (depth == 0)
        {
            foreach (var key in LoosePowerKeys)
            {
                if (TryGetProperty(element, key, out var value) && TryReadNumber(value, out var number))
                {
                    return number;
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var nested = ReadAveragePower(property.Value, depth + 1);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadNumber(JsonElement value, out double number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDouble(out number))
            {
                return false;
            }
        }
        else if (value.ValueKind != JsonValueKind.String ||
                 !double.TryParse(
                     value.GetString(),
                     NumberStyles.Float,
                     CultureInfo.InvariantCulture,
                     out number))
        {
            return false;
        }

        return double.IsFinite(number) && number > 0;
    }

    public static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        PlatformId platform,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(platform, response.StatusCode, body);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PlatformApiException(
                platform,
                (int)response.StatusCode,
                "平台返回了无法识别的数据格式。",
                body.Length > 500 ? body[..500] : body,
                ex);
        }
    }

    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        PlatformId platform,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw CreateException(platform, response.StatusCode, body);
    }

    public static async Task<byte[]> ReadBytesAsync(
        HttpResponseMessage response,
        PlatformId platform,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateException(platform, response.StatusCode, body);
        }

        const long maxBytes = 100L * 1024 * 1024;
        if (response.Content.Headers.ContentLength is > maxBytes)
        {
            throw new PlatformApiException(
                platform,
                (int)response.StatusCode,
                "活动文件超过 100 MB，已停止下载。");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.LongLength > maxBytes)
        {
            throw new PlatformApiException(
                platform,
                (int)response.StatusCode,
                "活动文件超过 100 MB，已停止下载。");
        }

        return bytes;
    }

    public static byte[] DecompressGzipIfNeeded(byte[] content)
    {
        if (content.Length < 2 || content[0] != 0x1F || content[1] != 0x8B)
        {
            return content;
        }

        using var input = new MemoryStream(content, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    public static string AuthenticationValue(string user, string password) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));

    public static AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    public static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", AuthenticationValue(user, password));

    public static async Task<PlatformApiException> CreateExceptionAsync(
        HttpResponseMessage response,
        PlatformId platform,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return CreateException(platform, response.StatusCode, body);
    }

    public static PlatformApiException CreateException(
        PlatformId platform,
        HttpStatusCode statusCode,
        string body)
    {
        var detail = ExtractError(body);
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => "授权已失效，请断开该平台后重新连接。",
            HttpStatusCode.Forbidden => "平台拒绝了此操作，请检查授权范围或账号权限。",
            HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后再试。",
            HttpStatusCode.NotFound => "请求的活动或接口不存在，可能已被删除。",
            HttpStatusCode.RequestEntityTooLarge => "活动文件过大，平台拒绝接收。",
            _ when (int)statusCode >= 500 => "平台服务暂时不可用，请稍后重试。",
            _ => "平台请求失败。",
        };

        if (!string.IsNullOrWhiteSpace(detail))
        {
            message += " " + detail;
        }

        return new PlatformApiException(platform, (int)statusCode, message, detail);
    }

    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                {
                    return Truncate(message.GetString());
                }

                if (root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return Truncate(error.GetString());
                    }

                    return Truncate(error.GetRawText());
                }

                if (root.TryGetProperty("errors", out var errors))
                {
                    if (errors.ValueKind == JsonValueKind.Array)
                    {
                        var parts = errors.EnumerateArray()
                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToArray();
                        return Truncate(string.Join("；", parts));
                    }

                    return Truncate(errors.GetRawText());
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to a short plain-text excerpt.
        }

        return Truncate(body);
    }

    private static string? Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length > 260 ? cleaned[..260] + "…" : cleaned;
    }
}
