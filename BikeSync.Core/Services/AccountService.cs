using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

public sealed class AccountService
{
    private readonly IAccountStore _store;
    private readonly ILogger<AccountService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<PlatformAccount> _accounts = new();

    public AccountService(IAccountStore store, ILogger<AccountService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public event EventHandler? AccountsChanged;

    public IReadOnlyList<PlatformAccount> Accounts => _accounts
        .OrderBy(a => a.Platform)
        .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool IsInitialized { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        _accounts = loaded.ToList();
        IsInitialized = true;
        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public PlatformAccount? Find(Guid id) => _accounts.FirstOrDefault(a => a.Id == id);

    public PlatformAccount? Find(PlatformId platform, string userId) =>
        _accounts.FirstOrDefault(a =>
            a.Platform == platform &&
            string.Equals(a.UserId, userId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the account or replaces the same platform/user pair.</summary>
    public async Task<PlatformAccount> UpsertAsync(
        PlatformAccount account,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingIndex = _accounts.FindIndex(a =>
                a.Id == account.Id ||
                (a.Platform == account.Platform &&
                 !string.IsNullOrWhiteSpace(a.UserId) &&
                 string.Equals(a.UserId, account.UserId, StringComparison.OrdinalIgnoreCase)));

            if (existingIndex >= 0)
            {
                account = account with { Id = _accounts[existingIndex].Id };
                _accounts[existingIndex] = account;
            }
            else
            {
                _accounts.Add(account);
            }

            await _store.SaveAsync(_accounts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Saved account {Platform}/{UserId}.", account.Platform, account.UserId);
        AccountsChanged?.Invoke(this, EventArgs.Empty);
        return account;
    }

    public async Task UpdateAsync(PlatformAccount account, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _accounts.FindIndex(a => a.Id == account.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("Account no longer exists.");
            }

            _accounts[index] = account;
            await _store.SaveAsync(_accounts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _accounts.RemoveAll(a => a.Id == accountId);
            await _store.SaveAsync(_accounts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        AccountsChanged?.Invoke(this, EventArgs.Empty);
    }
}
