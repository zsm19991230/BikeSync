using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using BikeSync.App.Services;
using BikeSync.App.ViewModels;
using BikeSync.App.Views;
using BikeSync.Core.Services;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BikeSync.App;

public partial class App : Application
{
    private AppHost? _host;
    private ILogger<App>? _logger;
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\BikeSync.App.SingleInstance",
            createdNew: out var createdNew);
        _ownsSingleInstanceMutex = createdNew;

        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "骑迹同步已经在运行。请切换到已打开的窗口。",
                "骑迹同步",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, false);
            ApplicationAccentColorManager.Apply(
                Color.FromRgb(0xFF, 0x6B, 0x2C),
                ApplicationTheme.Dark,
                false,
                false);

            _host = new AppHost();
            _logger = _host.GetRequiredService<ILogger<App>>();

            var paths = _host.GetRequiredService<AppPaths>();
            paths.EnsureCreated();

            var settings = _host.GetRequiredService<AppSettingsService>();
            await settings.LoadAsync();

            var accounts = _host.GetRequiredService<AccountService>();
            await accounts.InitializeAsync();

            var session = _host.GetRequiredService<AppSession>();
            await session.InitializeAsync();

            var window = _host.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            _logger.LogInformation("BikeSync started.");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Application startup failed.");
            System.Windows.MessageBox.Show(
                "骑迹同步启动失败：\n\n" + ex.Message +
                "\n\n请检查 %LocalAppData%\\BikeSync\\logs 中的日志。",
                "启动失败",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception.");
        System.Windows.MessageBox.Show(
            "出现了一个未处理的问题：\n\n" + e.Exception.Message +
            "\n\n应用会继续运行，详细信息已写入本地日志。",
            "骑迹同步",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("BikeSync exiting.");
        _host?.Dispose();
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
