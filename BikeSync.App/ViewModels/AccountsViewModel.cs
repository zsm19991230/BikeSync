using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using BikeSync.App.Services;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BikeSync.App.ViewModels;

public sealed class AccountCardViewModel
{
    public required PlatformDescriptor Descriptor { get; init; }
    public PlatformAccount? Account { get; init; }
    public required bool StravaConfigured { get; init; }

    public bool IsConnected => Account is not null;
    public string Name => Descriptor.DisplayName;
    public string Tagline => Descriptor.Tagline;
    public string BrandColor => Descriptor.BrandColor;
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

    public string AccountName => Account?.DisplayName ?? "尚未连接";
    public string DotColor => IsConnected ? "#34D399" : "#66798E";
    public string StatusText => Account is null
        ? "未连接"
        : $"已连接 · {Account.AuthLabel}";
    public string StatusDetail => Account is null
        ? Descriptor.AuthSummary
        : $"账户 ID {Account.UserId} · 上次连接 {Account.ConnectedAt.LocalDateTime:MM-dd HH:mm}";
    public string PrimaryActionText => IsConnected ? "重新授权" : "连接账户";
    public string ConnectionDot => IsConnected ? "●" : "○";
    public bool ShowsConfigHint => Descriptor.Id == PlatformId.Strava && !StravaConfigured;
    public string ConfigHint => "需要先配置 Strava API 应用";
}

public sealed partial class AccountsViewModel : ObservableObject, IPageAware
{
    private readonly AppSession _session;
    private readonly ConnectionService _connections;
    private readonly NavigationService _navigation;
    private readonly NotificationService _notifications;

    public AccountsViewModel(
        AppSession session,
        ConnectionService connections,
        NavigationService navigation,
        NotificationService notifications)
    {
        _session = session;
        _connections = connections;
        _navigation = navigation;
        _notifications = notifications;
        _session.Changed += (_, _) => RebuildCards();
        RebuildCards();
    }

    public ObservableCollection<AccountCardViewModel> Cards { get; } = new();

    [ObservableProperty]
    private bool _isDialogOpen;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private AccountCardViewModel? _dialogCard;

    [ObservableProperty]
    private string _dialogError = string.Empty;

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private string _authToken = string.Empty;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isDisconnectOpen;

    [ObservableProperty]
    private AccountCardViewModel? _disconnectCard;

    public bool IsStravaDialog => DialogCard?.Descriptor.Id == PlatformId.Strava;
    public bool IsIntervalsDialog => DialogCard?.Descriptor.Id == PlatformId.IntervalsIcu;
    public bool IsRwgpsDialog => DialogCard?.Descriptor.Id == PlatformId.RideWithGps;
    public bool IsMyWhooshDialog => DialogCard?.Descriptor.Id == PlatformId.MyWhoosh;
    public bool IsIgpsportDialog => DialogCard?.Descriptor.Id == PlatformId.IGpsport;
    public bool IsXingzheDialog => DialogCard?.Descriptor.Id == PlatformId.Xingzhe;
    public bool IsApiCredentialFormVisible => IsIntervalsDialog || IsRwgpsDialog;
    public bool IsAccountPasswordFormVisible => IsMyWhooshDialog || IsIgpsportDialog || IsXingzheDialog;
    public string AccountIdentifierLabel => IsMyWhooshDialog
        ? "MyWhoosh 邮箱"
        : IsIgpsportDialog ? "iGPSPORT 手机号或邮箱" : "行者账号";
    public string AccountPasswordPlaceholder => IsMyWhooshDialog
        ? "输入 MyWhoosh 密码"
        : IsIgpsportDialog ? "输入 iGPSPORT 密码" : "输入行者密码";
    public bool DialogCanBeClosed => !IsConnecting;
    public string DialogTitle => DialogCard is null ? string.Empty : $"连接 {DialogCard.Name}";
    public string DialogDescription => DialogCard?.Descriptor.Tagline ?? string.Empty;
    public string DialogAuthHint => DialogCard?.Descriptor.AuthSummary ?? string.Empty;
    public string DialogPrimaryText => DialogCard?.Descriptor.Id switch
    {
        PlatformId.Strava => "打开浏览器授权",
        PlatformId.IntervalsIcu => "验证并连接",
        PlatformId.RideWithGps => "验证并连接",
        PlatformId.MyWhoosh => "登录并连接",
        PlatformId.IGpsport => "登录并连接",
        PlatformId.Xingzhe => "登录并连接",
        _ => "连接",
    };

