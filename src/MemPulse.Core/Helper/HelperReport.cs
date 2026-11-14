using System.Text.Json;
using System.Text.Json.Serialization;

namespace MemPulse.Core.Helper;

public sealed record HelperReport(
    bool Success,
    string Message,
    IReadOnlyList<CleanupStep> Steps,
    DateTimeOffset TimestampUtc);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HelperReport))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class HelperJsonContext : JsonSerializerContext
{
}

public static class HelperReportIo
{
    public static void WriteReport(string path, HelperReport report)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(report, HelperJsonContext.Default.HelperReport);
            File.WriteAllText(tempPath, json);
            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static async Task<HelperReport?> TryReadReportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return null;

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(json, HelperJsonContext.Default.HelperReport);
    }
}
