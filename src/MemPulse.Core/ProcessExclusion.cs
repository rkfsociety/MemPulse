namespace MemPulse.Core;

/// <summary>Helpers for normalizing and matching process exclusion (whitelist) entries.</summary>
public static class ProcessExclusion
{
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string trimmed = name.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4].TrimEnd();

        return trimmed;
    }

    public static List<string> NormalizeList(IEnumerable<string>? items)
    {
        var result = new List<string>();
        if (items is null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in items)
        {
            string normalized = NormalizeName(raw);
            if (normalized.Length > 0 && seen.Add(normalized))
                result.Add(normalized);
        }
        return result;
    }

    public static List<string> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return NormalizeList(text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static string FormatList(IEnumerable<string>? items) =>
        items is null ? string.Empty : string.Join(", ", NormalizeList(items));

    public static bool IsExcluded(string? processName, IEnumerable<string>? excludedProcesses)
    {
        if (excludedProcesses is null)
            return false;

        string target = NormalizeName(processName);
        if (target.Length == 0)
            return false;

        foreach (string item in excludedProcesses)
        {
            if (string.Equals(target, NormalizeName(item), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
