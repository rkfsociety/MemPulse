using System.IO;

namespace MemPulse.Core;

/// <summary>Aggregated process names with the individual PIDs retained for diagnosis.</summary>
public sealed record ProcessGroupUsage(
    string Name,
    IReadOnlyList<ProcessUsage> Members,
    long WorkingBytes,
    long PrivateBytes,
    byte[]? IconBytes,
    double RelativeLoad)
{
    public int Count => Members.Count;
    public string CountText => Count.ToString();
    public string DisplayName => Count > 1 ? $"{Name}  ({Count})" : Name;
    public string Monogram => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "•";
    public string WorkingText => Format.Bytes(WorkingBytes);
    public string PrivateText => Format.Bytes(PrivateBytes);
    public string PidsText => string.Join(", ", Members.Select(x => x.Pid));
}

public enum ProcessSortOrder
{
    WorkingMemory,
    PrivateMemory,
    Name,
    ProcessCount
}

public static class ProcessGrouping
{
    /// <summary>Stable, predictable ordering for the process dashboard.</summary>
    public static IReadOnlyList<ProcessGroupUsage> Sort(
        IReadOnlyList<ProcessGroupUsage> groups, ProcessSortOrder order) =>
        order switch
        {
            ProcessSortOrder.PrivateMemory => groups
                .OrderByDescending(g => g.PrivateBytes)
                .ThenByDescending(g => g.WorkingBytes)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            ProcessSortOrder.Name => groups
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(g => g.WorkingBytes).ToArray(),
            ProcessSortOrder.ProcessCount => groups
                .OrderByDescending(g => g.Count)
                .ThenByDescending(g => g.WorkingBytes)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            _ => groups // Build() already orders by working set.
        };

    // A corrupted or unusually large snapshot must not crash the UI with a long overflow.
    private static long SaturatingSum(IEnumerable<long> values)
    {
        long sum = 0;
        foreach (long value in values)
        {
            if (value <= 0) continue;
            if (sum > long.MaxValue - value) return long.MaxValue;
            sum += value;
        }
        return sum;
    }

    /// <summary>Search grouped processes by application name or decimal PID.</summary>
    public static IReadOnlyList<ProcessGroupUsage> Filter(
        IReadOnlyList<ProcessGroupUsage> groups, string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query)) return groups;
        return groups.Where(group =>
            group.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            group.Members.Any(member =>
                member.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    .Contains(query, StringComparison.Ordinal))).ToArray();
    }

    public static IReadOnlyList<ProcessGroupUsage> Build(IEnumerable<ProcessUsage> processes)
    {
        var grouped = processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.OrderByDescending(p => p.WorkingBytes).ToArray();
                long working = SaturatingSum(members.Select(p => p.WorkingBytes));
                long privateBytes = SaturatingSum(members.Select(p => p.PrivateBytes));
                return new ProcessGroupUsage(
                    members[0].Name, members, working, privateBytes,
                    members.Select(p => p.IconBytes).FirstOrDefault(icon => icon is { Length: > 0 }),
                    0);
            })
            .OrderByDescending(g => g.WorkingBytes)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        long largest = Math.Max(1L, grouped.Length == 0 ? 0 : grouped[0].WorkingBytes);
        return grouped.Select(g => g with
            { RelativeLoad = Math.Clamp((double)g.WorkingBytes / largest * 100, 0, 100) })
            .ToArray();
    }
}
