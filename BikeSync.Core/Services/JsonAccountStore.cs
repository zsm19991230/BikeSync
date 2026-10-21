using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

public sealed class JsonAccountStore : IAccountStore
{
    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly ILogger<JsonAccountStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonAccountStore(
        AppPaths paths,
        ISecretProtector protector,
        ILogger<JsonAccountStore> logger)
    {
        _paths = paths;
        _protector = protector;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PlatformAccount>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AccountFile? file;
            try
            {
                file = await JsonFile.ReadAsync<AccountFile>(_paths.AccountsFile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                JsonFile.ArchiveCorrupt(_paths.AccountsFile);
                _logger.LogError(ex, "Account store is corrupt and was archived.");
                return Array.Empty<PlatformAccount>();
            }

            if (file?.Accounts is null)
            {
                return Array.Empty<PlatformAccount>();
            }

            var accounts = new List<PlatformAccount>(file.Accounts.Count);
            foreach (var stored in file.Accounts)
            {
                if (!Enum.TryParse<PlatformId>(stored.Platform, ignoreCase: true, out var platform))
                {
                    _logger.LogWarning("Skipping account with unknown platform {Platform}.", stored.Platform);
                    continue;
                }

                try
                {
                    accounts.Add(new PlatformAccount
                    {
                        Id = stored.Id == Guid.Empty ? Guid.NewGuid() : stored.Id,
                        Platform = platform,
                        UserId = stored.UserId ?? string.Empty,
                        DisplayName = string.IsNullOrWhiteSpace(stored.DisplayName)
                            ? PlatformCatalog.Get(platform).DisplayName
                            : stored.DisplayName,
                        AccessToken = _protector.Unprotect(stored.ProtectedAccessToken ?? string.Empty),
                        RefreshToken = _protector.Unprotect(stored.ProtectedRefreshToken ?? string.Empty),
                        ApiKey = _protector.Unprotect(stored.ProtectedApiKey ?? string.Empty),
                        AuthToken = _protector.Unprotect(stored.ProtectedAuthToken ?? string.Empty),
                        DeviceId = stored.DeviceId,
                        AccessTokenExpiresAt = stored.AccessTokenExpiresAt,
                        Scope = stored.Scope,
                        ConnectedAt = stored.ConnectedAt,
                    });
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogError(ex, "Skipping account {AccountId} whose secrets could not be decrypted.", stored.Id);
                }
            }

            return accounts;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<PlatformAccount> accounts, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = new AccountFile
            {
                Version = 1,
                Accounts = accounts.Select(account => new StoredAccount
                {
                    Id = account.Id,
                    Platform = account.Platform.ToString(),
                    UserId = account.UserId,
                    DisplayName = account.DisplayName,
                    ProtectedAccessToken = _protector.Protect(account.AccessToken ?? string.Empty),
                    ProtectedRefreshToken = _protector.Protect(account.RefreshToken ?? string.Empty),
                    ProtectedApiKey = _protector.Protect(account.ApiKey ?? string.Empty),
                    ProtectedAuthToken = _protector.Protect(account.AuthToken ?? string.Empty),
                    DeviceId = account.DeviceId,
                    AccessTokenExpiresAt = account.AccessTokenExpiresAt,
                    Scope = account.Scope,
                    ConnectedAt = account.ConnectedAt,
                }).ToList(),
            };

            _paths.EnsureCreated();
            await JsonFile.WriteAtomicAsync(_paths.AccountsFile, file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class AccountFile
    {
        public int Version { get; set; }
        public List<StoredAccount> Accounts { get; set; } = new();
    }

    private sealed class StoredAccount
    {
        public Guid Id { get; set; }
        public string? Platform { get; set; }
        public string? UserId { get; set; }
        public string? DisplayName { get; set; }
        public string? ProtectedAccessToken { get; set; }
        public string? ProtectedRefreshToken { get; set; }
        public string? ProtectedApiKey { get; set; }
        public string? ProtectedAuthToken { get; set; }
        public string? DeviceId { get; set; }
        public DateTimeOffset? AccessTokenExpiresAt { get; set; }
        public string? Scope { get; set; }
        public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.Now;
    }
}
