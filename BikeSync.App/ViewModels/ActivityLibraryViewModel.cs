using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using BikeSync.App.Services;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BikeSync.App.ViewModels;

public sealed partial class ActivityLibraryViewModel : ObservableObject, IPageAware
{
    private readonly AppSession _session;
    private readonly NavigationService _navigation;
    private readonly NotificationService _notifications;
    private readonly ActivityCacheService _cache;
    private string _accountSignature = string.Empty;
    private bool _suppressSourceReset;

    public ActivityLibraryViewModel(
        AppSession session,
        NavigationService navigation,
        NotificationService notifications,
        ActivityCacheService cache)
    {
        _session = session;
        _navigation = navigation;
        _notifications = notifications;
        _cache = cache;
        _session.Changed += (_, _) => RebuildSources();

        DateRanges = new[]
        {
            new DateRangeOption("最近 30 天", DateRangePreset.Last30Days),
            new DateRangeOption("最近 90 天", DateRangePreset.Last90Days),
            new DateRangeOption("今年", DateRangePreset.ThisYear),
            new DateRangeOption("自定义", DateRangePreset.Custom),
        };
        SelectedRange = DateRanges[1];
        FromDate = DateTime.Today.AddDays(-90);
        ToDate = DateTime.Today;

        ActivitiesView = CollectionViewSource.GetDefaultView(Rows);
        ActivitiesView.Filter = FilterActivity;
        RebuildSources();
    }

    public IReadOnlyList<DateRangeOption> DateRanges { get; }
    public ObservableCollection<AccountOption> SourceOptions { get; } = new();
    public ObservableCollection<ActivityRowViewModel> Rows { get; } = new();
    public ICollectionView ActivitiesView { get; }

    [ObservableProperty]
    private AccountOption? _selectedSource;

    [ObservableProperty]
    private DateRangeOption? _selectedRange;

    [ObservableProperty]
    private DateTime? _fromDate;

    [ObservableProperty]
    private DateTime? _toDate;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showCyclingOnly = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasLoaded;

    [ObservableProperty]
    private string _statusText = "选择数据源，然后加载骑行活动。";

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private int _visibleCount;

    [ObservableProperty]
    private string _selectedDistanceText = "0 km";

    [ObservableProperty]
    private string _selectedDurationText = "0:00";

    [ObservableProperty]
    private bool _areAllSelected;

    public bool IsCustomRange => SelectedRange?.Preset == DateRangePreset.Custom;
    public bool HasRows => Rows.Count > 0;
    public bool IsEmpty => HasLoaded && Rows.Count == 0;
    public bool HasNoAccounts => SourceOptions.Count == 0;
    public bool ShowEmptyState => HasNoAccounts || IsEmpty;
    public bool CanOpenSync => SelectedCount > 0 && SelectedSource is not null;
    public string SelectAllText => AreAllSelected ? "取消全选" : "全选当前列表";
    public string FilteredCountText => $"{VisibleCount} / {Rows.Count} 项";
    public string EmptyTitle => HasNoAccounts ? "先连接一个骑行平台" : "这个时间范围内没有活动";
    public string EmptyHint => HasNoAccounts
        ? "前往「平台连接」完成 MyWhoosh、Strava、intervals.icu 或 iGPSPORT 授权。"
        : "试试扩大日期范围，或关闭「仅骑行」筛选后重新加载。";

    partial void OnSelectedRangeChanged(DateRangeOption? value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
    }

