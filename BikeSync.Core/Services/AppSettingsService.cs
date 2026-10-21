using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Services;

public sealed class AppSettingsService
{
    private readonly IAppSettingsStore _store;
    private readonly AppPaths _paths;
    private AppSettings _current;

    public AppSettingsService(IAppSettingsStore store, AppPaths paths)
    {
        _store = store;
        _paths = paths;
        _current = AppSettings.CreateDefault(paths.DefaultDownloadDirectory);
    }

    public event EventHandler? SettingsChanged;

    public AppSettings Current => _current;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _current = await _store.LoadAsync(_paths.DefaultDownloadDirectory, cancellationToken)
            .ConfigureAwait(false);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _current = settings;
        await _store.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public string ResolveDownloadDirectory()
    {
        var directory = string.IsNullOrWhiteSpace(_current.DownloadDirectory)
            ? _paths.DefaultDownloadDirectory
            : _current.DownloadDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }
}