    public string ConnectedCountText => _session.AccountCount == 0
        ? "还没有连接任何平台"
        : $"已连接 {_session.AccountCount} 个账户 · 覆盖 {_session.ConnectedPlatformCount} 个平台";

    partial void OnDialogCardChanged(AccountCardViewModel? value)
    {
        OnPropertyChanged(nameof(IsStravaDialog));
        OnPropertyChanged(nameof(IsIntervalsDialog));
        OnPropertyChanged(nameof(IsRwgpsDialog));
        OnPropertyChanged(nameof(IsMyWhooshDialog));
        OnPropertyChanged(nameof(IsIgpsportDialog));
        OnPropertyChanged(nameof(IsXingzheDialog));
        OnPropertyChanged(nameof(IsApiCredentialFormVisible));
        OnPropertyChanged(nameof(IsAccountPasswordFormVisible));
        OnPropertyChanged(nameof(AccountIdentifierLabel));
        OnPropertyChanged(nameof(AccountPasswordPlaceholder));
        OnPropertyChanged(nameof(DialogTitle));
        OnPropertyChanged(nameof(DialogDescription));
        OnPropertyChanged(nameof(DialogAuthHint));
        OnPropertyChanged(nameof(DialogPrimaryText));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(DialogCanBeClosed));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public Task OnNavigatedToAsync()
    {
        _session.RefreshAccounts();
        RebuildCards();
        return Task.CompletedTask;
    }

