namespace BikeSync.Core.Models;

/// <summary>Supported cycling platforms.</summary>
public enum PlatformId
{
    Strava = 0,
    IntervalsIcu = 1,
    RideWithGps = 2,
    MyWhoosh = 3,
    IGpsport = 4,
    Xingzhe = 5,
}

public sealed record PlatformDescriptor(
    PlatformId Id,
    string DisplayName,
    string ShortName,
    string Tagline,
    string AuthSummary,
    string BrandColor,
    string WebsiteUrl,
    string SetupUrl,
    PlatformCapabilities Capabilities);

public sealed record PlatformCapabilities(
    bool CanListActivities,
    bool CanDownloadFiles,
    bool CanUploadActivities,
    bool RequiresDeveloperApp,
    bool SupportsRefreshToken,
    string FileFormats);

public static class PlatformCatalog
{
    private static readonly IReadOnlyDictionary<PlatformId, PlatformDescriptor> Descriptors =
        new Dictionary<PlatformId, PlatformDescriptor>
        {
            [PlatformId.Strava] = new(
                PlatformId.Strava,
                "Strava",
                "STRAVA",
                "全球骑友的骑行记录与社交主场",
                "OAuth 浏览器授权，令牌自动续期",
                "#FC4C02",
                "https://www.strava.com",
                "https://www.strava.com/settings/api",
                new PlatformCapabilities(true, true, true, true, true, "FIT / GPX / TCX")),
            [PlatformId.IntervalsIcu] = new(
                PlatformId.IntervalsIcu,
                "intervals.icu",
                "INTERVALS",
                "面向功率训练的深度分析平台",
                "个人 API Key，本地加密保存",
                "#4C8DFF",
                "https://intervals.icu",
                "https://intervals.icu/settings",
                new PlatformCapabilities(true, true, true, false, false, "FIT / GPX / TCX")),
            [PlatformId.RideWithGps] = new(
                PlatformId.RideWithGps,
                "Ride with GPS",
                "RWGPS",
                "路线规划与骑行导航专家",
                "API Key + Auth Token，本地加密保存",
                "#2A9D6F",
                "https://ridewithgps.com",
                "https://ridewithgps.com/api/api_clients",
                new PlatformCapabilities(true, true, true, true, false, "FIT / GPX / TCX / KML")),
            [PlatformId.MyWhoosh] = new(
                PlatformId.MyWhoosh,
                "MyWhoosh",
                "MYWHOOSH",
                "室内虚拟骑行与功率训练",
                "MyWhoosh 账号登录，令牌本地加密",
                "#19C37D",
                "https://www.mywhoosh.com",
                "https://www.mywhoosh.com",
                new PlatformCapabilities(true, true, false, false, false, "FIT")),
            [PlatformId.IGpsport] = new(
                PlatformId.IGpsport,
                "iGPSPORT（IPGSports）",
                "IGPSPORT",
                "国产码表与骑行数据平台",
                "iGPSPORT 账号登录（中国站）",
                "#E60012",
                "https://www.igpsport.com",
                "https://app.igpsport.cn",
                new PlatformCapabilities(true, true, true, false, false, "FIT")),
            [PlatformId.Xingzhe] = new(
                PlatformId.Xingzhe,
                "行者（Xingzhe）",
                "XINGZHE",
                "国内骑行社区与轨迹记录",
                "行者账号登录，Cookie 本地加密",
                "#FF7A00",
                "https://www.imxingzhe.com",
                "https://www.imxingzhe.com",
                new PlatformCapabilities(true, true, true, false, false, "FIT")),
        };

    public static IReadOnlyList<PlatformDescriptor> All { get; } =
        new[]
        {
            PlatformId.Strava, PlatformId.IntervalsIcu, PlatformId.RideWithGps,
            PlatformId.MyWhoosh, PlatformId.IGpsport, PlatformId.Xingzhe,
        }
            .Select(id => Descriptors[id])
            .ToArray();

    public static PlatformDescriptor Get(PlatformId id) => Descriptors[id];

    public static string Initial(PlatformId id) => id switch
    {
        PlatformId.Strava => "S",
        PlatformId.IntervalsIcu => "i",
        PlatformId.RideWithGps => "R",
        PlatformId.MyWhoosh => "M",
        PlatformId.IGpsport => "I",
        PlatformId.Xingzhe => "X",
        _ => "?",
    };

    public static bool TryGet(PlatformId id, out PlatformDescriptor descriptor) =>
        Descriptors.TryGetValue(id, out descriptor!);
}

/// <summary>Cycling-related sport names used by the supported platforms.</summary>
public static class CyclingSports
{
    private static readonly HashSet<string> KnownCycling = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ride", "VirtualRide", "EBikeRide", "GravelRide", "MountainBikeRide",
        "EMountainBikeRide", "Handcycle", "Velomobile", "Cyclocross", "Track",
        "Mountain Bike", "Mountain Biking", "Gravel Ride", "E-Bike Ride",
        "Virtual Ride", "E-Bike", "Bike", "Biking", "Cycling", "Road Cycling",
        "IndoorCycling", "Indoor Cycling",
    };

    private static readonly string[] CyclingMarkers =
    {
        "ride", "bike", "cycling", "cyclocross", "velomobile", "handcycle",
    };

    public static bool IsCycling(string? sport)
    {
        if (string.IsNullOrWhiteSpace(sport))
        {
            return true;
        }

        var value = sport.Trim();
        if (KnownCycling.Contains(value))
        {
            return true;
        }

        return CyclingMarkers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
