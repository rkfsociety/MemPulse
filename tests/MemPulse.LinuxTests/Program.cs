using MemPulse.Core;
using MemPulse.Linux;

static void Assert(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
}

const string meminfo = """
MemTotal:        8000000 kB
MemFree:         1000000 kB
MemAvailable:    2500000 kB
Buffers:          100000 kB
Cached:          1200000 kB
SReclaimable:     300000 kB
Committed_AS:    4000000 kB
CommitLimit:     9500000 kB
""";
var ram = LinuxProcfs.ParseMemInfo(meminfo);
Assert(ram.Total == 8000000UL * 1024 && ram.Available == 2500000UL * 1024, "meminfo total and available");
Assert(ram.Cache == 1500000UL * 1024, "reclaimable cache");
Assert(ram.Committed == 4000000UL * 1024 && ram.CommitLimit == 9500000UL * 1024, "commit values");
Assert(ram.Used == 5500000UL * 1024 && Math.Abs(ram.UsedPercent - 68.75) < .001, "Linux used RAM calculation");
var old = LinuxProcfs.ParseMemInfo("MemTotal: 100 kB\nMemFree: 10 kB\nBuffers: 15 kB\nCached: 20 kB");
Assert(old.Available == 45UL * 1024, "fallback without MemAvailable");
Assert(LinuxProcfs.ParseMemInfo("MemTotal: 100 kB\nMemAvailable: 999 kB").Available == 100UL * 1024,
    "clamp MemAvailable to physical RAM");
try { LinuxProcfs.ParseMemInfo("Cached: 1000 kB"); throw new Exception("should have thrown"); }
catch (InvalidDataException) { Console.WriteLine("PASS: missing MemTotal rejected"); }
Assert(RussianText.Applications(1) == "1 приложение" &&
    RussianText.Applications(2) == "2 приложения" &&
    RussianText.Applications(5) == "5 приложений" &&
    RussianText.Applications(11) == "11 приложений" &&
    RussianText.Applications(21) == "21 приложение" &&
    RussianText.Applications(112) == "112 приложений", "Russian application count declension");
var p = LinuxProcfs.ParseProcessStatus(123, "Name:\tmy process\nVmRSS:\t1234 kB\nVmData:\t4567 kB");
Assert(p.Pid == 123 && p.Name == "my process" &&
       p.WorkingBytes == 1234L * 1024 && p.PrivateBytes == 4567L * 1024, "process status fields");
var groups = ProcessGrouping.Build([p, p with { Pid = 234 }]);
Assert(groups.Count == 1 && groups[0].Count == 2, "group Linux processes");
Assert(ReferenceEquals(ProcessGrouping.Filter(groups, ""), groups), "empty process search preserves original groups");
Assert(ProcessGrouping.Filter(groups, " MY PROCESS ").Count == 1, "process search ignores case and outer whitespace");
Assert(ProcessGrouping.Filter(groups, "234").Count == 1, "process search finds a member PID");
Assert(ProcessGrouping.Filter(groups, "999").Count == 0, "process search rejects unknown PIDs");
var many = ProcessGrouping.Build(Enumerable.Range(1, 250)
    .Select(i => new ProcessUsage(i, $"app{i:D3}", i * 1024L, 0)));
Assert(many.Count == 250 && ProcessGrouping.Filter(many, "app").Count == 250,
    "large process lists remain complete for progressive paging");

var sortable = ProcessGrouping.Build([
    new ProcessUsage(1, "zeta", 1200, 50),
    new ProcessUsage(2, "alpha", 250, 100),
    new ProcessUsage(3, "ALPHA", 250, 100),
    new ProcessUsage(4, "beta", 800, 900)
]);
Assert(ReferenceEquals(ProcessGrouping.Sort(sortable, ProcessSortOrder.WorkingMemory), sortable) &&
       sortable[0].Name == "zeta", "default sorting retains the working-memory order");
Assert(ProcessGrouping.Sort(sortable, ProcessSortOrder.Name).Select(g => g.Name)
       .SequenceEqual(["alpha", "beta", "zeta"]), "sort processes alphabetically");
Assert(ProcessGrouping.Sort(sortable, ProcessSortOrder.PrivateMemory)[0].Name == "beta",
    "sort processes by private/data memory");
Assert(ProcessGrouping.Sort(sortable, ProcessSortOrder.ProcessCount)[0].Name == "alpha",
    "sort processes by group size");

string stat = "111 (my proc (test)) S " + string.Join(' ', Enumerable.Repeat("0", 18)) + " 123456 0 0";
Assert(LinuxProcfs.TryParseStartTicks(stat, out var ticks) && ticks == 123456, "stat parsing with spaces and brackets");
Assert(!LinuxProcfs.TryParseStartTicks("broken", out _), "reject invalid proc stat");
Assert(LinuxProcfs.TryParseStartTicks(stat.Replace(" S ", "\tS\t"), out var tabTicks) &&
       tabTicks == 123456, "stat parser accepts whitespace-separated fields");
var tabMem = LinuxProcfs.ParseMemInfo("MemTotal:\t100\tkB\nMemAvailable:\t40\tkB");
Assert(tabMem.Total == 100UL * 1024 && tabMem.Available == 40UL * 1024,
    "meminfo parser accepts tab-separated values and units");
var tabProcess = LinuxProcfs.ParseProcessStatus(42, "Name:\ttabbed\nVmRSS:\t8\tkB\nVmData:\t4\tkB");
Assert(tabProcess.WorkingBytes == 8192 && tabProcess.PrivateBytes == 4096,
    "process status parser accepts tab-separated values and units");
