using System.Globalization;
using BikeSync.App.Services;
using BikeSync.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Wpf.Ui.Controls;

namespace BikeSync.App.ViewModels;

public sealed record NavigationItem(
    AppPage Page,
    string Title,
    SymbolRegular Symbol,
    string Description);

public sealed record DateRangeOption(string Label, DateRangePreset Preset);

public sealed partial class ThemeOptionViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string AccentHex { get; init; }
    public required string BackgroundHex { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class PlatformStatusViewModel
{
    public required PlatformDescriptor Descriptor { get; init; }
    public required bool IsConnected { get; init; }
    public required string AccountName { get; init; }
    public required string StatusText { get; init; }

    public string Name => Descriptor.DisplayName;
    public string ShortName => Descriptor.ShortName;
    public string Tagline => Descriptor.Tagline;
    public string BrandColor => Descriptor.BrandColor;
    public string DotColor => IsConnected ? "#34D399" : "#66798E";
    public string ActionText => IsConnected ? "管理" : "去连接";
    public string Initial => Descriptor.Id switch
    {
        PlatformId.Strava => "S",
        PlatformId.IntervalsIcu => "i",
        PlatformId.RideWithGps => "R",
        PlatformId.MyWhoosh => "M",
        PlatformId.IGpsport => "I",
        PlatformId.Xingzhe => "X",
        _ => "?",
    };
}

public sealed class HistoryRowViewModel
{
    public required SyncHistoryEntry Entry { get; init; }

    public string Route => $"{Entry.SourceName}  →  {Entry.TargetName}";
    public string TimestampText => FormatRelative(Entry.Timestamp);
    public string SummaryText => Entry.Failed > 0
        ? $"成功 {Entry.Succeeded} · 重复 {Entry.Duplicates} · 失败 {Entry.Failed}"
        : $"成功 {Entry.Succeeded} 项" + (Entry.Duplicates > 0 ? $" · 重复 {Entry.Duplicates}" : string.Empty);
    public bool HasFailures => Entry.Failed > 0;
    public string StatusText => Entry.Failed > 0 ? "部分失败" : "已完成";

    private static string FormatRelative(DateTimeOffset time)
    {
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (delta.TotalHours < 1)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta.TotalDays < 1)
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return time.LocalDateTime.ToString("MM-dd HH:mm", CultureInfo.GetCultureInfo("zh-CN"));
    }
}

public sealed partial class ActivityRowViewModel : ObservableObject
{
    public ActivityRowViewModel(ActivitySummary activity)
    {
        Activity = activity;
    }

    public ActivitySummary Activity { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Id => Activity.Id;
    public string Name => Activity.Name;
    public string DateText => Activity.DisplayStartTime.Year == DateTime.Now.Year
        ? Activity.DisplayStartTime.ToString("MM月dd日 HH:mm", CultureInfo.GetCultureInfo("zh-CN"))
        : Activity.DisplayStartTime.ToString("yyyy年MM月dd日", CultureInfo.GetCultureInfo("zh-CN"));
    public string SportText => Activity.SportType;
    public string DistanceText => DisplayFormat.Distance(Activity.DistanceMeters);
    public string DurationText => DisplayFormat.Duration(Activity.MovingTime);
    public string ElevationText => DisplayFormat.Elevation(Activity.ElevationGainMeters);
    public string HeartRateText => Activity.AverageHeartRate is { } hr ? $"{hr} bpm" : "—";
    public string SourceText => Activity.AccountName;
    public string PlatformColor => PlatformCatalog.Get(Activity.Platform).BrandColor;
    public string PlatformShort => PlatformCatalog.Get(Activity.Platform).ShortName;
    public string PlatformInitial => PlatformCatalog.Initial(Activity.Platform);
}

public enum SyncRowState
{
    Pending,
    Running,
    Success,
    Duplicate,
    Failed,
}

public sealed partial class SyncRowViewModel : ObservableObject
{
    public SyncRowViewModel(ActivitySummary activity)
    {
        Activity = activity;
    }

    public ActivitySummary Activity { get; }

    [ObservableProperty]
    private SyncRowState _state = SyncRowState.Pending;

    [ObservableProperty]
    private string _statusText = "等待导入";

    [ObservableProperty]
    private string _detail = string.Empty;

    public string Name => Activity.Name;
    public string DateText => DisplayFormat.ShortDate(Activity.DisplayStartTime);
    public string DistanceText => DisplayFormat.Distance(Activity.DistanceMeters);
    public string SourceName => Activity.AccountName;
}

public static class DisplayFormat
{
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");

    public static string Distance(double meters)
    {
        if (meters >= 1000)
        {
            return $"{meters / 1000:0.0} km";
        }

        return $"{meters:0} m";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        }

        return $"{span.Minutes}:{span.Seconds:00}";
    }

    public static string Elevation(double meters) => $"{meters:0} m";

    public static string ShortDate(DateTime time) =>
        time.Year == DateTime.Now.Year
            ? time.ToString("MM-dd HH:mm", Zh)
            : time.ToString("yyyy-MM-dd", Zh);

    public static string DistanceCompact(double meters) =>
        meters >= 1000 ? $"{meters / 1000:0.0} km" : $"{meters:0} m";
}
