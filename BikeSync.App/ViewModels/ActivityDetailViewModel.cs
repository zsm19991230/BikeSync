using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BikeSync.App.Services;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;
using BikeSync.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BikeSync.App.ViewModels;

public sealed record MapTileViewModel(BitmapSource Image, double Left, double Top);

public sealed record DetailMetricViewModel(string Label, string Value, bool Highlight = false)
{
    public Brush ValueBrush => Highlight
        ? Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.Orange
        : Application.Current?.TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
}

/// <summary>Backs the activity detail overlay: header, map, elevation profile and metrics.</summary>
public sealed partial class ActivityDetailViewModel : ObservableObject
{
    private const double MapWidth = 640;
    private const double MapHeight = 300;
    private const double MapPadding = 34;

    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");

    private readonly ActivityDetailService _detailService;
    private readonly MapTileService _mapTiles;

    public ActivityDetailViewModel(ActivityDetailService detailService, MapTileService mapTiles)
    {
        _detailService = detailService;
        _mapTiles = mapTiles;
    }

    public ObservableCollection<DetailMetricViewModel> Metrics { get; } = new();
    public ObservableCollection<MapTileViewModel> MapTiles { get; } = new();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _subtitle = string.Empty;

    [ObservableProperty]
    private string _platformName = string.Empty;

    [ObservableProperty]
    private string _platformInitial = "?";

    [ObservableProperty]
    private string _platformColor = "#4C8DFF";

    [ObservableProperty]
    private string _sourceFileText = string.Empty;

    [ObservableProperty]
    private string _mapHint = string.Empty;

    [ObservableProperty]
    private string _tileHint = string.Empty;

    [ObservableProperty]
    private string _mapAttribution = string.Empty;

    [ObservableProperty]
    private bool _hasRoute;

    [ObservableProperty]
    private bool _hasMapTiles;

    [ObservableProperty]
    private bool _hasElevation;

    [ObservableProperty]
    private string _elevationCaption = string.Empty;

    [ObservableProperty]
    private PointCollection _routePoints = new();

    [ObservableProperty]
    private PointCollection _elevationPoints = new();

    [ObservableProperty]
    private double _startLeft;

    [ObservableProperty]
    private double _startTop;

    [ObservableProperty]
    private double _endLeft;

    [ObservableProperty]
    private double _endTop;

