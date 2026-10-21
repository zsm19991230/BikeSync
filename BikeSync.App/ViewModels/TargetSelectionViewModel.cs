using BikeSync.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BikeSync.App.ViewModels;

public sealed partial class TargetSelectionViewModel : ObservableObject
{
    public required AccountOption Option { get; init; }
    public Action? SelectionChanged { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public string Label => Option.Label;
    public string BrandColor => Option.BrandColor;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
