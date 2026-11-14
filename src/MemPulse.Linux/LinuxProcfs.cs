using System.Globalization;
using System.IO;
using MemPulse.Core;

namespace MemPulse.Linux;

/// <summary>Unprivileged Linux memory and process snapshots from procfs.</summary>
public sealed class LinuxProcfs
{
    private readonly string _root;

    public LinuxProcfs(string root = "/proc") => _root = root;

    public MemoryUsage ReadMemory()
    {
        var memory = ParseMemInfo(File.ReadAllText(Path.Combine(_root, "meminfo")));
        string swapsPath = Path.Combine(_root, "swaps");
        if (File.Exists(swapsPath))
        {
            try
            {
                var (zramTotal, zramUsed) = ParseProcSwapsZram(File.ReadAllText(swapsPath));
                return memory with { ZramTotal = zramTotal, ZramUsed = zramUsed };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // /proc/swaps may be unavailable in restricted containers.
            }
        }
        return memory;
    }

    public IReadOnlyList<ProcessUsage> ReadProcesses()
    {
        var result = new List<ProcessUsage>();
        foreach (string directory in Directory.EnumerateDirectories(_root))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None,
                CultureInfo.InvariantCulture, out int pid) || pid <= 0)
                continue;
            try
            {
                result.Add(ReadProcessStatus(pid, directory));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Processes can disappear during enumeration, and /proc may deny access.
            }
        }
        return result.OrderByDescending(p => p.WorkingBytes).ThenBy(p => p.Name).ToArray();
    }

    public static bool TryReadStartTicks(int pid, out ulong ticks)
    {
        ticks = 0;
        if (pid <= 0) return false;
        try { return TryParseStartTicks(File.ReadAllText($"/proc/{pid}/stat"), out ticks); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // /proc/PID/stat field 2 (comm) may contain spaces and parentheses.
    // Field 22 (starttime) is the twentieth token after the final ')' delimiter.
    public static bool TryParseStartTicks(string stat, out ulong ticks)
    {
        ticks = 0;
        int delimiter = stat.LastIndexOf(')');
        if (delimiter < 0 || delimiter + 1 >= stat.Length) return false;
        ReadOnlySpan<char> tailSpan = stat.AsSpan((delimiter + 1));
        int tokenCount = 0;
        int i = 0;
        while (i < tailSpan.Length)
        {
            // Skip whitespace
            while (i < tailSpan.Length && char.IsWhiteSpace(tailSpan[i])) i++;
            if (i >= tailSpan.Length) break;
            int start = i;
            while (i < tailSpan.Length && !char.IsWhiteSpace(tailSpan[i])) i++;
            tokenCount++;
            if (tokenCount == 20)
            {
                var token = tailSpan.Slice(start, i - start);
                return ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out ticks) && ticks != 0;
            }
        }
        return false;
    }

    public static MemoryUsage ParseMemInfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong memTotal = 0, memAvailable = 0, memFree = 0, buffers = 0;
        ulong cached = 0, sReclaimable = 0, committedAs = 0, commitLimit = 0;
        ulong swapTotal = 0, swapFree = 0;
        bool hasMemAvailable = false;

        foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty) continue;
            int colon = line.IndexOf(':');
            if (colon < 0) continue;

            ReadOnlySpan<char> key = line[..colon];
            ReadOnlySpan<char> value = line[(colon + 1)..];
            if (!TryParseKilobytes(value, out ulong bytes))
                continue;

            switch (key)
            {
                case "MemTotal": memTotal = bytes; break;
                case "MemAvailable": memAvailable = bytes; hasMemAvailable = true; break;
                case "MemFree": memFree = bytes; break;
                case "Buffers": buffers = bytes; break;
                case "Cached": cached = bytes; break;
                case "SReclaimable": sReclaimable = bytes; break;
                case "Committed_AS": committedAs = bytes; break;
                case "CommitLimit": commitLimit = bytes; break;
                case "SwapTotal": swapTotal = bytes; break;
                case "SwapFree": swapFree = bytes; break;
            }
        }

        if (memTotal == 0) throw new InvalidDataException("/proc/meminfo не содержит MemTotal.");
        ulong available = hasMemAvailable ? memAvailable :
            SaturatingAdd(SaturatingAdd(memFree, buffers), cached);
        ulong cache = SaturatingAdd(cached, sReclaimable);
        ulong swapUsed = swapTotal >= swapFree ? swapTotal - swapFree : 0;
        return new MemoryUsage(memTotal, Math.Min(memTotal, available), cache,
            committedAs, commitLimit, swapTotal, swapUsed);
    }

    public static (ulong ZramTotal, ulong ZramUsed) ParseProcSwapsZram(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong total = 0, used = 0;
        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty || line.StartsWith("Filename", StringComparison.OrdinalIgnoreCase))
                continue;

            // Format: Filename Type Size(KiB) Used(KiB) Priority
            int firstSpace = line.IndexOfAny(' ', '\t');
            if (firstSpace <= 0) continue;
            var name = line[..firstSpace];
            if (!name.Contains("zram", StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = line[(firstSpace + 1)..].TrimStart();
            int secondSpace = rest.IndexOfAny(' ', '\t');
            if (secondSpace <= 0) continue;
            rest = rest[(secondSpace + 1)..].TrimStart();

            int thirdSpace = rest.IndexOfAny(' ', '\t');
            if (thirdSpace <= 0) continue;
            if (!ulong.TryParse(rest[..thirdSpace], NumberStyles.None, CultureInfo.InvariantCulture, out ulong sizeKiB))
                continue;

            rest = rest[(thirdSpace + 1)..].TrimStart();
            int fourthSpace = rest.IndexOfAny(' ', '\t');
            var usedToken = fourthSpace > 0 ? rest[..fourthSpace] : rest;
            if (!ulong.TryParse(usedToken, NumberStyles.None, CultureInfo.InvariantCulture, out ulong usedKiB))
                continue;

            total = SaturatingAdd(total, sizeKiB <= ulong.MaxValue / 1024 ? sizeKiB * 1024 : ulong.MaxValue);
            used = SaturatingAdd(used, usedKiB <= ulong.MaxValue / 1024 ? usedKiB * 1024 : ulong.MaxValue);
        }
        return (total, used);
    }

    public static ProcessUsage ParseProcessStatus(int pid, string status)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        ArgumentNullException.ThrowIfNull(status);

        string? name = null;
        ulong resident = 0, data = 0;
        bool hasName = false, hasRss = false, hasData = false;

        foreach (ReadOnlySpan<char> line in status.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty) continue;
            if (TryParseStatusLine(line, ref name, ref resident, ref data, ref hasName, ref hasRss, ref hasData))
                break;
        }

        return new ProcessUsage(
            pid,
            name ?? "PID " + pid.ToString(CultureInfo.InvariantCulture),
            ToLong(resident),
            ToLong(data));
    }

    private static ProcessUsage ReadProcessStatus(int pid, string processDirectory)
    {
        string? name = null;
        ulong resident = 0, data = 0;
        bool hasName = false, hasRss = false, hasData = false;

        using (var reader = new StreamReader(Path.Combine(processDirectory, "status")))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (TryParseStatusLine(line.AsSpan(), ref name, ref resident, ref data, ref hasName, ref hasRss, ref hasData))
                    break;
            }
        }

        // Linux truncates /proc/<pid>/status Name (TASK_COMM_LEN - 1) at 15 bytes.
        // Only inspect /proc/<pid>/cmdline when missing or truncated at 15 chars.
        if (string.IsNullOrWhiteSpace(name) || name.Length == 15)
        {
            string? cmdlineName = TryReadProcessNameFromCmdline(Path.Combine(processDirectory, "cmdline"));
            if (!string.IsNullOrEmpty(cmdlineName))
                name = cmdlineName;
        }

        return new ProcessUsage(
            pid,
            name ?? $"PID {pid}",
            ToLong(resident),
            ToLong(data));
    }

    private static string? TryReadProcessNameFromCmdline(string cmdlinePath)
    {
        try
        {
            using var stream = new FileStream(cmdlinePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buffer = stackalloc byte[1024];
            int read = stream.Read(buffer);
            if (read <= 0) return null;

            ReadOnlySpan<byte> slice = buffer[..read];
            int nul = slice.IndexOf((byte)0);
            if (nul == 0) return null;
            if (nul > 0) slice = slice[..nul];

            string arg0 = System.Text.Encoding.UTF8.GetString(slice).Trim();
            if (arg0.Length == 0) return null;

            string fileName = Path.GetFileName(arg0);
            return string.IsNullOrWhiteSpace(fileName) ? arg0 : fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryParseStatusLine(
        ReadOnlySpan<char> line,
        ref string? name,
        ref ulong resident,
        ref ulong data,
        ref bool hasName,
        ref bool hasRss,
        ref bool hasData)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return false;

        ReadOnlySpan<char> key = line[..colon];
        ReadOnlySpan<char> value = line[(colon + 1)..].Trim();
        switch (key)
        {
            case "Name":
                if (value.Length > 0) name = value.ToString();
                hasName = true;
                break;
            case "VmRSS":
                if (TryParseKilobytes(value, out ulong rss)) resident = rss;
                hasRss = true;
                break;
            case "VmData":
                if (TryParseKilobytes(value, out ulong vmData)) data = vmData;
                hasData = true;
                break;
        }

        return hasName && hasRss && hasData;
    }

    private static long ToLong(ulong n) => n > long.MaxValue ? long.MaxValue : (long)n;

    private static ulong SaturatingAdd(ulong a, ulong b) =>
        ulong.MaxValue - a < b ? ulong.MaxValue : a + b;

    private static bool TryParseKilobytes(ReadOnlySpan<char> value, out ulong bytes)
    {
        bytes = 0;
        value = value.Trim();
        if (value.IsEmpty) return false;

        int wsIndex = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsWhiteSpace(value[i]))
            {
                wsIndex = i;
                break;
            }
        }

        ReadOnlySpan<char> numberSpan;
        if (wsIndex < 0)
        {
            numberSpan = value;
        }
        else
        {
            numberSpan = value[..wsIndex];
            ReadOnlySpan<char> rest = value[(wsIndex + 1)..].TrimStart();
            if (!rest.IsEmpty)
            {
                if (!rest.StartsWith("kB", StringComparison.Ordinal) ||
                    (rest.Length > 2 && !char.IsWhiteSpace(rest[2])))
                    return false;
            }
        }

        if (!ulong.TryParse(numberSpan, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
            return false;

        bytes = number > ulong.MaxValue / 1024 ? ulong.MaxValue : number * 1024;
        return true;
    }
}