    [ObservableProperty]
    private string? _webUrl;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    public async Task LoadAsync(
        PlatformAccount account,
        ActivitySummary summary,
        CancellationToken cancellationToken)
    {
        ApplySummary(summary);
        BuildMetrics(summary, null);
        IsLoading = true;
        ErrorMessage = string.Empty;

        try
        {
            ActivityDetail detail;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                detail = await _detailService.LoadAsync(account, summary, timeout.Token)
                    .ConfigureAwait(true);
            }

            SourceFileText =
                $"{ActivityFile.SafeFileName(summary.Name, "activity")}.{detail.File.Format.Extension()}";
            BuildMetrics(summary, detail.Track);
            BuildElevation(detail.Track);
            IsLoading = false;
            await BuildMapAsync(summary, detail.Track.Points, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "读取活动文件超时，地图暂时无法显示。";
            MapHint = "轨迹加载超时";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex switch
            {
                PlatformApiException api => api.Message,
                PlatformConfigurationException configuration => configuration.Message,
                _ => "无法读取该活动的轨迹文件：" + ex.Message,
            };
            MapHint = "轨迹不可用";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void ShowError(string message)
    {
        ErrorMessage = message;
        IsLoading = false;
    }

    private void ApplySummary(ActivitySummary summary)
    {
        Title = summary.Name;
        Subtitle =
            $"{summary.DisplayStartTime.ToString("yyyy年M月d日 HH:mm", Zh)} · {summary.SportType} · {summary.AccountName}";
        var descriptor = PlatformCatalog.Get(summary.Platform);
        PlatformName = descriptor.DisplayName;
        PlatformInitial = PlatformCatalog.Initial(summary.Platform);
        PlatformColor = descriptor.BrandColor;
        WebUrl = summary.WebUrl;
    }

    private void BuildMetrics(ActivitySummary summary, ActivityTrackData? track)
    {
        var movingSeconds = summary.MovingTime.TotalSeconds;
        var averageSpeed = movingSeconds > 0
            ? summary.DistanceMeters / movingSeconds
            : track?.AverageSpeedMetersPerSecond;

        Metrics.Clear();
        Metrics.Add(new DetailMetricViewModel("距离", DisplayFormat.Distance(summary.DistanceMeters)));
        Metrics.Add(new DetailMetricViewModel("爬升", DisplayFormat.Elevation(
            track?.AscentMeters ?? summary.ElevationGainMeters)));
        Metrics.Add(new DetailMetricViewModel("移动时间", DisplayFormat.Duration(summary.MovingTime)));
        Metrics.Add(new DetailMetricViewModel("总时长", DisplayFormat.Duration(summary.ElapsedTime)));
        Metrics.Add(new DetailMetricViewModel("平均速度", FormatSpeed(averageSpeed)));
        Metrics.Add(new DetailMetricViewModel("最高速度", FormatSpeed(
            track?.MaxSpeedMetersPerSecond)));
        Metrics.Add(new DetailMetricViewModel(
            "均功率",
            (track?.AveragePowerWatts ?? summary.AveragePowerWatts) is { } power
                ? $"{power:0} W"
                : "—",
            Highlight: true));
        Metrics.Add(new DetailMetricViewModel(
            "最大功率",
            track?.MaxPowerWatts is { } maxPower ? $"{maxPower:0} W" : "—"));
        Metrics.Add(new DetailMetricViewModel(
            "平均心率",
            (track?.AverageHeartRate ?? summary.AverageHeartRate) is { } heartRate
                ? $"{heartRate} bpm"
                : "—"));
        Metrics.Add(new DetailMetricViewModel(
            "最大心率",
            track?.MaxHeartRate is { } maxHeartRate ? $"{maxHeartRate} bpm" : "—"));
        Metrics.Add(new DetailMetricViewModel(
            "平均踏频",
            track?.AverageCadence is { } cadence ? $"{cadence} rpm" : "—"));
        Metrics.Add(new DetailMetricViewModel(
            "轨迹采样",
            track is null ? "—" : $"{track.Points.Count} 个 · {track.GpsPointCount} 个 GPS 点"));
    }

    private void BuildElevation(ActivityTrackData track)
    {
        var altitudes = track.Points
            .Where(p => p.AltitudeMeters is not null)
            .Select(p => p.AltitudeMeters!.Value)
            .Where(value => double.IsFinite(value))
            .ToArray();
        if (altitudes.Length < 2)
        {
            HasElevation = false;
            ElevationPoints = new PointCollection();
            return;
        }

        var samples = Downsample(altitudes, 640);
        var min = samples.Min();
        var max = samples.Max();
        if (max - min < 5)
        {
            var middle = (min + max) / 2;
            min = middle - 2.5;
            max = middle + 2.5;
        }

        var points = new PointCollection(samples.Length);
        var range = Math.Max(1, max - min);
        for (var index = 0; index < samples.Length; index++)
        {
            var x = samples.Length == 1 ? 0 : index * 640d / (samples.Length - 1);
            var y = 84 - (samples[index] - min) / range * 72;
            points.Add(new Point(x, y));
        }

        ElevationPoints = points;
        ElevationCaption = $"{min:0} m – {max:0} m";
        HasElevation = true;
    }

    private async Task BuildMapAsync(
        ActivitySummary summary,
        IReadOnlyList<ActivityTrackPoint> points,
        CancellationToken cancellationToken)
    {
        MapTiles.Clear();
        HasMapTiles = false;
        HasRoute = false;
        TileHint = string.Empty;
        RoutePoints = new PointCollection();

        var geo = points
            .Where(p => p.Latitude is not null && p.Longitude is not null)
            .Select(p => (Latitude: p.Latitude!.Value, Longitude: p.Longitude!.Value))
            .Where(p => p.Latitude is >= -85 and <= 85)
            .ToArray();
        if (geo.Length < 2)
        {
            MapHint = summary.HasGps
                ? "该活动没有可用的 GPS 轨迹点"
                : "室内骑行，没有 GPS 轨迹";
            return;
        }

        var minLat = geo.Min(p => p.Latitude);
        var maxLat = geo.Max(p => p.Latitude);
        var minLon = geo.Min(p => p.Longitude);
        var maxLon = geo.Max(p => p.Longitude);

        var zoom = 18;
        while (zoom > 2)
        {
            var topLeft = Project(maxLat, minLon, zoom);
            var bottomRight = Project(minLat, maxLon, zoom);
            if (Math.Abs(bottomRight.X - topLeft.X) <= MapWidth - MapPadding * 2 &&
                Math.Abs(bottomRight.Y - topLeft.Y) <= MapHeight - MapPadding * 2)
            {
                break;
            }

            zoom--;
        }

        var center = Project((minLat + maxLat) / 2, (minLon + maxLon) / 2, zoom);
        var viewLeft = center.X - MapWidth / 2;
        var viewTop = center.Y - MapHeight / 2;

        // Draw the route immediately; map tiles are added once the provider is known.
        ApplyRoute(geo, zoom, viewLeft, viewTop);

        var probeMaxTile = (1 << zoom) - 1;
        MapTileSource source;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(TimeSpan.FromSeconds(6));
            source = await _mapTiles.ResolveSourceAsync(
                    zoom,
                    Math.Clamp((int)Math.Floor(center.X / 256d), 0, probeMaxTile),
                    Math.Clamp((int)Math.Floor(center.Y / 256d), 0, probeMaxTile),
                    probe.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            source = MapTileSource.Amap;
        }

        MapAttribution = source == MapTileSource.Amap
            ? "© 高德地图"
            : "© OpenStreetMap contributors";

        if (source == MapTileSource.Amap)
        {
            // Amap raster tiles use GCJ-02, while activity files store WGS-84.
            geo = geo
                .Select(point => Wgs84ToGcj02(point.Latitude, point.Longitude))
                .ToArray();
            minLat = geo.Min(p => p.Latitude);
            maxLat = geo.Max(p => p.Latitude);
            minLon = geo.Min(p => p.Longitude);
            maxLon = geo.Max(p => p.Longitude);
            center = Project((minLat + maxLat) / 2, (minLon + maxLon) / 2, zoom);
            viewLeft = center.X - MapWidth / 2;
            viewTop = center.Y - MapHeight / 2;
            ApplyRoute(geo, zoom, viewLeft, viewTop);
        }

        var maxTile = (1 << zoom) - 1;
        var xMin = Math.Clamp((int)Math.Floor(viewLeft / 256d), 0, maxTile);
        var xMax = Math.Clamp((int)Math.Floor((viewLeft + MapWidth) / 256d), 0, maxTile);
        var yMin = Math.Clamp((int)Math.Floor(viewTop / 256d), 0, maxTile);
        var yMax = Math.Clamp((int)Math.Floor((viewTop + MapHeight) / 256d), 0, maxTile);

        var requests = new List<Task<(int X, int Y, BitmapSource? Image)>>();
        for (var x = xMin; x <= xMax; x++)
        {
            for (var y = yMin; y <= yMax; y++)
            {
                requests.Add(LoadTileAsync(source, zoom, x, y, cancellationToken));
            }
        }

        var tiles = await Task.WhenAll(requests).ConfigureAwait(true);
        foreach (var tile in tiles)
        {
            if (tile.Image is null)
            {
                continue;
            }

            MapTiles.Add(new MapTileViewModel(
                tile.Image,
                tile.X * 256d - viewLeft,
                tile.Y * 256d - viewTop));
        }

        HasMapTiles = MapTiles.Count > 0;
        if (!HasMapTiles)
        {
            TileHint = "地图瓦片暂不可用，仅显示轨迹";
        }
    }

    private void ApplyRoute(
        (double Latitude, double Longitude)[] geo,
        int zoom,
        double viewLeft,
        double viewTop)
    {
        var route = new PointCollection();
        var step = Math.Max(1, geo.Length / 2500);
        for (var index = 0; index < geo.Length; index += step)
        {
            var projected = Project(geo[index].Latitude, geo[index].Longitude, zoom);
            route.Add(new Point(projected.X - viewLeft, projected.Y - viewTop));
        }

        var last = Project(geo[^1].Latitude, geo[^1].Longitude, zoom);
        if (route.Count == 0 ||
            Math.Abs(route[^1].X - (last.X - viewLeft)) > 0.5 ||
            Math.Abs(route[^1].Y - (last.Y - viewTop)) > 0.5)
        {
            route.Add(new Point(last.X - viewLeft, last.Y - viewTop));
        }

        RoutePoints = route;
        HasRoute = true;
        MapHint = string.Empty;

        var start = Project(geo[0].Latitude, geo[0].Longitude, zoom);
        var end = Project(geo[^1].Latitude, geo[^1].Longitude, zoom);
        (StartLeft, StartTop) = MarkerPosition(start.X - viewLeft, start.Y - viewTop);
        (EndLeft, EndTop) = MarkerPosition(end.X - viewLeft, end.Y - viewTop);
    }

    private async Task<(int X, int Y, BitmapSource? Image)> LoadTileAsync(
        MapTileSource source,
        int zoom,
        int x,
        int y,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var image = await _mapTiles.GetTileAsync(source, zoom, x, y, timeout.Token)
                .ConfigureAwait(true);
            return (x, y, image);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (x, y, null);
        }
    }

    private static (double Left, double Top) MarkerPosition(double x, double y)
    {
        const double size = 12;
        const double half = size / 2;
        return (
            Math.Clamp(x, half + 2, MapWidth - half - 2) - half,
            Math.Clamp(y, half + 2, MapHeight - half - 2) - half);
    }

    private static (double X, double Y) Project(double latitude, double longitude, int zoom)
    {
        var scale = 256d * (1 << zoom);
        var x = (longitude + 180d) / 360d * scale;
        var clamped = Math.Clamp(latitude, -85.05112878, 85.05112878);
        var radians = clamped * Math.PI / 180d;
        var y = (1d - Math.Log(Math.Tan(radians) + 1d / Math.Cos(radians)) / Math.PI) / 2d * scale;
        return (x, y);
    }

    private const double GcjRadius = 6378245.0;
    private const double GcjEccentricity = 0.00669342162296594323;

    private static (double Latitude, double Longitude) Wgs84ToGcj02(
        double latitude,
        double longitude)
    {
        if (OutOfChina(latitude, longitude))
        {
            return (latitude, longitude);
        }

        var latitudeOffset = TransformLatitude(longitude - 105d, latitude - 35d);
        var longitudeOffset = TransformLongitude(longitude - 105d, latitude - 35d);
        var radians = latitude / 180d * Math.PI;
        var magic = Math.Sin(radians);
        magic = 1 - GcjEccentricity * magic * magic;
        var sqrtMagic = Math.Sqrt(magic);
        latitudeOffset = latitudeOffset * 180d /
                         (GcjRadius * (1 - GcjEccentricity) / (magic * sqrtMagic) * Math.PI);
        longitudeOffset = longitudeOffset * 180d /
                          (GcjRadius / sqrtMagic * Math.Cos(radians) * Math.PI);
        return (latitude + latitudeOffset, longitude + longitudeOffset);
    }

    private static bool OutOfChina(double latitude, double longitude) =>
        longitude < 72.004 || longitude > 137.8347 ||
        latitude < 0.8293 || latitude > 55.8271;

    private static double TransformLatitude(double x, double y)
    {
        var result = -100d + 2d * x + 3d * y + 0.2d * y * y + 0.1d * x * y +
                     0.2d * Math.Sqrt(Math.Abs(x));
        result += (20d * Math.Sin(6d * x * Math.PI) + 20d * Math.Sin(2d * x * Math.PI)) * 2d / 3d;
        result += (20d * Math.Sin(y * Math.PI) + 40d * Math.Sin(y / 3d * Math.PI)) * 2d / 3d;
        result += (160d * Math.Sin(y / 12d * Math.PI) + 320d * Math.Sin(y * Math.PI / 30d)) * 2d / 3d;
        return result;
    }

    private static double TransformLongitude(double x, double y)
    {
        var result = 300d + x + 2d * y + 0.1d * x * x + 0.1d * x * y +
                     0.1d * Math.Sqrt(Math.Abs(x));
        result += (20d * Math.Sin(6d * x * Math.PI) + 20d * Math.Sin(2d * x * Math.PI)) * 2d / 3d;
        result += (20d * Math.Sin(x * Math.PI) + 40d * Math.Sin(x / 3d * Math.PI)) * 2d / 3d;
        result += (150d * Math.Sin(x / 12d * Math.PI) + 300d * Math.Sin(x / 30d * Math.PI)) * 2d / 3d;
        return result;
    }

    private static double[] Downsample(double[] values, int maxPoints)
    {
        if (values.Length <= maxPoints)
        {
            return values;
        }

        var result = new double[maxPoints];
        for (var index = 0; index < maxPoints; index++)
        {
            result[index] = values[(int)Math.Round(index * (values.Length - 1d) / (maxPoints - 1))];
        }

        return result;
    }

    private static string FormatSpeed(double? metersPerSecond)
    {
        if (metersPerSecond is not { } speed || speed <= 0)
        {
            return "—";
        }

        return $"{speed * 3.6:0.0} km/h";
    }

    [RelayCommand]
    private void OpenOriginal()
    {
        if (string.IsNullOrWhiteSpace(WebUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(WebUrl) { UseShellExecute = true });
        }
        catch
        {
            // The window can stay open; the link is optional.
        }
    }
}
