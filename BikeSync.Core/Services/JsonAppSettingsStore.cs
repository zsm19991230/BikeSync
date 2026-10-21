using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly ILogger<JsonAppSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAppSettingsStore(
        AppPaths paths,
        ISecretProtector protector,
        ILogger<JsonAppSettingsStore> logger)
    {
        _paths = paths;
        _protector = protector;
        _logger = logger;
    }

    public async Task<AppSettings> LoadAsync(
        string defaultDownloadDirectory,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SettingsFile? file;
            try
            {
                file = await JsonFile.ReadAsync<SettingsFile>(_paths.SettingsFile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                JsonFile.ArchiveCorrupt(_paths.SettingsFile);
                _logger.LogError(ex, "Settings file is corrupt and was archived.");
                return AppSettings.CreateDefault(defaultDownloadDirectory);
            }

            if (file is null)
            {
                return AppSettings.CreateDefault(defaultDownloadDirectory);
            }

            string secret;
            try
            {
                secret = _protector.Unprotect(file.StravaClientSecretProtected ?? string.Empty);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Could not decrypt the Strava client secret.");
                secret = string.Empty;
            }

            var downloadDirectory = string.IsNullOrWhiteSpace(file.DownloadDirectory)
                ? defaultDownloadDirectory
                : file.DownloadDirectory;

            return new AppSettings
            {
                DownloadDirectory = downloadDirectory,
                SaveActivityCopies = file.SaveActivityCopies,
                Theme = string.IsNullOrWhiteSpace(file.Theme) ? "Dark" : file.Theme,
                Strava = new StravaAppSettings
                {
                    ClientId = file.StravaClientId ?? string.Empty,
                    ClientSecret = secret,
                    RedirectUri = string.IsNullOrWhiteSpace(file.StravaRedirectUri)
                        ? StravaAppSettings.DefaultRedirectUri
                        : file.StravaRedirectUri,
                },
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = new SettingsFile
            {
                DownloadDirectory = settings.DownloadDirectory,
                SaveActivityCopies = settings.SaveActivityCopies,
                Theme = settings.Theme,
                StravaClientId = settings.Strava.ClientId.Trim(),
                StravaClientSecretProtected = _protector.Protect(settings.Strava.ClientSecret),
                StravaRedirectUri = settings.Strava.RedirectUri,
            };

            _paths.EnsureCreated();
            await JsonFile.WriteAtomicAsync(_paths.SettingsFile, file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class SettingsFile
    {
        public string? DownloadDirectory { get; set; }
        public bool SaveActivityCopies { get; set; } = true;
        public string? Theme { get; set; }
        public string? StravaClientId { get; set; }
        public string? StravaClientSecretProtected { get; set; }
        public string? StravaRedirectUri { get; set; }
    }
}
