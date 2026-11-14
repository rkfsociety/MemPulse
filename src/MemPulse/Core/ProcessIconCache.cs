using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace MemPulse.Core;

/// <summary>
/// Best-effort process icons. Only the largest processes are examined on a worker thread.
/// Missing/protected processes retain the lightweight monogram fallback.
/// </summary>
public sealed class ProcessIconCache
{
    private const int MaxIconCacheEntries = 200;
    private const int MaxPidCacheEntries = 512;

    // Cache keyed by executable file path to avoid repeatedly extracting icons.
    private readonly LruCache<string, byte[]?> _iconCache = new(MaxIconCacheEntries, StringComparer.OrdinalIgnoreCase);
    // Cache keyed by PID + process name to avoid reopening processes on every tick.
    private readonly LruCache<int, (string Name, string? ExePath)> _pidPathCache = new(MaxPidCacheEntries);

    public IReadOnlyList<ProcessUsage> Decorate(IReadOnlyList<ProcessUsage> rows)
    {
        if (rows.Count == 0) return rows;

        long largest = Math.Max(1, rows[0].WorkingBytes);
        var result = new ProcessUsage[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            byte[]? png = null;
            if (i < 28)
            {
                var exePath = ResolveExecutablePath(p.Pid, p.Name);
                if (exePath != null && !_iconCache.TryGetValue(exePath, out png))
                {
                    png = ExtractFromPath(exePath);
                    _iconCache.Set(exePath, png);
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

    private string? ResolveExecutablePath(int pid, string processName)
    {
        if (pid <= 0) return null;
        if (_pidPathCache.TryGetValue(pid, out var cached) &&
            string.Equals(cached.Name, processName, StringComparison.OrdinalIgnoreCase))
        {
            return cached.ExePath;
        }

        string? exePath = GetExecutablePath(pid);
        _pidPathCache.Set(pid, (processName, exePath));
        return exePath;
    }

    private static string? GetExecutablePath(int pid)
    {
        IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;

        char[] buffer = ArrayPool<char>.Shared.Rent(1024);
        try
        {
            uint size = (uint)buffer.Length;
            if (Native.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
                return new string(buffer, 0, (int)size);
            return null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            Native.CloseHandle(handle);
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

    private sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
        private readonly LinkedList<(TKey Key, TValue Value)> _order = new();

        public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
        {
            _capacity = capacity;
            _map = new Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>>(capacity, comparer);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            value = default!;
            return false;
        }

        public void Set(TKey key, TValue value)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = (key, value);
                _order.Remove(existing);
                _order.AddFirst(existing);
                return;
            }

            if (_map.Count >= _capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }

            var node = _order.AddFirst((key, value));
            _map[key] = node;
        }
    }
}
