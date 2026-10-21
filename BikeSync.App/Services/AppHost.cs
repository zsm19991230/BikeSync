using System.Net;
using System.Net.Http;
using BikeSync.App.ViewModels;
using BikeSync.App.Views;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Providers;
using BikeSync.Core.Services;
using BikeSync.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BikeSync.App.Services;

public sealed class AppHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public AppHost()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<IAccountStore, JsonAccountStore>();
        services.AddSingleton<ISyncHistoryStore, SyncHistoryStore>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<StravaOAuthService>();
        services.AddSingleton<MyWhooshAuthService>();
        services.AddSingleton<IGpsportSigner>();
        services.AddSingleton<IGpsportAuthService>();
        services.AddSingleton<XingzheAuthService>();
        services.AddSingleton<ActivityCacheService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<AppSession>();

        services.AddSingleton<IPlatformProvider, StravaProvider>();
        services.AddSingleton<IPlatformProvider, IntervalsIcuProvider>();
        services.AddSingleton<IPlatformProvider, RideWithGpsProvider>();
        services.AddSingleton<IPlatformProvider, MyWhooshProvider>();
        services.AddSingleton<IPlatformProvider, IGpsportProvider>();
        services.AddSingleton<IPlatformProvider, XingzheProvider>();

        services.AddSingleton(_ =>
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(20),
            })
            {
                Timeout = TimeSpan.FromSeconds(100),
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BikeSync/1.1 (Windows; cycling data sync)");
            return client;
        });

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
        });

        services.AddSingleton<NotificationService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<AccountsViewModel>();
        services.AddSingleton<ActivityLibraryViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        _provider = services.BuildServiceProvider();
    }

    public IServiceProvider Services => _provider;

    public T GetRequiredService<T>() where T : notnull => _provider.GetRequiredService<T>();

    public void Dispose() => _provider.Dispose();
}
