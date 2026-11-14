using System.Globalization;

namespace MemPulse.Core.Platform.Linux;

public readonly record struct LinuxMemoryMapping(ulong Address, ulong Length);

public static class LinuxProcessMemory
{
    /// <summary>Only private, writable anonymous mappings: no shared/file/device/stack mappings.</summary>
    public static IReadOnlyList<LinuxMemoryMapping> ParseEligibleMappings(string maps, ulong maxBytes, int pageSize = 0)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (pageSize == 0) pageSize = Environment.SystemPageSize;
        if (pageSize <= 0 || (pageSize & (pageSize - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be a power of two.");

        var result = new List<LinuxMemoryMapping>();
        ulong remaining = maxBytes;
        foreach (string line in maps.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (remaining == 0 || result.Count >= 128) break;
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Reject file-backed regions even when procfs omits their pathname.
            if (parts.Length < 5 || parts[1] != "rw-p" ||
                parts[3] != "00:00" || parts[4] != "0") continue;
            if (parts.Length >= 6 && parts[5] != "[heap]" &&
                !parts[5].StartsWith("[anon:", StringComparison.Ordinal)) continue;
            string[] ends = parts[0].Split('-');
            if (ends.Length != 2 ||
                !ulong.TryParse(ends[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start) ||
                !ulong.TryParse(ends[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end) ||
                end <= start) continue;
            ulong length = Math.Min(end - start, remaining);
            length &= ~((ulong)pageSize - 1);
            if (length == 0) continue;
            result.Add(new LinuxMemoryMapping(start, length));
            remaining -= length;
        }
        return result;
    }
}
