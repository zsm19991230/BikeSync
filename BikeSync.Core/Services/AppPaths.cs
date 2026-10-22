namespace BikeSync.Core.Services;

public sealed class AppPaths
{
    public AppPaths(string? rootDirectory = null)
    {
        var environmentRoot = Environment.GetEnvironmentVariable("BIKESYNC_DATA_DIR");
        RootDirectory = rootDirectory ??
                        (string.IsNullOrWhiteSpace(environmentRoot)
                            ? ResolveWritableRoot()
                            : Path.GetFullPath(environmentRoot));

        AccountsFile = Path.Combine(RootDirectory, "accounts.json");
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        HistoryFile = Path.Combine(RootDirectory, "sync-history.json");
        ActivityCacheFile = Path.Combine(RootDirectory, "activity-cache.json");
        LogDirectory = Path.Combine(RootDirectory, "logs");
        // Downloaded activity files are kept so reopening a detail view does not
        // hit the platform again.
        ActivityFileCacheDirectory = Path.Combine(RootDirectory, "activity-files");
        MapTileCacheDirectory = Path.Combine(RootDirectory, "map-tiles");
        // Keep the default inside the per-user application data directory. The Documents
        // folder is sometimes protected by Controlled Folder Access or an enterprise policy.
        DefaultDownloadDirectory = Path.Combine(RootDirectory, "exports");
    }

    public string RootDirectory { get; }
    public string AccountsFile { get; }
    public string SettingsFile { get; }
    public string HistoryFile { get; }
    public string ActivityCacheFile { get; }
    public string LogDirectory { get; }
    public string ActivityFileCacheDirectory { get; }
    public string MapTileCacheDirectory { get; }
    public string DefaultDownloadDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ActivityFileCacheDirectory);
        Directory.CreateDirectory(MapTileCacheDirectory);
        Directory.CreateDirectory(DefaultDownloadDirectory);
    }

    /// <summary>
    /// Prefer the standard per-user app data directory. If policy or a sandbox denies
    /// writes there, fall back to a portable folder next to the executable, then TEMP.
    /// </summary>
    private static string ResolveWritableRoot()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BikeSync"),
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Path.GetTempPath(), "BikeSync"),
        };

        foreach (var candidate in candidates)
        {
            if (CanUse(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static bool CanUse(string root)
    {
        try
        {
            Directory.CreateDirectory(root);

            // Test the root itself, not only subdirectories. Some enterprise
            // policies deny files in the root while allowing logs/exports.
            var rootProbe = Path.Combine(root, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(rootProbe, "probe");
            File.Delete(rootProbe);

            foreach (var child in new[] { "logs", "exports" })
            {
                var directory = Path.Combine(root, child);
                Directory.CreateDirectory(directory);
                var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "probe");
                File.Delete(probe);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
