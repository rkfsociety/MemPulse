using System.Globalization;
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
                var status = File.ReadAllText(Path.Combine(directory, "status"));
                result.Add(ParseProcessStatus(pid, status));
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
        string[] tail = stat[(delimiter + 1)..].Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tail.Length >= 20 &&
            ulong.TryParse(tail[19], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) &&
            ticks != 0;
    }

    public static MemoryUsage ParseMemInfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var data = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon];
            if (TryParseKilobytes(line[(colon + 1)..], out var bytes))
                data[key] = bytes;
        }

        ulong Get(string key) => data.GetValueOrDefault(key);
        ulong total = Get("MemTotal");
        if (total == 0) throw new InvalidDataException("/proc/meminfo не содержит MemTotal.");
        ulong available = data.ContainsKey("MemAvailable") ? Get("MemAvailable") :
            SaturatingAdd(SaturatingAdd(Get("MemFree"), Get("Buffers")), Get("Cached"));
        ulong cache = SaturatingAdd(Get("Cached"), Get("SReclaimable"));
        return new MemoryUsage(total, Math.Min(total, available), cache,
            Get("Committed_AS"), Get("CommitLimit"));
    }

    public static ProcessUsage ParseProcessStatus(int pid, string status)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        ArgumentNullException.ThrowIfNull(status);
        string name = "PID " + pid.ToString(CultureInfo.InvariantCulture);
        ulong resident = 0, data = 0;
        foreach (string line in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon];
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "Name":
                    if (value.Length > 0) name = value;
                    break;
                case "VmRSS":
                    if (TryParseKilobytes(value, out var rss)) resident = rss;
                    break;
                case "VmData":
                    if (TryParseKilobytes(value, out var vmData)) data = vmData;
                    break;
            }
        }
        return new ProcessUsage(pid, name, ToLong(resident), ToLong(data));
    }

    private static long ToLong(ulong n) => n > long.MaxValue ? long.MaxValue : (long)n;

    private static ulong SaturatingAdd(ulong a, ulong b) =>
        ulong.MaxValue - a < b ? ulong.MaxValue : a + b;

    private static bool TryParseKilobytes(string value, out ulong bytes)
    {
        bytes = 0;
        var fields = value.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length == 0 ||
            !ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return false;
        if (fields.Length > 1 && fields[1] != "kB")
            return false;
        bytes = number > ulong.MaxValue / 1024 ? ulong.MaxValue : number * 1024;
        return true;
    }
}
