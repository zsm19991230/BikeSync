using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Services;

public sealed class ConnectionService
{
    private readonly AccountService _accounts;
    private readonly IProviderRegistry _providers;
    private readonly StravaOAuthService _stravaOAuth;
    private readonly MyWhooshAuthService _myWhooshAuth;
    private readonly IGpsportAuthService _igpsportAuth;
    private readonly XingzheAuthService _xingzheAuth;

    public ConnectionService(
        AccountService accounts,
        IProviderRegistry providers,
        StravaOAuthService stravaOAuth,
        MyWhooshAuthService myWhooshAuth,
        IGpsportAuthService igpsportAuth,
        XingzheAuthService xingzheAuth)
    {
        _accounts = accounts;
        _providers = providers;
        _stravaOAuth = stravaOAuth;
        _myWhooshAuth = myWhooshAuth;
        _igpsportAuth = igpsportAuth;
        _xingzheAuth = xingzheAuth;
    }

    public async Task<PlatformAccount> ConnectStravaAsync(CancellationToken cancellationToken)
    {
        var result = await _stravaOAuth.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await _accounts.UpsertAsync(result.Account, cancellationToken).ConfigureAwait(false);
    }

    public Task DisconnectAsync(Guid accountId, CancellationToken cancellationToken) =>
        _accounts.RemoveAsync(accountId, cancellationToken);

    public Task<PlatformAccount> ConnectIntervalsIcuAsync(
        string apiKey,
        CancellationToken cancellationToken) =>
        ConnectWithCredentialsAsync(
            PlatformId.IntervalsIcu,
            new PlatformAccount
            {
                Platform = PlatformId.IntervalsIcu,
                UserId = "pending",
                DisplayName = "intervals.icu",
                ApiKey = apiKey.Trim(),
            },
            cancellationToken);

    public Task<PlatformAccount> ConnectRideWithGpsAsync(
        string apiKey,
        string authToken,
        CancellationToken cancellationToken) =>
        ConnectWithCredentialsAsync(
            PlatformId.RideWithGps,
            new PlatformAccount
            {
                Platform = PlatformId.RideWithGps,
                UserId = "pending",
                DisplayName = "Ride with GPS",
                ApiKey = apiKey.Trim(),
                AuthToken = authToken.Trim(),
            },
            cancellationToken);

    public async Task<PlatformAccount> ConnectMyWhooshAsync(
        string username,
        string password,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var account = await _myWhooshAuth.ConnectAsync(username, password, deviceId, cancellationToken)
            .ConfigureAwait(false);
        return await _accounts.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlatformAccount> ConnectIGpsportAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var account = await _igpsportAuth.ConnectAsync(username, password, cancellationToken)
            .ConfigureAwait(false);
        return await _accounts.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlatformAccount> ConnectXingzheAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var account = await _xingzheAuth.ConnectAsync(username, password, cancellationToken)
            .ConfigureAwait(false);
        return await _accounts.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlatformAccount> ConnectWithCredentialsAsync(
        PlatformId platform,
        PlatformAccount candidate,
        CancellationToken cancellationToken)
    {
        var provider = _providers.Get(platform);
        var profile = await provider.GetProfileAsync(candidate, cancellationToken).ConfigureAwait(false);

        var account = candidate with
        {
            UserId = string.IsNullOrWhiteSpace(profile.UserId) ? candidate.UserId : profile.UserId,
            DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? candidate.DisplayName
                : profile.DisplayName,
            ConnectedAt = DateTimeOffset.Now,
        };

        return await _accounts.UpsertAsync(account, cancellationToken).ConfigureAwait(false);
    }
}
