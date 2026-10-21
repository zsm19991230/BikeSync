using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Services;

public sealed class XingzheAuthService
{
    private const string BaseUrl = "https://www.imxingzhe.com";

    public async Task<PlatformAccount> ConnectAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new PlatformConfigurationException("请输入行者账号和密码。");
        }

        using var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        using var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BikeSync/1.1");

        using var pageResponse = await http.GetAsync(BaseUrl + "/user/login", cancellationToken)
            .ConfigureAwait(false);
        pageResponse.EnsureSuccessStatusCode();
        var html = await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var match = Regex.Match(
            html,
            "id=[\"']pubkey[\"'][^>]*>(?<key>.*?)</textarea>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new PlatformApiException(
                PlatformId.Xingzhe,
                502,
                "行者登录页未返回公钥，平台接口可能已调整。");
        }

        var publicKey = WebUtility.HtmlDecode(match.Groups["key"].Value).Trim();
        if (!publicKey.Contains("BEGIN", StringComparison.Ordinal))
        {
            throw new PlatformApiException(PlatformId.Xingzhe, 502, "行者登录公钥格式无法识别。");
        }

        var rd = handler.CookieContainer
            .GetCookies(new Uri(BaseUrl))
            .Cast<Cookie>()
            .FirstOrDefault(c => string.Equals(c.Name, "rd", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        if (string.IsNullOrWhiteSpace(rd))
        {
            throw new PlatformApiException(PlatformId.Xingzhe, 502, "行者登录页缺少会话 Cookie。");
        }

        string encrypted;
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(publicKey);
            var payload = Encoding.UTF8.GetBytes(password + ";" + rd);
            encrypted = Convert.ToBase64String(rsa.Encrypt(payload, RSAEncryptionPadding.Pkcs1));
        }

        using var loginResponse = await http.PostAsync(
            BaseUrl + "/api/v4/account/login",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    account = username.Trim(),
                    password = encrypted,
                    source = "web",
                }),
                Encoding.UTF8,
                "application/json"),
            cancellationToken).ConfigureAwait(false);
        var body = await loginResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = ParseJson(body);
        var root = document.RootElement;
        var result = root.TryGetProperty("res", out var res)
            ? res.ValueKind == JsonValueKind.Number ? res.GetInt32() : int.TryParse(res.GetString(), out var n) ? n : 0
            : 0;
        if (result != 1)
        {
            var message = root.TryGetProperty("error_message", out var error)
                ? error.GetString()
                : "账号或密码不正确。";
            throw new PlatformApiException(PlatformId.Xingzhe, 401, "行者登录失败：" + message);
        }

        var cookieHeader = string.Join(
            "; ",
            handler.CookieContainer.GetCookies(new Uri(BaseUrl))
                .Cast<Cookie>()
                .Select(c => $"{c.Name}={c.Value}"));
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new PlatformApiException(PlatformId.Xingzhe, 502, "行者登录成功但未返回会话 Cookie。");
        }

        return new PlatformAccount
        {
            Platform = PlatformId.Xingzhe,
            UserId = username.Trim(),
            DisplayName = username.Trim(),
            AuthToken = cookieHeader,
            ConnectedAt = DateTimeOffset.Now,
        };
    }

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new PlatformApiException(PlatformId.Xingzhe, 502, "行者返回了无法识别的数据。", body, ex);
        }
    }
}
