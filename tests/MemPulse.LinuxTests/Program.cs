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
try
{
    LinuxProcfs.ParseMemInfo("Cached: 1000 kB");
    throw new Exception("Invalid meminfo should have thrown");
}
catch (InvalidDataException) { Console.WriteLine("PASS: missing MemTotal rejected"); }
var p = LinuxProcfs.ParseProcessStatus(123, "Name:\tmy process\nVmRSS:\t1234 kB\nVmData:\t4567 kB");
Assert(p.Pid == 123 && p.Name == "my process" &&
       p.WorkingBytes == 1234L * 1024 && p.PrivateBytes == 4567L * 1024, "process status fields");
var groups = ProcessGrouping.Build([p, p with { Pid = 234 }]);
Assert(groups.Count == 1 && groups[0].Count == 2, "group Linux processes");

if (OperatingSystem.IsLinux())
{
    var live = new LinuxProcfs();
    var snapshot = live.ReadMemory();
    Assert(snapshot.Total > 0 && snapshot.Available <= snapshot.Total, "live Linux memory snapshot");
    Assert(live.ReadProcesses().Count > 0, "live Linux process snapshot");
}
Console.WriteLine("Linux smoke tests passed.");