    partial void OnSelectedSourceChanged(AccountOption? value)
    {
        if (_suppressSourceReset)
        {
            return;
        }

        Rows.Clear();
        ActivitiesView.Refresh();
        HasLoaded = false;
        SelectedCount = 0;
        UpdateSelectionStats();
        StatusText = value is null ? "请选择数据源。" : "正在读取本地缓存…";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowEmptyState));
        LoadActivitiesCommand.NotifyCanExecuteChanged();

        if (value is not null)
        {
            _ = RestoreCachedOrFetchAsync(value);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ActivitiesView.Refresh();
        UpdateVisibleCount();
    }

    partial void OnShowCyclingOnlyChanged(bool value)
    {
        ActivitiesView.Refresh();
        UpdateVisibleCount();
    }

    partial void OnIsBusyChanged(bool value)
    {
        LoadActivitiesCommand.NotifyCanExecuteChanged();
    }

    public async Task OnNavigatedToAsync()
    {
        RebuildSources();
        if (SelectedSource is not null && !HasLoaded && !IsBusy)
        {
            // 启动时只读取本地缓存，避免每次打开应用都请求平台。
            await RestoreCachedAsync(SelectedSource, fetchWhenEmpty: false).ConfigureAwait(true);
        }
    }

    private void RebuildSources()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RebuildSources);
            return;
        }

        var options = _session.AccountOptions
            .Where(o => PlatformCatalog.Get(o.Platform).Capabilities.CanListActivities)
            .ToArray();
        var signature = string.Join("|", options.Select(o => $"{o.Id}:{o.Platform}:{o.DisplayName}"));
        if (signature == _accountSignature && SourceOptions.Count == options.Length)
        {
            return;
        }

        _accountSignature = signature;
        var selectedId = SelectedSource?.Id;
        _suppressSourceReset = true;
        SourceOptions.Clear();
        foreach (var option in options)
        {
            SourceOptions.Add(option);
        }

        SelectedSource = selectedId is { } id
            ? SourceOptions.FirstOrDefault(o => o.Id == id) ?? SourceOptions.FirstOrDefault()
            : SourceOptions.FirstOrDefault();
        _suppressSourceReset = false;

        OnPropertyChanged(nameof(HasNoAccounts));
        OnPropertyChanged(nameof(ShowEmptyState));
        if (SourceOptions.Count == 0)
        {
            Rows.Clear();
            HasLoaded = false;
            StatusText = "还没有连接任何平台。";
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private async Task RestoreCachedOrFetchAsync(AccountOption source)
    {
        if (IsBusy)
        {
            return;
        }

        ActivityCacheEntry? entry = null;
        try
        {
            entry = await _cache.GetAsync(source.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Warning("读取活动缓存失败，将重新从平台获取。");
        }

        if (SelectedSource?.Id != source.Id)
        {
            return;
        }

        if (entry is null)
        {
            HasLoaded = false;
            StatusText = "暂无本地缓存，正在从平台获取…";
            await LoadActivitiesAsync().ConfigureAwait(true);
            return;
        }

        ApplyCached(entry);
    }

    private async Task RestoreCachedAsync(AccountOption source, bool fetchWhenEmpty)
    {
        ActivityCacheEntry? entry = null;
        try
        {
            entry = await _cache.GetAsync(source.Id).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifications.Warning("读取活动缓存失败。");
        }

        if (SelectedSource?.Id != source.Id)
        {
            return;
        }

        if (entry is null)
        {
            HasLoaded = false;
            StatusText = "暂无本地缓存，点击「刷新活动」从平台获取。";
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowEmptyState));
            if (fetchWhenEmpty)
            {
                await LoadActivitiesAsync().ConfigureAwait(true);
            }

            return;
        }

        ApplyCached(entry);
    }

    private void ApplyCached(ActivityCacheEntry entry)
    {
        var query = BuildQuery();
        var cached = entry.Activities
            .Where(a => a.StartTime >= query.From && a.StartTime <= query.To)
            .OrderByDescending(a => a.StartTime)
            .Take(Math.Clamp(query.MaxResults, 1, 1000))
            .ToArray();

        ReplaceRows(cached);
        HasLoaded = true;
        ActivitiesView.Refresh();
        UpdateVisibleCount();
        UpdateSelectionStats();
        var rangeHint = entry.From > query.From || entry.To < query.To
            ? "，缓存范围较小，建议刷新"
            : string.Empty;
        StatusText =
            $"本地缓存 {cached.Length} 项 · 更新于 {entry.FetchedAt.LocalDateTime:MM-dd HH:mm}{rangeHint}";
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void ReplaceRows(IReadOnlyList<ActivitySummary> activities)
    {
        Rows.Clear();
        foreach (var activity in activities)
        {
            var row = new ActivityRowViewModel(activity);
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task LoadActivitiesAsync()
    {
        if (SelectedSource is null)
        {
            _notifications.Warning("请先选择数据源。");
            return;
        }

        if (SelectedRange?.Preset == DateRangePreset.Custom &&
            (FromDate is null || ToDate is null || FromDate.Value.Date > ToDate.Value.Date))
        {
            _notifications.Warning("请选择有效的自定义日期范围。");
            return;
        }

        IsBusy = true;
        StatusText = "正在从平台获取活动…";
        SelectedCount = 0;
        UpdateSelectionStats();

        try
        {
            var query = BuildQuery();
            var activities = await _session.SyncService.FetchActivitiesAsync(
                SelectedSource.Id,
                query,
                CancellationToken.None);

            ReplaceRows(activities);
            HasLoaded = true;
            ActivitiesView.Refresh();
            UpdateVisibleCount();
            UpdateSelectionStats();
            StatusText = ActivityCountText(activities.Count);
            _notifications.Success($"已加载 {activities.Count} 项骑行活动");

            var account = _session.FindAccount(SelectedSource.Id);
            if (account is not null)
            {
                await _cache.SaveAsync(account, query, activities, CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
        catch (PlatformApiException ex)
        {
            HasLoaded = true;
            StatusText = ex.Message;
            _notifications.Error(ex.Message);
        }
        catch (Exception ex)
        {
            HasLoaded = true;
            StatusText = "加载失败：" + ex.Message;
            _notifications.Error("加载活动失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private bool CanLoad() => !IsBusy && SelectedSource is not null;

    private ActivityQuery BuildQuery()
    {
        var preset = SelectedRange?.Preset ?? DateRangePreset.Last90Days;
        if (preset != DateRangePreset.Custom)
        {
            return ActivityQuery.ForPreset(preset) with
            {
                CyclingOnly = ShowCyclingOnly,
                Search = SearchText,
                MaxResults = 200,
            };
        }

        var from = FromDate ?? DateTime.Today.AddDays(-90);
        var to = ToDate ?? DateTime.Today;
        var fromOffset = new DateTimeOffset(from.Date, TimeZoneInfo.Local.GetUtcOffset(from.Date));
        var endLocal = to.Date.AddDays(1).AddSeconds(-1);
        var endOffset = new DateTimeOffset(endLocal, TimeZoneInfo.Local.GetUtcOffset(endLocal));

        return new ActivityQuery
        {
            Preset = preset,
            From = fromOffset,
            To = endOffset,
            CyclingOnly = ShowCyclingOnly,
            Search = SearchText,
            MaxResults = 200,
        };
    }

    private static string ActivityCountText(int count) =>
        count == 0 ? "没有找到活动。" : $"已加载 {count} 项活动，勾选后即可导入。";

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityRowViewModel.IsSelected))
        {
            UpdateSelectionStats();
        }
    }

    private void UpdateSelectionStats()
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Activity).ToArray();
        SelectedCount = selected.Length;
        SelectedDistanceText = DisplayFormat.DistanceCompact(selected.Sum(a => a.DistanceMeters));
        SelectedDurationText = DisplayFormat.Duration(TimeSpan.FromSeconds(selected.Sum(a => a.MovingTimeSeconds)));
        AreAllSelected = VisibleRows().Count > 0 && VisibleRows().All(r => r.IsSelected);
        OnPropertyChanged(nameof(AreAllSelected));
        OnPropertyChanged(nameof(SelectAllText));
        OnPropertyChanged(nameof(CanOpenSync));
        OnPropertyChanged(nameof(FilteredCountText));
    }

    private void UpdateVisibleCount()
    {
        VisibleCount = VisibleRows().Count;
        OnPropertyChanged(nameof(FilteredCountText));
        UpdateSelectionStats();
    }

    private List<ActivityRowViewModel> VisibleRows() =>
        ActivitiesView.Cast<ActivityRowViewModel>().ToList();

    private bool FilterActivity(object item)
    {
        if (item is not ActivityRowViewModel row)
        {
            return false;
        }

        if (ShowCyclingOnly && !row.Activity.IsCycling)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !row.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !row.SportText.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    [RelayCommand]
    private void ToggleSelectAll()
    {
        var visible = VisibleRows();
        var select = !(visible.Count > 0 && visible.All(r => r.IsSelected));
        foreach (var row in visible)
        {
            row.IsSelected = select;
        }

        UpdateSelectionStats();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var row in Rows)
        {
            row.IsSelected = false;
        }

        UpdateSelectionStats();
    }

    [RelayCommand]
    private void OpenAccounts() => _navigation.Navigate(AppPage.Accounts);

    [RelayCommand]
    private void OpenSync()
    {
        var selected = Rows.Where(r => r.IsSelected).Select(r => r.Activity).ToArray();
        if (selected.Length == 0 || SelectedSource is null)
        {
            _notifications.Warning("请至少勾选一项活动。");
            return;
        }

        var source = _session.FindAccount(SelectedSource.Id);
        if (source is null)
        {
            _notifications.Error("源平台账户已失效，请重新连接。");
            return;
        }

        _session.SetSyncSelection(source, selected);
        _navigation.OpenSync(source, selected);
    }
}
