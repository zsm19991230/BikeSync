using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Services;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly IReadOnlyDictionary<PlatformId, IPlatformProvider> _providers;

    public ProviderRegistry(IEnumerable<IPlatformProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.Platform);
    }

    public IPlatformProvider Get(PlatformId platform)
    {
        if (!_providers.TryGetValue(platform, out var provider))
        {
            throw new NotSupportedException($"平台 {platform} 尚未实现。");
        }

        return provider;
    }
}
