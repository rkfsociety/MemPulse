using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

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
        if (_cache.Count > 200) _cache.Clear();

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
