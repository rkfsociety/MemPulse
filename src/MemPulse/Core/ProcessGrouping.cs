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

public static class ProcessGrouping
{
    public static IReadOnlyList<ProcessGroupUsage> Build(IEnumerable<ProcessUsage> processes)
    {
        var grouped = processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var members = g.OrderByDescending(p => p.WorkingBytes).ToArray();
                long working = members.Sum(p => Math.Max(0, p.WorkingBytes));
                long privateBytes = members.Sum(p => Math.Max(0, p.PrivateBytes));
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
