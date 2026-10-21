using System.Collections.ObjectModel;
using System.Windows;
using BikeSync.App.Services;
using BikeSync.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BikeSync.App.ViewModels;

public sealed partial class SyncViewModel : ObservableObject, IPageAware
{
    private readonly AppSession _session;
    private readonly NavigationService _navigation;
    private readonly NotificationService _notifications;
    private CancellationTokenSource? _cts;
    private string _selectionSignature = string.Empty;

    public SyncViewModel(
        AppSession session,
        NavigationService navigation,
        NotificationService notifications)
    {
        _session = session;
        _navigation = navigation;
        _notifications = notifications;
        _session.Changed += (_, _) => RefreshTargets();
    }

    public ObservableCollection<TargetSelectionViewModel> TargetOptions { get; } = new();
    public ObservableCollection<SyncRowViewModel> Rows { get; } = new();

    public IReadOnlyList<AccountOption> SelectedTargets => TargetOptions
        .Where(t => t.IsSelected)
        .Select(t => t.Option)
        .ToArray();

    [ObservableProperty]
    private string _sourceTitle = "未选择数据";

    [ObservableProperty]
    private string _sourceSubtitle = "请先从活动库勾选要导入的骑行记录。";

    [ObservableProperty]
    private string _selectionSummary = "0 项活动";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = "准备就绪";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private int _successCount;

    [ObservableProperty]
    private int _duplicateCount;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private bool _saveLocalCopy = true;

    [ObservableProperty]
    private string _firstError = string.Empty;

