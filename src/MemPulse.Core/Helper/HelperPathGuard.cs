using System.Text.RegularExpressions;

namespace MemPulse.Core.Helper;

/// <summary>
/// Strict path and filename validation for elevated helper arguments.
/// Rejects path traversal, relative/unrooted data directories, and reparse points / symlinks.
/// </summary>
public static partial class HelperPathGuard
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string ValidateDataDirectory(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Каталог данных модуля не указан.", nameof(dataDirectory));

        string trimmed = dataDirectory.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            throw new UnauthorizedAccessException("Каталог данных должен быть абсолютным путём.");

        if (ContainsDotDotSegment(trimmed))
            throw new UnauthorizedAccessException("Путь каталога данных содержит запрещённые сегменты '..'.");

        string fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        if (!Directory.Exists(fullDir))
            throw new DirectoryNotFoundException($"Каталог данных не найден: {fullDir}");

        EnsureNoReparsePointsInHierarchy(fullDir);
        return fullDir;
    }

    public static string ResolveSafeReportPath(string dataDirectory, string? candidatePath) =>
        ResolveSafeFileInDirectory(dataDirectory, candidatePath, SafeReportNameRegex(), "файла отчёта");

    public static string ResolveSafeExcludeFilePath(string dataDirectory, string? candidatePath) =>
        ResolveSafeFileInDirectory(dataDirectory, candidatePath, SafeExcludeNameRegex(), "файла исключений");

    public static string ResolveSafeMarkerPath(string dataDirectory, string? candidatePath) =>
        ResolveSafeFileInDirectory(dataDirectory, candidatePath, SafeMarkerNameRegex(), "файла маркера");

    private static string ResolveSafeFileInDirectory(
        string dataDirectory,
        string? candidatePath,
        Regex fileNameRegex,
        string roleLabel)
    {
        string safeRoot = ValidateDataDirectory(dataDirectory);

        if (string.IsNullOrWhiteSpace(candidatePath))
            throw new ArgumentException($"Путь {roleLabel} не указан.", nameof(candidatePath));

        string trimmed = candidatePath.Trim();
        if (ContainsDotDotSegment(trimmed))
            throw new UnauthorizedAccessException($"Путь {roleLabel} содержит запрещённые сегменты '..'.");

        if (OperatingSystem.IsWindows() && HasAlternateDataStream(trimmed))
            throw new UnauthorizedAccessException($"Путь {roleLabel} содержит недопустимый поток данных.");

        string combined = Path.IsPathFullyQualified(trimmed)
            ? trimmed
            : Path.Combine(safeRoot, trimmed);

        string fullTarget = Path.GetFullPath(combined);
        string expectedPrefix = safeRoot + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(expectedPrefix, PathComparison))
            throw new UnauthorizedAccessException($"Путь {roleLabel} выходит за пределы разрешённого каталога.");

        string fileName = Path.GetFileName(fullTarget);
        if (string.IsNullOrEmpty(fileName) || !fileNameRegex.IsMatch(fileName))
            throw new UnauthorizedAccessException($"Недопустимое имя {roleLabel}: «{fileName}».");

        // Reject symlinks/reparse points on every intermediate directory and the target file itself.
        string relative = Path.GetRelativePath(safeRoot, fullTarget);
        string current = safeRoot;
        string[] segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            bool isLast = i == segments.Length - 1;
            if (isLast)
            {
                if (File.Exists(current) || Directory.Exists(current))
                    EnsureNotReparsePoint(current);
            }
            else if (Directory.Exists(current))
            {
                EnsureNotReparsePoint(current);
            }
        }

        return fullTarget;
    }

    private static bool ContainsDotDotSegment(string path)
    {
        string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(static s => s == "..");
    }

    private static bool HasAlternateDataStream(string path)
    {
        // Only drive-letter colon at index 1 (e.g. C:\...) is allowed on Windows.
        int firstColon = path.IndexOf(':');
        if (firstColon < 0)
            return false;
        if (firstColon != 1 || path.Length < 3 || !char.IsAsciiLetter(path[0]))
            return true;
        return path.IndexOf(':', 2) >= 0;
    }

    private static void EnsureNoReparsePointsInHierarchy(string fullDirectoryPath)
    {
        var dir = new DirectoryInfo(fullDirectoryPath);
        while (dir is not null)
        {
            if (dir.Exists)
                EnsureNotReparsePoint(dir.FullName);
            dir = dir.Parent;
        }
    }

    private static void EnsureNotReparsePoint(string fullPath)
    {
        FileAttributes attrs = File.GetAttributes(fullPath);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException($"Обнаружен symlink или reparse point: «{fullPath}».");
    }

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,80}\.json$")]
    private static partial Regex SafeReportNameRegex();

    [GeneratedRegex(@"^mempulse-exclude-[a-fA-F0-9]{32}\.json$")]
    private static partial Regex SafeExcludeNameRegex();

    [GeneratedRegex(@"^mempulse-auto-[a-fA-F0-9]{32}\.marker$")]
    private static partial Regex SafeMarkerNameRegex();
}
