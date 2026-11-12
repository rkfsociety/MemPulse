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
    // Cache keyed by executable file path to avoid repeatedly opening processes.
    private readonly Dictionary<string, byte[]?> _cache = new();

    public IReadOnlyList<ProcessUsage> Decorate(IReadOnlyList<ProcessUsage> rows)
    {
        if (rows.Count == 0) return rows;
        // Optional: limit cache size; prune stale entries.
        if (_cache.Count > 200)
        {
            var currentKeys = new HashSet<string>(rows.Select(p => GetExecutablePath(p.Pid)).Where(p => p != null)!);
            var keysToRemove = _cache.Keys.Where(k => !currentKeys.Contains(k)).ToList();
            foreach (var k in keysToRemove)
                _cache.Remove(k);
            while (_cache.Count > 200)
            {
                var oldestKey = _cache.Keys.First();
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
                var exePath = GetExecutablePath(p.Pid);
                if (exePath != null && !_cache.TryGetValue(exePath, out png))
                {
                    png = ExtractFromPath(exePath);
                    _cache[exePath] = png;
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

    private static string? GetExecutablePath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ExtractFromPath(string path)
    {
        try
        {
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
            return null;
        }
    }
}
