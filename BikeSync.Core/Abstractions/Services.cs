using BikeSync.Core.Models;

namespace BikeSync.Core.Abstractions;

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public interface IAccountStore
{
    Task<IReadOnlyList<PlatformAccount>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyList<PlatformAccount> accounts, CancellationToken cancellationToken = default);
}

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(string defaultDownloadDirectory, CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IPlatformProvider
{
    PlatformId Platform { get; }

    Task<AccountProfile> GetProfileAsync(PlatformAccount account, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken);

    Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken);

    Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken);
}

public interface IProviderRegistry
{
    IPlatformProvider Get(PlatformId platform);
}

public interface ISyncHistoryStore
{
    Task<IReadOnlyList<SyncHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(SyncHistoryEntry entry, CancellationToken cancellationToken = default);
}

public sealed class PlatformApiException : Exception
{
    public PlatformApiException(
        PlatformId platform,
        int statusCode,
        string message,
        string? detail = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Platform = platform;
        StatusCode = statusCode;
        Detail = detail;
    }

    public PlatformId Platform { get; }
    public int StatusCode { get; }
    public string? Detail { get; }
}

public sealed class PlatformConfigurationException : Exception
{
    public PlatformConfigurationException(string message) : base(message)
    {
    }
}
