using BikeSync.Core.Models;

namespace BikeSync.App.Services;

public enum AppPage
{
    Dashboard,
    Accounts,
    Activities,
    Sync,
    Settings,
}

public sealed record NavigationRequest(
    AppPage Page,
    PlatformAccount? SourceAccount = null,
    IReadOnlyList<ActivitySummary>? Activities = null);

public sealed class NavigationService
{
    public event EventHandler<NavigationRequest>? NavigationRequested;

    public void Navigate(AppPage page) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(page));

    public void OpenSync(PlatformAccount source, IReadOnlyList<ActivitySummary> activities) =>
        NavigationRequested?.Invoke(this, new NavigationRequest(AppPage.Sync, source, activities));
}

public interface IPageAware
{
    Task OnNavigatedToAsync();
}
