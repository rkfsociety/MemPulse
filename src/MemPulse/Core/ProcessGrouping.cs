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
    /// <summary>
    /// Compares process group snapshots for UI updates without triggering full collection resets
    /// on new Members array instances or sub-percent RelativeLoad fluctuations.
    /// </summary>
    public static IEqualityComparer<ProcessGroupUsage> SnapshotComparer { get; } = new ProcessGroupSnapshotComparer();

    private sealed class ProcessGroupSnapshotComparer : IEqualityComparer<ProcessGroupUsage>
    {
        public bool Equals(ProcessGroupUsage? x, ProcessGroupUsage? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            if (x.WorkingBytes != y.WorkingBytes ||
                x.PrivateBytes != y.PrivateBytes ||
                x.Members.Count != y.Members.Count ||
                !string.Equals(x.Name, y.Name, StringComparison.Ordinal) ||
                (x.IconBytes is null) != (y.IconBytes is null) ||
                Math.Abs(x.RelativeLoad - y.RelativeLoad) >= 0.5)
                return false;

            for (int i = 0; i < x.Members.Count; i++)
            {
                if (x.Members[i].Pid != y.Members[i].Pid)
                    return false;
            }
            return true;
        }

        public int GetHashCode(ProcessGroupUsage obj) =>
            HashCode.Combine(obj.Name, obj.WorkingBytes, obj.PrivateBytes, obj.Members.Count);
    }

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
    private static long SaturatingAdd(long sum, long value)
    {
        if (value <= 0) return sum;
        return sum > long.MaxValue - value ? long.MaxValue : sum + value;
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
                long working = 0;
                long privateBytes = 0;
                byte[]? icon = null;
                foreach (var member in members)
                {
                    working = SaturatingAdd(working, member.WorkingBytes);
                    privateBytes = SaturatingAdd(privateBytes, member.PrivateBytes);
                    if (icon is null && member.IconBytes is { Length: > 0 })
                        icon = member.IconBytes;
                }
                return new ProcessGroupUsage(
                    members[0].Name, members, working, privateBytes, icon, 0);
            })
            .OrderByDescending(g => g.WorkingBytes)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (grouped.Length == 0) return grouped;

        long largest = Math.Max(1L, grouped[0].WorkingBytes);
        for (int i = 0; i < grouped.Length; i++)
        {
            grouped[i] = grouped[i] with
            {
                RelativeLoad = Math.Clamp((double)grouped[i].WorkingBytes / largest * 100, 0, 100)
            };
        }
        return grouped;
    }
}
