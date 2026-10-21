using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BikeSync.Core.Services;

internal static class JsonFile
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A fixed ".tmp" name can collide with a stale handle from a previous
        // process (or another app instance), producing a misleading Access Denied.
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best effort cleanup; the original exception is more useful.
            }

            throw;
        }
    }

    public static void ArchiveCorrupt(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var backup = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Copy(path, backup, overwrite: false);
        }
        catch
        {
            // Best effort only; the caller reports the original parse failure.
        }
    }
}
