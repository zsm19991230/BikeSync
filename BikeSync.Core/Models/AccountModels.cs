namespace BikeSync.Core.Models;

public sealed record AccountProfile(string UserId, string DisplayName, string? AvatarUrl = null);

/// <summary>
/// In-memory account with decrypted secrets. Never serialize this type directly;
/// the JSON account store encrypts every secret with Windows DPAPI first.
/// </summary>
public sealed record PlatformAccount
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required PlatformId Platform { get; init; }
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? ApiKey { get; init; }
    public string? AuthToken { get; init; }
    /// <summary>Stable MyWhoosh client device id. Not a secret, but persisted for token continuity.</summary>
    public string? DeviceId { get; init; }
    public DateTimeOffset? AccessTokenExpiresAt { get; init; }
    public string? Scope { get; init; }
    public DateTimeOffset ConnectedAt { get; init; } = DateTimeOffset.Now;

    public PlatformDescriptor Descriptor => PlatformCatalog.Get(Platform);

    public string AuthLabel => Platform switch
    {
        PlatformId.Strava => "OAuth",
        PlatformId.IntervalsIcu => "API Key",
        PlatformId.RideWithGps => "API Token",
        PlatformId.MyWhoosh => "账号登录",
        PlatformId.IGpsport => "账号登录",
        PlatformId.Xingzhe => "账号登录",
        _ => "已连接",
    };

    public bool IsAccessTokenExpired(DateTimeOffset? now = null) =>
        AccessTokenExpiresAt is { } expiry && expiry <= (now ?? DateTimeOffset.UtcNow).AddMinutes(5);

    public PlatformAccount WithTokens(
        string? accessToken,
        string? refreshToken,
        DateTimeOffset? expiresAt,
        string? scope = null) => this with
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken ?? RefreshToken,
        AccessTokenExpiresAt = expiresAt,
        Scope = scope ?? Scope,
    };
}

public sealed record AccountConnectionResult(PlatformAccount Account, AccountProfile Profile);
