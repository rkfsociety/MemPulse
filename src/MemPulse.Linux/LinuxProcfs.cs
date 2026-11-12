using System.Globalization;
using System.IO;
using MemPulse.Core;

namespace MemPulse.Linux;

/// <summary>Unprivileged Linux memory and process snapshots from procfs.</summary>
public sealed class LinuxProcfs
{
    private readonly string _root;

    public LinuxProcfs(string root = "/proc") => _root = root;

    public MemoryUsage ReadMemory() => ParseMemInfo(File.ReadAllText(Path.Combine(_root, "meminfo")));

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
                result.Add(ReadProcessStatus(pid, Path.Combine(directory, "status")));
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
            }
        }

        if (memTotal == 0) throw new InvalidDataException("/proc/meminfo не содержит MemTotal.");
        ulong available = hasMemAvailable ? memAvailable :
            SaturatingAdd(SaturatingAdd(memFree, buffers), cached);
        ulong cache = SaturatingAdd(cached, sReclaimable);
        return new MemoryUsage(memTotal, Math.Min(memTotal, available), cache,
            committedAs, commitLimit);
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

    private static ProcessUsage ReadProcessStatus(int pid, string statusPath)
    {
        string? name = null;
        ulong resident = 0, data = 0;
        bool hasName = false, hasRss = false, hasData = false;

        using var reader = new StreamReader(statusPath);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (TryParseStatusLine(line.AsSpan(), ref name, ref resident, ref data, ref hasName, ref hasRss, ref hasData))
                break;
        }

        return new ProcessUsage(
            pid,
            name ?? "PID " + pid.ToString(CultureInfo.InvariantCulture),
            ToLong(resident),
            ToLong(data));
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
