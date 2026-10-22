using System.Collections.ObjectModel;
using System.Windows;
using BikeSync.App.Services;
using BikeSync.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace BikeSync.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DashboardViewModel _dashboard;
    private readonly AccountsViewModel _accounts;
    private readonly ActivityLibraryViewModel _activities;
    private readonly SyncViewModel _sync;
    private readonly SettingsViewModel _settings;
    private readonly AppSession _session;
    private readonly NavigationService _navigation;
    private readonly NotificationService _notifications;
    private readonly UpdateService _updates;
    private bool _suppressNavigation;
    private CancellationTokenSource? _toastCts;

    public MainWindowViewModel(
        DashboardViewModel dashboard,
        AccountsViewModel accounts,
        ActivityLibraryViewModel activities,
        SyncViewModel sync,
        SettingsViewModel settings,
        AppSession session,
        NavigationService navigation,
        NotificationService notifications,
        UpdateService updates)
    {
        _dashboard = dashboard;
        _accounts = accounts;
        _activities = activities;
        _sync = sync;
        _settings = settings;
        _session = session;
        _navigation = navigation;
        _notifications = notifications;
        _updates = updates;

        NavigationItems = new ObservableCollection<NavigationItem>
        {
            new(AppPage.Dashboard, "概览", SymbolRegular.Home24, "同步状态一览"),
            new(AppPage.Accounts, "平台连接", SymbolRegular.PlugConnected24, "管理骑行平台账户"),
            new(AppPage.Activities, "活动库", SymbolRegular.VehicleBicycle24, "浏览并选择骑行记录"),
            new(AppPage.Sync, "同步中心", SymbolRegular.ArrowSync24, "导入到目标平台"),
            new(AppPage.Settings, "设置", SymbolRegular.Settings24, "凭据与本地存储"),
        };

        _navigation.NavigationRequested += OnNavigationRequested;
        _notifications.Published += OnNotificationPublished;
        _session.Changed += (_, _) => RefreshSidebar();

        _suppressNavigation = true;
        SelectedNavigationItem = NavigationItems[0];
        _suppressNavigation = false;
        CurrentPage = _dashboard;
        RefreshSidebar();
        _ = InvokePageAwareAsync(_dashboard);
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    [ObservableProperty]
    private NavigationItem? _selectedNavigationItem;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _sidebarAccountsText = "尚未连接平台";

    [ObservableProperty]
    private string _sidebarSyncText = "本地数据安全存储";

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private string _toastTitle = string.Empty;

    [ObservableProperty]
    private string _toastColor = "#4C8DFF";

    [ObservableProperty]
    private SymbolRegular _toastSymbol = SymbolRegular.Info24;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    public double SidebarWidth => IsSidebarExpanded ? 250 : 0;
    public SymbolRegular SidebarToggleSymbol => IsSidebarExpanded
        ? SymbolRegular.PanelLeftContract24
        : SymbolRegular.PanelLeftExpand24;
    public string SidebarToggleText => IsSidebarExpanded ? "收起侧边栏" : "展开侧边栏";

    partial void OnIsSidebarExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(SidebarToggleSymbol));
        OnPropertyChanged(nameof(SidebarToggleText));
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    public string CurrentDateText => DateTime.Now.ToString("yyyy年M月d日",
        System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

    public string VersionText => $"{_updates.CurrentVersionText} · Windows Desktop";

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is null || _suppressNavigation)
        {
            return;
        }

        ShowPage(value.Page);
    }

    private void ShowPage(AppPage page)
    {
        object target = page switch
        {
            AppPage.Dashboard => _dashboard,
            AppPage.Accounts => _accounts,
            AppPage.Activities => _activities,
            AppPage.Sync => _sync,
            AppPage.Settings => _settings,
            _ => _dashboard,
        };

        CurrentPage = target;
        var item = NavigationItems.FirstOrDefault(i => i.Page == page);
        if (item is not null && !ReferenceEquals(SelectedNavigationItem, item))
        {
            _suppressNavigation = true;
            SelectedNavigationItem = item;
            _suppressNavigation = false;
        }

        if (target is IPageAware aware)
        {
            _ = InvokePageAwareAsync(aware);
        }
    }

    private async Task InvokePageAwareAsync(IPageAware page)
    {
        try
        {
            await page.OnNavigatedToAsync();
        }
        catch (Exception ex)
        {
            _notifications.Error("页面刷新失败：" + ex.Message);
        }
    }

    private void OnNavigationRequested(object? sender, NavigationRequest request)
    {
        if (request.Page == AppPage.Sync &&
            request.SourceAccount is not null &&
            request.Activities is { Count: > 0 })
        {
            _sync.Prepare(request.SourceAccount, request.Activities);
        }

        ShowPage(request.Page);
    }

    private void OnNotificationPublished(object? sender, AppNotification notification)
    {
        void Apply()
        {
            ToastMessage = notification.Message;
            ToastTitle = notification.Kind switch
            {
                NotificationKind.Success => "操作成功",
                NotificationKind.Warning => "请注意",
                NotificationKind.Error => "操作失败",
                _ => "提示",
            };
            ToastColor = notification.Kind switch
            {
                NotificationKind.Success => "#34D399",
                NotificationKind.Warning => "#FBBF24",
                NotificationKind.Error => "#F87171",
                _ => "#4C8DFF",
            };
            ToastSymbol = notification.Kind switch
            {
                NotificationKind.Success => SymbolRegular.CheckmarkCircle24,
                NotificationKind.Warning => SymbolRegular.Warning24,
                NotificationKind.Error => SymbolRegular.ErrorCircle24,
                _ => SymbolRegular.Info24,
            };
            IsToastVisible = true;

            _toastCts?.Cancel();
            _toastCts?.Dispose();
            _toastCts = new CancellationTokenSource();
            _ = HideToastAfterDelayAsync(_toastCts.Token);
        }

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    private async Task HideToastAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4.5), cancellationToken);
            IsToastVisible = false;
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer notification.
        }
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastCts?.Cancel();
        IsToastVisible = false;
    }

    [RelayCommand]
    private void GoToAccounts() => _navigation.Navigate(AppPage.Accounts);

    [RelayCommand]
    private void GoToActivities() => _navigation.Navigate(AppPage.Activities);

    private void RefreshSidebar()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshSidebar);
            return;
        }

        SidebarAccountsText = _session.AccountCount == 0
            ? "尚未连接平台"
            : $"{_session.AccountCount} 个账户 · {_session.ConnectedPlatformCount} 个平台";
        SidebarSyncText = _session.History.FirstOrDefault() is { } last
            ? $"上次同步 {last.Timestamp.LocalDateTime:MM-dd HH:mm}"
            : "本地数据安全存储";
    }
}
