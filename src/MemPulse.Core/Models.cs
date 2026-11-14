using System.Globalization;

namespace MemPulse.Core;

public sealed record MemoryUsage(ulong Total, ulong Available, ulong Cache, ulong Committed, ulong CommitLimit)
{
    public ulong Used => Total >= Available ? Total - Available : 0;
    public double UsedPercent => Total == 0 ? 0 : Math.Clamp((double)Used / Total * 100, 0, 100);
}

public sealed record ProcessUsage(int Pid, string Name, long WorkingBytes, long PrivateBytes)
{
    public string WorkingText => Format.Bytes(WorkingBytes);
    public string PrivateText => Format.Bytes(PrivateBytes);
    public string Monogram => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "•";
    // Scale is relative to the largest observed process in the same snapshot, not total RAM.
    public double RelativeLoad { get; init; }
    public byte[]? IconBytes { get; init; }
}

public static class Format
{
    private static readonly string[] Units = ["Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ"];
    public static string Bytes(long bytes) => Bytes((ulong)Math.Max(0, bytes));

    public static string Bytes(ulong bytes)
    {
        if (bytes == 0) return "0 Б";
        // Determine unit index using logarithm (base 1024)
        int index = (int)Math.Floor(Math.Log(bytes, 1024));
        index = Math.Min(index, Units.Length - 1);
        double size = bytes / Math.Pow(1024, index);
        return size.ToString(index == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + Units[index];
    }
}

public static class RussianText
{
    public static string Applications(int count)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        string noun = lastTwo is >= 11 and <= 14 ? "приложений"
            : last == 1 ? "приложение"
            : last is >= 2 and <= 4 ? "приложения" : "приложений";
        return count + " " + noun;
    }

    public static string Processes(int count)
    {
        int lastTwo = count % 100;
        int last = count % 10;
        string noun = lastTwo is >= 11 and <= 14 ? "процессов"
            : last == 1 ? "процесс"
            : last is >= 2 and <= 4 ? "процесса" : "процессов";
        return count + " " + noun;
    }
}

public sealed class AppSettings
{
    public int RefreshSeconds { get; set; } = 2;
    public bool AutoClean { get; set; }
    public int AutoCleanThreshold { get; set; } = 90;
    public int AutoCleanCooldownMinutes { get; set; } = 15;
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    public AppSettings Copy() => (AppSettings)MemberwiseClone();

    public static AppSettings Normalize(AppSettings? settings)
    {
        var result = settings ?? new AppSettings();
        result.RefreshSeconds = Math.Clamp(result.RefreshSeconds, 1, 10);
        result.AutoCleanThreshold = Math.Clamp(result.AutoCleanThreshold, AutoCleanupPolicy.MinThreshold, AutoCleanupPolicy.MaxThreshold);
        result.AutoCleanCooldownMinutes = Math.Clamp(result.AutoCleanCooldownMinutes, AutoCleanupPolicy.MinCooldownMinutes, AutoCleanupPolicy.MaxCooldownMinutes);
        return result;
    }
}

public sealed record CleanupOptions(bool Standby, bool WorkingSets, bool ModifiedPages);
public sealed record CleanupStep(string Name, bool Success, string Details);

public sealed class CleanupReport
{
    public List<CleanupStep> Steps { get; } = [];
    public bool AnySuccess => Steps.Any(step => step.Success);
    public string Summary => Steps.Count == 0 ? "Операции не выбраны." :
        string.Join(" · ", Steps.Select(step => (step.Success ? "✓ " : "✕ ") + step.Name +
            (step.Success ? "" : " (" + step.Details + ")")));
}
