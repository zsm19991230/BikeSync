namespace BikeSync.Core.Models;

public sealed class ImportOptions
{
    public bool SaveLocalCopy { get; init; } = true;
    public bool AppendSourceToDescription { get; init; } = true;
    public string? NameOverride { get; init; }
    public string? DescriptionOverride { get; init; }
    public string? SportType { get; init; }
    public string? ExternalId { get; init; }
    public bool MarkAsCommute { get; init; }
}

public sealed record ImportResult(
    bool Success,
    bool Duplicate,
    string? RemoteId,
    string? RemoteUrl,
    string Message)
{
    public static ImportResult Ok(string? remoteId, string? remoteUrl, string message = "导入成功") =>
        new(true, false, remoteId, remoteUrl, message);

    public static ImportResult Duplicated(string message) =>
        new(true, true, null, null, message);

    public static ImportResult Failed(string message) =>
        new(false, false, null, null, message);
}

public sealed record SyncProgress(
    int Completed,
    int Total,
    string ActivityId,
    string ActivityName,
    string Stage,
    int Percent);

public sealed class SyncItemResult
{
    public required ActivitySummary Activity { get; init; }
    public required ImportResult Result { get; init; }
    public string? SavedFilePath { get; init; }

    public bool IsSuccess => Result.Success;
    public bool IsDuplicate => Result.Duplicate;
}

public sealed class SyncSummary
{
    public required PlatformAccount SourceAccount { get; init; }
    public required PlatformAccount TargetAccount { get; init; }
    public required IReadOnlyList<SyncItemResult> Items { get; init; }
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;

    public int Total => Items.Count;
    public int Succeeded => Items.Count(i => i.IsSuccess && !i.IsDuplicate);
    public int Duplicates => Items.Count(i => i.IsDuplicate);
    public int Failed => Items.Count(i => !i.IsSuccess);
    public bool IsSuccess => Failed == 0;
    public double TotalDistanceMeters => Items.Sum(i => i.Activity.DistanceMeters);
    public string? FirstError => Items.FirstOrDefault(i => !i.IsSuccess)?.Result.Message;
}

public sealed class SyncHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public required PlatformId SourcePlatform { get; init; }
    public required string SourceName { get; init; }
    public required PlatformId TargetPlatform { get; init; }
    public required string TargetName { get; init; }
    public int Total { get; init; }
    public int Succeeded { get; init; }
    public int Duplicates { get; init; }
    public int Failed { get; init; }
    public string? FirstError { get; init; }
}

public sealed class SyncRequest
{
    public required PlatformAccount SourceAccount { get; init; }
    public required PlatformAccount TargetAccount { get; init; }
    public required IReadOnlyList<ActivitySummary> Activities { get; init; }
    public ImportOptions Options { get; init; } = new();
}
