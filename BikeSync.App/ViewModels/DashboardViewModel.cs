using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using BikeSync.App.Services;
using BikeSync.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BikeSync.App.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject, IPageAware
{
    private readonly AppSession _session;
    private readonly NavigationService _navigation;

    public DashboardViewModel(AppSession session, NavigationService navigation)
    {
        _session = session;
        _navigation = navigation;
        _session.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<PlatformStatusViewModel> Platforms { get; } = new();
    public ObservableCollection<HistoryRowViewModel> RecentSyncs { get; } = new();

    [ObservableProperty]
    private int _connectedPlatformCount;

    [ObservableProperty]
    private int _accountCount;

    [ObservableProperty]
    private int _importedTotal;

    [ObservableProperty]
    private string _lastSyncText = "还没有同步记录";

    [ObservableProperty]
    private string _greetingText = "你好，骑友";

    [ObservableProperty]
    private string _todayText = string.Empty;

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    private bool _hasAccounts;

    public string PlatformCounterText => $"{ConnectedPlatformCount} / {PlatformCatalog.All.Count}";

    private void Refresh()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Refresh);
            return;
        }

        ConnectedPlatformCount = _session.ConnectedPlatformCount;
        AccountCount = _session.AccountCount;
        ImportedTotal = _session.History.Sum(h => h.Succeeded);
        HasAccounts = _session.HasAccounts;
        HasHistory = _session.History.Count > 0;
        LastSyncText = _session.History.FirstOrDefault() is { } last
            ? $"{last.Timestamp.LocalDateTime:MM-dd HH:mm} · {last.SourceName} → {last.TargetName}"
            : "还没有同步记录";
        OnPropertyChanged(nameof(PlatformCounterText));

        var hour = DateTime.Now.Hour;
        GreetingText = hour switch
        {
            < 6 => "夜深了，骑友",
            < 11 => "早上好，骑友",
            < 14 => "中午好，骑友",
            < 18 => "下午好，骑友",
            _ => "晚上好，骑友",
        };
        TodayText = DateTime.Now.ToString("yyyy年M月d日 dddd",
            System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

        Platforms.Clear();
        foreach (var descriptor in PlatformCatalog.All)
        {
            var accounts = _session.Accounts.Where(a => a.Platform == descriptor.Id).ToArray();
            Platforms.Add(new PlatformStatusViewModel
            {
                Descriptor = descriptor,
                IsConnected = accounts.Length > 0,
                AccountName = accounts.FirstOrDefault()?.DisplayName ?? "未连接",
                StatusText = accounts.Length > 0
                    ? $"已连接 · {accounts[0].AuthLabel}"
                    : "点击连接",
            });
        }

        RecentSyncs.Clear();
        foreach (var entry in _session.History.Take(4))
        {
            RecentSyncs.Add(new HistoryRowViewModel { Entry = entry });
        }
    }

    public async Task OnNavigatedToAsync()
    {
        await _session.ReloadHistoryAsync().ConfigureAwait(true);
        Refresh();
    }

    [RelayCommand]
    private void OpenAccounts() => _navigation.Navigate(AppPage.Accounts);

    [RelayCommand]
    private void OpenActivities() => _navigation.Navigate(AppPage.Activities);

    [RelayCommand]
    private void OpenSettings() => _navigation.Navigate(AppPage.Settings);

    [RelayCommand]
    private void OpenPlatform(PlatformStatusViewModel? status)
    {
        if (status is null)
        {
            return;
        }

        TryOpen(status.Descriptor.WebsiteUrl);
    }

    private static void TryOpen(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // A failed helper launch is non-fatal.
        }
    }
}
