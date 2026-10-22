using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using BikeSync.Core.Services;

namespace BikeSync.App.Services;

public enum MapTileSource
{
    Amap,
    OpenStreetMap,
}

/// <summary>
/// Loads raster map tiles with Amap as the primary source (reachable in mainland
/// China) and OpenStreetMap as an international fallback. Tiles are cached in
/// memory and on disk because activity maps rarely change.
/// </summary>
public sealed class MapTileService
{
    private const string AmapUrl =
        "https://webrd0{0}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={1}&y={2}&z={3}";
    private const string OpenStreetMapUrl = "https://tile.openstreetmap.org/{0}/{1}/{2}.png";

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ConcurrentDictionary<string, BitmapSource> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sourceGate = new(1, 1);
    private MapTileSource? _source;

    public MapTileService(HttpClient http, AppPaths paths)
    {
        _http = http;
        _paths = paths;
    }

    /// <summary>Probes once and keeps the first provider that can serve a tile.</summary>
    public async Task<MapTileSource> ResolveSourceAsync(
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        if (_source is { } known)
        {
            return known;
        }

        await _sourceGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (_source is { } resolved)
            {
                return resolved;
            }

            if (await GetTileAsync(MapTileSource.Amap, zoom, x, y, cancellationToken)
                    .ConfigureAwait(true) is not null)
            {
                _source = MapTileSource.Amap;
                return MapTileSource.Amap;
            }

            if (await GetTileAsync(MapTileSource.OpenStreetMap, zoom, x, y, cancellationToken)
                    .ConfigureAwait(true) is not null)
            {
                _source = MapTileSource.OpenStreetMap;
                return MapTileSource.OpenStreetMap;
            }

            // Prefer Amap so the next attempt keeps using the China-friendly source.
            _source = MapTileSource.Amap;
            return MapTileSource.Amap;
        }
        finally
        {
            _sourceGate.Release();
        }
    }

    public async Task<BitmapSource?> GetTileAsync(
        MapTileSource source,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(source, zoom, x, y);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var image = await ReadFromDiskAsync(source, zoom, x, y, cancellationToken)
            .ConfigureAwait(true);
        if (image is null)
        {
            var bytes = await DownloadAsync(source, zoom, x, y, cancellationToken)
                .ConfigureAwait(true);
            if (bytes is not null)
            {
                image = CreateBitmap(bytes);
                if (image is not null)
                {
                    await SaveToDiskAsync(source, zoom, x, y, bytes, cancellationToken)
                        .ConfigureAwait(true);
                }
            }
        }

        if (image is not null)
        {
            _cache[key] = image;
        }

        return image;
    }

    private async Task<byte[]?> DownloadAsync(
        MapTileSource source,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = source == MapTileSource.Amap
                ? string.Format(AmapUrl, (x + y) % 4 + 1, x, y, zoom)
                : string.Format(OpenStreetMapUrl, zoom, x, y);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "BikeSync/1.2 (+https://github.com/zsm19991230/BikeSync)");

            using var response = await _http.SendAsync(request, cancellationToken)
                .ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(true);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<BitmapSource?> ReadFromDiskAsync(
        MapTileSource source,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        var path = DiskPath(source, zoom, x, y);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
            return bytes.Length == 0 ? null : CreateBitmap(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task SaveToDiskAsync(
        MapTileSource source,
        int zoom,
        int x,
        int y,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = DiskPath(source, zoom, x, y);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The on-screen tile already loaded; disk caching is best-effort.
        }
    }

    private string DiskPath(MapTileSource source, int zoom, int x, int y) =>
        Path.Combine(
            _paths.MapTileCacheDirectory,
            source == MapTileSource.Amap ? "amap" : "osm",
            zoom.ToString(),
            x.ToString(),
            y + ".png");

    private static string CacheKey(MapTileSource source, int zoom, int x, int y) =>
        $"{source}/{zoom}/{x}/{y}";

    private static BitmapSource? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