    public bool HasSelection => Rows.Count > 0;
    public bool HasTargets => TargetOptions.Count > 0;
    public bool CanStart => HasSelection && SelectedTargets.Count > 0 && !IsRunning;
    public bool HasFailures => FailedCount > 0;
    public string StartButtonText => IsRunning ? "正在同步…" : "开始导入";
    public string TargetHint => !HasTargets
        ? "至少需要连接一个支持导入的目标平台账户。"
        : SelectedTargets.Count == 0
            ? "请选择至少一个目标平台。"
            : "活动会分别上传到每个勾选的目标平台。";
    public string SelectedTargetSummary => SelectedTargets.Count == 0
        ? "未选择目标"
        : $"已选择 {SelectedTargets.Count} 个目标";

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(StartButtonText));
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnFailedCountChanged(int value) => OnPropertyChanged(nameof(HasFailures));

    public Task OnNavigatedToAsync()
    {
        if (_session.SyncSource is not null && _session.SelectedActivities.Count > 0)
        {
            var signature = BuildSelectionSignature(_session.SelectedActivities);
            if (signature != _selectionSignature)
            {
                Prepare(_session.SyncSource, _session.SelectedActivities.ToArray());
            }
        }
        else if (Rows.Count == 0)
        {
            SourceTitle = "还没有选择活动";
            SourceSubtitle = "请先在活动库勾选骑行记录，再进入到导入流程。";
            SelectionSummary = "0 项活动";
        }

        RefreshTargets();
        return Task.CompletedTask;
    }

    public void Prepare(PlatformAccount source, IReadOnlyList<ActivitySummary> activities)
    {
        Rows.Clear();
        foreach (var activity in activities)
        {
            Rows.Add(new SyncRowViewModel(activity));
        }

        _selectionSignature = BuildSelectionSignature(activities);
        SourceTitle = $"{source.Descriptor.DisplayName} · {source.DisplayName}";
        var distance = activities.Sum(a => a.DistanceMeters);
        var duration = TimeSpan.FromSeconds(activities.Sum(a => a.MovingTimeSeconds));
        SourceSubtitle = $"{activities.Count} 项骑行 · {DisplayFormat.DistanceCompact(distance)} · {DisplayFormat.Duration(duration)}";
        SelectionSummary = $"{activities.Count} 项活动待导入";
        HasResult = false;
        ResultTitle = string.Empty;
        ResultSummary = string.Empty;
        FirstError = string.Empty;
        SuccessCount = 0;
        DuplicateCount = 0;
        FailedCount = 0;
        ProgressValue = 0;
        ProgressText = "准备就绪";
        StatusMessage = string.Empty;

        RefreshTargets();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTargets()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RefreshTargets);
            return;
        }

        var sourceId = _session.SyncSource?.Id;
        var previouslySelected = TargetOptions
            .Where(t => t.IsSelected)
            .Select(t => t.Option.Id)
            .ToHashSet();
        var hadOptions = TargetOptions.Count > 0;
        var options = _session.AccountOptions
            .Where(o => o.Id != sourceId &&
                        PlatformCatalog.Get(o.Platform).Capabilities.CanUploadActivities)
            .ToArray();

        TargetOptions.Clear();
        for (var index = 0; index < options.Length; index++)
        {
            var option = options[index];
            var isSelected = previouslySelected.Contains(option.Id) ||
                             (!hadOptions && index == 0);
            var item = new TargetSelectionViewModel
            {
                Option = option,
                IsSelected = isSelected,
                SelectionChanged = OnTargetSelectionChanged,
            };
            TargetOptions.Add(item);
        }

        if (!TargetOptions.Any(t => t.IsSelected) && TargetOptions.Count > 0)
        {
            TargetOptions[0].IsSelected = true;
        }

        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(SelectedTargets));
        OnPropertyChanged(nameof(SelectedTargetSummary));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    private void OnTargetSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTargets));
        OnPropertyChanged(nameof(SelectedTargetSummary));
        OnPropertyChanged(nameof(TargetHint));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAllTargets()
    {
        foreach (var target in TargetOptions)
        {
            target.IsSelected = true;
        }
    }

    [RelayCommand]
    private void ClearTargets()
    {
        foreach (var target in TargetOptions)
        {
            target.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_session.SyncSource is null || Rows.Count == 0)
        {
            return;
        }

        var source = _session.FindAccount(_session.SyncSource.Id);
        var targets = SelectedTargets
            .Select(t => _session.FindAccount(t.Id))
            .Where(a => a is not null)
            .Cast<PlatformAccount>()
            .ToArray();
        if (source is null || targets.Length == 0)
        {
            _notifications.Error("平台账户状态已变化，请返回活动库重新选择。");
            return;
        }

        IsRunning = true;
        HasResult = false;
        StatusMessage = string.Empty;
        SuccessCount = 0;
        DuplicateCount = 0;
        FailedCount = 0;
        ProgressValue = 0;
        ProgressText = "正在准备导入…";
        foreach (var row in Rows)
        {
            row.State = SyncRowState.Pending;
            row.StatusText = "等待导入";
            row.Detail = string.Empty;
        }

        _cts = new CancellationTokenSource();
        var targetResults =
            new Dictionary<string, List<(string TargetName, ImportResult Result, string? SavedPath)>>(
                StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            targetResults[row.Activity.Id] = new();
        }

        try
        {
            for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                var target = targets[targetIndex];
                var currentIndex = targetIndex;
                var progress = new Progress<SyncProgress>(update =>
                {
                    var overall = (currentIndex + update.Percent / 100d) / targets.Length * 100d;
                    ProgressValue = overall;
                    ProgressText =
                        $"{currentIndex + 1}/{targets.Length} · {target.Descriptor.ShortName} · {update.Completed}/{update.Total} · {update.Stage}";
                    var row = Rows.FirstOrDefault(r => r.Activity.Id == update.ActivityId);
                    if (row is not null)
                    {
                        row.State = SyncRowState.Running;
                        row.StatusText = $"{target.Descriptor.ShortName} · {update.Stage}";
                    }
                });

                var summary = await _session.SyncService.ImportAsync(
                    new SyncRequest
                    {
                        SourceAccount = source,
                        TargetAccount = target,
                        Activities = Rows.Select(r => r.Activity).ToArray(),
                        Options = new ImportOptions
                        {
                            SaveLocalCopy = SaveLocalCopy,
                            AppendSourceToDescription = true,
                        },
                    },
                    progress,
                    _cts.Token);

                SuccessCount += summary.Succeeded;
                DuplicateCount += summary.Duplicates;
                FailedCount += summary.Failed;
                if (!string.IsNullOrWhiteSpace(summary.FirstError) &&
                    string.IsNullOrWhiteSpace(FirstError))
                {
                    FirstError = summary.FirstError;
                }

                foreach (var item in summary.Items)
                {
                    var row = Rows.FirstOrDefault(r => r.Activity.Id == item.Activity.Id);
                    if (row is null)
                    {
                        continue;
                    }

                    var detail = $"[{target.Descriptor.ShortName}] {item.Result.Message}";
                    if (!string.IsNullOrWhiteSpace(item.SavedFilePath))
                    {
                        detail += $" · 本地副本：{item.SavedFilePath}";
                    }

                    row.Detail = string.IsNullOrWhiteSpace(row.Detail)
                        ? detail
                        : row.Detail + Environment.NewLine + detail;
                    targetResults[row.Activity.Id].Add(
                        (target.Descriptor.ShortName, item.Result, item.SavedFilePath));
                }
            }

            foreach (var row in Rows)
            {
                var results = targetResults[row.Activity.Id];
                var failed = results.Count(r => !r.Result.Success);
                if (failed > 0)
                {
                    row.State = SyncRowState.Failed;
                    row.StatusText = $"{failed}/{results.Count} 个目标失败";
                }
                else if (results.Count > 0 && results.All(r => r.Result.Duplicate))
                {
                    row.State = SyncRowState.Duplicate;
                    row.StatusText = $"{results.Count} 个目标均已存在";
                }
                else
                {
                    row.State = SyncRowState.Success;
                    row.StatusText = $"{results.Count} 个目标完成";
                }
            }

            HasResult = true;
            ProgressValue = 100;
            ProgressText = "同步完成";
            ResultTitle = FailedCount == 0 ? "全部目标处理完成" : "部分目标未能导入";
            ResultSummary =
                $"{targets.Length} 个目标 · 成功 {SuccessCount} 次 · 重复 {DuplicateCount} 次 · 失败 {FailedCount} 次";
            await _session.ReloadHistoryAsync();

            if (FailedCount == 0)
            {
                _notifications.Success($"导入完成：{targets.Length} 个目标，成功 {SuccessCount} 次" +
                                       (DuplicateCount > 0 ? $"，跳过重复 {DuplicateCount} 次" : string.Empty));
            }
            else
            {
                _notifications.Warning($"导入完成，{FailedCount} 次上传失败。");
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "已取消";
            ResultTitle = "同步已取消";
            ResultSummary = "已完成的平台上传不会被撤销。";
            HasResult = true;
            _notifications.Warning("同步已取消。");
        }
        catch (Exception ex)
        {
            ProgressText = "同步中断";
            StatusMessage = ex.Message;
            ResultTitle = "同步中断";
            ResultSummary = ex.Message;
            HasResult = true;
            _notifications.Error("同步失败：" + ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand]
    private void BackToActivities() => _navigation.Navigate(AppPage.Activities);

    [RelayCommand]
    private void OpenLocalFolder()
    {
        var path = _session.Settings.ResolveDownloadDirectory();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            _notifications.Error("无法打开本地导出文件夹。");
        }
    }

    private static string BuildSelectionSignature(IEnumerable<ActivitySummary> activities) =>
        string.Join("|", activities.Select(a => $"{a.Platform}:{a.Id}"));
}
