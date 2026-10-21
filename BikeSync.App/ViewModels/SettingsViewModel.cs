using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using BikeSync.App.Services;
using BikeSync.Core.Models;
using BikeSync.Core.Services;
using BikeSync.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace BikeSync.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IPageAware
{
    private readonly AppSession _session;
    private readonly AppPaths _paths;
    private readonly NotificationService _notifications;
    private readonly UpdateService _updates;
    private UpdateInfo? _availableUpdate;

    public SettingsViewModel(
        AppSession session,
        AppPaths paths,
        NotificationService notifications,
        UpdateService updates)
    {
        _session = session;
        _paths = paths;
        _notifications = notifications;
        _updates = updates;
        LoadFromSettings();
    }

    [ObservableProperty]
    private string _stravaClientId = string.Empty;

    [ObservableProperty]
    private string _stravaClientSecret = string.Empty;

    [ObservableProperty]
    private string _downloadDirectory = string.Empty;

    [ObservableProperty]
    private bool _saveActivityCopies = true;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isCheckingUpdate;

    [ObservableProperty]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateStatus = "检查是否有新版本。";

    [ObservableProperty]
    private string _latestVersionText = string.Empty;

    [ObservableProperty]
    private string _updateNotes = string.Empty;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private string _updateProgressText = string.Empty;

    public string CurrentVersionText => _updates.CurrentVersionText;
    public bool CanInstallUpdate =>
        IsUpdateAvailable && _availableUpdate?.DownloadUri is not null && !IsInstallingUpdate;

    public string CallbackUrl => StravaAppSettings.DefaultRedirectUri;
    public string DataRoot => _paths.RootDirectory;
    public string LogDirectory => _paths.LogDirectory;
    public string AppVersion => "v" + (Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "1.0.0");

    public bool IsStravaConfigured =>
        !string.IsNullOrWhiteSpace(StravaClientId) && !string.IsNullOrWhiteSpace(StravaClientSecret);

    public string StravaConfigStatus => IsStravaConfigured ? "已配置" : "未配置";

    partial void OnIsUpdateAvailableChanged(bool value) =>
        OnPropertyChanged(nameof(CanInstallUpdate));

    partial void OnIsInstallingUpdateChanged(bool value) =>
        OnPropertyChanged(nameof(CanInstallUpdate));

    partial void OnStravaClientIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsStravaConfigured));
        OnPropertyChanged(nameof(StravaConfigStatus));
    }

    partial void OnStravaClientSecretChanged(string value)
    {
        OnPropertyChanged(nameof(IsStravaConfigured));
        OnPropertyChanged(nameof(StravaConfigStatus));
    }

    public Task OnNavigatedToAsync()
    {
        LoadFromSettings();
        return Task.CompletedTask;
    }

    private void LoadFromSettings()
    {
        var settings = _session.Settings.Current;
        StravaClientId = settings.Strava.ClientId;
        StravaClientSecret = settings.Strava.ClientSecret;
        DownloadDirectory = settings.DownloadDirectory;
        SaveActivityCopies = settings.SaveActivityCopies;
        StatusText = string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            StatusText = "请选择活动文件保存目录。";
            return;
        }

        IsSaving = true;
        StatusText = "正在保存…";
        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            var current = _session.Settings.Current;
            var settings = new AppSettings
            {
                DownloadDirectory = DownloadDirectory.Trim(),
                SaveActivityCopies = SaveActivityCopies,
                Theme = current.Theme,
                Strava = new StravaAppSettings
                {
                    ClientId = StravaClientId.Trim(),
                    ClientSecret = StravaClientSecret,
                    RedirectUri = StravaAppSettings.DefaultRedirectUri,
                },
            };

            await _session.Settings.SaveAsync(settings);
            StatusText = "设置已保存。";
            _notifications.Success("设置已保存，敏感信息已使用 Windows DPAPI 加密。");
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
            _notifications.Error("保存设置失败：" + ex.Message);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void BrowseDownloadDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择活动文件保存目录",
            InitialDirectory = Directory.Exists(DownloadDirectory)
                ? DownloadDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            DownloadDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void OpenDownloadDirectory()
    {
        var directory = string.IsNullOrWhiteSpace(DownloadDirectory)
            ? _paths.DefaultDownloadDirectory
            : DownloadDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _notifications.Error("无法创建导出目录：" + ex.Message);
            return;
        }

        TryOpen(directory);
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        UpdateStatus = "正在检查更新…";
        UpdateProgress = 0;
        UpdateProgressText = string.Empty;
        try
        {
            var update = await _updates.CheckAsync(CancellationToken.None);
            if (update is null)
            {
                _availableUpdate = null;
                IsUpdateAvailable = false;
                LatestVersionText = string.Empty;
                UpdateNotes = string.Empty;
                UpdateStatus = $"当前已是最新版本 {_updates.CurrentVersionText}。";
                return;
            }

            _availableUpdate = update;
            IsUpdateAvailable = true;
            LatestVersionText = $"{update.Tag} · {update.Source}";
            UpdateNotes = TrimNotes(update.Notes);
            UpdateStatus = update.DownloadUri is null
                ? "发现新版本，请打开发布页手动下载。"
                : "发现新版本，可以直接下载并安装。";
        }
        catch (Exception ex)
        {
            UpdateStatus = "检查更新失败：" + ex.Message;
            _notifications.Error("检查更新失败：" + ex.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is null || IsInstallingUpdate)
        {
            return;
        }

        if (_availableUpdate.DownloadUri is null)
        {
            OpenReleasePage();
            return;
        }

        IsInstallingUpdate = true;
        UpdateStatus = "正在下载更新…";
        UpdateProgress = 0;
        var progress = new Progress<double>(value =>
        {
            UpdateProgress = value;
            UpdateProgressText = $"{value:0}%";
        });
        try
        {
            var prepared = await _updates.PrepareAsync(
                _availableUpdate,
                progress,
                CancellationToken.None);
            UpdateStatus = "下载完成，正在重启并应用更新…";
            _updates.LaunchInstaller(prepared);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatus = "更新安装失败：" + ex.Message;
            _notifications.Error("更新安装失败：" + ex.Message);
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _availableUpdate?.ReleaseUri.ToString() ?? UpdateService.GitHubReleases.ToString();
        TryOpen(url);
    }

    [RelayCommand]
    private void OpenDataDirectory() => TryOpen(_paths.RootDirectory);

    [RelayCommand]
    private void OpenLogDirectory() => TryOpen(_paths.LogDirectory);

    [RelayCommand]
    private void OpenStravaApi() =>
        TryOpen("https://www.strava.com/settings/api");

    [RelayCommand]
    private void OpenIntervalsSettings() =>
        TryOpen("https://intervals.icu/settings");

    [RelayCommand]
    private void OpenRideWithGpsApi() =>
        TryOpen("https://ridewithgps.com/api/api_clients");

    [RelayCommand]
    private void CopyCallbackUrl()
    {
        try
        {
            Clipboard.SetText(CallbackUrl);
            _notifications.Success("回调地址已复制。");
        }
        catch
        {
            _notifications.Error("无法写入剪贴板。");
        }
    }

    private static string TrimNotes(string notes)
    {
        var value = notes.Trim();
        return value.Length <= 1200 ? value : value[..1200] + "…";
    }

    private void TryOpen(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch
        {
            _notifications.Error("无法打开目标位置。");
        }
    }
}
