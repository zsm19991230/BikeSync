using System.Collections.ObjectModel;
using System.Windows;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Services;

namespace BikeSync.App.Services;

public sealed record AccountOption(
    Guid Id,
    PlatformId Platform,
    string DisplayName,
    string PlatformName,
    string AuthLabel,
    string BrandColor)
{
    public string Initial => PlatformCatalog.Initial(Platform);

    public string Label => $"{DisplayName} · {PlatformName}";
}

public sealed class AppSession
{
    private readonly AccountService _accounts;
    private readonly ISyncHistoryStore _historyStore;
    private readonly IProviderRegistry _providers;

    public AppSession(
        AccountService accounts,
        AppSettingsService settings,
        SyncService syncService,
        ISyncHistoryStore historyStore,
        IProviderRegistry providers)
    {
        _accounts = accounts;
        _historyStore = historyStore;
        _providers = providers;
        Settings = settings;
        SyncService = syncService;
        _accounts.AccountsChanged += (_, _) => RefreshAccounts();
    }

    public event EventHandler? Changed;

    public AppSettingsService Settings { get; }
    public SyncService SyncService { get; }
    public ObservableCollection<PlatformAccount> Accounts { get; } = new();
    public ObservableCollection<SyncHistoryEntry> History { get; } = new();
    public ObservableCollection<ActivitySummary> SelectedActivities { get; } = new();

    public PlatformAccount? SyncSource { get; private set; }
    public int ConnectedPlatformCount => Accounts.Select(a => a.Platform).Distinct().Count();
    public int AccountCount => Accounts.Count;
    public bool HasAccounts => Accounts.Count > 0;

    public IReadOnlyList<AccountOption> AccountOptions => Accounts
        .Select(a => new AccountOption(
            a.Id,
            a.Platform,
            a.DisplayName,
            a.Descriptor.DisplayName,
            a.AuthLabel,
            a.Descriptor.BrandColor))
        .ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshAccounts();
        await ReloadHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    public void RefreshAccounts()
    {
        void Apply()
        {
            var selectedSyncSource = SyncSource?.Id;
            Accounts.Clear();
            foreach (var account in _accounts.Accounts)
            {
                Accounts.Add(account);
            }

            SyncSource = selectedSyncSource is { } id
                ? Accounts.FirstOrDefault(a => a.Id == id)
                : null;

            Changed?.Invoke(this, EventArgs.Empty);
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

    public async Task ReloadHistoryAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _historyStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        void Apply()
        {
            History.Clear();
            foreach (var entry in entries.OrderByDescending(e => e.Timestamp).Take(20))
            {
                History.Add(entry);
            }

            Changed?.Invoke(this, EventArgs.Empty);
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

    public PlatformAccount? FindAccount(Guid id) => Accounts.FirstOrDefault(a => a.Id == id);

    public IPlatformProvider Provider(PlatformId platform) => _providers.Get(platform);

    public void SetSyncSelection(PlatformAccount source, IReadOnlyList<ActivitySummary> activities)
    {
        SyncSource = Accounts.FirstOrDefault(a => a.Id == source.Id) ?? source;
        SelectedActivities.Clear();
        foreach (var activity in activities)
        {
            SelectedActivities.Add(activity);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSyncSelection()
    {
        SyncSource = null;
        SelectedActivities.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
