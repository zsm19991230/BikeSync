using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using Microsoft.Extensions.Logging;

namespace BikeSync.Core.Services;

public sealed class SyncHistoryStore : ISyncHistoryStore
{
    private const int MaxEntries = 100;
    private readonly AppPaths _paths;
    private readonly ILogger<SyncHistoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SyncHistoryStore(AppPaths paths, ILogger<SyncHistoryStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SyncHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                return await JsonFile.ReadAsync<List<SyncHistoryEntry>>(_paths.HistoryFile, cancellationToken)
                    .ConfigureAwait(false) ?? new List<SyncHistoryEntry>();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
            {
                _logger.LogError(ex, "Could not load sync history.");
                return Array.Empty<SyncHistoryEntry>();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(SyncHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<SyncHistoryEntry> entries;
            try
            {
                entries = await JsonFile.ReadAsync<List<SyncHistoryEntry>>(_paths.HistoryFile, cancellationToken)
                    .ConfigureAwait(false) ?? new List<SyncHistoryEntry>();
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
            {
                _logger.LogError(ex, "Could not load sync history; starting a new history file.");
                entries = new List<SyncHistoryEntry>();
            }

            entries.Insert(0, entry);
            if (entries.Count > MaxEntries)
            {
                entries.RemoveRange(MaxEntries, entries.Count - MaxEntries);
            }

            _paths.EnsureCreated();
            await JsonFile.WriteAtomicAsync(_paths.HistoryFile, entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
