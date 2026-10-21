using System.Globalization;
using System.Text;
using System.Text.Json;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Core.Providers;

public sealed class IGpsportProvider : IPlatformProvider
{
    private readonly HttpClient _http;
    private readonly IGpsportSigner _signer;

    public IGpsportProvider(HttpClient http, IGpsportSigner signer)
    {
        _http = http;
        _signer = signer;
    }

    public PlatformId Platform => PlatformId.IGpsport;

    public async Task<AccountProfile> GetProfileAsync(
        PlatformAccount account,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        var client = new IGpsportApiClient(_http, _signer, token);
        await client.ValidateAsync(cancellationToken).ConfigureAwait(false);
        var subject = IGpsportApiClient.ReadJwtSubject(token);
        return new AccountProfile(
            string.IsNullOrWhiteSpace(subject) ? account.UserId : subject,
            string.IsNullOrWhiteSpace(account.DisplayName) ? "iGPSPORT" : account.DisplayName);
    }

    public async Task<IReadOnlyList<ActivitySummary>> ListActivitiesAsync(
        PlatformAccount account,
        ActivityQuery query,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        var client = new IGpsportApiClient(_http, _signer, token);
        var result = new List<ActivitySummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        const int pageSize = 20;
        for (var page = 1; page <= 50 && result.Count < query.MaxResults; page++)
        {
            var data = await client.QueryActivitiesAsync(
                page,
                pageSize,
                null,
                query.From,
                query.To,
                cancellationToken).ConfigureAwait(false);
            if (!data.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var batch = 0;
            foreach (var row in rows.EnumerateArray())
            {
                batch++;
                var id = ReadText(row, "rideId") ?? ReadText(row, "id");
                var start = ReadDate(row, "startTime") ?? ReadDate(row, "startTimeString") ??
                            ReadDate(row, "beginTime");
                if (string.IsNullOrWhiteSpace(id) || start is null || !seen.Add(id))
                {
                    continue;
                }

                var name = ReadText(row, "title") ?? "iGPSPORT 骑行";
                if (!string.IsNullOrWhiteSpace(query.Search) &&
                    !name.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new ActivitySummary
                {
                    Id = id,
                    Platform = PlatformId.IGpsport,
                    AccountId = account.Id,
                    AccountName = account.DisplayName,
                    Name = name,
                    SportType = "Ride",
                    StartTime = start.Value.ToLocalTime(),
                    LocalStartTime = start.Value.ToLocalTime().DateTime,
                    DistanceMeters = Math.Max(0, ReadDouble(row, "rideDistance")),
                    MovingTimeSeconds = ParseDuration(ReadValue(row, "recordTime")),
                    ElapsedTimeSeconds = ParseDuration(ReadValue(row, "recordTime")),
                    ElevationGainMeters = Math.Max(0, ReadDouble(row, "totalAscent")),
                    HasGps = true,
                    SourceFileType = "FIT",
                    WebUrl = "https://app.igpsport.cn",
                });

                if (result.Count >= query.MaxResults)
                {
                    break;
                }
            }

            var totalRows = ReadInt(data, "totalRows");
            if (batch == 0 || (totalRows > 0 && page * pageSize >= totalRows))
            {
                break;
            }
        }

        return result.OrderByDescending(a => a.StartTime).ToArray();
    }

    public async Task<ActivityFile> DownloadActivityAsync(
        PlatformAccount account,
        ActivitySummary activity,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        var client = new IGpsportApiClient(_http, _signer, token);
        var raw = await client.DownloadActivityAsync(activity.Id, cancellationToken).ConfigureAwait(false);
        var format = DetectFormat(raw);
        return new ActivityFile
        {
            Content = raw,
            FileName = ActivityFile.SafeFileName(
                $"{activity.DisplayStartTime:yyyy-MM-dd}_{activity.Name}_{activity.Id}",
                "igpsport-activity") + "." + format.Extension(),
            Format = format,
            SourceActivityId = activity.Id,
        };
    }

    public async Task<ImportResult> UploadActivityAsync(
        PlatformAccount account,
        ActivityFile file,
        ImportOptions options,
        CancellationToken cancellationToken)
    {
        var token = RequireToken(account);
        FitPreparation prepared;
        try
        {
            prepared = FitPreparer.Prepare(file.Content, TimeZoneInfo.Local, file.SourceFtpWatts ?? 0);
        }
        catch (InvalidDataException)
        {
            // Never block a valid FIT upload solely because optional compatibility
            // enrichment (NP/IF/TSS or a specific record layout) was unavailable.
            FitPreparer.Validate(file.Content, PlatformId.IGpsport);
            prepared = new FitPreparation(file.Content, "使用原始 FIT（未补充兼容字段）", false);
        }

        var client = new IGpsportApiClient(_http, _signer, token);
        var title = options.NameOverride;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "MyWhoosh 骑行";
        }

        var (_, rideId) = await client.UploadAsync(prepared.Content, title, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rideId))
        {
            return ImportResult.Ok(
                null,
                null,
                $"已提交 iGPSPORT，平台正在后台处理。{prepared.Summary}");
        }

