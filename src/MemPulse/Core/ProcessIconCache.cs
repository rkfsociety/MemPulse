using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Linq;

namespace MemPulse.Core;

/// <summary>
/// Best-effort process icons. Only the largest processes are examined on a worker thread.
/// Missing/protected processes retain the lightweight monogram fallback.
/// </summary>
public sealed class ProcessIconCache
{
    private readonly Dictionary<(int Pid, string Name), byte[]?> _cache = new();

    public IReadOnlyList<ProcessUsage> Decorate(IReadOnlyList<ProcessUsage> rows)
    {
        if (rows.Count == 0) return rows;
        // Optional: limit cache size; we will prune stale entries instead of clearing entire cache.
if (_cache.Count > 200)
{
    // Keep only entries for processes that appear in the current snapshot.
    // This prevents unnecessary re‑extracting of icons for active processes.
    var currentKeys = new HashSet<(int Pid, string Name)>(rows.Select(p => (p.Pid, p.Name)));
    var keysToRemove = _cache.Keys.Where(k => !currentKeys.Contains(k)).ToList();
    foreach (var k in keysToRemove)
        _cache.Remove(k);
    // If cache is still too large (unlikely), remove oldest entries (simple LRU placeholder).
    while (_cache.Count > 200)
    {
        var oldestKey = _cache.Keys.First(); // Dictionary iteration order is insertion order in .NET 5+.
        _cache.Remove(oldestKey);
    }
}

        long largest = Math.Max(1, rows[0].WorkingBytes);
        var result = new ProcessUsage[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            byte[]? png = null;
            if (i < 28)
            {
                var key = (p.Pid, p.Name);
                if (!_cache.TryGetValue(key, out png))
                {
                    png = Extract(p.Pid);
                    _cache[key] = png;
                }
            }
            result[i] = p with
            {
                IconBytes = png,
                RelativeLoad = Math.Clamp((double)p.WorkingBytes / largest * 100, 0, 100)
            };
        }
        return result;
    }

    private static byte[]? Extract(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException or ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            // Protected, short-lived and system processes frequently have no readable icon.
            return null;
        }
    }
}
