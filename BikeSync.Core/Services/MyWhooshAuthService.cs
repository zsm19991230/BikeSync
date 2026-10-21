using BikeSync.Core.Models;
using BikeSync.Core.Providers;

namespace BikeSync.Core.Services;

public sealed class MyWhooshAuthService
{
    private readonly HttpClient _http;

    public MyWhooshAuthService(HttpClient http)
    {
        _http = http;
    }

    public async Task<PlatformAccount> ConnectAsync(
        string username,
        string password,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var device = string.IsNullOrWhiteSpace(deviceId) ? MyWhooshApi.NewDeviceId() : deviceId;
        var api = new MyWhooshApi(_http);
        var session = await api.LoginAsync(username, password, device, cancellationToken)
            .ConfigureAwait(false);

        return new PlatformAccount
        {
            Platform = PlatformId.MyWhoosh,
            UserId = session.UserId,
            DisplayName = session.DisplayName,
            AccessToken = session.AccessToken,
            DeviceId = session.DeviceId,
            ConnectedAt = DateTimeOffset.Now,
        };
    }
}
