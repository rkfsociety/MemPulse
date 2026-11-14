using System.Globalization;

namespace MemPulse.Core.Helper;

public static class HelperCommandBuilder
{
    public static IReadOnlyList<string> BuildWindowsCleanArgs(
        CleanupOptions options,
        string dataDirectory,
        string reportPath,
        string? excludeFilePath = null)
    {
        var args = new List<string>
        {
            "--clean-cache",
            "--data-dir", dataDirectory,
            "--report", reportPath
        };
        if (options.Standby) args.Add("--standby");
        if (options.WorkingSets) args.Add("--workingsets");
        if (options.ModifiedPages) args.Add("--modified");
        if (options.WorkingSets && !string.IsNullOrWhiteSpace(excludeFilePath))
        {
            args.Add("--exclude-file");
            args.Add(excludeFilePath);
        }
        return args;
    }

    public static IReadOnlyList<string> BuildWindowsTrimArgs(
        int pid,
        string dataDirectory,
        string reportPath) =>
    [
        "--trim-process",
        pid.ToString(CultureInfo.InvariantCulture),
        "--data-dir", dataDirectory,
        "--report", reportPath
    ];

    public static IReadOnlyList<string> BuildLinuxCleanArgs(
        string dataDirectory,
        string reportPath) =>
    [
        "--clean-cache",
        "--data-dir", dataDirectory,
        "--report", reportPath
    ];

    public static IReadOnlyList<string> BuildLinuxTrimArgs(
        int pid,
        ulong startTicks,
        string dataDirectory,
        string reportPath) =>
    [
        "--trim-process",
        pid.ToString(CultureInfo.InvariantCulture),
        "--start-ticks",
        startTicks.ToString(CultureInfo.InvariantCulture),
        "--data-dir", dataDirectory,
        "--report", reportPath
    ];

    public static IReadOnlyList<string> BuildLinuxAutoCleanArgs(
        int threshold,
        int cooldownMinutes,
        int parentPid,
        ulong parentStartTicks,
        string dataDirectory,
        string markerPath,
        string reportPath) =>
    [
        "--auto-clean",
        "--threshold", threshold.ToString(CultureInfo.InvariantCulture),
        "--cooldown", cooldownMinutes.ToString(CultureInfo.InvariantCulture),
        "--parent-pid", parentPid.ToString(CultureInfo.InvariantCulture),
        "--parent-ticks", parentStartTicks.ToString(CultureInfo.InvariantCulture),
        "--data-dir", dataDirectory,
        "--marker", markerPath,
        "--report", reportPath
    ];
}
