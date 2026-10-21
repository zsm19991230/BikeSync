namespace BikeSync.Core.Models;

public sealed class AppSettings
{
    public string DownloadDirectory { get; set; } = string.Empty;
    public bool SaveActivityCopies { get; set; } = true;
    public string Theme { get; set; } = "Dark";
    public StravaAppSettings Strava { get; set; } = new();

    public static AppSettings CreateDefault(string defaultDownloadDirectory) => new()
    {
        DownloadDirectory = defaultDownloadDirectory,
        SaveActivityCopies = true,
        Theme = "Dark",
        Strava = new StravaAppSettings(),
    };
}

public sealed class StravaAppSettings
{
    public const string DefaultRedirectUri = "http://127.0.0.1:53682/callback";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = DefaultRedirectUri;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
