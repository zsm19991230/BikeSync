using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Core.Services;

public sealed class IGpsportAuthService
{
    private readonly HttpClient _http;
    private readonly IGpsportSigner _signer;

    public IGpsportAuthService(HttpClient http, IGpsportSigner signer)
    {
        _http = http;
        _signer = signer;
    }

    public async Task<PlatformAccount> ConnectAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var client = new IGpsportApiClient(_http, _signer);
        var token = await client.LoginAsync(username, password, cancellationToken)
            .ConfigureAwait(false);
        var subject = IGpsportApiClient.ReadJwtSubject(token);
        return new PlatformAccount
        {
            Platform = PlatformId.IGpsport,
            UserId = string.IsNullOrWhiteSpace(subject) ? username.Trim() : subject,
            DisplayName = username.Trim(),
            AccessToken = token,
            AccessTokenExpiresAt = IGpsportApiClient.ReadJwtExpiry(token),
            ConnectedAt = DateTimeOffset.Now,
        };
    }
}
