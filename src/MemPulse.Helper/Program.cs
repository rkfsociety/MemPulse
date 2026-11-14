using System.Globalization;
using System.Text.Json;
using MemPulse.Core;
using MemPulse.Core.Helper;

namespace MemPulse.Helper;

public enum HelperCommandMode
{
    CleanCache,
    TrimProcess,
    AutoClean
}

public sealed record ParsedHelperCommand(
    HelperCommandMode Mode,
    string DataDirectory,
    string ReportPath,
    bool Standby,
    bool WorkingSets,
    bool ModifiedPages,
    string? ExcludeFilePath,
    int Pid,
    ulong StartTicks,
    int Threshold,
    int CooldownMinutes,
    int ParentPid,
    ulong ParentTicks,
    string? MarkerPath);

public static class HelperCli
{
    public static ParsedHelperCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args.Length > 32)
            throw new ArgumentException("Некорректное количество аргументов.");

        HelperCommandMode? mode = null;
        string? dataDir = null;
        string? reportPath = null;
        bool standby = false;
        bool workingSets = false;
        bool modified = false;
        string? excludeFile = null;
        int pid = 0;
        ulong startTicks = 0;
        int threshold = 0;
        int cooldown = 0;
        int parentPid = 0;
        ulong parentTicks = 0;
        string? marker = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.IsNullOrWhiteSpace(arg) || arg.Length > 1024)
                throw new ArgumentException("Пустой или слишком длинный аргумент.");

            switch (arg)
            {
                case "--clean-cache":
                    SetMode(ref mode, HelperCommandMode.CleanCache);
                    break;
                case "--trim-process":
                    SetMode(ref mode, HelperCommandMode.TrimProcess);
                    pid = ParsePositiveInt(NextValue(args, ref i, arg), "PID");
                    break;
                case "--auto-clean":
                    SetMode(ref mode, HelperCommandMode.AutoClean);
                    break;
                case "--data-dir":
                    dataDir = NextValue(args, ref i, arg);
                    break;
                case "--report":
                    reportPath = NextValue(args, ref i, arg);
                    break;
                case "--standby":
                    standby = true;
                    break;
                case "--workingsets":
                    workingSets = true;
                    break;
                case "--modified":
                    modified = true;
                    break;
                case "--exclude-file":
                    excludeFile = NextValue(args, ref i, arg);
                    break;
                case "--start-ticks":
                    startTicks = ParsePositiveUlong(NextValue(args, ref i, arg), "start-ticks");
                    break;
                case "--threshold":
                    threshold = ParsePositiveInt(NextValue(args, ref i, arg), "threshold");
                    break;
                case "--cooldown":
                    cooldown = ParsePositiveInt(NextValue(args, ref i, arg), "cooldown");
                    break;
                case "--parent-pid":
                    parentPid = ParsePositiveInt(NextValue(args, ref i, arg), "parent-pid");
                    break;
                case "--parent-ticks":
                    parentTicks = ParsePositiveUlong(NextValue(args, ref i, arg), "parent-ticks");
                    break;
                case "--marker":
                    marker = NextValue(args, ref i, arg);
                    break;
                default:
                    throw new ArgumentException($"Неизвестный или запрещённый аргумент: {arg}");
            }
        }

        if (mode is null)
            throw new ArgumentException("Не указана команда операции.");

        string safeDataDir = HelperPathGuard.ValidateDataDirectory(dataDir);
        string safeReportPath = HelperPathGuard.ResolveSafeReportPath(safeDataDir, reportPath);
        string? safeExclude = excludeFile is null ? null : HelperPathGuard.ResolveSafeExcludeFilePath(safeDataDir, excludeFile);
        string? safeMarker = marker is null ? null : HelperPathGuard.ResolveSafeMarkerPath(safeDataDir, marker);

        switch (mode.Value)
        {
            case HelperCommandMode.CleanCache:
                if (pid != 0 || startTicks != 0 || safeMarker is not null)
                    throw new ArgumentException("Лишние параметры для --clean-cache.");
                if (OperatingSystem.IsWindows() && !standby && !workingSets && !modified)
                    throw new ArgumentException("Для --clean-cache на Windows требуется хотя бы один флаг очистки.");
                break;

            case HelperCommandMode.TrimProcess:
                if (standby || workingSets || modified || safeExclude is not null || safeMarker is not null)
                    throw new ArgumentException("Лишние параметры для --trim-process.");
                if (pid <= 1 || pid == Environment.ProcessId)
                    throw new ArgumentException("Недопустимый PID процесса.");
                if (OperatingSystem.IsLinux() && startTicks == 0)
                    throw new ArgumentException("На Linux для --trim-process требуется --start-ticks.");
                break;

            case HelperCommandMode.AutoClean:
                if (threshold is < AutoCleanupPolicy.MinThreshold or > AutoCleanupPolicy.MaxThreshold)
                    throw new ArgumentException("Порог автоочистки вне допустимого диапазона (70–98).");
                if (cooldown is < AutoCleanupPolicy.MinCooldownMinutes or > AutoCleanupPolicy.MaxCooldownMinutes)
                    throw new ArgumentException("Пауза автоочистки вне допустимого диапазона (5–120).");
                if (parentPid <= 1 || parentTicks == 0 || safeMarker is null)
                    throw new ArgumentException("Неполные или недопустимые параметры --auto-clean.");
                break;
        }

        return new ParsedHelperCommand(
            mode.Value,
            safeDataDir,
            safeReportPath,
            standby,
            workingSets,
            modified,
            safeExclude,
            pid,
            startTicks,
            threshold,
            cooldown,
            parentPid,
            parentTicks,
            safeMarker);
    }

    public static async Task<int> ExecuteAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ParsedHelperCommand command;
        try
        {
            command = Parse(args);
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        HelperReport report;
        try
        {
            report = await DispatchAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report = new HelperReport(
                false,
                ex.Message,
                [new CleanupStep("MemPulse.Helper", false, ex.Message)],
                DateTimeOffset.UtcNow);
        }

        try
        {
            HelperReportIo.WriteReport(command.ReportPath, report);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Не удалось записать отчёт: " + ex.Message);
            return 1;
        }

        return report.Success ? 0 : 1;
    }

    private static async Task<HelperReport> DispatchAsync(ParsedHelperCommand command, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return command.Mode switch
            {
                HelperCommandMode.CleanCache => WindowsCleanupExecutor.Clean(new CleanupOptions(
                    command.Standby,
                    command.WorkingSets,
                    command.ModifiedPages,
                    LoadExcluded(command.ExcludeFilePath))),
                HelperCommandMode.TrimProcess => WindowsCleanupExecutor.TrimProcess(command.Pid),
                _ => new HelperReport(false, "Режим не поддерживается на Windows.", [], DateTimeOffset.UtcNow)
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return command.Mode switch
            {
                HelperCommandMode.CleanCache => LinuxCleanupExecutor.CleanCache(),
                HelperCommandMode.TrimProcess => LinuxCleanupExecutor.TrimProcess(command.Pid, command.StartTicks),
                HelperCommandMode.AutoClean => await LinuxCleanupExecutor.RunAutoAsync(
                    command.Threshold,
                    command.CooldownMinutes,
                    command.ParentPid,
                    command.ParentTicks,
                    command.MarkerPath!,
                    command.ReportPath,
                    cancellationToken).ConfigureAwait(false),
                _ => new HelperReport(false, "Неизвестный режим.", [], DateTimeOffset.UtcNow)
            };
        }

        return new HelperReport(false, "Операционная система не поддерживается.", [], DateTimeOffset.UtcNow);
    }

    private static IReadOnlyList<string>? LoadExcluded(string? excludeFilePath)
    {
        if (string.IsNullOrEmpty(excludeFilePath) || !File.Exists(excludeFilePath))
            return null;

        var info = new FileInfo(excludeFilePath);
        if (info.Length > 256 * 1024)
            throw new InvalidDataException("Файл исключений превышает допустимый размер.");

        string json = File.ReadAllText(excludeFilePath);
        var list = JsonSerializer.Deserialize(json, HelperJsonContext.Default.ListString);
        return list is { Count: > 0 } ? ProcessExclusion.NormalizeList(list) : null;
    }

    private static void SetMode(ref HelperCommandMode? current, HelperCommandMode target)
    {
        if (current is not null)
            throw new ArgumentException("Указано несколько команд одновременно.");
        current = target;
    }

    private static string NextValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Отсутствует значение для флага {flag}.");
        index++;
        return args[index];
    }

    private static int ParsePositiveInt(string raw, string name)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
            throw new ArgumentException($"Некорректное значение {name}: {raw}");
        return value;
    }

    private static ulong ParsePositiveUlong(string raw, string name)
    {
        if (!ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) || value == 0)
            throw new ArgumentException($"Некорректное значение {name}: {raw}");
        return value;
    }
}

internal static class Program
{
    public static Task<int> Main(string[] args) => HelperCli.ExecuteAsync(args);
}