    private void RebuildCards()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(RebuildCards);
            return;
        }

        Cards.Clear();
        var stravaConfigured = _session.Settings.Current.Strava.IsConfigured;
        foreach (var descriptor in PlatformCatalog.All)
        {
            var account = _session.Accounts
                .Where(a => a.Platform == descriptor.Id)
                .OrderByDescending(a => a.ConnectedAt)
                .FirstOrDefault();
            Cards.Add(new AccountCardViewModel
            {
                Descriptor = descriptor,
                Account = account,
                StravaConfigured = stravaConfigured,
            });
        }

        OnPropertyChanged(nameof(ConnectedCountText));
    }

    [RelayCommand]
    private void OpenConnect(AccountCardViewModel? card)
    {
        if (card is null || IsConnecting)
        {
            return;
        }

        if (card.Descriptor.Id == PlatformId.Strava && !card.StravaConfigured)
        {
            _notifications.Warning("请先在「设置」中填写 Strava Client ID 和 Client Secret。");
            _navigation.Navigate(AppPage.Settings);
            return;
        }

        DialogCard = card;
        DialogError = string.Empty;
        ApiKey = string.Empty;
        AuthToken = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        IsDialogOpen = true;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (DialogCard is null)
        {
            return;
        }

        DialogError = string.Empty;
        if (IsIntervalsDialog && string.IsNullOrWhiteSpace(ApiKey))
        {
            DialogError = "请输入 intervals.icu 的 API Key。";
            return;
        }

        if (IsRwgpsDialog &&
            (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(AuthToken)))
        {
            DialogError = "请输入 Ride with GPS 的 API Key 与 Auth Token。";
            return;
        }

        if (IsAccountPasswordFormVisible &&
            (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password)))
        {
            DialogError = IsMyWhooshDialog
                ? "请输入 MyWhoosh 邮箱和密码。"
                : IsIgpsportDialog
                    ? "请输入 iGPSPORT 手机号/邮箱和密码。"
                    : "请输入行者账号和密码。";
            return;
        }

        IsConnecting = true;
        try
        {
            PlatformAccount account = DialogCard.Descriptor.Id switch
            {
                PlatformId.Strava => await _connections.ConnectStravaAsync(CancellationToken.None),
                PlatformId.IntervalsIcu => await _connections.ConnectIntervalsIcuAsync(
                    ApiKey,
                    CancellationToken.None),
                PlatformId.RideWithGps => await _connections.ConnectRideWithGpsAsync(
                    ApiKey,
                    AuthToken,
                    CancellationToken.None),
                PlatformId.MyWhoosh => await _connections.ConnectMyWhooshAsync(
                    Username,
                    Password,
                    DialogCard.Account?.DeviceId,
                    CancellationToken.None),
                PlatformId.IGpsport => await _connections.ConnectIGpsportAsync(
                    Username,
                    Password,
                    CancellationToken.None),
                PlatformId.Xingzhe => await _connections.ConnectXingzheAsync(
                    Username,
                    Password,
                    CancellationToken.None),
                _ => throw new NotSupportedException(),
            };

            IsDialogOpen = false;
            DialogCard = null;
            ApiKey = string.Empty;
            AuthToken = string.Empty;
            Username = string.Empty;
            Password = string.Empty;
            _session.RefreshAccounts();
            RebuildCards();
            _notifications.Success($"已连接 {account.DisplayName}");
        }
        catch (PlatformApiException ex)
        {
            DialogError = ex.Message;
        }
        catch (PlatformConfigurationException ex)
        {
            DialogError = ex.Message;
        }
        catch (OperationCanceledException)
        {
            DialogError = "连接已取消。";
        }
        catch (UnauthorizedAccessException)
        {
            DialogError =
                "无法写入本地账户文件。请确认当前 Windows 用户对数据目录有写权限，" +
                "或在启动前设置 BIKESYNC_DATA_DIR 指向可写目录后重试。";
        }
        catch (IOException)
        {
            DialogError =
                "无法保存本地账户文件，文件可能被其他程序占用。" +
                "请关闭重复打开的 BikeSync 进程后重试。";
        }
        catch (Exception ex)
        {
            DialogError = "连接失败：" + ex.Message;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanConnect() => !IsConnecting;

    [RelayCommand]
    private void CancelDialog()
    {
        if (IsConnecting)
        {
            return;
        }

        IsDialogOpen = false;
        DialogCard = null;
        ApiKey = string.Empty;
        AuthToken = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        DialogError = string.Empty;
    }

    [RelayCommand]
    private void RequestDisconnect(AccountCardViewModel? card)
    {
        if (card?.Account is null)
        {
            return;
        }

        DisconnectCard = card;
        IsDisconnectOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDisconnectAsync()
    {
        var account = DisconnectCard?.Account;
        if (account is null)
        {
            return;
        }

        try
        {
            await _connections.DisconnectAsync(account.Id, CancellationToken.None);

            IsDisconnectOpen = false;
            DisconnectCard = null;
            _session.RefreshAccounts();
            RebuildCards();
            _notifications.Success("已断开连接，本地令牌已删除。");
        }
        catch (Exception ex)
        {
            _notifications.Error("断开连接失败：" + ex.Message);
        }
    }

    [RelayCommand]
    private void CancelDisconnect()
    {
        IsDisconnectOpen = false;
        DisconnectCard = null;
    }

    [RelayCommand]
    private void OpenSetup(AccountCardViewModel? card)
    {
        var url = card?.Descriptor.SetupUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            _notifications.Error("无法打开浏览器，请手动访问平台开发者设置。");
        }
    }

    [RelayCommand]
    private void OpenAccountsDoc(AccountCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        var url = card.Descriptor.Id switch
        {
            PlatformId.Strava => "https://developers.strava.com/docs/getting-started/",
            PlatformId.IntervalsIcu => "https://forum.intervals.icu/t/api-access-to-intervals-icu/609",
            PlatformId.RideWithGps => "https://ridewithgps.com/api/v1/doc/authentication",
            PlatformId.MyWhoosh => "https://www.mywhoosh.com",
            PlatformId.IGpsport => "https://app.igpsport.cn",
            PlatformId.Xingzhe => "https://www.imxingzhe.com",
            _ => card.Descriptor.SetupUrl,
        };

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            _notifications.Error("无法打开浏览器。");
        }
    }
}
