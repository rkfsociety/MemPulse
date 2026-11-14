using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MemPulse.Core.Platform.Windows;

/// <summary>
/// Best-effort Windows process icon extractor without System.Drawing or WinForms dependencies.
/// Extracts 32-bit icons as PNG bytes for top processes; falls back to null (monogram).
/// </summary>
public sealed class WindowsProcessIconProvider : IProcessIconProvider
{
    private const int MaxIconCacheEntries = 200;
    private const int MaxPidCacheEntries = 512;

    private readonly LruCache<string, byte[]?> _iconCache = new(MaxIconCacheEntries, StringComparer.OrdinalIgnoreCase);
    private readonly LruCache<int, (string Name, string? ExePath)> _pidPathCache = new(MaxPidCacheEntries);

    public IReadOnlyList<ProcessUsage> Decorate(IReadOnlyList<ProcessUsage> rows)
    {
        if (rows.Count == 0)
            return rows;

        long largest = Math.Max(1, rows[0].WorkingBytes);
        var result = new ProcessUsage[rows.Count];
        bool isWindows = OperatingSystem.IsWindows();

        for (int i = 0; i < rows.Count; i++)
        {
            var p = rows[i];
            byte[]? png = null;
            if (isWindows && i < 28)
            {
                var exePath = ResolveExecutablePath(p.Pid, p.Name);
                if (exePath is not null && !_iconCache.TryGetValue(exePath, out png))
                {
                    png = ExtractPngFromPath(exePath);
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
        if (pid <= 0)
            return null;

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
        IntPtr handle = WindowsNative.OpenProcess(WindowsNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        char[] buffer = ArrayPool<char>.Shared.Rent(1024);
        try
        {
            uint size = (uint)buffer.Length;
            if (WindowsNative.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0)
                return new string(buffer, 0, (int)size);
            return null;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
            WindowsNative.CloseHandle(handle);
        }
    }

    private static byte[]? ExtractPngFromPath(string path)
    {
        try
        {
            var shfi = new WindowsNative.SHFILEINFO();
            IntPtr result = WindowsNative.SHGetFileInfo(
                path,
                0,
                out shfi,
                (uint)Marshal.SizeOf<WindowsNative.SHFILEINFO>(),
                WindowsNative.SHGFI_ICON | WindowsNative.SHGFI_LARGEICON);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                return IconHandleToPng(shfi.hIcon);
            }
            finally
            {
                WindowsNative.DestroyIcon(shfi.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? IconHandleToPng(IntPtr hIcon)
    {
        if (!WindowsNative.GetIconInfo(hIcon, out var iconInfo))
            return null;

        try
        {
            if (iconInfo.hbmColor == IntPtr.Zero)
                return null;

            if (WindowsNative.GetObject(iconInfo.hbmColor, Marshal.SizeOf<WindowsNative.BITMAP>(), out var bmp) == 0)
                return null;

            int width = bmp.bmWidth;
            int height = bmp.bmHeight;
            if (width is <= 0 or > 256 || height is <= 0 or > 256)
                return null;

            var bmi = new WindowsNative.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<WindowsNative.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = WindowsNative.BI_RGB
            };

            byte[] bgra = new byte[width * height * 4];
            IntPtr hdc = WindowsNative.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return null;

            try
            {
                int lines = WindowsNative.GetDIBits(
                    hdc,
                    iconInfo.hbmColor,
                    0,
                    (uint)height,
                    bgra,
                    ref bmi,
                    WindowsNative.DIB_RGB_COLORS);
                if (lines != height)
                    return null;
            }
            finally
            {
                WindowsNative.ReleaseDC(IntPtr.Zero, hdc);
            }

            // Convert BGRA -> RGBA and ensure non-empty alpha for legacy 32bpp icons without alpha channel.
            bool anyAlpha = false;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] != 0)
                {
                    anyAlpha = true;
                    break;
                }
            }

            byte[] rgba = new byte[bgra.Length];
            for (int i = 0; i < bgra.Length; i += 4)
            {
                rgba[i] = bgra[i + 2];
                rgba[i + 1] = bgra[i + 1];
                rgba[i + 2] = bgra[i];
                rgba[i + 3] = anyAlpha ? bgra[i + 3] : (byte)255;
            }

            return EncodeRgbaPng(width, height, rgba);
        }
        finally
        {
            if (iconInfo.hbmColor != IntPtr.Zero)
                WindowsNative.DeleteObject(iconInfo.hbmColor);
            if (iconInfo.hbmMask != IntPtr.Zero)
                WindowsNative.DeleteObject(iconInfo.hbmMask);
        }
    }

    private static byte[] EncodeRgbaPng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        int stride = width * 4;
        byte[] raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int dstRow = y * (stride + 1);
            raw[dstRow] = 0; // filter None
            Buffer.BlockCopy(rgba, y * stride, raw, dstRow + 1, stride);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());

        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        output.Write(len);
        output.Write(type);
        output.Write(data);

        uint crc = ComputeCrc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type)
            crc = UpdateCrc(crc, b);
        foreach (byte b in data)
            crc = UpdateCrc(crc, b);
        return crc ^ 0xFFFFFFFFu;

        static uint UpdateCrc(uint c, byte b)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            return c;
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