        return ImportResult.Ok(
            rideId,
            $"https://app.igpsport.cn/activity/{rideId}",
            $"已导入 iGPSPORT。{prepared.Summary}");
    }

    private static string RequireToken(PlatformAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            throw new PlatformApiException(
                PlatformId.IGpsport,
                401,
                "iGPSPORT 登录已失效，请在「平台连接」中重新登录。");
        }

        return account.AccessToken;
    }

    private static ActivityFileFormat DetectFormat(byte[] raw)
    {
        if (raw.Length >= 12 &&
            raw[8] == (byte)'.' && raw[9] == (byte)'F' &&
            raw[10] == (byte)'I' && raw[11] == (byte)'T')
        {
            return ActivityFileFormat.Fit;
        }

        var prefix = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, 256));
        if (prefix.TrimStart().StartsWith("<gpx", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityFileFormat.Gpx;
        }

        if (prefix.Contains("TrainingCenterDatabase", StringComparison.OrdinalIgnoreCase))
        {
            return ActivityFileFormat.Tcx;
        }

        return ActivityFileFormat.Fit;
    }

    private static string? ReadText(JsonElement element, string name)
    {
        var value = ReadValue(element, name);
        return value?.ToString();
    }

    private static object? ReadValue(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static double ReadDouble(JsonElement element, string name)
    {
        var value = ReadValue(element, name);
        return value is not null &&
               double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        var value = ReadValue(element, name);
        return value is not null && int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        var value = ReadValue(element, name)?.ToString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy.MM.dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd", "yyyy.MM.dd", "yyyy/MM/dd", "yyyy-MM-dd'T'HH:mm:ss",
        };
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed;
        }

        return DateTime.TryParseExact(
            value,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var exact)
            ? new DateTimeOffset(exact)
            : null;
    }

    private static int ParseDuration(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return Math.Max(0, seconds);
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
        {
            return (int)Math.Max(0, span.TotalSeconds);
        }

        var hours = 0;
        var minutes = 0;
        var secondsPart = 0;
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"(?<value>\d+)\s*(?<unit>h|hour|hours|小时|m|min|minute|minutes|分|s|sec|second|seconds|秒)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (!int.TryParse(match.Groups["value"].Value, out var number))
            {
                continue;
            }

            switch (match.Groups["unit"].Value.ToLowerInvariant())
            {
                case "h":
                case "hour":
                case "hours":
                case "小时":
                    hours += number;
                    break;
                case "m":
                case "min":
                case "minute":
                case "minutes":
                case "分":
                    minutes += number;
                    break;
                default:
                    secondsPart += number;
                    break;
            }
        }

        return Math.Max(0, hours * 3600 + minutes * 60 + secondsPart);
    }
}
