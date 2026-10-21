using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Services;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Updates;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    Uri? DownloadUri,
    Uri ReleaseUri,
    string Source);

public sealed record PreparedUpdate(
    Version Version,
    string StagingDirectory,
    string ExecutablePath);

public sealed class UpdateService
{
    public const string GitHubOwner = "zsm19991230";
    public const string GitHubRepo = "BikeSync";
    public const string GiteeOwner = "zhaosanmao";
    public const string GiteeRepo = "bike-sync";

    public static readonly Uri GitHubReleases =
        new($"https://github.com/{GitHubOwner}/{GitHubRepo}/releases");
    public static readonly Uri GiteeReleases =
        new($"https://gitee.com/{GiteeOwner}/{GiteeRepo}/releases");

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(HttpClient http, AppPaths paths, ILogger<UpdateService> logger)
    {
        _http = http;
        _paths = paths;
        _logger = logger;
    }

    public Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 1, 0);

    public string CurrentVersionText => "v" + CurrentVersion.ToString(3);

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        var gitHub = await TryGitHubAsync(cancellationToken).ConfigureAwait(false);
        if (gitHub is not null)
        {
            return gitHub.Version > CurrentVersion ? gitHub : null;
        }

        var gitee = await TryGiteeAsync(cancellationToken).ConfigureAwait(false);
        return gitee is not null && gitee.Version > CurrentVersion ? gitee : null;
    }

    public async Task<PreparedUpdate> PrepareAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (update.DownloadUri is null)
        {
            throw new InvalidOperationException("该版本没有可下载的安装包，请打开发布页手动下载。");
        }

        var updatesRoot = Path.Combine(_paths.RootDirectory, "updates");
        Directory.CreateDirectory(updatesRoot);
        var zipPath = Path.Combine(updatesRoot, $"BikeSync-{update.Version}.zip");

        using (var response = await _http.GetAsync(
                   update.DownloadUri,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var output = new FileStream(
                zipPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);
            var buffer = new byte[64 * 1024];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                if (total is > 0)
                {
                    progress?.Report(Math.Clamp(copied * 100d / total.Value, 0, 100));
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var stageRoot = Path.Combine(
            updatesRoot,
            $"stage-{update.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, stageRoot);
            var executable = Directory
                .EnumerateFiles(stageRoot, "BikeSync.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (executable is null)
            {
                throw new InvalidDataException("更新包中没有找到 BikeSync.exe。");
            }

            progress?.Report(100);
            return new PreparedUpdate(
                update.Version,
                Path.GetDirectoryName(executable)!,
                executable);
        }
        catch
        {
            try
            {
                Directory.Delete(stageRoot, recursive: true);
            }
            catch
            {
                // Best effort.
            }

            throw;
        }
    }

    public void LaunchInstaller(PreparedUpdate update)
    {
        var appDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var batchPath = Path.Combine(
            _paths.RootDirectory,
            "updates",
            $"apply-{update.Version}-{Guid.NewGuid():N}.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(batchPath)!);

        var script = $"""
            @echo off
            chcp 65001 >nul
            setlocal
            :wait
            tasklist /FI "PID eq {Environment.ProcessId}" 2>nul | find "{Environment.ProcessId}" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            robocopy "{Escape(update.StagingDirectory)}" "{Escape(appDirectory)}" /E /R:5 /W:2 /NFL /NDL /NJH /NJS /NP > "%TEMP%\BikeSync-update.log" 2>&1
            if errorlevel 8 goto failed
            start "" "{Escape(Path.Combine(appDirectory, "BikeSync.exe"))}"
            exit /b 0
            :failed
            start "" "{Escape(Path.Combine(appDirectory, "BikeSync.exe"))}"
            exit /b 1
            """;
        File.WriteAllText(batchPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new ProcessStartInfo("cmd.exe", $"/c \"\"{batchPath}\"\"")
        {
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process.Start(startInfo);
        _logger.LogInformation("Started updater batch for version {Version}.", update.Version);
    }

    private async Task<UpdateInfo?> TryGitHubAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest");
            request.Headers.TryAddWithoutValidation("User-Agent", "BikeSync/1.1");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ParseRelease(document.RootElement, "GitHub", downloadSuffix: ".zip");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "GitHub update check failed.");
            return null;
        }
    }

    private async Task<UpdateInfo?> TryGiteeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://gitee.com/api/v5/repos/{GiteeOwner}/{GiteeRepo}/releases/latest");
            request.Headers.TryAddWithoutValidation("User-Agent", "BikeSync/1.1");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return ParseRelease(document.RootElement, "Gitee", downloadSuffix: ".zip");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogDebug(ex, "Gitee update check failed.");
            return null;
        }
    }

    private static UpdateInfo? ParseRelease(JsonElement root, string source, string downloadSuffix)
    {
        var tag = ReadString(root, "tag_name") ?? ReadString(root, "tag");
        if (string.IsNullOrWhiteSpace(tag) ||
            !Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var version))
        {
            return null;
        }

        Uri? download = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = ReadString(asset, "name");
                var url = ReadString(asset, "browser_download_url") ?? ReadString(asset, "download_url");
                if (name is not null &&
                    name.EndsWith(downloadSuffix, StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    download = uri;
                    break;
                }
            }
        }

        var releaseUrl = ReadString(root, "html_url") ?? ReadString(root, "url") ??
                         (source == "GitHub" ? GitHubReleases.ToString() : GiteeReleases.ToString());
        return new UpdateInfo(
            version,
            tag,
            ReadString(root, "name") ?? tag,
            ReadString(root, "body") ?? string.Empty,
            download,
            Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri)
                ? releaseUri
                : source == "GitHub" ? GitHubReleases : GiteeReleases,
            source);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Escape(string value) => value.Replace("\"", "\"\"");
}
