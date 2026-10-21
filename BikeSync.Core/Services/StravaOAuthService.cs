using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Core.Services;

public sealed class StravaOAuthService
{
    private const string AuthorizeUrl = "https://www.strava.com/oauth/authorize";
    private const string TokenUrl = "https://www.strava.com/oauth/token";
    private const string Scope = "read,activity:read_all,activity:write";

    private readonly HttpClient _http;
    private readonly AppSettingsService _settings;

    public StravaOAuthService(HttpClient http, AppSettingsService settings)
    {
        _http = http;
        _settings = settings;
    }

    public async Task<AccountConnectionResult> ConnectAsync(CancellationToken cancellationToken)
    {
        var config = _settings.Current.Strava;
        if (!config.IsConfigured)
        {
            throw new PlatformConfigurationException(
                "请先在「设置」中填写 Strava Client ID 与 Client Secret。");
        }

        if (!int.TryParse(config.ClientId.Trim(), out _))
        {
            throw new PlatformConfigurationException("Strava Client ID 应为纯数字。");
        }

        var redirectUri = string.IsNullOrWhiteSpace(config.RedirectUri)
            ? StravaAppSettings.DefaultRedirectUri
            : config.RedirectUri.Trim();

        var state = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var authorizeUri = AuthorizeUrl +
            "?client_id=" + Uri.EscapeDataString(config.ClientId.Trim()) +
            "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
            "&response_type=code" +
            "&approval_prompt=auto" +
            "&scope=" + Uri.EscapeDataString(Scope) +
            "&state=" + Uri.EscapeDataString(state);

        var callbackTask = LoopbackOAuthReceiver.WaitForCallbackAsync(
            redirectUri,
            state,
            TimeSpan.FromMinutes(5),
            "Strava 授权成功",
            cancellationToken);

        try
        {
            Process.Start(new ProcessStartInfo(authorizeUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            throw new PlatformConfigurationException("无法打开系统浏览器，请检查默认浏览器设置。");
        }

        var callback = await callbackTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(callback.Error))
        {
            throw new PlatformConfigurationException(
                callback.Error.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
                    ? "你取消了 Strava 授权。"
                    : $"Strava 授权失败：{callback.ErrorDescription ?? callback.Error}");
        }

        if (string.IsNullOrWhiteSpace(callback.Code))
        {
            throw new PlatformApiException(PlatformId.Strava, 400, "Strava 未返回授权码，请重试。");
        }

        var token = await ExchangeCodeAsync(config, callback.Code, redirectUri, cancellationToken)
            .ConfigureAwait(false);

        var displayName = string.Join(' ',
            new[] { token.FirstName, token.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = $"Strava 运动员 {token.AthleteId}";
        }

        var account = new PlatformAccount
        {
            Platform = PlatformId.Strava,
            UserId = token.AthleteId,
            DisplayName = displayName.Trim(),
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            AccessTokenExpiresAt = token.ExpiresAt,
            Scope = token.Scope,
            ConnectedAt = DateTimeOffset.Now,
        };

        return new AccountConnectionResult(
            account,
            new AccountProfile(token.AthleteId, account.DisplayName, token.AvatarUrl));
    }

    public async Task<PlatformAccount> RefreshAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        if (account.Platform != PlatformId.Strava)
        {
            throw new InvalidOperationException("Only Strava accounts need token refresh.");
        }

        var config = _settings.Current.Strava;
        if (!config.IsConfigured)
        {
            throw new PlatformConfigurationException(
                "Strava 登录已过期，且应用凭据缺失。请在「设置」中重新填写 Strava API 应用信息。");
        }

        if (string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            throw new PlatformConfigurationException("Strava 刷新令牌缺失，请重新连接 Strava。");
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId.Trim(),
            ["client_secret"] = config.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = account.RefreshToken,
        };

        using var response = await _http.PostAsync(
            TokenUrl,
            new FormUrlEncodedContent(form),
            cancellationToken).ConfigureAwait(false);

        using var doc = await HttpApi.ReadJsonAsync(response, PlatformId.Strava, cancellationToken)
            .ConfigureAwait(false);

        var token = ParseToken(doc.RootElement);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new PlatformApiException(PlatformId.Strava, 500, "Strava 未返回新的访问令牌。");
        }

        return account.WithTokens(
            token.AccessToken,
            token.RefreshToken,
            token.ExpiresAt,
            token.Scope);
    }

    private async Task<StravaToken> ExchangeCodeAsync(
        StravaAppSettings config,
        string code,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId.Trim(),
            ["client_secret"] = config.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
        };

        using var response = await _http.PostAsync(
            TokenUrl,
            new FormUrlEncodedContent(form),
            cancellationToken).ConfigureAwait(false);

        using var doc = await HttpApi.ReadJsonAsync(response, PlatformId.Strava, cancellationToken)
            .ConfigureAwait(false);

        return ParseToken(doc.RootElement);
    }

    private static StravaToken ParseToken(JsonElement root)
    {
        var accessToken = GetString(root, "access_token") ?? string.Empty;
        var refreshToken = GetString(root, "refresh_token");
        var scope = GetString(root, "scope");
        var expiresAt = GetLong(root, "expires_at") is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow.AddHours(5);

        string athleteId = string.Empty;
        string? firstName = null;
        string? lastName = null;
        string? avatarUrl = null;

        if (root.TryGetProperty("athlete", out var athlete) &&
            athlete.ValueKind == JsonValueKind.Object)
        {
            athleteId = GetLong(athlete, "id")?.ToString() ?? GetString(athlete, "id") ?? string.Empty;
            firstName = GetString(athlete, "firstname");
            lastName = GetString(athlete, "lastname");
            avatarUrl = GetString(athlete, "profile");
        }

        if (string.IsNullOrWhiteSpace(athleteId))
        {
            athleteId = "strava-unknown";
        }

        return new StravaToken(
            accessToken,
            refreshToken,
            scope,
            expiresAt,
            athleteId,
            firstName,
            lastName,
            avatarUrl);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String &&
               long.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private sealed record StravaToken(
        string AccessToken,
        string? RefreshToken,
        string? Scope,
        DateTimeOffset ExpiresAt,
        string AthleteId,
        string? FirstName,
        string? LastName,
        string? AvatarUrl);
}
