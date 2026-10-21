namespace BikeSync.Core.Models;

/// <summary>
/// Last successfully fetched activity page for one account. This is deliberately a
/// plain JSON cache: activity summaries contain no credentials.
/// </summary>
public sealed class ActivityCacheEntry
{
    public Guid AccountId { get; set; }
    public PlatformId Platform { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public bool CyclingOnly { get; set; } = true;
    public List<ActivitySummary> Activities { get; set; } = new();
}