var now = DateTimeOffset.UtcNow;
Assert(LinuxAutoCleanupPolicy.ShouldClean(91, 90, now, DateTimeOffset.MinValue), "above threshold triggers auto cleanup");
Assert(!LinuxAutoCleanupPolicy.ShouldClean(89, 90, now, DateTimeOffset.MinValue), "below threshold does not trigger");
Assert(!LinuxAutoCleanupPolicy.ShouldClean(91, 90, now, now.AddMinutes(5)), "cooldown prevents repeated cleaning");
Assert(!LinuxAutoCleanupPolicy.ShouldClean(double.NaN, 90, now, DateTimeOffset.MinValue), "invalid usage does not trigger");

const string maps = """
100000-120000 rw-p 00000000 00:00 0
120000-140000 r--p 00000000 00:00 0
140000-160000 rw-s 00000000 00:00 0
160000-180000 rw-p 00000000 00:00 0 [stack]
180000-1a0000 rw-p 00000000 00:00 0 [heap]
1a0000-1c0000 rw-p 00000000 08:01 333 /usr/lib/libfoo.so
""";
var candidates = LinuxProcessMemory.ParseEligibleMappings(maps, 128 * 1024);
Assert(candidates.Count == 1 && candidates[0].Address == 0x100000 && candidates[0].Length == 128 * 1024,
    "bounded page-out only selects anonymous private writable maps");
Assert(LinuxProcessMemory.ParseEligibleMappings(maps, 0).Count == 0, "zero budget cannot pageout");

const string guardedMaps = """
200000-220000 rw-p 00000000 08:01 333
220000-240000 rw-p 00000000 00:00 0 [stack]
240000-260000 rw-p 00000000 00:00 0 [anon:managed heap]
260000-280000 rw-p 00000000 00:00 0
""";
var guarded = LinuxProcessMemory.ParseEligibleMappings(guardedMaps, 256 * 1024, 65536);
Assert(guarded.Count == 2 && guarded[0].Address == 0x240000 &&
       guarded[1].Address == 0x260000 && guarded.All(m => m.Length == 128 * 1024),
    "page-out rejects file-backed and stack maps while retaining anonymous regions");
var limited = LinuxProcessMemory.ParseEligibleMappings(guardedMaps, 100000, 65536);
Assert(limited.Count == 1 && limited[0].Length == 65536,
    "page-out budget is aligned to the actual Linux page size");
try
{
    LinuxProcessMemory.ParseEligibleMappings(guardedMaps, 4096, 3000);
    throw new Exception("invalid page size was accepted");
}
catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS: invalid page size rejected"); }

// Multiple LinuxSettingsStore instances must not race while replacing settings.
string settingsDirectory = Path.Combine(Path.GetTempPath(), "MemPulse-linux-settings-" + Guid.NewGuid().ToString("N"));
try
{
    string settingsPath = Path.Combine(settingsDirectory, "linux-settings.json");
    var store = new LinuxSettingsStore(settingsPath);
    await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        new LinuxSettingsStore(settingsPath).Save(new AppSettings { RefreshSeconds = i % 10 + 1 }))));
    Assert(store.Load().RefreshSeconds is >= 1 and <= 10,
        "parallel Linux settings saves leave a valid JSON snapshot");
    Assert(!Directory.EnumerateFiles(settingsDirectory, "*.tmp").Any(),
        "Linux settings staging files are removed after saving");
    if (OperatingSystem.IsLinux())
    {
        var mode = File.GetUnixFileMode(settingsPath);
        Assert((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
            "Linux settings file remains private to the current user");
    }
}
finally
{
    if (Directory.Exists(settingsDirectory)) Directory.Delete(settingsDirectory, true);
}

string fakeProcRoot = Path.Combine(Path.GetTempPath(), "mempulse-proc-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(Path.Combine(fakeProcRoot, "101"));
    File.WriteAllText(Path.Combine(fakeProcRoot, "101", "status"), "Name:\tbash\nState:\tS (sleeping)\nVmRSS:\t2048 kB\nVmData:\t512 kB\n");
    File.WriteAllText(Path.Combine(fakeProcRoot, "101", "cmdline"), "/bin/other-should-not-override\0");

    Directory.CreateDirectory(Path.Combine(fakeProcRoot, "102"));
    File.WriteAllText(Path.Combine(fakeProcRoot, "102", "status"), "Name:\tWebKitWebProces\nState:\tS (sleeping)\nVmRSS:\t4096 kB\nVmData:\t1024 kB\n");
    File.WriteAllText(Path.Combine(fakeProcRoot, "102", "cmdline"), "/usr/lib/webkit/WebKitWebProcess\0--type=gpu\0");

    var procfs = new LinuxProcfs(fakeProcRoot);
    var procs = procfs.ReadProcesses();
    Assert(procs.Count == 2, "fake procfs reads both processes");
    Assert(procs.Any(x => x.Pid == 102 && x.Name == "WebKitWebProcess"), "15-char truncated name expands from cmdline");
    Assert(procs.Any(x => x.Pid == 101 && x.Name == "bash"), "short process name skips cmdline override");
}
finally
{
    if (Directory.Exists(fakeProcRoot)) Directory.Delete(fakeProcRoot, true);
}

if (OperatingSystem.IsLinux())
{
    var live = new LinuxProcfs();
    var snapshot = live.ReadMemory();
    Assert(snapshot.Total > 0 && snapshot.Available <= snapshot.Total, "live Linux memory snapshot");
    Assert(live.ReadProcesses().Count > 0, "live Linux process snapshot");
    Assert(LinuxProcfs.TryReadStartTicks(Environment.ProcessId, out var liveStart) && liveStart > 0,
        "live process identity");
}
Console.WriteLine("Linux smoke tests passed.");
