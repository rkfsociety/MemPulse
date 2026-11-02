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
var p = LinuxProcfs.ParseProcessStatus(123, "Name:\tmy process\nVmRSS:\t1234 kB\nVmData:\t4567 kB");
Assert(p.Pid == 123 && p.Name == "my process" &&
       p.WorkingBytes == 1234L * 1024 && p.PrivateBytes == 4567L * 1024, "process status fields");
var groups = ProcessGrouping.Build([p, p with { Pid = 234 }]);
Assert(groups.Count == 1 && groups[0].Count == 2, "group Linux processes");

string stat = "111 (my proc (test)) S " + string.Join(' ', Enumerable.Repeat("0", 19)) + " 123456 0 0";
Assert(LinuxProcfs.TryParseStartTicks(stat, out var ticks) && ticks == 123456, "stat parsing with spaces and brackets");
Assert(!LinuxProcfs.TryParseStartTicks("broken", out _), "reject invalid proc stat");
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
